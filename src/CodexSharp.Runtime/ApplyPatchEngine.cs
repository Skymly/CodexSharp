using System.Text;
using System.Text.RegularExpressions;

namespace CodexSharp.Runtime;

public sealed record PatchResult(bool Ok, string Output);

public static class ApplyPatchEngine
{
    public static PatchResult Apply(string patch, WorkspaceSandbox sandbox, Action<string>? onFile = null)
    {
        var normalized = patch.Replace("\r\n", "\n");
        if (!normalized.Contains("*** Begin Patch", StringComparison.Ordinal) ||
            !normalized.Contains("*** End Patch", StringComparison.Ordinal))
        {
            return new PatchResult(false, "Patch must be wrapped in *** Begin Patch / *** End Patch.");
        }

        var body = Between(normalized, "*** Begin Patch", "*** End Patch");
        var blocks = Regex.Split(body, @"(?=^\*\*\* (?:Add File|Update File|Delete File):)", RegexOptions.Multiline)
            .Select(b => b.Trim('\n', ' ', '\t'))
            .Where(b => b.Length > 0)
            .ToArray();

        if (blocks.Length == 0)
        {
            return new PatchResult(false, "Patch contained no file operations.");
        }

        var log = new StringBuilder();
        foreach (var block in blocks)
        {
            if (block.StartsWith("*** Add File:", StringComparison.Ordinal))
            {
                var path = HeaderPath(block, "*** Add File:");
                sandbox.EnsureWritable(path);
                                var content = string.Join(Environment.NewLine, Lines(block).Skip(1).Select(StripPlus));
                var full = sandbox.Resolve(path);
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                File.WriteAllText(full, content + Environment.NewLine);
                log.AppendLine($"A {path}");
                onFile?.Invoke("A " + path + Environment.NewLine);
            }
            else if (block.StartsWith("*** Delete File:", StringComparison.Ordinal))
            {
                var path = HeaderPath(block, "*** Delete File:");
                sandbox.EnsureWritable(path);
                var full = sandbox.Resolve(path);
                if (File.Exists(full))
                {
                    File.Delete(full);
                    log.AppendLine($"D {path}");
                onFile?.Invoke("D " + path + Environment.NewLine);
                }
                else
                {
                    return new PatchResult(false, $"Cannot delete missing file: {path}");
                }
            }
            else if (block.StartsWith("*** Update File:", StringComparison.Ordinal))
            {
                var path = HeaderPath(block, "*** Update File:");
                sandbox.EnsureWritable(path);
                var full = sandbox.Resolve(path);
                if (!File.Exists(full))
                {
                    return new PatchResult(false, $"Cannot update missing file: {path}");
                }

                var original = File.ReadAllText(full).Replace("\r\n", "\n");
                var updated = ApplyUpdate(original, string.Join('\n', Lines(block).Skip(1)));
                if (updated is null)
                {
                    return new PatchResult(false, $"Failed to apply hunks to {path}");
                }

                File.WriteAllText(full, updated.Replace("\n", Environment.NewLine));
                var moveLine = Lines(block).FirstOrDefault(l => l.StartsWith("*** Move to: ", StringComparison.Ordinal));
                if (moveLine is not null)
                {
                    var dest = moveLine["*** Move to: ".Length..].Trim();
                    sandbox.EnsureWritable(dest);
                    var destFull = sandbox.Resolve(dest);
                    Directory.CreateDirectory(Path.GetDirectoryName(destFull)!);
                    File.Move(full, destFull, overwrite: true);
                    log.AppendLine($"R {path} -> {dest}");
                    onFile?.Invoke($"R {path} -> {dest}" + Environment.NewLine);
                }
                else
                {
                    log.AppendLine($"M {path}");
                    onFile?.Invoke($"M {path}" + Environment.NewLine);
                }
            }
            else
            {
                return new PatchResult(false, $"Unknown patch operation:\n{block.Split('\n')[0]}");
            }
        }

        return new PatchResult(true, log.ToString().Trim());
    }

    private static string? ApplyUpdate(string original, string hunkBody)
    {
        var lines = original.Split('\n').ToList();
        var hunks = Regex.Split(hunkBody, @"(?=^@@)", RegexOptions.Multiline)
            .Select(h => h.Trim('\n'))
            .Where(h => h.Length > 0)
            .ToArray();

        if (hunks.Length == 0)
        {
            hunks = [hunkBody.Trim('\n')];
        }

        foreach (var hunk in hunks)
        {
            var oldLines = new List<string>();
            var newLines = new List<string>();
            foreach (var line in hunk.Split('\n'))
            {
                if (line.StartsWith("@@", StringComparison.Ordinal) || line.StartsWith("***", StringComparison.Ordinal) || line.Length == 0)
                {
                    continue;
                }

                if (line.StartsWith('+'))
                {
                    newLines.Add(line[1..]);
                }
                else if (line.StartsWith('-'))
                {
                    oldLines.Add(line[1..]);
                }
                else
                {
                    var context = line.StartsWith(' ') ? line[1..] : line;
                    oldLines.Add(context);
                    newLines.Add(context);
                }
            }

            if (oldLines.Count == 0)
            {
                lines.AddRange(newLines);
                continue;
            }

            var index = IndexOfSequence(lines, oldLines);
            if (index < 0)
            {
                return null;
            }

            lines.RemoveRange(index, oldLines.Count);
            lines.InsertRange(index, newLines);
        }

        return string.Join('\n', lines);
    }

    private static int IndexOfSequence(List<string> haystack, List<string> needle)
    {
        for (var i = 0; i <= haystack.Count - needle.Count; i++)
        {
            var ok = true;
            for (var j = 0; j < needle.Count; j++)
            {
                if (!haystack[i + j].Equals(needle[j], StringComparison.Ordinal))
                {
                    ok = false;
                    break;
                }
            }

            if (ok)
            {
                return i;
            }
        }

        return -1;
    }

    private static string HeaderPath(string block, string header)
    {
        var first = block.Split('\n')[0];
        return first[header.Length..].Trim();
    }

    private static IEnumerable<string> Lines(string block) =>
        block.Replace("\r\n", "\n").Split('\n');

    private static string StripPlus(string line) =>
        line.StartsWith('+') ? line[1..] : line;

    private static string Between(string text, string start, string end)
    {
        var a = text.IndexOf(start, StringComparison.Ordinal);
        var b = text.LastIndexOf(end, StringComparison.Ordinal);
        if (a < 0 || b < 0 || b <= a)
        {
            return "";
        }

        return text[(a + start.Length)..b].Trim('\n');
    }
}

