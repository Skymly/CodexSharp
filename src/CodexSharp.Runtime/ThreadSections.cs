using System.Text.Json;
using CodexSharp.Protocol;

namespace CodexSharp.Runtime;

public sealed record ThreadSectionInfo(string Id, string Name, string? Color, string? Icon);

/// Independently persisted thread sections from vendor/codex app-server-protocol threadSection/*.
public static class ThreadSections
{
    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };

    private static string FilePath => Path.Combine(CodexPaths.Home, "sections.json");

    public static IReadOnlyList<ThreadSectionInfo> List()
    {
        lock (Gate)
        {
            return Load().Sections.Select(ToInfo).ToList();
        }
    }

    public static ThreadSectionInfo Create(string name, string? color = null, string? icon = null)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("name is required", nameof(name));
        }

        lock (Gate)
        {
            var store = Load();
            var section = new SectionRecord(Ids.newId("sec"), name.Trim(), color, icon);
            store.Sections.Add(section);
            Save(store);
            return ToInfo(section);
        }
    }

    public static ThreadSectionInfo? Update(string id, string name, bool appearanceSpecified, string? color, string? icon)
    {
        lock (Gate)
        {
            var store = Load();
            var section = store.Sections.FirstOrDefault(s => s.Id == id);
            if (section is null)
            {
                return null;
            }

            section.Name = string.IsNullOrWhiteSpace(name) ? section.Name : name.Trim();
            if (appearanceSpecified)
            {
                section.Color = color;
                section.Icon = icon;
            }

            Save(store);
            return ToInfo(section);
        }
    }

    public static bool Delete(string id)
    {
        lock (Gate)
        {
            var store = Load();
            var removed = store.Sections.RemoveAll(s => s.Id == id);
            store.Membership.RemoveAll(m => m.SectionId == id);
            if (removed > 0)
            {
                Save(store);
            }

            return removed > 0;
        }
    }

    public static bool Move(string threadId, string? sectionId, string? beforeThreadId)
    {
        if (string.IsNullOrWhiteSpace(threadId))
        {
            return false;
        }

        lock (Gate)
        {
            var store = Load();
            store.Membership.RemoveAll(m => m.ThreadId == threadId);
            if (string.IsNullOrWhiteSpace(sectionId))
            {
                Save(store);
                return true;
            }

            if (store.Sections.All(s => s.Id != sectionId))
            {
                return false;
            }

            var peers = store.Membership.Where(m => m.SectionId == sectionId).OrderBy(m => m.Index).ToList();
            store.Membership.RemoveAll(m => m.SectionId == sectionId);
            var insertAt = peers.Count;
            if (!string.IsNullOrWhiteSpace(beforeThreadId))
            {
                var idx = peers.FindIndex(m => m.ThreadId == beforeThreadId);
                if (idx >= 0)
                {
                    insertAt = idx;
                }
            }

            peers.Insert(insertAt, new MembershipRecord(threadId, sectionId, insertAt));
            for (var i = 0; i < peers.Count; i++)
            {
                peers[i].Index = i;
            }

            store.Membership.AddRange(peers);
            Save(store);
            return true;
        }
    }

    public static string? SectionOf(string threadId)
    {
        lock (Gate)
        {
            return Load().Membership.FirstOrDefault(m => m.ThreadId == threadId)?.SectionId;
        }
    }

    private static ThreadSectionInfo ToInfo(SectionRecord s) => new(s.Id, s.Name, s.Color, s.Icon);

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
        public List<SectionRecord> Sections { get; set; } = [];
        public List<MembershipRecord> Membership { get; set; } = [];
    }

    private sealed class SectionRecord
    {
        public SectionRecord() { }
        public SectionRecord(string id, string name, string? color, string? icon)
        {
            Id = id;
            Name = name;
            Color = color;
            Icon = icon;
        }

        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string? Color { get; set; }
        public string? Icon { get; set; }
    }

    private sealed class MembershipRecord
    {
        public MembershipRecord() { }
        public MembershipRecord(string threadId, string sectionId, int index)
        {
            ThreadId = threadId;
            SectionId = sectionId;
            Index = index;
        }

        public string ThreadId { get; set; } = "";
        public string SectionId { get; set; } = "";
        public int Index { get; set; }
    }
}
