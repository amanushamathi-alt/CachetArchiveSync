using Cachet.ArchiveSync.Core.Helpers;
using FluentAssertions;

namespace Cachet.ArchiveSync.Api.Tests;

/// <summary>CAS-053 Q-C — unit tests for <see cref="B2KeyPath.Combine"/> (no Core test project exists; lives with the Api tests).</summary>
public sealed class B2KeyPathTests
{
    [Theory]
    [InlineData("D/Shares/Operations", "", "D/Shares/Operations")]
    [InlineData("D/Shares/Operations", "Ops", "D/Shares/Operations/Ops")]
    [InlineData("D/Shares/Operations", "Ops/Audit", "D/Shares/Operations/Ops/Audit")]
    public void Test_B2KeyPath_Combine_JoinsPrefixAndRootRelativeFolder(string prefix, string folder, string expected)
    {
        // Act
        var result = B2KeyPath.Combine(prefix, folder);

        // Assert
        result.Should().Be(expected);
    }
}
