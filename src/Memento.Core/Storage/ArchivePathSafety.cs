namespace Memento.Core.Storage;

/// <summary>Rejects filesystem paths that could redirect archive I/O through a reparse point.</summary>
internal static class ArchivePathSafety
{
    public static string GetArchiveRoot(SqliteArchive archive)
    {
        ArgumentNullException.ThrowIfNull(archive);
        var dataDirectory = Path.GetDirectoryName(archive.DatabasePath);
        return Path.GetDirectoryName(dataDirectory ?? archive.DatabasePath)
            ?? dataDirectory
            ?? Path.GetDirectoryName(archive.DatabasePath)
            ?? Path.GetPathRoot(archive.DatabasePath)
            ?? throw new InvalidOperationException("The archive database has no usable root directory.");
    }

    public static bool IsPathUnderRoot(string path, string root)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(root)) return false;
        try
        {
            var fullPath = Path.GetFullPath(path);
            var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return string.Equals(fullPath, fullRoot, StringComparison.OrdinalIgnoreCase)
                || fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || fullPath.StartsWith(fullRoot + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    public static void EnsureNoReparsePointInPath(string path, string description)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("A filesystem path is required.", nameof(path));

        var current = Path.GetFullPath(path);
        while (!string.IsNullOrEmpty(current))
        {
            try
            {
                // GetAttributes can inspect a dangling link itself, unlike
                // File.Exists/Directory.Exists which report the target state.
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException($"{description} cannot contain a reparse point.");
            }
            catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
            {
                // A missing path is safe to skip; its existing parents are
                // still checked on the way to the filesystem root.
            }
            catch (UnauthorizedAccessException error)
            {
                throw new IOException($"{description} cannot be inspected safely.", error);
            }

            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent) || string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
                break;
            current = parent;
        }
    }
}
