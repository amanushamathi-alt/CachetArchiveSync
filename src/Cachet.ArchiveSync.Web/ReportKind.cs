namespace Cachet.ArchiveSync.Web;

/// <summary>
/// CAS-032 §3 — which report <c>ReportModal</c> is opened in: the Pre-Action Impact Report (FR-25)
/// or the Post-Action Outcome Report (FR-26). Used only by that component and its two host pages.
/// </summary>
public enum ReportKind
{
    PreAction,
    PostAction
}
