namespace CodexSharp.AppServer;

public static class ItemsView
{
    public static string Text(string content, string? view, int max = 200)
    {
        content ??= "";
        if (string.Equals(view, "full", StringComparison.OrdinalIgnoreCase) || content.Length <= max)
        {
            return content;
        }

        return content[..max] + "…";
    }
}
