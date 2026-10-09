using System.Reflection;
using System.Text;
using System.Text.Json;
using Cachet.ArchiveSync.Api.Content;
using Cachet.ArchiveSync.Api.Controllers;
using Cachet.ArchiveSync.Api.Services;
using Cachet.ArchiveSync.Api.Storage;
using Cachet.ArchiveSync.Core.Dtos;
using Cachet.ArchiveSync.Core.Interfaces;
using FluentAssertions;

namespace Cachet.ArchiveSync.Api.Tests;

/// <summary>
/// CAS-040 §13 — SC-12 (constructor reflection): nothing on the Api file path takes a B2
/// client, B2 delete client or credential protector (§12 B2 Credential Handling). The HTTP
/// response scan half of SC-12 is proven in
/// <c>Cachet.ArchiveSync.Api.IntegrationTests.FilesControllerIntegrationTests</c>, and the Web
/// §8.5 endpoints in <c>Cachet.ArchiveSync.Web.Tests.FileViewer.FilePassThroughIntegrationTests</c>
/// (same test name in each).
/// </summary>
public sealed class FileSecurityArchitectureTests
{
    private static readonly Type[] ForbiddenDependencies =
    {
        typeof(IB2Client),
        typeof(IUserScopedB2DeleteClient),
        typeof(ICredentialProtector)
    };

    [Fact]
    public async Task Test_SC12_NoB2DependencyOrCredentialOnFilePath()
    {
        // FilesController, FileContentService and every collaborator they construct through DI.
        var filePathTypes = new[]
        {
            typeof(FilesController),
            typeof(FileContentService),
            typeof(MirrorFileReader),
            typeof(OfficePreviewRenderer),
            typeof(PreviewConversionLimiter)
        };

        foreach (var type in filePathTypes)
        {
            var parameterTypes = type.GetConstructors(BindingFlags.Public | BindingFlags.Instance)
                .SelectMany(c => c.GetParameters())
                .Select(p => p.ParameterType)
                .ToArray();

            parameterTypes.Should().NotBeEmpty($"{type.Name} has a DI constructor to inspect");
            parameterTypes.Should().NotContain(t => ForbiddenDependencies.Any(f => f.IsAssignableFrom(t)),
                $"{type.Name} must not take a B2 client or credential dependency (SC-12)");
        }

        // FileContentService's fields likewise hold nothing B2-related.
        typeof(FileContentService).GetFields(BindingFlags.NonPublic | BindingFlags.Instance)
            .Select(f => f.FieldType)
            .Should().NotContain(t => ForbiddenDependencies.Any(f => f.IsAssignableFrom(t)));

        // A successful result carries no B2 host, token or key id — only the file's own bytes/type/name.
        var harness = new FileContentServiceHarness();
        harness.Seed(FileViewerTestFixtures.Record(fileIndexId: 1200, extension: "pdf", source: "B2"), FileViewerTestFixtures.Pdf());
        var sut = harness.Build();
        var metadata = await sut.GetMetadataAsync(1200, "KeywordSearch", CancellationToken.None);
        var view = await sut.GetViewAsync(1200, "KeywordSearch", 1, CancellationToken.None);
        var scan = JsonSerializer.Serialize(metadata.Metadata) + JsonSerializer.Serialize(view with { Content = null });
        await view.Content!.DisposeAsync();
        scan.Should().NotContainEquivalentOf("backblaze");
        scan.Should().NotContainEquivalentOf("authorization");
        scan.Should().NotContainEquivalentOf("keyId");
    }
}
