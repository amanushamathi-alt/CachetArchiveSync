using Cachet.ArchiveSync.Api.Repositories;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;

namespace Cachet.ArchiveSync.Api.Tests;

/// <summary>
/// DB-free slice of <see cref="TaskQueueRepository"/> (CAS-054 coverage follow-up). The repository
/// constructs <c>SqlConnection</c> directly (no connection seam - same as every other Dapper
/// repository here), so the result-mapping code (RestartAsync OUTPUT mapping, claim column mapping)
/// is only reachable with a live database and is covered by the skip-marked real-DB tests in
/// <c>Cachet.ArchiveSync.Api.IntegrationTests</c>. These tests exercise the constructor guard and,
/// against an unreachable server, each method's argument/parameter-building path up to the failed
/// connection, proving no method swallows a connection failure.
/// </summary>
public sealed class TaskQueueRepositoryTests
{
    private const string UnreachableConnectionString =
        "Server=127.0.0.1,1;Database=CachetArchiveSync;User Id=u;Password=p;Connect Timeout=2;ConnectRetryCount=0;Encrypt=False";

    private static TaskQueueRepository CreateUnreachable() =>
        new(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:CachetArchiveSync"] = UnreachableConnectionString })
            .Build());

    [Fact]
    public void Test_Construct_MissingConnectionString_Throws()
    {
        var act = () => new TaskQueueRepository(new ConfigurationBuilder().Build());

        act.Should().Throw<InvalidOperationException>().WithMessage("*ConnectionStrings:CachetArchiveSync*");
    }

    public static IEnumerable<object[]> Calls() => new[]
    {
        Call("Insert", (r, ct) => r.InsertAsync("a", "b", "KeywordSearch", 1, null, ct)),
        Call("ClaimNext", (r, ct) => r.ClaimNextAsync("worker-svc", ct)),
        Call("UpdateStatus", (r, ct) => r.UpdateStatusAsync(1, "worker-svc", "Completed", null, ct)),
        Call("Get", (r, ct) => r.GetAsync(1, ct)),
        Call("UpdateProgress", (r, ct) => r.UpdateProgressAsync(1, "worker-svc", 1, 2, ct)),
        Call("RestartWithResume", (r, ct) => r.RestartAsync(1, 2, "a", "b", ct, 24)),
        Call("RestartWithoutResume", (r, ct) => r.RestartAsync(1, 2, "a", "b", ct)),
        Call("UpdateResumeWatermark", (r, ct) => r.UpdateResumeWatermarkAsync(1, "worker-svc", 10, ct)),
        Call("SweepInterrupted", (r, ct) => r.SweepInterruptedAsync("worker-svc", "reason", ct)),
        Call("List", (r, ct) => r.ListAsync(1, null, null, 1, 25, ct)),
        Call("GetSummary", (r, ct) => r.GetSummaryAsync(1, ct)),
        Call("Acknowledge", (r, ct) => r.AcknowledgeAsync(1, 2, "api-svc", ct)),
        Call("HasActiveTaskOfType", (r, ct) => r.HasActiveTaskOfTypeAsync("SyncCopy", ct)),
    };

    private static object[] Call(string name, Func<TaskQueueRepository, CancellationToken, Task> call) => new object[] { name, call };

    [Theory]
    [MemberData(nameof(Calls))]
    public async Task Test_Methods_PropagateConnectionFailure(string name, Func<TaskQueueRepository, CancellationToken, Task> call)
    {
        _ = name;
        var repository = CreateUnreachable();

        var act = async () => await call(repository, CancellationToken.None);

        await act.Should().ThrowAsync<SqlException>();
    }
}
