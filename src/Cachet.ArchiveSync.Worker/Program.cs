using Cachet.ArchiveSync.Api.B2;
using Cachet.ArchiveSync.Api.Content;
using Cachet.ArchiveSync.Api.Repositories;
using Cachet.ArchiveSync.Api.Services;
using Cachet.ArchiveSync.Api.Storage;
using Cachet.ArchiveSync.Core.Configuration;
using Cachet.ArchiveSync.Core.Interfaces;
using Cachet.ArchiveSync.Worker;
using Cachet.ArchiveSync.Worker.Extraction;
using Serilog;

// Child-process mode: this same executable is re-launched to run document extraction in isolation
// (see ExtractorHost). Must run before any host/logging/DB setup — the child does none of that.
if (args.Contains(ExtractorHost.Argument))
{
    return ExtractorHost.Run(Console.OpenStandardInput(), Console.OpenStandardOutput());
}

var builder = Host.CreateApplicationBuilder(args);

// CAS-041 §2: B2 system settings from dbo.configuration_detail, loaded once at startup (reuses Api's
// loader via the existing project reference). Added last, so a non-blank DB value overrides the
// environment variable; a missing/blank row falls back to it.
builder.Configuration.Sources.Add(new DatabaseConfigurationSource
{
    ConnectionString = builder.Configuration.GetConnectionString("CachetArchiveSync"),
    Enabled = builder.Configuration.GetValue<bool?>("ConfigurationDatabase:Enabled") ?? true,
    LoadGroup = ConfigurationDetailLoader.Load,
    AllowedNamesByGroup = new Dictionary<string, IReadOnlyCollection<string>>
    {
        ["B2_Keys"] = new[] { "B2__SystemKeyId", "B2__SystemApplicationKey", "B2__BucketName" }
    }
});

// Rolling file log: sink path, retention and levels are read from the "Serilog" section of
// appsettings.json. Extracted text, file content and search terms are never logged (CAS-012 §11);
// processors log only the action, file name/path and exceptions. dispose: true flushes on shutdown.
Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(builder.Configuration)
    .CreateLogger();
builder.Logging.AddSerilog(Log.Logger, dispose: true);

// Last-chance record of a managed exception that escapes every handler (a native crash, stack
// overflow or out-of-memory kill cannot be caught or logged from managed code).
AppDomain.CurrentDomain.UnhandledException += (_, e) =>
{
    Log.Fatal(e.ExceptionObject as Exception, "Unhandled exception; process terminating: {IsTerminating}", e.IsTerminating);
    Log.CloseAndFlush();
};

builder.Services.AddHostedService<Worker>();

// CAS-028 §3.6: Windows Service hosting (no-op when run as a console app or under a test host);
// Worker options bound and validated on start (§9, V1/V2) so an invalid value stops the service.
builder.Services.AddWindowsService(o => o.ServiceName = "Cachet Archive Sync Worker");
// AddWindowsService also registers the Windows Event Log logger. That provider throws when the event
// log cannot be written (e.g. "Not enough memory resources"), and Microsoft.Extensions.Logging rethrows
// provider failures into the calling code - which failed whole searches. The Serilog file is the log;
// Worker:UseWindowsEventLog=true restores the Event Log provider.
if (!(builder.Configuration.GetValue<bool?>("Worker:UseWindowsEventLog") ?? false))
{
    foreach (var descriptor in builder.Services
        .Where(d => d.ServiceType == typeof(ILoggerProvider)
            && d.ImplementationType?.FullName == "Microsoft.Extensions.Logging.EventLog.EventLogLoggerProvider")
        .ToList())
    {
        builder.Services.Remove(descriptor);
    }
}
builder.Services.AddOptions<WorkerOptions>()
    .Bind(builder.Configuration.GetSection(WorkerOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();
// CAS-028 §3.7: single-instance lock — singleton, since it owns the one dedicated non-pooled
// connection whose session holds the lock for the process lifetime.
builder.Services.AddSingleton<IWorkerInstanceLock, SqlWorkerInstanceLock>();

// CAS-005 §3/§17: registration only — no Worker job calls this yet (first caller is CAS-001).
builder.Services.AddScoped<IAuditLogRepository, AuditLogRepository>();
builder.Services.AddScoped<IAuditLogService, AuditLogService>();

// CAS-006 §3/§8/§9: KeywordSearch task processor + the general claim-loop's repositories. Reuses
// Api's Dapper repository implementations via the existing direct project reference (same
// convention as the IAuditLogRepository/IAuditLogService registration above) rather than
// duplicating the same repository class in both projects.
builder.Services.AddScoped<ISearchResultRepository, SearchResultRepository>();
builder.Services.AddScoped<ITaskQueueRepository, TaskQueueRepository>();
builder.Services.AddScoped<IFileIndexRepository, FileIndexRepository>();
// CAS-012 §2/§5: filesystem-based Content Match reads/matching, called from
// KeywordSearchTaskProcessor for each Content candidate before any B2 cross-check.
builder.Services.AddScoped<IBucketMirrorRootRepository, BucketMirrorRootRepository>();
builder.Services.AddScoped<IMirrorFileReader, MirrorFileReader>();
// Extraction runs in child processes so a parser crash/hang on one file cannot stop the service.
builder.Services.AddSingleton<ExtractorHostPool>();
builder.Services.AddScoped<IFileContentMatcher, OutOfProcessFileContentMatcher>();
// CAS-029 §3.4: per-task progress reporter factory (scoped, over the scope's
// ITaskQueueRepository) and the TimeProvider its throttle uses.
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<TaskProgressReporterFactory>();
builder.Services.AddScoped<IWorkerTaskProcessor, KeywordSearchTaskProcessor>();

// CAS-007 §3/§9: ContentScan + SyncCopy/SyncUpdate/SyncPurge task processors and their
// repositories. IODriveFileReader is the local-mirror-read abstraction SyncCopy/SyncUpdate need.
// CAS-013 §2/§5: ODriveFileReader now resolves its root per-bucket via
// IBucketRootMappingRepository (dbo.odrive_bucket_mapping) instead of a single flat config key —
// registered here too (same cross-project reuse convention as IBucketMirrorRootRepository above),
// since ODriveFileReader is constructed in this process, not only in Api.
builder.Services.AddScoped<ISyncResultRepository, SyncResultRepository>();
builder.Services.AddScoped<IBucketRootMappingRepository, BucketRootMappingRepository>();
builder.Services.AddScoped<IODriveFileReader, ODriveFileReader>();
builder.Services.AddScoped<IWorkerTaskProcessor, ContentScanTaskProcessor>();
builder.Services.AddScoped<IWorkerTaskProcessor, SyncCopyTaskProcessor>();
builder.Services.AddScoped<IWorkerTaskProcessor, SyncUpdateTaskProcessor>();
// CAS-062: Purge from B2 also removes the file's local B2 mirror copy.
builder.Services.AddScoped<IB2MirrorFileRemover, B2MirrorFileRemover>();
builder.Services.AddScoped<IWorkerTaskProcessor, SyncPurgeTaskProcessor>();

// CAS-037 §6/§9: B2 mirror population task processor. Reuses the already-registered
// IFileIndexRepository/ITaskQueueRepository/TaskProgressReporterFactory/IB2Client/
// IBucketMirrorRootRepository/IAuditLogService above — no new repository/service registrations
// needed for this processor.
// CAS-059: limits B2MirrorSync to the mapping's b2_path_prefix (same class the Api uses).
builder.Services.AddScoped<IB2PathPrefixProvider, B2PathPrefixProvider>();
builder.Services.AddScoped<IWorkerTaskProcessor, B2MirrorSyncTaskProcessor>();

// CAS-001 §6.1/§17: same B2Client implementation as Api (direct project reference, not a shared
// Infrastructure project — P4). Singleton for the same per-process token-cache reason as Api's
// registration; the Worker is the eventual caller for mirror population (§6.2) and delete (§6.4),
// both out of scope for this run — registration only, no job calls this yet.
builder.Services.AddHttpClient("B2Client");
builder.Services.AddSingleton<IB2Client>(sp =>
{
    var httpClient = sp.GetRequiredService<IHttpClientFactory>().CreateClient("B2Client");
    return new B2Client(httpClient, sp.GetRequiredService<IConfiguration>(), sp.GetRequiredService<ILogger<B2Client>>());
});

var host = builder.Build();
host.Run();
return 0;
