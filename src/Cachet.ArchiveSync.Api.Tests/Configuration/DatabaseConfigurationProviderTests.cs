using Cachet.ArchiveSync.Core.Configuration;
using Cachet.ArchiveSync.Core.Dtos;
using FluentAssertions;
using Microsoft.Extensions.Configuration;

namespace Cachet.ArchiveSync.Api.Tests.Configuration;

/// <summary>
/// Unit tests for <see cref="DatabaseConfigurationSource"/>/<see cref="DatabaseConfigurationProvider"/>
/// (CAS-041 §5/§13 "DatabaseConfigurationProviderTests"). The SQL call is replaced by a fake
/// <see cref="DatabaseConfigurationSource.LoadGroup"/> delegate, so no database is touched; the
/// Dapper loaders themselves are covered by manual verification (§13/§16). All values below are
/// dummy strings — no real B2 credential appears in this fixture (FR-03).
/// </summary>
public sealed class DatabaseConfigurationProviderTests
{
    private const string ConnectionString = "Server=fake;Database=fake;";

    // Mirrors the Web host's allow-list in src/Cachet.ArchiveSync.Web/Program.cs (CAS-041 §2 item 2).
    // Api.Tests does not reference the Web project, so the dictionary is restated here verbatim.
    private static Dictionary<string, IReadOnlyCollection<string>> WebAllowList() => new()
    {
        ["Api_Url"] = new[] { "Api__BaseUrl" },
        ["B2_Keys"] = new[] { "B2__BucketName" }
    };

    private static Dictionary<string, IReadOnlyCollection<string>> ApiAllowList() => new()
    {
        ["B2_Keys"] = new[] { "B2__SystemKeyId", "B2__SystemApplicationKey", "B2__BucketName" }
    };

    private static ConfigurationDetailRow Row(string name, string? value) =>
        new() { ConfigurationDetailName = name, ConfigurationValue1 = value };

    private static DatabaseConfigurationProvider LoadProvider(DatabaseConfigurationSource source)
    {
        var provider = new DatabaseConfigurationProvider(source);
        provider.Load();
        return provider;
    }

    private static IConfiguration BuildWithEarlierSource(
        IDictionary<string, string?> earlier, DatabaseConfigurationSource dbSource)
    {
        var builder = new ConfigurationBuilder().AddInMemoryCollection(earlier);
        builder.Sources.Add(dbSource);
        return builder.Build();
    }

    // ---- SC-1: "B2__SystemKeyId" → "B2:SystemKeyId", value = configuration_value_1 ----------------

    [Fact]
    public void Test_SC1_DoubleUnderscoreMapsToColon()
    {
        var source = new DatabaseConfigurationSource
        {
            ConnectionString = ConnectionString,
            AllowedNamesByGroup = ApiAllowList(),
            LoadGroup = (_, _) => new[] { Row("B2__SystemKeyId", "dummy-key-id") }
        };

        var provider = LoadProvider(source);

        provider.TryGet("B2:SystemKeyId", out var value).Should().BeTrue();
        value.Should().Be("dummy-key-id");
        provider.TryGet("B2__SystemKeyId", out _).Should().BeFalse();
    }

    // ---- SC-2: DB value overrides an earlier source (env var stand-in) -----------------------------

    [Fact]
    public void Test_SC2_DatabaseValueOverridesEarlierSource()
    {
        var config = BuildWithEarlierSource(
            new Dictionary<string, string?> { ["B2:BucketName"] = "env-bucket" },
            new DatabaseConfigurationSource
            {
                ConnectionString = ConnectionString,
                AllowedNamesByGroup = ApiAllowList(),
                LoadGroup = (_, _) => new[] { Row("B2__BucketName", "db-bucket") }
            });

        config["B2:BucketName"].Should().Be("db-bucket");
    }

    // ---- SC-3: missing row leaves the earlier-source value in effect -------------------------------

    [Fact]
    public void Test_SC3_MissingRowFallsBackToEarlierSource()
    {
        var config = BuildWithEarlierSource(
            new Dictionary<string, string?> { ["B2:BucketName"] = "env-bucket" },
            new DatabaseConfigurationSource
            {
                ConnectionString = ConnectionString,
                AllowedNamesByGroup = ApiAllowList(),
                LoadGroup = (_, _) => Array.Empty<ConfigurationDetailRow>()
            });

        config["B2:BucketName"].Should().Be("env-bucket");
    }

    // ---- SC-4: Web allow-list never yields the B2 system key, even if the group returns it ---------

    [Fact]
    public void Test_SC4_WebAllowListExcludesSystemKey()
    {
        var source = new DatabaseConfigurationSource
        {
            ConnectionString = ConnectionString,
            AllowedNamesByGroup = WebAllowList(),
            LoadGroup = (_, group) => group switch
            {
                "B2_Keys" => new[]
                {
                    Row("B2__SystemKeyId", "dummy-key-id"),
                    Row("B2__SystemApplicationKey", "dummy-app-key"),
                    Row("B2__BucketName", "db-bucket")
                },
                "Api_Url" => new[] { Row("Api__BaseUrl", "https://api.example.test/") },
                _ => Array.Empty<ConfigurationDetailRow>()
            }
        };

        var provider = LoadProvider(source);

        provider.TryGet("B2:SystemKeyId", out _).Should().BeFalse();
        provider.TryGet("B2:SystemApplicationKey", out _).Should().BeFalse();
        provider.TryGet("B2:BucketName", out var bucket).Should().BeTrue();
        bucket.Should().Be("db-bucket");
        provider.TryGet("Api:BaseUrl", out var baseUrl).Should().BeTrue();
        baseUrl.Should().Be("https://api.example.test/");
    }

    // ---- SC-5: Enabled=false or blank connection string → loader never called ----------------------

    [Theory]
    [InlineData(false, ConnectionString)]
    [InlineData(true, null)]
    [InlineData(true, "")]
    [InlineData(true, "   ")]
    public void Test_SC5_DisabledOrNoConnectionStringSkipsLoad(bool enabled, string? connectionString)
    {
        var calls = 0;
        var source = new DatabaseConfigurationSource
        {
            Enabled = enabled,
            ConnectionString = connectionString,
            AllowedNamesByGroup = ApiAllowList(),
            LoadGroup = (_, _) =>
            {
                calls++;
                return new[] { Row("B2__BucketName", "db-bucket") };
            }
        };

        var provider = LoadProvider(source);

        calls.Should().Be(0);
        provider.TryGet("B2:BucketName", out _).Should().BeFalse();
    }

    // ---- V1: only allow-listed names in allow-listed groups become keys ----------------------------

    [Fact]
    public void Test_V1_OnlyAllowListedNamesAreLoaded()
    {
        var requestedGroups = new List<string>();
        var source = new DatabaseConfigurationSource
        {
            ConnectionString = ConnectionString,
            AllowedNamesByGroup = new Dictionary<string, IReadOnlyCollection<string>>
            {
                ["B2_Keys"] = new[] { "B2__BucketName" }
            },
            LoadGroup = (_, group) =>
            {
                requestedGroups.Add(group);
                return new[]
                {
                    Row("B2__BucketName", "db-bucket"),
                    Row("Unlisted__Setting", "should-not-load")
                };
            }
        };

        var provider = LoadProvider(source);

        requestedGroups.Should().Equal("B2_Keys");
        provider.TryGet("B2:BucketName", out _).Should().BeTrue();
        provider.TryGet("Unlisted:Setting", out _).Should().BeFalse();
        provider.GetChildKeys(Enumerable.Empty<string>(), null)
            .Should().OnlyContain(k => k == "B2");
    }

    // ---- V2: NULL/whitespace value not added; earlier source value remains -------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Test_V2_BlankValueIsSkipped(string? blankValue)
    {
        var dbSource = new DatabaseConfigurationSource
        {
            ConnectionString = ConnectionString,
            AllowedNamesByGroup = ApiAllowList(),
            LoadGroup = (_, _) => new[] { Row("B2__BucketName", blankValue) }
        };

        LoadProvider(dbSource).TryGet("B2:BucketName", out _).Should().BeFalse();

        var config = BuildWithEarlierSource(
            new Dictionary<string, string?> { ["B2:BucketName"] = "env-bucket" }, dbSource);
        config["B2:BucketName"].Should().Be("env-bucket");
    }

    // ---- V3: loader failure → InvalidOperationException naming the group, no values ---------------

    [Fact]
    public void Test_V3_LoadFailureThrowsWithoutValues()
    {
        const string secretLooking = "K005-dummy-secret-looking-value";
        var inner = new InvalidOperationException("simulated SQL failure");
        var source = new DatabaseConfigurationSource
        {
            ConnectionString = ConnectionString,
            AllowedNamesByGroup = new Dictionary<string, IReadOnlyCollection<string>>
            {
                ["B2_Keys"] = new[] { "B2__SystemApplicationKey" },
                ["Api_Url"] = new[] { "Api__BaseUrl" }
            },
            LoadGroup = (_, group) => group == "B2_Keys"
                ? new[] { Row("B2__SystemApplicationKey", secretLooking) }
                : throw inner
        };

        var act = () => new DatabaseConfigurationProvider(source).Load();

        var thrown = act.Should().Throw<InvalidOperationException>().Which;
        thrown.Message.Should().Contain("Api_Url");
        thrown.Message.Should().NotContain(secretLooking);
        thrown.Message.Should().NotContain(ConnectionString);
        thrown.InnerException.Should().BeSameAs(inner);
    }
}
