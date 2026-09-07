namespace CodexSharp.Runtime;

public sealed record BedrockAwsProfile(string Name, string? Region);
public sealed record BedrockEnvironmentCredential(string Type, string? Region);
public sealed record BedrockDiscoverResult(
    IReadOnlyList<BedrockAwsProfile> Profiles,
    IReadOnlyList<BedrockEnvironmentCredential> EnvironmentCredentials);

/// <summary>
/// Honest AWS profile/env discovery for account/bedrock/discover.
/// Never returns secret values — only profile names, credential kinds, and regions.
/// </summary>
public static class BedrockDiscover
{
    public static BedrockDiscoverResult Scan()
    {
        var profiles = new Dictionary<string, BedrockAwsProfile>(StringComparer.OrdinalIgnoreCase);
        var envCreds = new List<BedrockEnvironmentCredential>();

        foreach (var (name, region) in ReadConfigRegions())
        {
            profiles[name] = new BedrockAwsProfile(name, region);
        }

        foreach (var name in ReadCredentialProfiles())
        {
            if (!profiles.ContainsKey(name))
            {
                profiles[name] = new BedrockAwsProfile(name, null);
            }
        }

        var envRegion = FirstNonEmpty(
            Environment.GetEnvironmentVariable("AWS_REGION"),
            Environment.GetEnvironmentVariable("AWS_DEFAULT_REGION"));

        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AWS_ACCESS_KEY_ID")))
        {
            envCreds.Add(new BedrockEnvironmentCredential("accessKeys", envRegion));
        }

        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AWS_BEARER_TOKEN_BEDROCK"))
            || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("BEDROCK_API_KEY")))
        {
            envCreds.Add(new BedrockEnvironmentCredential("bedrockApiKey", envRegion));
        }

        return new BedrockDiscoverResult(profiles.Values.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToArray(), envCreds);
    }

    private static IEnumerable<string> ReadCredentialProfiles()
    {
        var path = FirstNonEmpty(
            Environment.GetEnvironmentVariable("AWS_SHARED_CREDENTIALS_FILE"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".aws", "credentials"));
        return ReadIniSections(path);
    }

    private static IEnumerable<(string Name, string? Region)> ReadConfigRegions()
    {
        var path = FirstNonEmpty(
            Environment.GetEnvironmentVariable("AWS_CONFIG_FILE"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".aws", "config"));
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            yield break;
        }

        string? section = null;
        string? region = null;
        foreach (var raw in File.ReadLines(path))
        {
            var line = StripComment(raw).Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                if (section is not null)
                {
                    yield return (section, region);
                }

                section = NormalizeConfigSection(line[1..^1].Trim());
                region = null;
                continue;
            }

            var eq = line.IndexOf('=');
            if (eq <= 0 || section is null) continue;
            var key = line[..eq].Trim();
            var value = line[(eq + 1)..].Trim();
            if (key.Equals("region", StringComparison.OrdinalIgnoreCase) && value.Length > 0)
            {
                region = value;
            }
        }

        if (section is not null)
        {
            yield return (section, region);
        }
    }

    private static IEnumerable<string> ReadIniSections(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            yield break;
        }

        foreach (var raw in File.ReadLines(path))
        {
            var line = StripComment(raw).Trim();
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                var name = line[1..^1].Trim();
                if (name.Length > 0) yield return name;
            }
        }
    }

    private static string NormalizeConfigSection(string name)
    {
        const string prefix = "profile ";
        return name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? name[prefix.Length..].Trim() : name;
    }

    private static string StripComment(string line)
    {
        var hash = line.IndexOf('#');
        return hash >= 0 ? line[..hash] : line;
    }

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
}

