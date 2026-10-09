using System.Text.RegularExpressions;
using Cachet.ArchiveSync.Core.Enums;
using FluentAssertions;

namespace Cachet.ArchiveSync.Worker.Tests;

/// <summary>
/// CAS-028 §3.5 / §12 — the Worker's C# literals must agree with
/// <c>dbo.task_queue_sweep_interrupted</c>'s SQL literals. Both assertions read the stored-procedure
/// mirror from disk (<c>Database/CachetArchiveSync/Stored-Procedures/</c>) rather than hard-coding
/// the SQL text, so a drift on either side fails the test.
/// </summary>
public sealed class WorkerTaskModuleMapTests
{
    private static string ReadSweepProcedure() =>
        File.ReadAllText(Path.Combine(
            RepoRoot.Find(), "Database", "CachetArchiveSync", "Stored-Procedures",
            "dbo.task_queue_sweep_interrupted.sql"));

    // CASE s.task_type WHEN N'KeywordSearch' THEN N'Keyword Search' ELSE N'O-Drive Sync' END
    private static readonly Regex TabModuleCase = new(
        @"CASE\s+\w+\.task_type\s+WHEN\s+N'(?<when>\w+)'\s+THEN\s+N'(?<then>[^']+)'\s+ELSE\s+N'(?<else>[^']+)'\s+END",
        RegexOptions.IgnoreCase);

    // ... ELSE N'O-Drive Sync' END  ,N'TaskInterruptedByRestart'  (the audit_log.action column value)
    private static readonly Regex ActionLiteralAfterCase = new(
        @"ELSE\s+N'[^']+'\s+END\s*,\s*N'(?<action>\w+)'",
        RegexOptions.IgnoreCase);

    [Theory]
    [InlineData("KeywordSearch")]
    [InlineData("ContentScan")]
    [InlineData("SyncCopy")]
    [InlineData("SyncUpdate")]
    [InlineData("SyncPurge")]
    public void Test_TaskTypeToTabModule_MatchesSweepProcCase(string taskType)
    {
        // Arrange — evaluate the SP's CASE for this task_type.
        var match = TabModuleCase.Match(ReadSweepProcedure());
        match.Success.Should().BeTrue("the sweep SP's tab_module CASE expression must be present");
        var sqlModule = taskType == match.Groups["when"].Value
            ? match.Groups["then"].Value
            : match.Groups["else"].Value;

        // Act
        var csharpModule = WorkerTaskModuleMap.ToTabModule(taskType);

        // Assert
        csharpModule.Should().Be(sqlModule);
        csharpModule.Should().NotBe("Task Manager");
        csharpModule.Should().Be(taskType == "KeywordSearch" ? "Keyword Search" : "O-Drive Sync");
    }

    [Fact]
    public void Test_TaskInterruptedByRestart_EnumNameMatchesSqlLiteral()
    {
        // Arrange
        // CAS-056 amended the sweep SP with a first branch that audits TaskCancelled, so the proc now has more
        // than one "CASE ... END, N'<action>'" insert; assert on the whole set rather than the first match.
        var actions = ActionLiteralAfterCase.Matches(ReadSweepProcedure()).Select(m => m.Groups["action"].Value).ToList();
        actions.Should().NotBeEmpty("the sweep SP must insert a literal audit_log.action value after the tab_module CASE");

        // Assert - Enum.ToString() is the stored audit_log.action literal (CAS-005 §15).
        actions.Should().Contain(nameof(AuditAction.TaskInterruptedByRestart));
        actions.Should().Contain(nameof(AuditAction.TaskCancelled));
        AuditAction.TaskInterruptedByRestart.ToString().Should().Be("TaskInterruptedByRestart");
        AuditAction.TaskCancelled.ToString().Should().Be("TaskCancelled");
    }
}
