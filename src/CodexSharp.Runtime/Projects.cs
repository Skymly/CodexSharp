using System.Text.Json;
using CodexSharp.Protocol;

namespace CodexSharp.Runtime;

public sealed record ProjectRootInfo(string Path);

public sealed record ProjectInfo(
    string Id,
    string Name,
    IReadOnlyList<ProjectRootInfo> Roots,
    IReadOnlyDictionary<string, string> Metadata,
    long Position,
    long CreatedAt,
    long UpdatedAt,
    long? RecencyAt);

public sealed record ProjectMutation(ProjectInfo Project, bool Created);

/// JSON-backed projects from vendor/codex app-server-protocol project/*.
public static class ProjectStore
{
    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private static string FilePath => Path.Combine(CodexPaths.Home, "projects.json");

    public static IReadOnlyList<ProjectInfo> List(string sortKey = "position", bool descending = false)
    {
        lock (Gate)
        {
            var items = Load().Projects.Select(ToInfo).ToList();
            items.Sort((a, b) =>
            {
                int cmp;
                if (string.Equals(sortKey, "recencyAt", StringComparison.OrdinalIgnoreCase))
                {
                    if (a.RecencyAt is null && b.RecencyAt is null) cmp = a.Position.CompareTo(b.Position);
                    else if (a.RecencyAt is null) cmp = 1;
                    else if (b.RecencyAt is null) cmp = -1;
                    else cmp = a.RecencyAt.Value.CompareTo(b.RecencyAt.Value);
                }
                else
                {
                    cmp = a.Position.CompareTo(b.Position);
                }

                return descending ? -cmp : cmp;
            });
            return items;
        }
    }

    public static ProjectInfo? Read(string projectId)
    {
        lock (Gate)
        {
            var rec = Load().Projects.FirstOrDefault(p => p.Id == projectId);
            return rec is null ? null : ToInfo(rec);
        }
    }

    public static string? PrimaryRoot(string projectId) =>
        Read(projectId)?.Roots.FirstOrDefault()?.Path;

    public static IReadOnlyList<string> ExtraRoots(string? projectId)
    {
        if (string.IsNullOrWhiteSpace(projectId))
        {
            return [];
        }

        var roots = Read(projectId)?.Roots;
        if (roots is null || roots.Count <= 1)
        {
            return [];
        }

        return roots.Skip(1).Select(r => r.Path).ToList();
    }

    public static ProjectMutation Create(string name, IEnumerable<string> roots, IReadOnlyDictionary<string, string>? metadata, string idempotencyKey)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("name is required", nameof(name));
        }

        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            throw new ArgumentException("idempotencyKey is required", nameof(idempotencyKey));
        }

        lock (Gate)
        {
            var store = Load();
            if (store.Idempotency.TryGetValue(idempotencyKey, out var existingId))
            {
                var live = store.Projects.FirstOrDefault(p => p.Id == existingId);
                if (live is not null)
                {
                    return new ProjectMutation(ToInfo(live), false);
                }

                var dead = store.Deleted.FirstOrDefault(p => p.Id == existingId);
                if (dead is not null)
                {
                    return new ProjectMutation(ToInfo(dead), false);
                }
            }

            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var rec = new ProjectRecord
            {
                Id = Ids.newId("proj"),
                Name = name.Trim(),
                Roots = NormalizeRoots(roots),
                Metadata = metadata is null ? [] : metadata.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal),
                Position = store.Projects.Count == 0 ? 0 : store.Projects.Max(p => p.Position) + 1,
                CreatedAt = now,
                UpdatedAt = now,
            };
            store.Projects.Add(rec);
            store.Idempotency[idempotencyKey] = rec.Id;
            Save(store);
            return new ProjectMutation(ToInfo(rec), true);
        }
    }

    public static ProjectMutation Import(string name, IEnumerable<string> roots, IReadOnlyDictionary<string, string>? metadata, IEnumerable<string>? threadIds, string idempotencyKey)
    {
        var created = Create(name, roots, metadata, idempotencyKey);
        if (!created.Created)
        {
            return created;
        }

        lock (Gate)
        {
            var store = Load();
            if (threadIds is not null)
            {
                foreach (var threadId in threadIds.Where(t => !string.IsNullOrWhiteSpace(t)))
                {
                    AssignLocked(store, threadId, created.Project.Id);
                }
            }

            Save(store);
            return new ProjectMutation(ToInfo(store.Projects.First(p => p.Id == created.Project.Id)), true);
        }
    }

    public static ProjectInfo? Update(string projectId, string? name, IEnumerable<string>? roots, IReadOnlyDictionary<string, string>? metadata)
    {
        lock (Gate)
        {
            var store = Load();
            var rec = store.Projects.FirstOrDefault(p => p.Id == projectId);
            if (rec is null)
            {
                return null;
            }

            if (!string.IsNullOrWhiteSpace(name))
            {
                rec.Name = name.Trim();
            }

            if (roots is not null)
            {
                rec.Roots = NormalizeRoots(roots);
            }

            if (metadata is not null)
            {
                rec.Metadata = metadata.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
            }

            rec.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            Save(store);
            return ToInfo(rec);
        }
    }

    public static bool Move(string projectId, string? beforeProjectId)
    {
        lock (Gate)
        {
            var store = Load();
            var rec = store.Projects.FirstOrDefault(p => p.Id == projectId);
            if (rec is null)
            {
                return false;
            }

            var ordered = store.Projects.OrderBy(p => p.Position).ToList();
            ordered.RemoveAll(p => p.Id == projectId);
            var insertAt = ordered.Count;
            if (!string.IsNullOrWhiteSpace(beforeProjectId))
            {
                var idx = ordered.FindIndex(p => p.Id == beforeProjectId);
                if (idx >= 0)
                {
                    insertAt = idx;
                }
            }

            ordered.Insert(insertAt, rec);
            for (var i = 0; i < ordered.Count; i++)
            {
                ordered[i].Position = i;
                ordered[i].UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            }

            store.Projects = ordered;
            Save(store);
            return true;
        }
    }

    public static bool Delete(string projectId)
    {
        lock (Gate)
        {
            var store = Load();
            var rec = store.Projects.FirstOrDefault(p => p.Id == projectId);
            if (rec is null)
            {
                return false;
            }

            store.Projects.Remove(rec);
            store.Deleted.RemoveAll(p => p.Id == projectId);
            store.Deleted.Add(rec);
            store.Membership.RemoveAll(m => m.ProjectId == projectId);
            Save(store);
            return true;
        }
    }

    public static bool Assign(string threadId, string? projectId)
    {
        if (string.IsNullOrWhiteSpace(threadId))
        {
            return false;
        }

        lock (Gate)
        {
            var store = Load();
            if (!AssignLocked(store, threadId, projectId))
            {
                return false;
            }

            Save(store);
            return true;
        }
    }

    public static string? ProjectOf(string threadId)
    {
        lock (Gate)
        {
            return Load().Membership.FirstOrDefault(m => m.ThreadId == threadId)?.ProjectId;
        }
    }

    private static bool AssignLocked(Store store, string threadId, string? projectId)
    {
        store.Membership.RemoveAll(m => m.ThreadId == threadId);
        if (string.IsNullOrWhiteSpace(projectId))
        {
            return true;
        }

        var rec = store.Projects.FirstOrDefault(p => p.Id == projectId);
        if (rec is null)
        {
            return false;
        }

        store.Membership.Add(new MembershipRecord { ThreadId = threadId, ProjectId = projectId });
        rec.RecencyAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        rec.UpdatedAt = rec.RecencyAt.Value;
        return true;
    }

    private static List<string> NormalizeRoots(IEnumerable<string> roots) =>
        roots
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Select(r =>
            {
                try { return Path.GetFullPath(r.Trim()); }
                catch { return r.Trim(); }
            })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static ProjectInfo ToInfo(ProjectRecord rec) =>
        new(
            rec.Id,
            rec.Name,
            rec.Roots.Select(p => new ProjectRootInfo(p)).ToList(),
            rec.Metadata,
            rec.Position,
            rec.CreatedAt,
            rec.UpdatedAt,
            rec.RecencyAt);

    private static Store Load()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                return new Store();
            }

            return JsonSerializer.Deserialize<Store>(File.ReadAllText(FilePath), Json) ?? new Store();
        }
        catch
        {
            return new Store();
        }
    }

    private static void Save(Store store)
    {
        CodexPaths.EnsureLayout();
        File.WriteAllText(FilePath, JsonSerializer.Serialize(store, Json));
    }

    private sealed class Store
    {
        public List<ProjectRecord> Projects { get; set; } = [];
        public List<ProjectRecord> Deleted { get; set; } = [];
        public Dictionary<string, string> Idempotency { get; set; } = new(StringComparer.Ordinal);
        public List<MembershipRecord> Membership { get; set; } = [];
    }

    private sealed class ProjectRecord
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public List<string> Roots { get; set; } = [];
        public Dictionary<string, string> Metadata { get; set; } = new(StringComparer.Ordinal);
        public long Position { get; set; }
        public long CreatedAt { get; set; }
        public long UpdatedAt { get; set; }
        public long? RecencyAt { get; set; }
    }

    private sealed class MembershipRecord
    {
        public string ThreadId { get; set; } = "";
        public string ProjectId { get; set; } = "";
    }
}
