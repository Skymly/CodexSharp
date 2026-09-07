using System.Collections.Generic;

namespace CodexSharp.AppServer;

public static class TimelinePaging
{
    public static (IReadOnlyList<T> Page, string? NextCursor, string? BackwardsCursor) Slice<T>(
        IReadOnlyList<T> data,
        string? cursor,
        int limit,
        bool descending = false)
    {
        if (limit < 1)
        {
            limit = 50;
        }

        if (limit > 200)
        {
            limit = 200;
        }

        IReadOnlyList<T> ordered = descending ? data.Reverse().ToList() : data;

        var start = 0;
        if (!string.IsNullOrWhiteSpace(cursor) && int.TryParse(cursor, out var parsed))
        {
            start = Math.Max(0, parsed);
        }

        if (start >= ordered.Count)
        {
            return ([], null, null);
        }

        var page = ordered.Skip(start).Take(limit).ToList();
        var next = start + page.Count < ordered.Count ? (start + page.Count).ToString() : null;
        var backwards = page.Count > 0 ? start.ToString() : null;
        return (page, next, backwards);
    }
}
