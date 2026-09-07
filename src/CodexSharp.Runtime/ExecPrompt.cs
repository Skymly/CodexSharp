using System.Text;

namespace CodexSharp.Runtime;

public static class ExecPrompt
{
    public static string Compose(string? prompt, IReadOnlyList<string>? images, string? schemaJson)
    {
        var sb = new StringBuilder(prompt ?? "");
        if (images is not null)
        {
            foreach (var image in images)
            {
                if (string.IsNullOrWhiteSpace(image)) continue;
                if (sb.Length > 0) sb.AppendLine();
                sb.Append(ImageMessageContent.Marker).Append(Path.GetFullPath(image));
            }
        }

        if (!string.IsNullOrWhiteSpace(schemaJson))
        {
            if (sb.Length > 0) sb.AppendLine();
            sb.AppendLine("Respond with JSON that matches this schema:");
            sb.Append(schemaJson);
        }

        return sb.ToString();
    }
}
