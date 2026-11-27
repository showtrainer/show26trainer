using System.Text.Json;
using ShowTrainer.Core.Guest;
using ShowTrainer.Core.Infra;
using ShowTrainer.Core.Resolve;

namespace ShowTrainer.Core.Session;

/// <summary>One player edit the user wants to keep: field (rating Name or setting Label), the value set, and the game's value before it.</summary>
public sealed class RememberedEdit
{
    public string Player { get; set; } = "";
    public string Field { get; set; } = "";
    public int Value { get; set; }
    public int Original { get; set; }
    public DateTime When { get; set; }
}

/// <summary>Live state of a remembered edit.</summary>
public sealed record EditStatus(RememberedEdit Edit, PlayerRef? Player, int? Now, string State)
{
    public bool NeedsReapply => Player != null && Now is int n && n != Edit.Value;
}

/// <summary>Every player edit made with the trainer, kept per career (file per RTTS player name) across launches,
/// so edits the game later changes (progression, streaks, slumps) can be found and re-applied.</summary>
public sealed class EditMemory
{
    public string Career { get; set; } = "";
    public string? BuildId { get; set; }
    public List<RememberedEdit> Edits { get; set; } = new();

    [System.Text.Json.Serialization.JsonIgnore] public string? Path { get; private set; }

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static EditMemory Open(string dir, string career, string? buildId)
    {
        var path = System.IO.Path.Combine(dir, PlayerSnapshot.SafeFileName(career) + ".json");
        EditMemory m;
        try { m = File.Exists(path) ? JsonSerializer.Deserialize<EditMemory>(File.ReadAllText(path), Json) ?? new() : new(); }
        catch (Exception ex) { Log.Error($"Edit memory {path} unreadable ({ex.Message}); starting a new one"); File.Copy(path, path + ".bad", true); m = new(); }
        m.Career = career; m.BuildId ??= buildId; m.Path = path;
        return m;
    }

    public void Save()
    {
        if (Path == null) return;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        var tmp = Path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
        File.Move(tmp, Path, true);
    }

    /// <summary>Records a write (or an undo, which is a write back). An edit that returns to the game's original value is forgotten.</summary>
    public void Remember(string player, string field, int oldValue, int newValue)
    {
        var e = Edits.FirstOrDefault(x => x.Player == player && x.Field == field);
        if (e == null) { if (oldValue != newValue) Edits.Add(new RememberedEdit { Player = player, Field = field, Original = oldValue, Value = newValue, When = DateTime.Now }); }
        else if (newValue == e.Original) Edits.Remove(e);
        else { e.Value = newValue; e.When = DateTime.Now; }
        Save();
    }

    public void Forget(IEnumerable<RememberedEdit> edits) { foreach (var e in edits.ToList()) Edits.Remove(e); Save(); }

    /// <summary>Where each edit stands now. <paramref name="all"/> is one PlayerTable.All read (names must be unique to be matched).</summary>
    public List<EditStatus> Status(GuestLayout l, List<PlayerRef> all)
    {
        var byName = all.GroupBy(p => p.Name).ToDictionary(g => g.Key, g => g.ToList());
        return Edits.OrderBy(e => e.Player).ThenBy(e => e.Field).Select(e =>
        {
            if (!byName.TryGetValue(e.Player, out var ps)) return new EditStatus(e, null, null, "player not found (loading, or left the league?)");
            if (ps.Count > 1) return new EditStatus(e, null, null, $"{ps.Count} players share this name; edit them by hand");
            var now = PlayerTable.ReadField(l, ps[0], e.Field);
            return new EditStatus(e, ps[0], now, now == null ? "unreadable" : now == e.Value ? "kept" : $"changed by the game (now {PlayerTable.DescribeField(e.Field, now.Value)})");
        }).ToList();
    }

    /// <summary>Writes back every edit the game changed. Returns how many were written, and the first refusal (null if none).</summary>
    public (int Written, string? Error) Reapply(GuestLayout l, IEnumerable<EditStatus> items, EditHistory? history)
    {
        int n = 0;
        foreach (var s in items.Where(x => x.NeedsReapply))
        {
            if (PlayerTable.WriteField(l, s.Player!, s.Edit.Field, s.Edit.Value, history) is string why) return (n, $"{s.Edit.Player} {s.Edit.Field}: {why}");
            n++;
        }
        return (n, null);
    }
}
