using System.Collections.ObjectModel;

namespace CodexSharp.Runtime;

public sealed record HonestCapability(string Id, string Label, string Status, string Detail);

/// Capabilities that exist as protocol surface but must not be presented as enabled.
public static class HonestStubs
{
    public static readonly HonestCapability[] Capabilities =
    [
        new("computer_use", "Computer Use", "notConfigured", "No OS computer-use agent."),
        new("browser_use", "Browser Use", "notConfigured", "No in-app browser."),
        new("realtime", "Voice / realtime", "notConfigured", "No GPT-Live or WebRTC session."),
        new("remote", "Remote", "notConfigured", "No remote-control transport."),
        new("ssh_remote", "SSH remote project", "underDevelopment", "SSH tunnel is underDevelopment. Not paired."),
        new("mcp_oauth", "MCP OAuth", "notConfigured", "No official connector cloud OAuth. Use HTTP MCP bearer env."),
        new("cloud", "Cloud thread / worktree", "notConfigured", "No Codex Cloud backend."),
        new("credits", "Credits", "notConfigured", "No ChatGPT usage credits."),
        new("code_mode_host", "code_mode_host", "underDevelopment", "No standalone JS code-mode host."),
        new("guardian", "Guardian", "underDevelopment", "No Guardian review."),
        new("windowsSandbox", "windowsSandbox", "notConfigured", "No elevated Windows sandbox."),
    ];

    public static readonly ReadOnlyCollection<string> LockedFeatures = new(
    [
        "computer_use",
        "browser_use",
        "realtime",
        "guardian",
        "code_mode_host",
    ]);

    public static bool IsLockedFeature(string name) =>
        LockedFeatures.Contains(name, StringComparer.OrdinalIgnoreCase);

    public static bool ForbidsEnable(string name) => IsLockedFeature(name);

    public static bool IsTogglableFeature(string name) =>
        !string.IsNullOrWhiteSpace(name) && !IsLockedFeature(name);

    public static string StatusOf(string id)
    {
        var hit = Capabilities.FirstOrDefault(c => c.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        return hit?.Status ?? "notConfigured";
    }

    public static string CloudSlashMessage() =>
        "notConfigured  Codex Cloud is not available in CodexSharp. /cloud and /cloud-environment have no remote backend.";

    public static string PetOverlayMessage() =>
        "notConfigured  CodexSharp has no pet overlay (VPM-03 WON'T). Use /pets for a local TUI ascii pet only.";

    public static string WorkLayoutMessage() =>
        "交付物 (local). Cloud Work is notConfigured — not connected.";
}
