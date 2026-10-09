using Cachet.ArchiveSync.Core.Dtos;
using Cachet.ArchiveSync.Core.Interfaces;
using Dapper;
using Microsoft.Data.SqlClient;

namespace Cachet.ArchiveSync.Api.Repositories;

/// <summary>
/// Dapper implementation of <see cref="IFileIndexRepository"/>. See
/// <see cref="FileIndexCandidateRow"/>'s XML doc: <c>dbo.file_index_search</c> and
/// <c>dbo.file_index_soft_delete_upd</c> have not yet been authored by db-author (BLOCKED,
/// flagged in the implementer's report). Written against their expected shape so the Worker's
/// task-processor logic compiles and is unit-testable via a mocked repository; it will fail at
/// runtime against a real database until db-author adds these procedures.
/// </summary>
public sealed class FileIndexRepository : IFileIndexRepository
{
    private readonly string _connectionString;

    public FileIndexRepository(IConfiguration configuration)
    {
        _connectionString = configuration.GetConnectionString("CachetArchiveSync")
            ?? throw new InvalidOperationException(
                "Configuration key 'ConnectionStrings:CachetArchiveSync' must be set.");
    }

    public async Task<IReadOnlyList<FileIndexCandidateRow>> SearchCandidatesAsync(
        string bucketName, string fileExtension, string term, DateOnly? createdFrom, DateOnly? createdTo, CancellationToken ct,
        string? source = null)
    {
        var parameters = new DynamicParameters();
        parameters.Add("@bucket_name", bucketName);
        parameters.Add("@source", source); // CAS-047 §7 (null = all sources)
        parameters.Add("@file_extension", fileExtension);
        parameters.Add("@term", term);
        // CAS-038 §7/§11 — typed DATE parameters (null = no date predicate).
        parameters.Add("@created_from", createdFrom?.ToDateTime(TimeOnly.MinValue), System.Data.DbType.Date);
        parameters.Add("@created_to", createdTo?.ToDateTime(TimeOnly.MinValue), System.Data.DbType.Date);

        await using var connection = new SqlConnection(_connectionString);

        var rows = await connection.QueryAsync<Row>(new CommandDefinition(
            "dbo.file_index_search",
            parameters,
            commandType: System.Data.CommandType.StoredProcedure,
            cancellationToken: ct));

        return rows.Select(row => new FileIndexCandidateRow
        {
            FileIndexId = row.file_index_id,
            FileName = row.file_name,
            RelativePath = row.relative_path,
            FileSizeBytes = row.file_size_bytes,
            ModifiedAt = SqlDateTimeMapping.ToUtcOffset(row.modified_at),
            MatchType = row.match_type,
            MatchSnippet = row.match_snippet,
            Source = row.source,
            BucketName = row.bucket_name,
            Sha1Hash = row.sha1_hash
        }).ToList();
    }

    public async Task SoftDeleteAsync(int fileIndexId, string updatedBy, CancellationToken ct)
    {
        var parameters = new DynamicParameters();
        parameters.Add("@file_index_id", fileIndexId);
        parameters.Add("@updated_by", updatedBy);

        await using var connection = new SqlConnection(_connectionString);

        await connection.ExecuteAsync(new CommandDefinition(
            "dbo.file_index_soft_delete_upd",
            parameters,
            commandType: System.Data.CommandType.StoredProcedure,
            cancellationToken: ct));
    }

    /// <summary>
    /// CAS-007 §5 Comparison Algorithm step 1/step 5 — fetches every page of
    /// <c>dbo.file_index_lst</c> for <paramref name="source"/> and accumulates them (§5 step 5's
    /// "one paginated query per source, not one row at a time"). See this interface method's XML
    /// doc — <c>dbo.file_index_lst</c> has not been authored (BLOCKED, flagged in the
    /// implementer's report).
    /// </summary>
    public async Task<IReadOnlyList<FileIndexRow>> ListActiveBySourceAsync(string source, CancellationToken ct)
    {
        const int pageSize = 500;
        var page = 1;
        var results = new List<FileIndexRow>();

        while (true)
        {
            var parameters = new DynamicParameters();
            parameters.Add("@source", source);
            parameters.Add("@page", page);
            parameters.Add("@pageSize", pageSize);
            parameters.Add("@totalCount", dbType: System.Data.DbType.Int32, direction: System.Data.ParameterDirection.Output);

            await using var connection = new SqlConnection(_connectionString);

            var rows = (await connection.QueryAsync<ListRow>(new CommandDefinition(
                "dbo.file_index_lst",
                parameters,
                commandType: System.Data.CommandType.StoredProcedure,
                cancellationToken: ct))).ToList();

            results.AddRange(rows.Select(row => new FileIndexRow
            {
                FileIndexId = row.file_index_id,
                Source = row.source,
                BucketName = row.bucket_name,
                RelativePath = row.relative_path,
                FileName = row.file_name,
                FileExtension = row.file_extension,
                FileSizeBytes = row.file_size_bytes,
                ModifiedAt = SqlDateTimeMapping.ToUtcOffset(row.modified_at),
                Sha1Hash = row.sha1_hash
            }));

            if (rows.Count < pageSize)
            {
                break;
            }

            page++;
        }

        return results;
    }

    public async Task<int> InsertAsync(
        string createdBy,
        string updatedBy,
        string source,
        string? bucketName,
        string relativePath,
        string fileName,
        string? fileExtension,
        long fileSizeBytes,
        DateTimeOffset modifiedAt,
        string? sha1Hash,
        DateTimeOffset? lastSeenAt,
        CancellationToken ct)
    {
        var parameters = new DynamicParameters();
        parameters.Add("@created_by", createdBy);
        parameters.Add("@updated_by", updatedBy);
        parameters.Add("@source", source);
        parameters.Add("@bucket_name", bucketName);
        parameters.Add("@relative_path", relativePath);
        parameters.Add("@file_name", fileName);
        parameters.Add("@file_extension", fileExtension);
        parameters.Add("@file_size_bytes", fileSizeBytes);
        parameters.Add("@modified_at", modifiedAt.UtcDateTime);
        parameters.Add("@sha1_hash", sha1Hash);
        parameters.Add("@last_seen_at", lastSeenAt?.UtcDateTime);
        parameters.Add("@file_index_id", dbType: System.Data.DbType.Int32, direction: System.Data.ParameterDirection.Output);

        await using var connection = new SqlConnection(_connectionString);

        await connection.ExecuteAsync(new CommandDefinition(
            "dbo.file_index_ins",
            parameters,
            commandType: System.Data.CommandType.StoredProcedure,
            cancellationToken: ct));

        return parameters.Get<int>("@file_index_id");
    }

    public async Task UpdateAsync(
        int fileIndexId,
        string updatedBy,
        string? sha1Hash,
        long? fileSizeBytes,
        DateTimeOffset? modifiedAt,
        DateTimeOffset? lastSeenAt,
        bool? isActive,
        CancellationToken ct)
    {
        var parameters = new DynamicParameters();
        parameters.Add("@file_index_id", fileIndexId);
        parameters.Add("@updated_by", updatedBy);
        parameters.Add("@sha1_hash", sha1Hash);
        parameters.Add("@file_size_bytes", fileSizeBytes);
        parameters.Add("@modified_at", modifiedAt?.UtcDateTime);
        parameters.Add("@last_seen_at", lastSeenAt?.UtcDateTime);
        parameters.Add("@is_active", isActive);

        await using var connection = new SqlConnection(_connectionString);

        await connection.ExecuteAsync(new CommandDefinition(
            "dbo.file_index_upd",
            parameters,
            commandType: System.Data.CommandType.StoredProcedure,
            cancellationToken: ct));
    }

    /// <summary>CAS-040 §5/§6 step 1 — one row via <c>dbo.file_index_get</c>, inactive rows
    /// included (zero rows = <c>null</c>).</summary>
    public async Task<FileIndexRecord?> GetAsync(int fileIndexId, CancellationToken ct)
    {
        var parameters = new DynamicParameters();
        parameters.Add("@file_index_id", fileIndexId);

        await using var connection = new SqlConnection(_connectionString);

        var row = await connection.QuerySingleOrDefaultAsync<GetRow>(new CommandDefinition(
            "dbo.file_index_get",
            parameters,
            commandType: System.Data.CommandType.StoredProcedure,
            cancellationToken: ct));

        if (row is null)
        {
            return null;
        }

        return new FileIndexRecord
        {
            FileIndexId = row.file_index_id,
            Source = row.source,
            BucketName = row.bucket_name,
            RelativePath = row.relative_path,
            FileName = row.file_name,
            FileExtension = row.file_extension,
            FileSizeBytes = row.file_size_bytes,
            ModifiedAt = SqlDateTimeMapping.ToUtcOffset(row.modified_at),
            IsActive = row.is_active
        };
    }

    // A class (not a positional record) so sha1_hash is optional: Dapper leaves it null when the
    // deployed dbo.file_index_search predates the column, so app and SP deploy in either order.
    private sealed class Row
    {
        public int file_index_id { get; set; }
        public string file_name { get; set; } = string.Empty;
        public string relative_path { get; set; } = string.Empty;
        public long file_size_bytes { get; set; }
        public DateTime modified_at { get; set; }
        public string match_type { get; set; } = string.Empty;
        public string? match_snippet { get; set; }
        public string source { get; set; } = string.Empty;
        public string bucket_name { get; set; } = string.Empty;
        public string? sha1_hash { get; set; }
    }

    private sealed record ListRow(
        int file_index_id,
        string source,
        string? bucket_name,
        string relative_path,
        string file_name,
        string? file_extension,
        long file_size_bytes,
        DateTime modified_at,
        string? sha1_hash);

    private sealed record GetRow(
        int file_index_id,
        string source,
        string? bucket_name,
        string relative_path,
        string file_name,
        string? file_extension,
        long file_size_bytes,
        DateTime modified_at,
        bool is_active);
}
