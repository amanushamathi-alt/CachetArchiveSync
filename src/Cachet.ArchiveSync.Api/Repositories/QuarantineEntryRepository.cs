using System.Data;
using Cachet.ArchiveSync.Core.Dtos;
using Cachet.ArchiveSync.Core.Interfaces;
using Dapper;
using Microsoft.Data.SqlClient;

namespace Cachet.ArchiveSync.Api.Repositories;

/// <summary>
/// Dapper implementation of <see cref="IQuarantineEntryRepository"/>, calling
/// <c>dbo.quarantine_entry_ins</c> (CAS-006 §5). Maps the stored procedure's friendly
/// <c>RAISERROR</c> messages ("search_result_id not found." / "file is already actively
/// quarantined.") to <see cref="QuarantineInsertOutcome"/> — by design, per the procedure's own
/// header comment, so the application layer can produce a clean per-file 404/409 (§7 V7/V8)
/// instead of surfacing a raw constraint-violation exception.
/// </summary>
public sealed class QuarantineEntryRepository : IQuarantineEntryRepository
{
    private const string SearchResultNotFoundMessage = "search_result_id not found.";
    private const string AlreadyActiveMessage = "file is already actively quarantined.";
    private const string FileIndexNotFoundMessage = "file_index_id not found or not active.";
    private const string FolderEmptyMessage = "folder holds no active files.";

    private readonly string _connectionString;

    public QuarantineEntryRepository(IConfiguration configuration)
    {
        _connectionString = configuration.GetConnectionString("CachetArchiveSync")
            ?? throw new InvalidOperationException(
                "Configuration key 'ConnectionStrings:CachetArchiveSync' must be set.");
    }

    public async Task<QuarantineInsertResult> InsertAsync(
        string createdBy, string updatedBy, int searchResultId, CancellationToken ct)
    {
        var parameters = new DynamicParameters();
        parameters.Add("@created_by", createdBy);
        parameters.Add("@updated_by", updatedBy);
        parameters.Add("@search_result_id", searchResultId);
        parameters.Add("@quarantine_entry_id", dbType: DbType.Int32, direction: ParameterDirection.Output);

        await using var connection = new SqlConnection(_connectionString);

        try
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "dbo.quarantine_entry_ins",
                parameters,
                commandType: CommandType.StoredProcedure,
                cancellationToken: ct));
        }
        catch (SqlException ex) when (ex.Message.Contains(SearchResultNotFoundMessage, StringComparison.Ordinal))
        {
            return new QuarantineInsertResult { Outcome = QuarantineInsertOutcome.SearchResultNotFound };
        }
        catch (SqlException ex) when (ex.Message.Contains(AlreadyActiveMessage, StringComparison.Ordinal))
        {
            return new QuarantineInsertResult { Outcome = QuarantineInsertOutcome.AlreadyActive };
        }

        return new QuarantineInsertResult
        {
            Outcome = QuarantineInsertOutcome.Added,
            QuarantineEntryId = parameters.Get<int>("@quarantine_entry_id")
        };
    }

    /// <summary>CAS-008 §5/§6 — all Active rows (Pre-Action Report), via <c>dbo.quarantine_entry_lst</c>.
    /// CAS-050 made the SP's paging parameters mandatory, so this requests one maximal-size page.</summary>
    public async Task<IReadOnlyList<QuarantineEntryListRow>> ListActiveAsync(CancellationToken ct)
    {
        var page = await ListActivePagedAsync(1, int.MaxValue, ct);
        return page.Rows;
    }

    /// <summary>CAS-050 §7 — backs <c>GET /quarantine</c>, via <c>dbo.quarantine_entry_lst</c>.</summary>
    public async Task<QuarantineEntryListPage> ListActivePagedAsync(int page, int pageSize, CancellationToken ct)
    {
        var parameters = new DynamicParameters();
        parameters.Add("@page", page);
        parameters.Add("@page_size", pageSize);
        parameters.Add("@total_count", dbType: DbType.Int32, direction: ParameterDirection.Output);

        await using var connection = new SqlConnection(_connectionString);

        var rows = await connection.QueryAsync<ListRow>(new CommandDefinition(
            "dbo.quarantine_entry_lst",
            parameters,
            commandType: CommandType.StoredProcedure,
            cancellationToken: ct));

        var mapped = rows.Select(row => new QuarantineEntryListRow
        {
            QuarantineEntryId = row.quarantine_entry_id,
            FileIndexId = row.file_index_id,
            Name = row.name,
            Path = row.path,
            QuarantinedOn = SqlDateTimeMapping.ToUtcOffset(row.quarantined_on),
            Status = row.status,
            SourceTab = row.source_tab
        }).ToList();

        // OUTPUT parameters are populated only after the result set has been fully read (QueryAsync buffers).
        return new QuarantineEntryListPage
        {
            Rows = mapped,
            TotalCount = parameters.Get<int>("@total_count")
        };
    }

    /// <summary>CAS-050 §7 — <c>dbo.quarantine_entry_ins_browse_file</c>; friendly RAISERROR messages mapped to outcomes.</summary>
    public async Task<QuarantineInsertResult> InsertBrowseFileAsync(
        string createdBy, string updatedBy, int fileIndexId, CancellationToken ct)
    {
        var parameters = new DynamicParameters();
        parameters.Add("@file_index_id", fileIndexId);
        parameters.Add("@created_by", createdBy);
        parameters.Add("@updated_by", updatedBy);
        parameters.Add("@quarantine_entry_id", dbType: DbType.Int32, direction: ParameterDirection.Output);

        await using var connection = new SqlConnection(_connectionString);

        try
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "dbo.quarantine_entry_ins_browse_file",
                parameters,
                commandType: CommandType.StoredProcedure,
                cancellationToken: ct));
        }
        catch (SqlException ex) when (ex.Message.Contains(FileIndexNotFoundMessage, StringComparison.Ordinal))
        {
            return new QuarantineInsertResult { Outcome = QuarantineInsertOutcome.SearchResultNotFound };
        }
        catch (SqlException ex) when (ex.Message.Contains(AlreadyActiveMessage, StringComparison.Ordinal))
        {
            return new QuarantineInsertResult { Outcome = QuarantineInsertOutcome.AlreadyActive };
        }

        return new QuarantineInsertResult
        {
            Outcome = QuarantineInsertOutcome.Added,
            QuarantineEntryId = parameters.Get<int>("@quarantine_entry_id")
        };
    }

    /// <summary>CAS-050 §7 — <c>dbo.quarantine_entry_ins_browse_folder</c>, one set-based statement; <c>null</c> when the folder holds no active files.</summary>
    public async Task<QuarantineBrowseFolderAddRow?> InsertBrowseFolderAsync(
        string createdBy, string updatedBy, string source, string bucketName, string folderPath, CancellationToken ct)
    {
        var parameters = new DynamicParameters();
        parameters.Add("@source", source);
        parameters.Add("@bucket_name", bucketName);
        parameters.Add("@folder_path", folderPath);
        parameters.Add("@created_by", createdBy);
        parameters.Add("@updated_by", updatedBy);
        parameters.Add("@added", dbType: DbType.Int32, direction: ParameterDirection.Output);
        parameters.Add("@already_quarantined", dbType: DbType.Int32, direction: ParameterDirection.Output);

        await using var connection = new SqlConnection(_connectionString);

        try
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "dbo.quarantine_entry_ins_browse_folder",
                parameters,
                commandType: CommandType.StoredProcedure,
                cancellationToken: ct));
        }
        catch (SqlException ex) when (ex.Message.Contains(FolderEmptyMessage, StringComparison.Ordinal))
        {
            return null;
        }

        return new QuarantineBrowseFolderAddRow
        {
            Added = parameters.Get<int>("@added"),
            AlreadyQuarantined = parameters.Get<int>("@already_quarantined")
        };
    }

    /// <summary>CAS-050 §7 — <c>dbo.quarantine_entry_del_browse_file</c> (hard delete).</summary>
    public async Task<int> HardDeleteBrowseFileAsync(int fileIndexId, CancellationToken ct)
    {
        var parameters = new DynamicParameters();
        parameters.Add("@file_index_id", fileIndexId);
        parameters.Add("@rows_affected", dbType: DbType.Int32, direction: ParameterDirection.Output);

        await using var connection = new SqlConnection(_connectionString);

        await connection.ExecuteAsync(new CommandDefinition(
            "dbo.quarantine_entry_del_browse_file",
            parameters,
            commandType: CommandType.StoredProcedure,
            cancellationToken: ct));

        return parameters.Get<int>("@rows_affected");
    }

    /// <summary>CAS-050 §7 — <c>dbo.quarantine_entry_del_browse_folder</c> (hard delete, all origins); <c>null</c> when the folder holds no active files.</summary>
    public async Task<int?> HardDeleteBrowseFolderAsync(string source, string bucketName, string folderPath, CancellationToken ct)
    {
        var parameters = new DynamicParameters();
        parameters.Add("@source", source);
        parameters.Add("@bucket_name", bucketName);
        parameters.Add("@folder_path", folderPath);
        parameters.Add("@rows_affected", dbType: DbType.Int32, direction: ParameterDirection.Output);

        await using var connection = new SqlConnection(_connectionString);

        try
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "dbo.quarantine_entry_del_browse_folder",
                parameters,
                commandType: CommandType.StoredProcedure,
                cancellationToken: ct));
        }
        catch (SqlException ex) when (ex.Message.Contains(FolderEmptyMessage, StringComparison.Ordinal))
        {
            return null;
        }

        return parameters.Get<int>("@rows_affected");
    }

    /// <summary>CAS-050 §7 — <c>dbo.quarantine_entry_browse_state_lst</c>: result set 1 = quarantined direct-child file ids,
    /// result set 2 = per direct-child folder counts.</summary>
    public async Task<QuarantineBrowseStateResponse> GetBrowseStateAsync(
        string source, string bucketName, string folderPath, CancellationToken ct)
    {
        var parameters = new DynamicParameters();
        parameters.Add("@source", source);
        parameters.Add("@bucket_name", bucketName);
        parameters.Add("@folder_path", folderPath);

        await using var connection = new SqlConnection(_connectionString);

        await using var grid = await connection.QueryMultipleAsync(new CommandDefinition(
            "dbo.quarantine_entry_browse_state_lst",
            parameters,
            commandType: CommandType.StoredProcedure,
            cancellationToken: ct));

        var fileIds = (await grid.ReadAsync<int>()).ToList();
        var folders = (await grid.ReadAsync<StateFolderRow>())
            .Select(row => new QuarantineBrowseStateFolder
            {
                FolderName = row.folder_name,
                TotalFiles = row.total_files,
                QuarantinedFiles = row.quarantined_files
            })
            .ToList();

        return new QuarantineBrowseStateResponse { FileIndexIds = fileIds, Folders = folders };
    }

    private const string ActiveStatus = "Active";

    /// <summary>
    /// CAS-008 §6.4/§7 V3 per-file resolution for Permanent Delete, via <c>dbo.quarantine_entry_get</c>.
    /// The procedure returns 0 or 1 row for the given id with no status filter applied in SQL (by
    /// design — see the procedure's own header comment) so "unknown id" and "found but not Active"
    /// stay distinguishable in principle; this repository applies the <c>Active</c> filter itself
    /// and returns <c>null</c> for both the no-row and the not-Active case, since both map to the
    /// same per-file <c>file-not-found</c> outcome at the service layer (§7 V3).
    /// </summary>
    public async Task<QuarantineEntryForDeleteRow?> GetActiveForDeleteAsync(int quarantineEntryId, CancellationToken ct)
    {
        var parameters = new DynamicParameters();
        parameters.Add("@quarantine_entry_id", quarantineEntryId);

        await using var connection = new SqlConnection(_connectionString);

        var row = await connection.QuerySingleOrDefaultAsync<GetForDeleteRow>(new CommandDefinition(
            "dbo.quarantine_entry_get",
            parameters,
            commandType: CommandType.StoredProcedure,
            cancellationToken: ct));

        if (row is null || !string.Equals(row.status, ActiveStatus, StringComparison.Ordinal))
        {
            return null;
        }

        return new QuarantineEntryForDeleteRow
        {
            QuarantineEntryId = row.quarantine_entry_id,
            FileIndexId = row.file_index_id,
            Name = row.name,
            Path = row.path
        };
    }

    /// <summary>CAS-008 §5/§6.4 — backs the status-transition step of Permanent Delete, via
    /// <c>dbo.quarantine_entry_delete_upd</c>. Returns whether the transition actually happened
    /// (<c>@rows_affected &gt; 0</c>) — <c>false</c> means the row was not <c>Active</c> at update
    /// time.</summary>
    public async Task<bool> TryTransitionToDeletedAsync(int quarantineEntryId, string updatedBy, CancellationToken ct)
    {
        var parameters = new DynamicParameters();
        parameters.Add("@quarantine_entry_id", quarantineEntryId);
        parameters.Add("@updated_by", updatedBy);
        parameters.Add("@rows_affected", dbType: DbType.Int32, direction: ParameterDirection.Output);

        await using var connection = new SqlConnection(_connectionString);

        await connection.ExecuteAsync(new CommandDefinition(
            "dbo.quarantine_entry_delete_upd",
            parameters,
            commandType: CommandType.StoredProcedure,
            cancellationToken: ct));

        return parameters.Get<int>("@rows_affected") > 0;
    }

    /// <summary>CAS-014 §5/§7 — backs <c>POST /quarantine/remove</c>'s per-file hard-delete step, via
    /// <c>dbo.quarantine_entry_del</c>. Returns whether a row was actually deleted
    /// (<c>@rows_affected &gt; 0</c>) — <c>false</c> means the id was unknown or not <c>Active</c>.</summary>
    public async Task<bool> HardDeleteActiveAsync(int quarantineEntryId, CancellationToken ct)
    {
        var parameters = new DynamicParameters();
        parameters.Add("@quarantine_entry_id", quarantineEntryId);
        parameters.Add("@rows_affected", dbType: DbType.Int32, direction: ParameterDirection.Output);

        await using var connection = new SqlConnection(_connectionString);

        await connection.ExecuteAsync(new CommandDefinition(
            "dbo.quarantine_entry_del",
            parameters,
            commandType: CommandType.StoredProcedure,
            cancellationToken: ct));

        return parameters.Get<int>("@rows_affected") > 0;
    }

    private sealed record ListRow(
        int quarantine_entry_id,
        string name,
        string path,
        DateTime quarantined_on,
        string status,
        int file_index_id,
        string source_tab);

    private sealed record StateFolderRow(
        string folder_name,
        int total_files,
        int quarantined_files);

    private sealed record GetForDeleteRow(
        int quarantine_entry_id,
        int file_index_id,
        string name,
        string path,
        string status);
}
