using System.Text;

namespace CodexSharp.Runtime;

public static class ClipboardImageStore
{
    public static string SavePng(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.Length == 0)
        {
            throw new ArgumentException("empty image", nameof(bytes));
        }

        var dir = Path.Combine(Path.GetTempPath(), "codexsharp-paste");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, Guid.NewGuid().ToString("N") + ".png");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    public static byte[]? AsBytes(object? data)
    {
        switch (data)
        {
            case byte[] bytes:
                return bytes;
            case MemoryStream ms:
                return ms.ToArray();
            case Stream stream:
                using (var copy = new MemoryStream())
                {
                    stream.CopyTo(copy);
                    return copy.ToArray();
                }
            case string s when s.StartsWith("data:image", StringComparison.OrdinalIgnoreCase):
                var comma = s.IndexOf(',');
                return comma > 0 ? Convert.FromBase64String(s[(comma + 1)..]) : null;
            default:
                return null;
        }
    }
}