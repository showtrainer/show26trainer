using System.Text.Json;
using ShowTrainer.Core.Guest;
using ShowTrainer.Core.Session;

namespace ShowTrainer.Core.Resolve;

/// <summary>A player's known ratings and entry settings at one moment, by field name (so it survives relaunches).
/// Only decoded fields are kept; pointers and unknown bytes are never saved or restored.</summary>
public sealed class PlayerSnapshot
{
    public string Name { get; set; } = "";
    public string Position { get; set; } = "";
    public DateTime Taken { get; set; }
    public string? BuildId { get; set; }
    public Dictionary<string, int> Ratings { get; set; } = new();
    public Dictionary<string, int> Settings { get; set; } = new();
    /// <summary>Read-only details (jersey, age, bats, ...), formatted. Kept for CSV export; never compared or restored.</summary>
    public Dictionary<string, string> Info { get; set; } = new();

    public sealed record Difference(string Kind, string Field, int? Now, int Then, string NowText, string ThenText);

    private static string Key(EntryField f) => f.Label;

    public static PlayerSnapshot? Take(GuestLayout l, PlayerRef p)
    {
        var rec = l.Memory.ReadBytes(p.Ratings, 0x30);
        var ent = l.Memory.ReadBytes(p.Entry, PlayerTable.Stride);
        if (rec == null || ent == null) return null;
        var s = new PlayerSnapshot { Name = p.Name, Position = p.Position, Taken = DateTime.Now, BuildId = l.BuildId };
        foreach (var f in PlayerTable.Fields) s.Ratings[f.Name] = rec[f.Offset];
        foreach (var f in PlayerTable.EntryFields) s.Settings[Key(f)] = (ent[f.Offset] & f.Mask) >> f.Shift;
        foreach (var f in PlayerTable.InfoFields) s.Info[f.Name] = f.Format(f.Get(ent));
        return s;
    }

    /// <summary>Fields whose live value differs from the snapshot.</summary>
    public List<Difference> Compare(GuestLayout l, PlayerRef p)
    {
        var d = new List<Difference>();
        foreach (var f in PlayerTable.Fields)
            if (Ratings.TryGetValue(f.Name, out var then) && PlayerTable.Read(l, p, f) is var now && now != then)
                d.Add(new("Rating", f.Name, now, then, now?.ToString() ?? "?", then.ToString()));
        foreach (var f in PlayerTable.EntryFields)
            if (Settings.TryGetValue(Key(f), out var then) && PlayerTable.Read(l, p, f) is var now && now != then)
                d.Add(new("Setting", f.Label, now, then, now is int n ? f.Describe(n) : "?", f.Describe(then)));
        return d;
    }

    /// <summary>Writes back every differing field through the normal guarded writes. Returns null on success, else why it stopped
    /// (fields before that point were written and are in the history).</summary>
    public string? Restore(GuestLayout l, PlayerRef p, EditHistory? history, out int written)
    {
        written = 0;
        if (!Name.Equals(p.Name, StringComparison.OrdinalIgnoreCase)) return $"this snapshot is for {Name}, not {p.Name}";
        if (BuildId != null && l.BuildId != null && BuildId != l.BuildId) return "the snapshot was taken on a different game build";
        foreach (var f in PlayerTable.Fields)
            if (Ratings.TryGetValue(f.Name, out var v) && PlayerTable.Read(l, p, f) != v)
            {
                if (PlayerTable.Write(l, p, f, v, history) is string why) return $"{f.Name}: {why}";
                written++;
            }
        foreach (var f in PlayerTable.EntryFields)
            if (Settings.TryGetValue(Key(f), out var v) && PlayerTable.Read(l, p, f) != v)
            {
                // Values outside the editable range (e.g. a fielder's unknown role code) are left alone.
                if (v > f.MaxValue) continue;
                if (PlayerTable.Write(l, p, f, v, history) is string why) return $"{f.Name}: {why}";
                written++;
            }
        return null;
    }

    public static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, Json));
    }

    public static PlayerSnapshot Load(string path) =>
        JsonSerializer.Deserialize<PlayerSnapshot>(File.ReadAllText(path), Json) ?? throw new InvalidDataException("Empty snapshot file.");

    /// <summary>Every named player, for a league-wide before/after comparison (e.g. across a simmed day).</summary>
    public static List<PlayerSnapshot> TakeAll(GuestLayout l) =>
        PlayerTable.All(l).Select(p => Take(l, p)).OfType<PlayerSnapshot>().ToList();

    public static void SaveAll(string path, List<PlayerSnapshot> all)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, JsonSerializer.Serialize(all, Json));
    }

    public static List<PlayerSnapshot> LoadAll(string path) =>
        JsonSerializer.Deserialize<List<PlayerSnapshot>>(File.ReadAllText(path), Json) ?? new();

    public sealed record LeagueChange(string Player, string Field, string Then, string Now);

    /// <summary>Every field that differs between a league snapshot and now. Players missing now, or whose name is shared, are counted, not compared.</summary>
    public static List<LeagueChange> CompareAll(GuestLayout l, List<PlayerSnapshot> then, List<PlayerRef> now, out int skipped)
    {
        var live = now.GroupBy(p => p.Name).ToDictionary(g => g.Key, g => g.ToList());
        var res = new List<LeagueChange>(); skipped = 0;
        foreach (var g in then.GroupBy(x => x.Name))
        {
            if (g.Count() != 1 || !live.TryGetValue(g.Key, out var ps) || ps.Count != 1) { skipped++; continue; }
            res.AddRange(g.First().Compare(l, ps[0]).Select(d => new LeagueChange(g.Key, d.Field, d.ThenText, d.NowText)));
        }
        return res;
    }

    /// <summary>One CSV row per player: name, position, every rating, then the settings (quirks as one ';' list).</summary>
    public static string ToCsv(List<PlayerSnapshot> all)
    {
        static string Q(string v) => v.Contains(',') || v.Contains('"') ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;
        var other = PlayerTable.EntryFields.Where(f => f.Group != "Quirks").ToList();
        var sb = new System.Text.StringBuilder();
        sb.AppendLine(string.Join(",", new[] { "Name", "Position" }.Concat(PlayerTable.InfoFields.Select(f => f.Name)).Concat(PlayerTable.Fields.Select(f => f.Name)).Concat(other.Select(f => f.Label)).Append("Quirks").Select(Q)));
        foreach (var s in all.OrderBy(x => x.Name))
        {
            var cells = new List<string> { s.Name, s.Position };
            cells.AddRange(PlayerTable.InfoFields.Select(f => s.Info.GetValueOrDefault(f.Name, "")));
            cells.AddRange(PlayerTable.Fields.Select(f => s.Ratings.TryGetValue(f.Name, out var v) ? v.ToString() : ""));
            cells.AddRange(other.Select(f => s.Settings.TryGetValue(f.Label, out var v) ? f.Describe(v) : ""));
            cells.Add(string.Join(";", PlayerTable.EntryFields.Where(f => f.Group == "Quirks" && s.Settings.GetValueOrDefault(f.Label) == 1).Select(f => f.Name)));
            sb.AppendLine(string.Join(",", cells.Select(Q)));
        }
        return sb.ToString();
    }

    public static string SafeFileName(string name) => string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) || c == ' ' ? '_' : c));
}
