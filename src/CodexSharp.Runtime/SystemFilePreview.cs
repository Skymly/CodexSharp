using System.Diagnostics;
using System.Text.RegularExpressions;

namespace CodexSharp.Runtime;

public sealed record PreviewResult(string Path, bool Opened, string? Error);

/// Open Office/PDF with the OS default app (ART-01). Never uploads.
public static class SystemFilePreview
{
    public static readonly string[] Extensions = [".xlsx", ".xls", ".xlsm", ".pdf", ".docx", ".doc", ".pptx", ".ppt"];

    public static bool IsOffice(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var ext = System.IO.Path.GetExtension(path);
        return Extensions.Contains(ext, StringComparer.OrdinalIgnoreCase);
    }

    public static IReadOnlyList<string> Extract(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        var hits = new List<string>();
        foreach (Match m in Regex.Matches(text, @"[A-Za-z]:\\[^\s""<>|*?]+\.(?:xlsx|xls|xlsm|pdf|docx|doc|pptx|ppt)|(?:\.{0,2}[\\/])?[^\s""<>|*?]+\.(?:xlsx|xls|xlsm|pdf|docx|doc|pptx|ppt)", RegexOptions.IgnoreCase))
        {
            var value = m.Value.Trim().Trim('"', '\'', '.', ',', ';');
            if (IsOffice(value) && !hits.Contains(value, StringComparer.OrdinalIgnoreCase))
            {
                hits.Add(value);
            }
        }

        return hits;
    }

    public static PreviewResult TryOpen(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return new PreviewResult("", false, "path is required");
        }

        string full;
        try
        {
            full = Path.IsPathRooted(path) ? Path.GetFullPath(path) : Path.GetFullPath(path);
        }
        catch (Exception ex)
        {
            return new PreviewResult(path, false, ex.Message);
        }

        if (!File.Exists(full))
        {
            return new PreviewResult(full, false, "file not found");
        }

        try
        {
            var psi = new ProcessStartInfo(full) { UseShellExecute = true };
            using var proc = Process.Start(psi);
            if (proc is null)
            {
                return new PreviewResult(full, false, "no default application");
            }

            return new PreviewResult(full, true, null);
        }
        catch (Exception ex)
        {
            return new PreviewResult(full, false, ex.Message);
        }
    }
}
