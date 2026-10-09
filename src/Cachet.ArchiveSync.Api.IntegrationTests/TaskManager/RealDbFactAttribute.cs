using Microsoft.Extensions.Configuration;

namespace Cachet.ArchiveSync.Api.IntegrationTests.TaskManager;

/// <summary>
/// CAS-030 §13 "Must actually run": a <see cref="FactAttribute"/> that runs against a real SQL
/// Server (LocalDB on the dev host) whenever <c>ConnectionStrings:CachetArchiveSync</c> is configured
/// — same <c>appsettings.IntegrationTests.json</c> / environment-variable source every other real-DB
/// suite in this project reads — and is reported as <b>Skipped</b> (never silently passed) only when
/// no connection string is configured at all. Unlike the hard-coded <c>[Fact(Skip = …)]</c> used by
/// the older suites (the CAS-028 §0 finding), nothing has to be edited to make these tests run: set
/// <c>ConnectionStrings__CachetArchiveSync</c> to a database with the migration chain through
/// <c>20260924_130000__CAS-030-task-completion-notification.sql</c> deployed. Per CAS-030 §13, a
/// skip here is still a gate failure for the spec.
/// </summary>
public sealed class RealDbFactAttribute : FactAttribute
{
    public RealDbFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(RealDbConfiguration.ConnectionString))
        {
            Skip = "CAS-030 real-DB test: ConnectionStrings:CachetArchiveSync is not configured " +
                   "(set ConnectionStrings__CachetArchiveSync to a LocalDB database with the CAS-030 " +
                   "migration deployed). A skip is a gate failure for CAS-030 (§13).";
        }
    }
}

/// <summary>Shared configuration source for <see cref="RealDbFactAttribute"/> and its test classes.</summary>
internal static class RealDbConfiguration
{
    private static readonly Lazy<string?> LazyConnectionString = new(() =>
        new ConfigurationBuilder()
            .AddJsonFile("appsettings.IntegrationTests.json", optional: true)
            .AddEnvironmentVariables()
            .Build()
            .GetConnectionString("CachetArchiveSync"));

    public static string? ConnectionString => LazyConnectionString.Value;
}
