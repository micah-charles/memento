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
            if (File.Exists(current) || Directory.Exists(current))
            {
                try
                {
                    if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                        throw new IOException($"{description} cannot contain a reparse point.");
                }
                catch (UnauthorizedAccessException error)
                {
                    throw new IOException($"{description} cannot be inspected safely.", error);
                }
            }

            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent) || string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
                break;
            current = parent;
        }
    }
}
