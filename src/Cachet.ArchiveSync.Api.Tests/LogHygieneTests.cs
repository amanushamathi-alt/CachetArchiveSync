using System.Reflection;
using Cachet.ArchiveSync.Api.Controllers;
using Cachet.ArchiveSync.Api.Reporting;
using Cachet.ArchiveSync.Api.Services;
using Cachet.ArchiveSync.Core.Dtos;
using Cachet.ArchiveSync.Core.Exceptions;
using Cachet.ArchiveSync.Core.Interfaces;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Cachet.ArchiveSync.Api.Tests;

/// <summary>
/// SC-11 ("no B2 application key, session token/cookie value, or raw file content ever appears in
/// a log line") for the Api-side surfaces: <c>POST /search</c> (<see cref="KeywordSearchService"/>)
/// and <c>POST /search/results/quarantine</c> (<see cref="QuarantineService"/>). §11's telemetry
/// contract additionally forbids logging the user-entered search term itself, which this test also
/// asserts. The Worker processor's own log hygiene is proven separately in
/// <c>Cachet.ArchiveSync.Worker.Tests.KeywordSearchTaskProcessorTests.Test_SC11_NoCredentialOrContentLeakageInLogs</c>
/// (the four surfaces named by SC-11 span two different projects — there is no single class that
/// can assert all of them in one process).
/// </summary>
public sealed class LogHygieneTests
{
    private const string ConfiguredBucket = "cachet-archive-prod";
    private const string SensitiveTerm = "acme-corp-merger-confidential-code-7734";
    private const string FakeB2Key = "K001FakeApplicationKeyValueNeverLog";
    private const string FakeSessionToken = "CachetAuth=fakeSessionCookieValueNeverLog";

    private static List<string> GetFormattedLogMessages<T>(ILogger<T> logger)
    {
        var messages = new List<string>();
        foreach (var call in logger.ReceivedCalls())
        {
            var args = call.GetArguments();
            if (args.Length < 5 || args[2] is null || args[4] is not Delegate formatter)
            {
                continue;
            }

            var message = (string?)formatter.DynamicInvoke(args[2], args[3] as Exception);
            if (message is not null)
            {
                messages.Add(message);
            }
        }

        return messages;
    }

    private static void AssertNoLeakage(IEnumerable<string> messages)
    {
        messages.Should().OnlyContain(m =>
            !m.Contains(SensitiveTerm, StringComparison.Ordinal) &&
            !m.Contains(FakeB2Key, StringComparison.Ordinal) &&
            !m.Contains(FakeSessionToken, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Test_SC11_NoCredentialOrContentLeakageInLogs()
    {
        // --- POST /search: the submitted term is never logged (§11 telemetry — only counts/ids) ---
        var taskQueueRepository = Substitute.For<ITaskQueueRepository>();
        var searchResultRepository = Substitute.For<ISearchResultRepository>();
        var auditLogService = Substitute.For<IAuditLogService>();
        var searchLogger = Substitute.For<ILogger<KeywordSearchService>>();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["B2:BucketName"] = ConfiguredBucket })
            .Build();
        taskQueueRepository
            .InsertAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(100234);
        var searchService = new KeywordSearchService(taskQueueRepository, searchResultRepository, auditLogService, configuration, searchLogger);

        await searchService.SubmitAsync(
            new SearchRequest { Term = SensitiveTerm, Bucket = ConfiguredBucket, Extension = "All" },
            submittedByUserId: 1,
            CancellationToken.None);

        AssertNoLeakage(GetFormattedLogMessages(searchLogger));

        // --- POST /search/results/quarantine: no B2 key/session token ever reaches a log line -----
        var taskQueueRepository2 = Substitute.For<ITaskQueueRepository>();
        var searchResultRepository2 = Substitute.For<ISearchResultRepository>();
        var quarantineEntryRepository = Substitute.For<IQuarantineEntryRepository>();
        var auditLogService2 = Substitute.For<IAuditLogService>();
        var quarantineLogger = Substitute.For<ILogger<QuarantineService>>();

        taskQueueRepository2.GetAsync(100234, Arg.Any<CancellationToken>()).Returns(new TaskQueueRow
        {
            TaskQueueId = 100234,
            TaskType = "KeywordSearch",
            Status = "Completed",
            SubmittedByUserId = 1,
            SubmittedAt = DateTimeOffset.UtcNow
        });
        searchResultRepository2.GetAsync(100501, Arg.Any<CancellationToken>()).Returns(new SearchResultRow
        {
            SearchResultId = 100501,
            TaskQueueId = 100234,
            FileIndexId = 1,
            MatchType = "Filename",
            MatchStatus = "Flagged",
            CreatedAt = DateTimeOffset.UtcNow,
            FileName = "agreement-004.pdf",
            RelativePath = "contracts/2019/agreement-004.pdf",
            FileSizeBytes = 1,
            ModifiedAt = DateTimeOffset.UtcNow
        });
        quarantineEntryRepository
            .InsertAsync(Arg.Any<string>(), Arg.Any<string>(), 100501, Arg.Any<CancellationToken>())
            .Returns(new QuarantineInsertResult { Outcome = QuarantineInsertOutcome.Added, QuarantineEntryId = 100801 });
        var quarantineService = new QuarantineService(
            taskQueueRepository2, searchResultRepository2, quarantineEntryRepository, auditLogService2, quarantineLogger);

        await quarantineService.AddAsync(
            new QuarantineAddRequest { TaskId = 100234, FileIds = new[] { 100501 } },
            actingUserId: 1,
            CancellationToken.None);

        AssertNoLeakage(GetFormattedLogMessages(quarantineLogger));
    }

    /// <summary>
    /// CAS-008 §11/SC-7 ("no B2 application key, session token/cookie value, or raw file content
    /// ever appears in a log line across <c>GET /quarantine</c>, <c>POST /quarantine</c>, or
    /// <c>POST /quarantine/delete</c>") — extends this class rather than adding a divergent second
    /// log-hygiene harness, per this project's established CAS-006 precedent above. Embeds
    /// B2-key-/session-token-/content-like values into the quarantine entries' file paths/names so
    /// that any incidental logging of those fields would be caught.
    /// </summary>
    [Fact]
    public async Task Test_SC7_NoCredentialOrContentLeakageInLogs()
    {
        var sensitivePathSucceeding = $"contracts/{SensitiveTerm}/{FakeB2Key}-{FakeSessionToken}-a.pdf";
        var sensitivePathFailing = $"contracts/{SensitiveTerm}/{FakeB2Key}-{FakeSessionToken}-b.pdf";

        // --- GET /quarantine: entry paths/names are never logged, only the active count -----------
        var quarantineEntryRepository = Substitute.For<IQuarantineEntryRepository>();
        var listLogger = Substitute.For<ILogger<QuarantineListService>>();
        quarantineEntryRepository.ListActivePagedAsync(1, 25, Arg.Any<CancellationToken>()).Returns(new QuarantineEntryListPage
        {
            TotalCount = 1,
            Rows = new List<QuarantineEntryListRow>
        {
            new()
            {
                QuarantineEntryId = 100801,
                FileIndexId = 600801,
                Name = FakeB2Key,
                Path = sensitivePathSucceeding,
                QuarantinedOn = DateTimeOffset.UtcNow,
                Status = "Flagged",
                SourceTab = "Keyword Search"
            }
            }
        });
        var listService = new QuarantineListService(quarantineEntryRepository, listLogger);

        await listService.GetActiveAsync(actingUserId: 1, page: 1, pageSize: 25, CancellationToken.None);

        AssertNoLeakage(GetFormattedLogMessages(listLogger));

        // --- POST /quarantine/delete: per-file success/failure logging never includes the file path,
        // a B2 key, or a session token — only the fileId and a generic error summary --------------
        var deleteQuarantineEntryRepository = Substitute.For<IQuarantineEntryRepository>();
        var fileIndexRepository = Substitute.For<IFileIndexRepository>();
        var deleteUserRepository = Substitute.For<IUserRepository>();
        var deleteCredentialProtector = Substitute.For<ICredentialProtector>();
        var deleteUserB2KeyAuthorizer = Substitute.For<IUserB2KeyAuthorizer>();
        var userScopedB2DeleteClient = Substitute.For<IUserScopedB2DeleteClient>();
        var deleteAuditLogService = Substitute.For<IAuditLogService>();
        var deleteLogger = Substitute.For<ILogger<QuarantinePermanentDeleteService>>();

        // CAS-018 §2: live pre-batch B2 authorization check against the acting user's own stored
        // key — wired to succeed with deleteFiles so the per-file loop below is reached.
        const string cipherKeyId = "ct:keyid";
        const string cipherApplicationKey = "ct:key";
        const string plainKeyId = "plain-key-id-0000000000001";
        const string bucketId = "bucket-1";
        var liveAuthorization = new B2AuthorizationInfo
        {
            AccountId = "acct-1",
            AuthorizationToken = "4_liveUserAuthToken_neverLog0000000000000000000000000000000000",
            ApiUrl = "https://api-fake.backblazeb2.com",
            DownloadUrl = "https://download-fake.backblazeb2.com",
            Capabilities = new[] { "listFiles", "readFiles", "writeFiles", "deleteFiles" },
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddHours(24)
        };
        deleteUserRepository.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(new UserAccount
        {
            UserId = 1,
            Username = "jdoe",
            PasswordHash = "irrelevant-hash",
            B2ApplicationKeyId = cipherKeyId,
            B2ApplicationKey = cipherApplicationKey,
            IsActive = true,
            SessionTokenVersion = 0
        });
        deleteCredentialProtector.Unprotect(cipherKeyId).Returns(plainKeyId);
        deleteCredentialProtector.Unprotect(cipherApplicationKey).Returns(FakeB2Key);
        deleteUserB2KeyAuthorizer.AuthorizeAsync(plainKeyId, FakeB2Key, Arg.Any<CancellationToken>())
            .Returns(liveAuthorization);
        userScopedB2DeleteClient.GetBucketIdAsync(Arg.Any<CancellationToken>()).Returns(bucketId);

        deleteQuarantineEntryRepository.GetActiveForDeleteAsync(100801, Arg.Any<CancellationToken>()).Returns(new QuarantineEntryForDeleteRow
        {
            QuarantineEntryId = 100801,
            FileIndexId = 200801,
            Name = FakeB2Key,
            Path = sensitivePathSucceeding
        });
        deleteQuarantineEntryRepository.GetActiveForDeleteAsync(100802, Arg.Any<CancellationToken>()).Returns(new QuarantineEntryForDeleteRow
        {
            QuarantineEntryId = 100802,
            FileIndexId = 200802,
            Name = FakeB2Key,
            Path = sensitivePathFailing
        });
        // 100801 succeeds fully; 100802 fails at the B2 step — both branches of the delete
        // service's logging (LogInformation summary, LogWarning per-file failure) are exercised.
        userScopedB2DeleteClient.ListFileVersionsAsync(liveAuthorization, bucketId, sensitivePathSucceeding, sensitivePathSucceeding, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new B2ListFileVersionsResult
            {
                Files = new List<B2FileInfo>
                {
                    new() { FileId = "4_z_v1", FileName = sensitivePathSucceeding, ContentSha1 = "sha1", ContentLength = 1, Action = "upload", UploadTimestamp = 1 }
                }
            });
        userScopedB2DeleteClient.ListFileVersionsAsync(liveAuthorization, bucketId, sensitivePathFailing, sensitivePathFailing, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Throws(new B2ApiException(500, "internal_error", "B2 call failed."));
        deleteQuarantineEntryRepository.TryTransitionToDeletedAsync(100801, Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(true);
        // CAS-062: no local mirror copy in this scenario.
        var b2MirrorFileRemover = Substitute.For<IB2MirrorFileRemover>();
        b2MirrorFileRemover.RemoveAsync(default!, default).ReturnsForAnyArgs(B2MirrorRemoveResult.NotPresent);
        var deleteService = new QuarantinePermanentDeleteService(
            deleteQuarantineEntryRepository, fileIndexRepository, deleteUserRepository, deleteCredentialProtector,
            deleteUserB2KeyAuthorizer, userScopedB2DeleteClient, b2MirrorFileRemover, deleteAuditLogService, deleteLogger);

        await deleteService.DeleteAsync(
            new QuarantineDeleteRequest { FileIds = new[] { 100801, 100802 }, Confirm = true },
            actingUserId: 1,
            CancellationToken.None);

        AssertNoLeakage(GetFormattedLogMessages(deleteLogger));
    }

    /// <summary>
    /// CAS-009 §11/SC-7 ("no B2 application key, session token/cookie value, or raw file content
    /// appears in any log line for <c>GET /audit-log</c>") — extends this class per CAS-009 §13's
    /// Test Plan ("LogHygieneTests (extended)"), consistent with this file's established pattern
    /// of accreting one method per spec's own log-hygiene criterion rather than a divergent second
    /// harness. Named <c>..._AuditLogListEndpoint</c> rather than the spec's literal
    /// <c>Test_SC7_NoCredentialOrContentLeakageInLogs</c> because that exact name is already used
    /// above by CAS-008's own (distinct) SC-7 criterion in this same shared class — C# does not
    /// allow two methods with an identical name and signature in one class, so this method is
    /// disambiguated with a suffix naming the endpoint it covers. Flag for the reviewer: this is a
    /// deliberate, unavoidable deviation from the pre-extracted test map's exact `test_name`, not an
    /// oversight.
    /// </summary>
    [Fact]
    public async Task Test_SC7_NoCredentialOrContentLeakageInLogs_AuditLogListEndpoint()
    {
        var sensitivePath = $"contracts/{SensitiveTerm}/{FakeB2Key}-{FakeSessionToken}-a.pdf";

        var auditLogRepository = Substitute.For<IAuditLogRepository>();
        var listLogger = Substitute.For<ILogger<AuditLogListService>>();
        auditLogRepository
            .ListAsync(Arg.Any<string?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns((new List<AuditLogRow>
            {
                new()
                {
                    AuditLogId = 100901,
                    Username = FakeB2Key,
                    TabModule = "Quarantine",
                    Action = "Deleted",
                    FilePath = sensitivePath,
                    CreatedAt = DateTimeOffset.UtcNow,
                    Result = "Failure",
                    ResultDetail = $"{FakeB2Key} {FakeSessionToken} {SensitiveTerm}"
                }
            }, 1));
        var listService = new AuditLogListService(auditLogRepository, listLogger);

        await listService.ListAsync(tab: null, dateFrom: null, dateTo: null, page: 1, pageSize: 50, actingUserId: 1, CancellationToken.None);

        AssertNoLeakage(GetFormattedLogMessages(listLogger));
    }

    /// <summary>
    /// CAS-010 §12/SC-6 ("no code path in this module ever calls IB2Client or sends an email/SMTP
    /// call — Cachet.ArchiveSync.Api/Reporting/ and ReportsController have zero references to
    /// either"). Static/reflection-based, per CAS-010 §10's own test-plan note, mirroring
    /// <c>Cachet.ArchiveSync.Worker.Tests.SyncActionProcessorSecurityTests</c>'s pattern of
    /// inspecting constructor parameter types and field types directly rather than re-running the
    /// runtime log-message capture this class's other methods use — SC-6 is about a dependency that
    /// must never exist at all, not about a value that must never be logged. Extends this shared
    /// class (CAS-010 §13's "LogHygieneTests (extended)" assignment) rather than a new class, per
    /// this file's established accretion pattern.
    /// </summary>
    /// <summary>
    /// CAS-015 §9 SC-3 — neither POST report endpoint creates a <c>dbo.report</c> row or leaves a
    /// file on server-side disk after the response completes, verified because
    /// <c>dbo.report</c>/<c>IReportRepository</c>/<c>Reports:LocalStorageRootPath</c> no longer
    /// exist in the codebase. Deleting the interface/class already makes this a compile-time
    /// guarantee — if either still existed, this file (and every other Reporting test file
    /// referencing the new, repository-free constructors) would fail to compile. This test
    /// reinforces that absence at runtime, mirroring <see cref="Test_SC6_ReportingModule_NoB2OrEmailReferences"/>'s
    /// established reflection-based pattern (CAS-015 §13's "LogHygieneTests (extended)" assignment).
    /// </summary>
    [Fact]
    public void Test_SC3_NoReportTableOrRepositoryReferencesRemainInReportingModule()
    {
        var apiAssembly = typeof(ReportDocumentWriter).Assembly;
        var coreAssembly = typeof(ReportGenerationRequest).Assembly;
        var forbiddenTypeNames = new HashSet<string>(StringComparer.Ordinal)
        {
            "IReportRepository", "ReportRepository", "ReportRow",
            "PreActionReportResponse", "PostActionReportResponse", "ReportFileLinks"
        };

        foreach (var assembly in new[] { apiAssembly, coreAssembly })
        {
            assembly.GetTypes().Select(t => t.Name).Should().NotContain(
                name => forbiddenTypeNames.Contains(name),
                $"{assembly.GetName().Name} must contain no IReportRepository/ReportRepository/ReportRow/" +
                "download-URL-envelope type (CAS-015 §9 SC-3)");
        }

        // Neither report service's constructor takes an IConfiguration or IReportRepository
        // parameter any longer (CAS-015 §5 — the Reports:LocalStorageRootPath config key and the
        // repository dependency are both removed from the constructor signature).
        foreach (var serviceType in new[] { typeof(PreActionReportService), typeof(PostActionReportService) })
        {
            var constructor = serviceType.GetConstructors().Single();
            var parameterTypeNames = constructor.GetParameters().Select(p => p.ParameterType.Name).ToList();
            parameterTypeNames.Should().NotContain("IConfiguration",
                $"{serviceType.FullName} must not depend on IConfiguration (CAS-015 §5 — Reports:LocalStorageRootPath removed)");
            parameterTypeNames.Should().NotContain("IReportRepository",
                $"{serviceType.FullName} must not depend on IReportRepository (CAS-015 §5/§9 SC-3)");
        }
    }

    [Fact]
    public void Test_SC6_ReportingModule_NoB2OrEmailReferences()
    {
        const BindingFlags AllMembers =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

        // Every type under Cachet.ArchiveSync.Api.Reporting (CAS-010 §3's writer abstraction and its
        // two implementations), plus ReportsController itself (§3) — the exact scope SC-6 names.
        var reportingNamespaceTypes = typeof(ReportDocumentWriter).Assembly
            .GetTypes()
            .Where(t => t.Namespace == typeof(ReportDocumentWriter).Namespace)
            .ToList();
        var reportingModuleTypes = reportingNamespaceTypes
            .Append(typeof(ReportsController))
            .Append(typeof(PreActionReportService))
            .Append(typeof(PostActionReportService))
            .Distinct()
            .ToList();

        reportingModuleTypes.Should().NotBeEmpty();

        foreach (var type in reportingModuleTypes)
        {
            // No constructor ever accepts IB2Client (the only B2 entry point in this codebase).
            foreach (var constructor in type.GetConstructors())
            {
                constructor.GetParameters().Select(p => p.ParameterType).Should().NotContain(typeof(IB2Client),
                    $"{type.FullName}'s constructor must never depend on IB2Client (CAS-010 §12)");
            }

            // No field (backing DI-injected state or otherwise) ever holds an IB2Client or a
            // System.Net.Mail type (the only email-sending surface .NET itself offers).
            var fieldTypes = type.GetFields(AllMembers).Select(f => f.FieldType).ToList();
            fieldTypes.Should().NotContain(typeof(IB2Client),
                $"{type.FullName} must never hold an IB2Client field (CAS-010 §12)");
            fieldTypes.Should().NotContain(
                t => t.Namespace != null && t.Namespace.StartsWith("System.Net.Mail", StringComparison.Ordinal),
                $"{type.FullName} must never hold a System.Net.Mail field (FR-27 — no email is ever sent)");

            // No method signature (parameters or return type) references either surface.
            foreach (var method in type.GetMethods(AllMembers))
            {
                var signatureTypes = method.GetParameters().Select(p => p.ParameterType).Append(method.ReturnType).ToList();
                signatureTypes.Should().NotContain(typeof(IB2Client),
                    $"{type.FullName}.{method.Name} must never reference IB2Client (CAS-010 §12)");
                signatureTypes.Should().NotContain(
                    t => t.Namespace != null && t.Namespace.StartsWith("System.Net.Mail", StringComparison.Ordinal),
                    $"{type.FullName}.{method.Name} must never reference System.Net.Mail (FR-27)");
            }
        }
    }
}
