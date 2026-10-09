using Cachet.ArchiveSync.Core.Dtos;
using Dapper;
using Microsoft.Data.SqlClient;

namespace Cachet.ArchiveSync.Api.Repositories;

/// <summary>
/// CAS-041 §2: Dapper call to <c>dbo.configuration_detail_lst</c>, supplied to
/// <see cref="Core.Configuration.DatabaseConfigurationSource.LoadGroup"/> by Api's and Worker's
/// <c>Program.cs</c>. Synchronous by necessity — <c>IConfigurationProvider.Load</c> is synchronous
/// and runs once at host startup, before DI exists (hence static, not a registered repository).
/// </summary>
public static class ConfigurationDetailLoader
{
    public static IReadOnlyList<ConfigurationDetailRow> Load(string connectionString, string configurationName)
    {
        var parameters = new DynamicParameters();
        parameters.Add("@ConfigurationName", configurationName);

        using var connection = new SqlConnection(connectionString);

        return connection.Query<ConfigurationDetailRow>(
            "dbo.configuration_detail_lst",
            parameters,
            commandType: System.Data.CommandType.StoredProcedure).AsList();
    }
}
