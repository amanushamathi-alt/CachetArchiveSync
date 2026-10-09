using Cachet.ArchiveSync.Api.Repositories;
using Dapper;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;

namespace Cachet.ArchiveSync.Api.IntegrationTests;

/// <summary>
/// <c>[Fact]</c> that runs only when the <c>ConnectionStrings:CachetArchiveSync</c> database is
/// reachable AND has <c>dbo.file_index_browse_lst</c> deployed (migration
/// <c>20261004_120000__CAS-043-files-browse-api.sql</c>); otherwise it is reported as Skipped with
/// the reason - never as a false pass. Same connection-string sources as the repo's other real-DB
/// tests (<c>appsettings.IntegrationTests.json</c> or <c>ConnectionStrings__CachetArchiveSync</c>).
/// </summary>
public sealed class BrowseSqlFactAttribute : FactAttribute
{
    public BrowseSqlFactAttribute()
    {
        Skip = BrowseSqlSupport.SkipReasonOrNull(
            "SELECT OBJECT_ID(N'dbo.file_index_browse_lst')",
            "dbo.file_index_browse_lst is not deployed on the target database (apply 20261004_120000__CAS-043-files-browse-api.sql).");
    }
}

/// <summary>
/// Same skip-when-unavailable behavior for the <c>B2PathPrefixProvider</c> tests: needs
/// <c>dbo.odrive_bucket_mapping.b2_path_prefix</c> (CAS-031 migration) and <c>dbo.odrive_bucket_mapping_get</c>.
/// </summary>
public sealed class PrefixSqlFactAttribute : FactAttribute
{
    public PrefixSqlFactAttribute()
    {
        Skip = BrowseSqlSupport.SkipReasonOrNull(
            "SELECT CASE WHEN COL_LENGTH(N'dbo.odrive_bucket_mapping', N'b2_path_prefix') IS NOT NULL THEN OBJECT_ID(N'dbo.odrive_bucket_mapping_get') END",
            "dbo.odrive_bucket_mapping.b2_path_prefix / dbo.odrive_bucket_mapping_get not deployed (apply 20260924_140000__CAS-031-index-refresh-seed.sql).");
    }
}

internal static class BrowseSqlSupport
{
    public static string ConnectionString { get; } = new ConfigurationBuilder()
        .AddJsonFile("appsettings.IntegrationTests.json", optional: true)
        .AddEnvironmentVariables()
        .Build()
        .GetConnectionString("CachetArchiveSync") ?? string.Empty;

    /// <summary>Null when <paramref name="probeSql"/> returns a non-null object id; otherwise the skip reason.</summary>
    public static string? SkipReasonOrNull(string probeSql, string notDeployedReason)
    {
        if (string.IsNullOrWhiteSpace(ConnectionString))
        {
            return "No ConnectionStrings:CachetArchiveSync configured; run against a real instance with the CAS-043 migration applied.";
        }

        try
        {
            using var connection = new SqlConnection(ConnectionString);
            connection.Open();
            return connection.ExecuteScalar<int?>(probeSql) is null ? notDeployedReason : null;
        }
        catch (Exception ex)
        {
            return "SQL Server not reachable for the CAS-043 repository tests: " + ex.GetType().Name;
        }
    }
}

/// <summary>
/// CAS-043 §13 - real SQL Server tests of <c>dbo.file_index_browse_lst</c> through
/// <see cref="FileBrowseRepository"/>. Each test class instance seeds rows under its own unique
/// bucket name and deletes them on dispose, so no other data in the database is read or touched.
/// </summary>
[Collection("CAS-047 file_index real-DB")]
public sealed class FilesBrowseRepositoryTests : IAsyncLifetime
{
    private readonly string _bucket = "cas043-" + Guid.NewGuid().ToString("N")[..12];
    private readonly string _otherBucket = "cas043-other-" + Guid.NewGuid().ToString("N")[..12];
    private int _nextTick;

    private FileBrowseRepository NewRepository() =>
        new(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:CachetArchiveSync"] = BrowseSqlSupport.ConnectionString })
            .Build());

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (string.IsNullOrWhiteSpace(BrowseSqlSupport.ConnectionString))
        {
            return;
        }

        try
        {
            await using var connection = new SqlConnection(BrowseSqlSupport.ConnectionString);
            await connection.ExecuteAsync(
                "DELETE FROM dbo.file_index WHERE bucket_name IN (@a, @b) AND created_by = N'cas043-test'",
                new { a = _bucket, b = _otherBucket });
        }
        catch (SqlException)
        {
            // Skipped runs never reached the database; nothing to clean.
        }
    }

    private async Task SeedAsync(
        string path, DateTime? modifiedAt = null, bool active = true, string source = "B2", string? bucket = null, long size = 10)
    {
        var name = path[(path.LastIndexOf('/') + 1)..];
        var dot = name.LastIndexOf('.');
        var extension = dot >= 0 ? name[(dot + 1)..].ToUpperInvariant() : null;
        await using var connection = new SqlConnection(BrowseSqlSupport.ConnectionString);
        await connection.ExecuteAsync(
            @"INSERT INTO dbo.file_index
                (created_by, updated_by, source, bucket_name, relative_path, file_name, file_extension,
                 file_size_bytes, modified_at, last_seen_at, is_active)
              VALUES (N'cas043-test', N'cas043-test', @source, @bucket, @path, @name, @ext,
                      @size, @modified, SYSUTCDATETIME(), @active)",
            new
            {
                source,
                bucket = bucket ?? _bucket,
                path,
                name,
                ext = extension,
                size,
                modified = modifiedAt ?? new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(_nextTick++),
                active
            });
    }

    // SC-1
    [BrowseSqlFact]
    public async Task Test_SC1_RootListsDistinctTopLevelFoldersAndRootFiles()
    {
        await SeedAsync("root-file.txt");
        await SeedAsync("Top1/a.pdf");
        await SeedAsync("Top1/b.pdf");
        await SeedAsync("Top1/sub/c.pdf");
        await SeedAsync("Top2/d.pdf");

        var page = await NewRepository().BrowseAsync("B2", _bucket, string.Empty, false, CancellationToken.None);

        page.Folders.Select(f => f.FolderName).Should().Equal("Top1", "Top2");
        page.Files.Select(f => f.FileName).Should().Equal("root-file.txt");
        page.TotalFolders.Should().Be(2);
        page.TotalFiles.Should().Be(1);
    }

    private static Dictionary<string, bool> HasSubfolders(Cachet.ArchiveSync.Core.Dtos.FileBrowsePage page) =>
        page.Folders.ToDictionary(f => f.FolderName, f => f.HasSubfolders, StringComparer.OrdinalIgnoreCase);

    // CAS-046 SC-1
    [BrowseSqlFact]
    public async Task Test_SC1_HasSubfoldersTrueOnlyWhenADeeperFolderExists()
    {
        await SeedAsync("Leaf/a.txt");
        await SeedAsync("Leaf/b.txt");
        await SeedAsync("Branch/a.txt");
        await SeedAsync("Branch/sub/c.txt");
        await SeedAsync("Branch/sub2/x/y.txt");
        await SeedAsync("Deep/a/b/c.txt");
        var repository = NewRepository();

        var root = await repository.BrowseAsync("B2", _bucket, string.Empty, false, CancellationToken.None);
        var branch = await repository.BrowseAsync("B2", _bucket, "Branch", false, CancellationToken.None);
        var foldersOnly = await repository.BrowseAsync("B2", _bucket, string.Empty, true, CancellationToken.None);

        HasSubfolders(root).Should().Equal(new Dictionary<string, bool>
        {
            ["Branch"] = true,
            ["Deep"] = true,
            ["Leaf"] = false
        });
        HasSubfolders(branch).Should().Equal(new Dictionary<string, bool>
        {
            ["sub"] = false,
            ["sub2"] = true
        });
        HasSubfolders(foldersOnly).Should().Equal(HasSubfolders(root));
    }

    // CAS-046 SC-2
    [BrowseSqlFact]
    public async Task Test_SC2_HasSubfoldersIgnoresInactiveOtherSourceAndEmptySegments()
    {
        await SeedAsync("Control/x/f.txt");                       // real subfolder -> true
        await SeedAsync("Inact/direct.txt");
        await SeedAsync("Inact/x/f.txt", active: false);          // only deeper row is inactive
        await SeedAsync("Odr/direct.txt");
        await SeedAsync("Odr/x/f.txt", source: "ODrive");         // only deeper row is another source
        await SeedAsync("OtherB/direct.txt");
        await SeedAsync("OtherB/x/f.txt", bucket: _otherBucket);  // only deeper row is another bucket
        await SeedAsync("Empty/direct.txt");
        await SeedAsync("Empty//x.txt");                          // empty segment is not a subfolder
        var repository = NewRepository();

        var page = await repository.BrowseAsync("B2", _bucket, string.Empty, false, CancellationToken.None);

        HasSubfolders(page).Should().Equal(new Dictionary<string, bool>
        {
            ["Control"] = true,
            ["Inact"] = false,
            ["Odr"] = false,
            ["OtherB"] = false,
            ["Empty"] = false
        });
    }

    // CAS-046 SC-3
    [BrowseSqlFact]
    public async Task Test_SC3_HasSubfoldersMergesAcrossCaseVariants()
    {
        await SeedAsync("Adrian/a.txt");
        await SeedAsync("ADRIAN/sub/b.txt");
        await SeedAsync("ZED/sub/b.txt");
        await SeedAsync("Zed/a.txt");
        await SeedAsync("Only/a.txt");
        await SeedAsync("ONLY/b.txt");         // both spellings leaf -> false

        var page = await NewRepository().BrowseAsync("B2", _bucket, string.Empty, false, CancellationToken.None);

        page.Folders.Should().HaveCount(3, "case variants merge into one folder");
        HasSubfolders(page).Should().Equal(new Dictionary<string, bool>
        {
            ["Adrian"] = true,
            ["Zed"] = true,
            ["Only"] = false
        });
    }

    // SC-2
    [BrowseSqlFact]
    public async Task Test_SC2_FolderListsOnlyDirectChildren()
    {
        await SeedAsync("A/B/f1.txt");
        await SeedAsync("A/B/C/f2.txt");
        await SeedAsync("A/B/C/D/f3.txt");
        await SeedAsync("A/B2/sibling.txt");
        await SeedAsync("A/above.txt");

        var page = await NewRepository().BrowseAsync("B2", _bucket, "A/B", false, CancellationToken.None);

        page.Folders.Select(f => f.FolderName).Should().Equal("C");
        page.Files.Select(f => f.FileName).Should().Equal("f1.txt");
        page.TotalFolders.Should().Be(1);
        page.TotalFiles.Should().Be(1);
    }

    // SC-3
    [BrowseSqlFact]
    public async Task Test_SC3_InactiveOtherSourceAndOtherBucketRowsExcluded()
    {
        const string prefix = "D/Shares/Operations";
        await SeedAsync(prefix + "/active.txt");
        await SeedAsync(prefix + "/ActiveFolder/in.txt");
        await SeedAsync(prefix + "/inactive.txt", active: false);
        await SeedAsync(prefix + "/InactiveFolder/in.txt", active: false);
        await SeedAsync(prefix + "/odrive.txt", source: "ODrive");
        await SeedAsync(prefix + "/ODriveFolder/in.txt", source: "ODrive");
        await SeedAsync(prefix + "/otherbucket.txt", bucket: _otherBucket);
        await SeedAsync(prefix + "/OtherBucketFolder/in.txt", bucket: _otherBucket);
        await SeedAsync("Elsewhere/outside-prefix.txt");   // same bucket, outside the prefix

        var page = await NewRepository().BrowseAsync("B2", _bucket, prefix, false, CancellationToken.None);

        page.Folders.Select(f => f.FolderName).Should().Equal("ActiveFolder");
        page.Files.Select(f => f.FileName).Should().Equal("active.txt");
        page.TotalFolders.Should().Be(1);
        page.TotalFiles.Should().Be(1);
    }

    // SC-4
    [BrowseSqlFact]
    public async Task Test_SC4_FolderLatestModifiedIsMaxOfDescendants()
    {
        var newest = new DateTime(2026, 9, 14, 15, 2, 11, DateTimeKind.Utc);
        await SeedAsync("F/old.txt", new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        await SeedAsync("F/deep/er/newest.txt", newest);
        await SeedAsync("F/mid.txt", new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc));
        await SeedAsync("F/inactive-newer.txt", new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc), active: false);

        var page = await NewRepository().BrowseAsync("B2", _bucket, string.Empty, false, CancellationToken.None);

        var folder = page.Folders.Should().ContainSingle().Subject;
        folder.FolderName.Should().Be("F");
        folder.LatestModifiedAt.Should().Be(new DateTimeOffset(newest));
    }

    // SC-5
    [BrowseSqlFact]
    public async Task Test_SC5_ListsAreSortedAndNothingIsTruncated()
    {
        foreach (var folder in new[] { "echo", "alpha", "delta", "bravo", "charlie" })
        {
            await SeedAsync($"{folder}/x.txt");
        }

        // 2,500 direct files in one round trip (zero-padded names, so ordinal order is the sort order).
        await using (var connection = new SqlConnection(BrowseSqlSupport.ConnectionString))
        {
            await connection.ExecuteAsync(
                @"INSERT INTO dbo.file_index
                    (created_by, updated_by, source, bucket_name, relative_path, file_name, file_extension,
                     file_size_bytes, modified_at, last_seen_at, is_active)
                  SELECT N'cas043-test', N'cas043-test', N'B2', @bucket,
                         N'big/f' + RIGHT(N'0000' + CAST(n AS NVARCHAR(10)), 4) + N'.txt',
                         N'f' + RIGHT(N'0000' + CAST(n AS NVARCHAR(10)), 4) + N'.txt', N'TXT',
                         10, SYSUTCDATETIME(), SYSUTCDATETIME(), 1
                  FROM (SELECT TOP (2500) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS n
                        FROM sys.all_objects a CROSS JOIN sys.all_objects b) t",
                new { bucket = _bucket });
        }

        var repository = NewRepository();
        var root = await repository.BrowseAsync("B2", _bucket, string.Empty, false, CancellationToken.None);
        var big = await repository.BrowseAsync("B2", _bucket, "big", false, CancellationToken.None);

        root.Folders.Select(f => f.FolderName).Should().Equal("alpha", "big", "bravo", "charlie", "delta", "echo");
        root.TotalFolders.Should().Be(root.Folders.Count);
        root.TotalFiles.Should().Be(root.Files.Count);

        big.Files.Should().HaveCount(2500, "nothing is truncated");
        big.TotalFiles.Should().Be(2500);
        big.Folders.Should().BeEmpty();
        big.TotalFolders.Should().Be(0);
        big.Files.Select(f => f.FileName).Should().BeInAscendingOrder(StringComparer.Ordinal)
            .And.OnlyHaveUniqueItems();
    }

    // Empty path segments never produce an empty folder name (a key with a double slash right after the prefix).
    [BrowseSqlFact]
    public async Task Test_SC5_EmptyFolderNamesAreExcludedAndTotalsMatch()
    {
        await SeedAsync("Top/a.txt");
        await SeedAsync("/leading-slash.txt");
        await SeedAsync("Top//double.txt");

        var root = await NewRepository().BrowseAsync("B2", _bucket, string.Empty, false, CancellationToken.None);

        root.Folders.Select(f => f.FolderName).Should().Equal("Top");
        root.TotalFolders.Should().Be(1);
        root.TotalFiles.Should().Be(root.Files.Count);
    }

    // SC-6
    [BrowseSqlFact]
    public async Task Test_SC6_FoldersOnlyOmitsFiles()
    {
        await SeedAsync("Sub1/a.txt");
        await SeedAsync("Sub2/b.txt");
        await SeedAsync("loose1.txt");
        await SeedAsync("loose2.txt");

        var page = await NewRepository().BrowseAsync("B2", _bucket, string.Empty, true, CancellationToken.None);

        page.Folders.Select(f => f.FolderName).Should().Equal("Sub1", "Sub2");
        page.TotalFolders.Should().Be(2);
        page.Files.Should().BeEmpty();
        page.TotalFiles.Should().Be(0);
    }

    // V7
    [BrowseSqlFact]
    public async Task Test_V7_WildcardCharactersInFolderAreLiteral()
    {
        await SeedAsync("100%_done/in.txt");
        await SeedAsync("100Xadone/decoy.txt");
        await SeedAsync("a[b]/c.txt");
        await SeedAsync("ab/decoy2.txt");
        await SeedAsync("u_s/d.txt");
        await SeedAsync("uxs/decoy3.txt");

        var repository = NewRepository();
        var percentUnderscore = await repository.BrowseAsync("B2", _bucket, "100%_done", false, CancellationToken.None);
        var bracket = await repository.BrowseAsync("B2", _bucket, "a[b]", false, CancellationToken.None);
        var underscore = await repository.BrowseAsync("B2", _bucket, "u_s", false, CancellationToken.None);
        var wildcardOnly = await repository.BrowseAsync("B2", _bucket, "1%", false, CancellationToken.None);

        percentUnderscore.Files.Select(f => f.FileName).Should().Equal("in.txt");
        bracket.Files.Select(f => f.FileName).Should().Equal("c.txt");
        underscore.Files.Select(f => f.FileName).Should().Equal("d.txt");
        wildcardOnly.TotalFolders.Should().Be(0, "'%' must not act as a wildcard");
        wildcardOnly.TotalFiles.Should().Be(0);
    }
}

/// <summary>
/// CAS-043 coverage of <see cref="B2PathPrefixProvider"/> (not a map item): the normalization of
/// <c>dbo.odrive_bucket_mapping.b2_path_prefix</c> that <c>FileBrowseService</c> depends on for
/// SC-12 and V6. Seeds under unique bucket names and removes them on dispose.
/// </summary>
public sealed class B2PathPrefixProviderTests : IAsyncLifetime
{
    private readonly List<string> _buckets = new();

    private B2PathPrefixProvider NewProvider() =>
        new(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:CachetArchiveSync"] = BrowseSqlSupport.ConnectionString })
            .Build());

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (_buckets.Count == 0)
        {
            return;
        }

        await using var connection = new SqlConnection(BrowseSqlSupport.ConnectionString);
        await connection.ExecuteAsync(
            "DELETE FROM dbo.odrive_bucket_mapping WHERE bucket_name IN @buckets AND created_by = N'cas043-test'",
            new { buckets = _buckets });
    }

    private async Task<string> SeedAsync(string? prefix, bool active = true)
    {
        var bucket = "cas043-map-" + Guid.NewGuid().ToString("N")[..12];
        _buckets.Add(bucket);
        await using var connection = new SqlConnection(BrowseSqlSupport.ConnectionString);
        await connection.ExecuteAsync(
            @"INSERT INTO dbo.odrive_bucket_mapping
                (created_by, updated_by, bucket_name, odrive_root_path, b2_mirror_root_path, is_active, b2_path_prefix)
              VALUES (N'cas043-test', N'cas043-test', @bucket, N'X:\odrive', N'X:\b2', @active, @prefix)",
            new { bucket, active, prefix });
        return bucket;
    }

    [PrefixSqlFact]
    public async Task Test_Provider_NormalizesPrefixAndSignalsMissingMapping()
    {
        var plain = await SeedAsync("D/Shares/Operations");
        var messy = await SeedAsync(@"\D\Shares/Operations/ ");
        var inactive = await SeedAsync("D/Shares/Operations", active: false);
        var provider = NewProvider();

        (await provider.GetPrefixAsync(plain, CancellationToken.None)).Should().Be("D/Shares/Operations");
        (await provider.GetPrefixAsync(messy, CancellationToken.None)).Should().Be("D/Shares/Operations");
        (await provider.GetPrefixAsync(inactive, CancellationToken.None)).Should().BeNull("an inactive mapping row is no mapping");
        (await provider.GetPrefixAsync("cas043-no-such-bucket", CancellationToken.None)).Should().BeNull();
    }
}
