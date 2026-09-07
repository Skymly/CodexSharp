using System.Diagnostics;

namespace CodexSharp.Runtime;

/// Best-effort clipboard read for TUI paste. Windows uses PowerShell; other OS return null.
/// Does not claim a connected clipboard service when the host has none.
public static class ClipboardPaste
{
    public static string? GetText(int timeoutMs = 1500)
    {
        if (!OperatingSystem.IsWindows()) return null;
        var text = RunPowerShell("Get-Clipboard -Raw", timeoutMs, sta: false);
        return string.IsNullOrEmpty(text) ? null : text.Replace("\r\n", "\n");
    }

    public static byte[]? GetPng(int timeoutMs = 2500)
    {
        if (!OperatingSystem.IsWindows()) return null;
        const string script = """
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
$img = [System.Windows.Forms.Clipboard]::GetImage()
if ($null -eq $img) { exit 2 }
$ms = New-Object System.IO.MemoryStream
$img.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
[Convert]::ToBase64String($ms.ToArray())
""";
        var b64 = RunPowerShell(script, timeoutMs, sta: true);
        if (string.IsNullOrWhiteSpace(b64)) return null;
        try { return Convert.FromBase64String(b64.Trim()); }
        catch { return null; }
    }

    private static string? RunPowerShell(string command, int timeoutMs, bool sta)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            if (sta) psi.ArgumentList.Add("-STA");
            psi.ArgumentList.Add("-NoLogo");
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-Command");
            psi.ArgumentList.Add(command);
            using var process = Process.Start(psi);
            if (process is null) return null;
            if (!process.WaitForExit(timeoutMs))
            {
                try { process.Kill(true); } catch { /* ignore */ }
                return null;
            }

            var output = process.StandardOutput.ReadToEnd();
            return process.ExitCode == 0 ? output : null;
        }
        catch
        {
            return null;
        }
    }
}
