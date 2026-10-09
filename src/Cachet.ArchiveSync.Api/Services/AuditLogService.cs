using Cachet.ArchiveSync.Core.Dtos;
using Cachet.ArchiveSync.Core.Interfaces;

namespace Cachet.ArchiveSync.Api.Services;

/// <summary>
/// Thin wrapper over <see cref="IAuditLogRepository"/> — the call surface other modules use
/// (CAS-005 §3). Implements §7 V1 (tab_module fail-fast validation) and §11 telemetry. The
/// repository call is awaited synchronously, never fire-and-forget (CAS-005 §12).
/// </summary>
public sealed class AuditLogService : IAuditLogService
{
    // CK_audit_log_tab_module's fixed value set (CAS-003). Scoped to this service boundary per
    // CAS-005 §7 V1 — not a Core enum, since no other caller needs a TabModule type yet (P4).
    private static readonly HashSet<string> ValidTabModules = new(StringComparer.Ordinal)
    {
        "Auth", "Keyword Search", "O-Drive Sync", "Quarantine", "Task Manager", "Audit Log", "Reports"
    };

    private readonly IAuditLogRepository _repository;
    private readonly ILogger<AuditLogService> _logger;

    public AuditLogService(IAuditLogRepository repository, ILogger<AuditLogService> logger)
    {
        _repository = repository;
        _logger = logger;
    }

    public async Task<int> WriteAsync(AuditLogEntry entry, CancellationToken ct)
    {
        if (!ValidTabModules.Contains(entry.TabModule))
        {
            throw new ArgumentException(
                $"'{entry.TabModule}' is not a recognized tab_module value.", nameof(entry));
        }

        _logger.LogInformation(
            "Audit row write attempt: {Action} / {Result} for user {UserId}, tab {TabModule}",
            entry.Action, entry.Result, entry.UserId, entry.TabModule);

        try
        {
            var auditLogId = await _repository.InsertAsync(entry, ct);

            _logger.LogInformation(
                "Audit row written: {Action} / {Result} for user {UserId}, tab {TabModule}, audit_log_id {AuditLogId}",
                entry.Action, entry.Result, entry.UserId, entry.TabModule, auditLogId);

            return auditLogId;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Audit log write failed for {Action} / user {UserId} — caller must decide whether to fail the parent operation",
                entry.Action, entry.UserId);
            throw;
        }
    }
}
