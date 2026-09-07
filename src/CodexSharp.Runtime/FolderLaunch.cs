using System.Diagnostics;

namespace CodexSharp.Runtime;

public static class FolderLaunch
{
    public static ProcessStartInfo ExplorerStartInfo(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("path is required", nameof(path));
        }

        var full = Path.GetFullPath(path);
        if (!Directory.Exists(full))
        {
            throw new DirectoryNotFoundException(full);
        }

        return new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = full,
            UseShellExecute = true,
        };
    }
}
