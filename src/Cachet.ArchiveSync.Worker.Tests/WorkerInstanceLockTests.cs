using FluentAssertions;
using Microsoft.Extensions.Configuration;

namespace Cachet.ArchiveSync.Worker.Tests;

/// <summary>
/// Real-DB test for <see cref="SqlWorkerInstanceLock"/> over <c>dbo.worker_instance_lock_acquire</c> /
/// <c>dbo.worker_instance_lock_held</c> (CAS-028 §3.7, §13 "WorkerInstanceLockTests" — SC-13): the
/// session-owned exclusive <c>sp_getapplock</c> is exclusive across two independent sessions and is
/// released when the owning (non-pooled) connection is disposed.
///
/// Skip-marked — same convention and connection-string source as the Api.IntegrationTests real-DB
/// suites (<c>ConnectionStrings__CachetArchiveSync</c> via environment variables or
/// <c>appsettings.IntegrationTests.json</c>). Un-skip and run manually against a real instance with
/// the CAS-028 migration deployed.
/// </summary>
public sealed class WorkerInstanceLockTests
{
    private const string SkipReason =
        "Requires a live SQL Server instance against the CachetArchiveSync database, with the " +
        "CAS-028 migration deployed — not available in this sandbox; run manually against a real " +
        "instance before merging.";

    private readonly IConfiguration _configuration;

    public WorkerInstanceLockTests()
    {
        _configuration = new ConfigurationBuilder()
            .AddJsonFile("appsettings.IntegrationTests.json", optional: true)
            .AddEnvironmentVariables()
            .Build();
    }

    [Fact(Skip = SkipReason)]
    public async Task Test_SC13_InstanceLock_ExclusiveAcrossSessions_ReleasedOnDispose()
    {
        // Arrange — two independent "Worker instances", each with its own dedicated session.
        var instanceA = new SqlWorkerInstanceLock(_configuration);
        await using var instanceB = new SqlWorkerInstanceLock(_configuration);

        try
        {
            // Act / Assert — A acquires and holds it.
            (await instanceA.TryAcquireAsync(CancellationToken.None)).Should().BeTrue();
            (await instanceA.IsHeldAsync(CancellationToken.None)).Should().BeTrue();

            // B is refused and does not hold it while A's session is alive.
            (await instanceB.TryAcquireAsync(CancellationToken.None)).Should().BeFalse();
            (await instanceB.IsHeldAsync(CancellationToken.None)).Should().BeFalse();
            (await instanceA.IsHeldAsync(CancellationToken.None)).Should().BeTrue();
        }
        finally
        {
            // Disposing A closes its non-pooled connection, ending the session → lock released.
            await instanceA.DisposeAsync();
        }

        // B now acquires and holds it.
        (await instanceB.TryAcquireAsync(CancellationToken.None)).Should().BeTrue();
        (await instanceB.IsHeldAsync(CancellationToken.None)).Should().BeTrue();
    }
}
