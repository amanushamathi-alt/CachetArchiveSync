using System.Text.RegularExpressions;
using Cachet.ArchiveSync.Web.Tests.Auth;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Cachet.ArchiveSync.Web.Tests;

/// <summary>
/// CAS-030 §13 "TaskPollingOptionsTests" — V8 and SC-12 for <see cref="TaskPollingOptions"/>
/// (§8.4, §9). Both are proved against the REAL <c>Cachet.ArchiveSync.Web/Program.cs</c> wiring
/// (<c>AddOptions&lt;TaskPollingOptions&gt;().Bind(...).ValidateDataAnnotations().ValidateOnStart()</c>)
/// through <see cref="LoginWebApplicationFactory"/>, overriding only the one configuration key under
/// test. SC-12's "no hard-coded 5-second interval remains" clause is a source scan of the Web
/// project, since a constant that is never exercised cannot be observed at runtime.
/// </summary>
public sealed class TaskPollingOptionsTests
{
    private const string ConfigKey = "Worker:TaskManagerPollIntervalSeconds";

    private static WebApplicationFactory<Program> CreateFactory(string? pollIntervalSeconds) =>
        new LoginWebApplicationFactory().WithWebHostBuilder(builder =>
        {
            if (pollIntervalSeconds is not null)
            {
                builder.ConfigureAppConfiguration((_, configBuilder) =>
                    configBuilder.AddInMemoryCollection(new Dictionary<string, string?> { [ConfigKey] = pollIntervalSeconds }));
            }
        });

    private static IEnumerable<Exception> ExceptionChain(Exception exception)
    {
        var pending = new Stack<Exception>();
        pending.Push(exception);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            yield return current;
            if (current is AggregateException aggregate)
            {
                foreach (var inner in aggregate.InnerExceptions)
                {
                    pending.Push(inner);
                }
            }
            else if (current.InnerException is not null)
            {
                pending.Push(current.InnerException);
            }
        }
    }

    // -----------------------------------------------------------------------------------------
    // V8
    // -----------------------------------------------------------------------------------------

    [Theory]
    [InlineData("4")]
    [InlineData("61")]
    [InlineData("0")]
    [InlineData("-10")]
    public void Test_V8_PollIntervalOutOfRange_FailsValidation(string configuredValue)
    {
        // Arrange
        using var factory = CreateFactory(configuredValue);

        // Act — starting the host runs ValidateOnStart.
        var act = () => factory.CreateClient();

        // Assert — the Web host fails at startup with OptionsValidationException (§7 V8).
        var thrown = act.Should().Throw<Exception>().Which;
        ExceptionChain(thrown).OfType<OptionsValidationException>().Should().ContainSingle()
            .Which.OptionsType.Should().Be(typeof(TaskPollingOptions));
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("7.5")]
    public void Test_V8_PollIntervalNotAnInteger_FailsAtStartup(string configuredValue)
    {
        // "must be an integer": a non-integer value cannot even bind, so startup fails (as
        // InvalidOperationException from the configuration binder, before data-annotation validation).
        using var factory = CreateFactory(configuredValue);

        var act = () => factory.CreateClient();

        act.Should().Throw<Exception>();
    }

    [Theory]
    [InlineData("5", 5)]
    [InlineData("60", 60)]
    public void Test_V8_PollIntervalBoundariesAccepted(string configuredValue, int expectedSeconds)
    {
        using var factory = CreateFactory(configuredValue);
        using var client = factory.CreateClient();

        factory.Services.GetRequiredService<IOptions<TaskPollingOptions>>().Value.PollInterval
            .Should().Be(TimeSpan.FromSeconds(expectedSeconds));
    }

    // -----------------------------------------------------------------------------------------
    // SC-12
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void Test_SC12_PollInterval_BoundFromConfiguration()
    {
        // 1. Default: the Web host's own appsettings.json value is 10 s.
        using (var defaultFactory = CreateFactory(pollIntervalSeconds: null))
        using (defaultFactory.CreateClient())
        {
            var options = defaultFactory.Services.GetRequiredService<IOptions<TaskPollingOptions>>().Value;
            options.TaskManagerPollIntervalSeconds.Should().Be(10);
            options.PollInterval.Should().Be(TimeSpan.FromSeconds(10));
        }

        // 2. The key Worker:TaskManagerPollIntervalSeconds overrides it (env var
        //    Worker__TaskManagerPollIntervalSeconds maps onto this same key, §9).
        using (var overriddenFactory = CreateFactory("15"))
        using (overriddenFactory.CreateClient())
        {
            overriddenFactory.Services.GetRequiredService<IOptions<TaskPollingOptions>>().Value.PollInterval
                .Should().Be(TimeSpan.FromSeconds(15));
        }

        // 3. The POCO default (used when the key is absent entirely) is also 10 s.
        new TaskPollingOptions().TaskManagerPollIntervalSeconds.Should().Be(10);

        // 4. Every poll loop in scope reads the bound option; no hard-coded 5-second interval remains
        //    anywhere in Cachet.ArchiveSync.Web.
        var webRoot = FindWebProjectRoot();
        var sources = Directory.EnumerateFiles(webRoot, "*.*", SearchOption.AllDirectories)
            .Where(path => path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".razor", StringComparison.OrdinalIgnoreCase))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .ToList();
        sources.Should().NotBeEmpty();

        var hardCodedFiveSeconds = new Regex(@"TimeSpan\.FromSeconds\(\s*5(\.0+)?\s*\)|TimeSpan\.FromMilliseconds\(\s*5000\s*\)|new\s+TimeSpan\(\s*0\s*,\s*0\s*,\s*5\s*\)");
        sources.Where(path => hardCodedFiveSeconds.IsMatch(File.ReadAllText(path)))
            .Should().BeEmpty("SC-12: no hard-coded 5-second poll interval may remain in Cachet.ArchiveSync.Web");

        foreach (var component in new[]
                 {
                     Path.Combine("Components", "Layout", "TaskManagerBadge.razor"),
                     Path.Combine("Components", "Pages", "KeywordSearch.razor"),
                     Path.Combine("Components", "Pages", "ODriveSyncContentScan.razor"),
                 })
        {
            var text = File.ReadAllText(Path.Combine(webRoot, component));
            text.Should().Contain("IOptions<TaskPollingOptions>", $"{component} must inject the bound interval (§8.4)");
            Regex.Matches(text, @"new PeriodicTimer\((?<arg>[^)]*)\)").Select(m => m.Groups["arg"].Value)
                .Should().NotBeEmpty().And.OnlyContain(arg => arg.Contains("PollInterval"),
                    $"every PeriodicTimer in {component} must use the configured interval");
        }
    }

    private static string FindWebProjectRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "Cachet.ArchiveSync.Web");
            if (File.Exists(Path.Combine(candidate, "Cachet.ArchiveSync.Web.csproj")))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Cachet.ArchiveSync.Web project folder not found above the test output directory.");
    }
}
