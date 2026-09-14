namespace Memento.Core.Storage;

/// <summary>Rejects filesystem paths that could redirect archive I/O through a reparse point.</summary>
internal static class ArchivePathSafety
{
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
