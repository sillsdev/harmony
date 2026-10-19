using SIL.Harmony.Config;

namespace SIL.Harmony.Resource;

/// <summary>
/// Converts between the absolute paths callers see and the form stored in the LocalResource table.
/// With <see cref="HarmonyConfig.StoreLocalResourcePathsRelativeToCache"/> on, files under the cache directory are
/// stored relative to it (always with '/' separators) so the cache can move between runs.
/// </summary>
internal static class LocalResourcePaths
{
    /// <summary>
    /// absolute, normalized path for a caller or download supplied path. Relative input resolves against the cache
    /// directory, never the working directory.
    /// </summary>
    public static string Normalize(HarmonyConfig config, string path)
    {
        return Path.IsPathRooted(path)
            ? Path.GetFullPath(path)
            : Path.GetFullPath(Path.Combine(config.LocalResourceCachePath, path));
    }

    public static string ToStored(HarmonyConfig config, string absolutePath)
    {
        if (!config.StoreLocalResourcePathsRelativeToCache) return absolutePath;
        var root = Path.GetFullPath(config.LocalResourceCachePath);
        var relative = Path.GetRelativePath(root, absolutePath);
        if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar))
            return absolutePath;
        return relative.Replace(Path.DirectorySeparatorChar, '/');
    }

    /// <summary>
    /// resolves regardless of the flag, so rows written while it was on still resolve after it is turned off
    /// </summary>
    public static string FromStored(HarmonyConfig config, string storedPath)
    {
        if (Path.IsPathRooted(storedPath)) return storedPath;
        return Path.GetFullPath(Path.Combine(config.LocalResourceCachePath, storedPath.Replace('/', Path.DirectorySeparatorChar)));
    }
}
