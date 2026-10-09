using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Cachet.ArchiveSync.Worker.Tests;

/// <summary>
/// CAS-028 §9 / §7 V1–V2 — <see cref="WorkerOptions"/> binding, DataAnnotations validation and
/// defaults. Options are registered exactly as <c>Program.cs</c> does
/// (<c>AddOptions&lt;WorkerOptions&gt;().Bind(section).ValidateDataAnnotations()</c>); resolving
/// <c>IOptions&lt;WorkerOptions&gt;.Value</c> runs the same validation <c>ValidateOnStart()</c>
/// triggers at host start.
/// </summary>
public sealed class WorkerOptionsTests
{
    private static WorkerOptions Resolve(IDictionary<string, string?> values)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var services = new ServiceCollection();
        services.AddOptions<WorkerOptions>()
            .Bind(configuration.GetSection(WorkerOptions.SectionName))
            .ValidateDataAnnotations();
        return services.BuildServiceProvider().GetRequiredService<IOptions<WorkerOptions>>().Value;
    }

    // ---- V-1: Worker:PollingIntervalSeconds must be 1–60 --------------------------------------------

    [Theory]
    [InlineData(0, false)]
    [InlineData(61, false)]
    [InlineData(-5, false)]
    [InlineData(1, true)]
    [InlineData(60, true)]
    public void Test_V1_WorkerOptions_PollingIntervalOutOfRange_FailsValidation(int value, bool valid)
    {
        // Arrange
        var values = new Dictionary<string, string?> { ["Worker:PollingIntervalSeconds"] = value.ToString() };

        // Act
        var act = () => Resolve(values);

        // Assert
        if (valid)
        {
            act.Should().NotThrow().Which.PollingIntervalSeconds.Should().Be(value);
        }
        else
        {
            act.Should().Throw<OptionsValidationException>()
                .Which.Failures.Should().ContainSingle(f => f.Contains(nameof(WorkerOptions.PollingIntervalSeconds)));
        }
    }

    // ---- V-2: Worker:MaxDegreeOfParallelism must be 1–16 --------------------------------------------

    [Theory]
    [InlineData(0, false)]
    [InlineData(17, false)]
    [InlineData(-1, false)]
    [InlineData(1, true)]
    [InlineData(16, true)]
    public void Test_V2_WorkerOptions_ParallelismOutOfRange_FailsValidation(int value, bool valid)
    {
        // Arrange
        var values = new Dictionary<string, string?> { ["Worker:MaxDegreeOfParallelism"] = value.ToString() };

        // Act
        var act = () => Resolve(values);

        // Assert
        if (valid)
        {
            act.Should().NotThrow().Which.MaxDegreeOfParallelism.Should().Be(value);
        }
        else
        {
            act.Should().Throw<OptionsValidationException>()
                .Which.Failures.Should().ContainSingle(f => f.Contains(nameof(WorkerOptions.MaxDegreeOfParallelism)));
        }
    }

    // ---- CAS-029 V-1: Worker:ProgressReportIntervalSeconds must be 5–300, default 15 ----------------

    [Theory]
    [InlineData(4, false)]
    [InlineData(301, false)]
    [InlineData(0, false)]
    [InlineData(-15, false)]
    [InlineData(5, true)]
    [InlineData(300, true)]
    [InlineData(null, true)]
    public void Test_V1_WorkerOptions_ProgressIntervalOutOfRange_FailsValidation(int? value, bool valid)
    {
        // Arrange — null means the key is absent, so the class default applies.
        var values = new Dictionary<string, string?>();
        if (value is not null)
        {
            values["Worker:ProgressReportIntervalSeconds"] = value.Value.ToString();
        }

        // Act
        var act = () => Resolve(values);

        // Assert
        if (valid)
        {
            act.Should().NotThrow().Which.ProgressReportIntervalSeconds.Should().Be(value ?? 15);
        }
        else
        {
            act.Should().Throw<OptionsValidationException>()
                .Which.Failures.Should().ContainSingle(f => f.Contains(nameof(WorkerOptions.ProgressReportIntervalSeconds)));
        }

        if (value is null)
        {
            // §9 default 15 is also what the shipped Worker appsettings.json states.
            var appSettingsPath = Path.Combine(RepoRoot.Find(), "src", "Cachet.ArchiveSync.Worker", "appsettings.json");
            var shipped = new ConfigurationBuilder().AddJsonFile(appSettingsPath, optional: false).Build();
            shipped["Worker:ProgressReportIntervalSeconds"].Should().Be("15");
        }
    }

    // ---- §9: defaults are 5 s / 4, both in code and in the shipped Worker appsettings.json ----------

    [Fact]
    public void Test_WorkerOptions_Defaults_Are5And4()
    {
        // Arrange — no Worker section at all → class defaults apply.
        var bound = Resolve(new Dictionary<string, string?>());

        // Assert — class defaults
        bound.PollingIntervalSeconds.Should().Be(5);
        bound.MaxDegreeOfParallelism.Should().Be(4);

        // Assert — the shipped appsettings.json carries the same stated defaults (§9 / §18 checklist).
        var appSettingsPath = Path.Combine(
            RepoRoot.Find(), "src", "Cachet.ArchiveSync.Worker", "appsettings.json");
        var shipped = new ConfigurationBuilder().AddJsonFile(appSettingsPath, optional: false).Build();
        shipped["Worker:PollingIntervalSeconds"].Should().Be("5");
        shipped["Worker:MaxDegreeOfParallelism"].Should().Be("4");
    }
}

/// <summary>Locates the repository root by walking up from the test binaries to the folder holding <c>Database</c>.</summary>
internal static class RepoRoot
{
    public static string Find()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "Database", "CachetArchiveSync")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            "Could not locate the repository root (a folder containing Database/CachetArchiveSync) above " +
            AppContext.BaseDirectory);
    }
}
