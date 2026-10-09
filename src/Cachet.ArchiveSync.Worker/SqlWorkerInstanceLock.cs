using System.Data;
using Dapper;
using Microsoft.Data.SqlClient;

namespace Cachet.ArchiveSync.Worker;

/// <summary>
/// CAS-028 §3.7 — <see cref="IWorkerInstanceLock"/> over a session-owned exclusive
/// <c>sp_getapplock</c> (<c>dbo.worker_instance_lock_acquire</c> / <c>dbo.worker_instance_lock_held</c>).
/// Registered as a singleton. Owns one dedicated, <b>non-pooled</b> <see cref="SqlConnection"/>
/// (<c>Pooling=false</c>, so disposing it really ends the session and releases the lock), opened on
/// the first acquire attempt and kept open for the process lifetime. Called only sequentially from
/// <see cref="Worker"/>'s single execute loop, so the connection is never used concurrently.
/// </summary>
public sealed class SqlWorkerInstanceLock : IWorkerInstanceLock
{
    private readonly SqlConnection _connection;

    public SqlWorkerInstanceLock(IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("CachetArchiveSync")
            ?? throw new InvalidOperationException(
                "Configuration key 'ConnectionStrings:CachetArchiveSync' must be set.");

        var builder = new SqlConnectionStringBuilder(connectionString) { Pooling = false };
        _connection = new SqlConnection(builder.ConnectionString);
    }

    public async Task<bool> TryAcquireAsync(CancellationToken ct)
    {
        if (_connection.State != ConnectionState.Open)
        {
            if (_connection.State != ConnectionState.Closed)
            {
                // Broken session: its lock (if any) is already gone; start a fresh session.
                await _connection.CloseAsync();
            }

            await _connection.OpenAsync(ct);
        }

        var parameters = new DynamicParameters();
        parameters.Add("@acquired", dbType: DbType.Boolean, direction: ParameterDirection.Output);

        await _connection.ExecuteAsync(new CommandDefinition(
            "dbo.worker_instance_lock_acquire",
            parameters,
            commandType: CommandType.StoredProcedure,
            cancellationToken: ct));

        return parameters.Get<bool>("@acquired");
    }

    public async Task<bool> IsHeldAsync(CancellationToken ct)
    {
        // Fail safe (§3.7): a closed/broken session cannot hold the lock, and any exception from
        // the held check counts as not held.
        if (_connection.State != ConnectionState.Open)
        {
            return false;
        }

        try
        {
            var parameters = new DynamicParameters();
            parameters.Add("@held", dbType: DbType.Boolean, direction: ParameterDirection.Output);

            await _connection.ExecuteAsync(new CommandDefinition(
                "dbo.worker_instance_lock_held",
                parameters,
                commandType: CommandType.StoredProcedure,
                cancellationToken: ct));

            return parameters.Get<bool?>("@held") == true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        // Closing the non-pooled connection ends the session, which releases the session-owned lock.
        await _connection.DisposeAsync();
    }
}
