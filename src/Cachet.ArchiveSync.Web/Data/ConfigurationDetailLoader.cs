using Cachet.ArchiveSync.Core.Dtos;
using Dapper;
using Microsoft.Data.SqlClient;

namespace Cachet.ArchiveSync.Web.Data;

/// <summary>
/// CAS-041 §2: Web-local Dapper call to <c>dbo.configuration_detail_lst</c>, supplied to
/// <see cref="Core.Configuration.DatabaseConfigurationSource.LoadGroup"/> by this project's
/// <c>Program.cs</c>. Duplicates Api's <c>ConfigurationDetailLoader</c> rather than referencing it —
/// Web does not reference Api (same precedent as <see cref="UserRepository"/>). Synchronous by
/// necessity: <c>IConfigurationProvider.Load</c> is synchronous and runs once at startup.
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
