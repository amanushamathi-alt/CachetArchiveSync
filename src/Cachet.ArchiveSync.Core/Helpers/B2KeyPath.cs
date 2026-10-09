namespace Cachet.ArchiveSync.Core.Helpers;

/// <summary>
/// CAS-053 Q-C — joins a bucket's <c>b2_path_prefix</c> and a root-relative folder into the full B2 key
/// path stored in <c>file_index.relative_path</c>. The prefix is never empty in production (no
/// empty-prefix branch); the bucket root (empty folder) maps to the prefix itself.
/// </summary>
public static class B2KeyPath
{
    public static string Combine(string prefix, string folder) =>
        folder.Length == 0 ? prefix : prefix + "/" + folder;
}
