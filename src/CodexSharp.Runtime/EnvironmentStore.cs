namespace CodexSharp.Runtime;

public sealed record EnvironmentRecord(string EnvironmentId, string ExecServerUrl);

public static class EnvironmentStore
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, EnvironmentRecord> Remote = new(StringComparer.Ordinal);

    public const string LocalId = "local";

    public static void Add(string environmentId, string execServerUrl)
    {
        if (string.IsNullOrWhiteSpace(environmentId)) throw new ArgumentException("environmentId is required");
        if (string.IsNullOrWhiteSpace(execServerUrl)) throw new ArgumentException("execServerUrl is required");
        if (string.Equals(environmentId, LocalId, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("local environment cannot be added as remote");
        lock (Gate)
        {
            Remote[environmentId] = new EnvironmentRecord(environmentId, execServerUrl.Trim());
        }
    }

    public static (string Status, string? Error) Status(string environmentId)
    {
        if (string.IsNullOrWhiteSpace(environmentId) || string.Equals(environmentId, LocalId, StringComparison.OrdinalIgnoreCase))
            return ("ready", null);
        lock (Gate)
        {
            if (!Remote.ContainsKey(environmentId))
                return ("unknown", "environment is not configured");
            return ("pending", "CodexSharp has no remote exec-server transport");
        }
    }

    public static EnvironmentRecord? Get(string environmentId)
    {
        lock (Gate) return Remote.TryGetValue(environmentId, out var rec) ? rec : null;
    }
}
