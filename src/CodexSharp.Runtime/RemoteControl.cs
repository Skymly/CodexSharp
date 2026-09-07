using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CodexSharp.Runtime;

public sealed record RemoteControlSnapshot(string Status, string InstallationId, string ServerName);

public sealed record RemoteControlPairing(
    string PairingCode,
    string? ManualPairingCode,
    string EnvironmentId,
    long ExpiresAt);

public sealed record RemoteControlClientInfo(
    string ClientId,
    string? DisplayName,
    string? DeviceType,
    string? Platform,
    string? OsVersion,
    string? DeviceModel,
    string? AppVersion,
    long? LastSeenAt);

public static class RemoteControlState
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, RemoteControlPairing> Pairings = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, List<RemoteControlClientInfo>> Clients = new(StringComparer.OrdinalIgnoreCase);

    private static string FilePath => Path.Combine(CodexPaths.Home, "remote-control.json");

    public static RemoteControlSnapshot Read()
    {
        lock (Gate)
        {
            var enabled = false;
            try
            {
                if (File.Exists(FilePath))
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(FilePath));
                    enabled = doc.RootElement.TryGetProperty("enabled", out var e) && e.ValueKind == JsonValueKind.True;
                }
            }
            catch { /* ignore */ }

            var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(CodexPaths.Home))).ToLowerInvariant()[..12];
            // CodexSharp has no remote-control transport; enabling reports errored instead of connected.
            var status = enabled ? "errored" : "disabled";
            return new RemoteControlSnapshot(status, "inst_" + id, "codexsharp");
        }
    }

    public static RemoteControlSnapshot SetEnabled(bool enabled)
    {
        lock (Gate)
        {
            CodexPaths.EnsureLayout();
            File.WriteAllText(FilePath, JsonSerializer.Serialize(new { enabled }));
            if (!enabled)
            {
                Pairings.Remove(CodexPaths.Home);
                Clients.Remove(CodexPaths.Home);
            }

            return Read();
        }
    }

    public static RemoteControlPairing StartPairing(bool manualCode)
    {
        lock (Gate)
        {
            var snap = Read();
            var envId = "env_" + snap.InstallationId;
            var pairingCode = NewCode(8);
            string? manual = manualCode ? $"{NewCode(4)}-{NewCode(4)}" : null;
            var expires = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 300;
            var pairing = new RemoteControlPairing(pairingCode, manual, envId, expires);
            Pairings[CodexPaths.Home] = pairing;
            return pairing;
        }
    }

    public static bool PairingClaimed(string? pairingCode, string? manualPairingCode)
    {
        lock (Gate)
        {
            var hasPairing = !string.IsNullOrWhiteSpace(pairingCode);
            var hasManual = !string.IsNullOrWhiteSpace(manualPairingCode);
            if (hasPairing == hasManual)
            {
                throw new ArgumentException(hasPairing
                    ? "remoteControl/pairing/status accepts either pairingCode or manualPairingCode, not both"
                    : "remoteControl/pairing/status requires pairingCode or manualPairingCode");
            }

            if (!Pairings.TryGetValue(CodexPaths.Home, out var pairing))
            {
                throw new ArgumentException("pairing code is unknown");
            }

            var match = hasPairing
                ? string.Equals(pairing.PairingCode, pairingCode, StringComparison.Ordinal)
                : string.Equals(pairing.ManualPairingCode, manualPairingCode, StringComparison.Ordinal);
            if (!match)
            {
                throw new ArgumentException("pairing code is unknown");
            }

            // No remote client can claim a pairing without a transport.
            return false;
        }
    }

    public static IReadOnlyList<RemoteControlClientInfo> ListClients(string environmentId)
    {
        if (string.IsNullOrWhiteSpace(environmentId))
        {
            throw new ArgumentException("environmentId is required");
        }

        lock (Gate)
        {
            return Clients.TryGetValue(CodexPaths.Home, out var list) ? list.ToArray() : [];
        }
    }

    public static void RevokeClient(string environmentId, string clientId)
    {
        if (string.IsNullOrWhiteSpace(environmentId))
        {
            throw new ArgumentException("environmentId is required");
        }

        if (string.IsNullOrWhiteSpace(clientId))
        {
            throw new ArgumentException("clientId is required");
        }

        lock (Gate)
        {
            if (!Clients.TryGetValue(CodexPaths.Home, out var list) || list.RemoveAll(c => c.ClientId == clientId) == 0)
            {
                throw new KeyNotFoundException("remote-control client not found: " + clientId);
            }
        }
    }

    private static string NewCode(int length)
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        Span<byte> bytes = stackalloc byte[length];
        RandomNumberGenerator.Fill(bytes);
        var chars = new char[length];
        for (var i = 0; i < length; i++)
        {
            chars[i] = alphabet[bytes[i] % alphabet.Length];
        }

        return new string(chars);
    }
}
