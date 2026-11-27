using ShowTrainer.Core.Guest;
using ShowTrainer.Core.Infra;

namespace ShowTrainer.Core.Session;

/// <summary>One write made by the trainer: the raw bytes before and after, and how to check the target is still the same thing.</summary>
public sealed class EditRecord
{
    public DateTime When { get; init; } = DateTime.Now;
    /// <summary>Who or what was edited, e.g. "Aaron Judge" or "Targets".</summary>
    public required string Target { get; init; }
    public required string Field { get; init; }
    public required string OldText { get; init; }
    public required string NewText { get; init; }
    public required ulong Address { get; init; }
    public required byte[] Before { get; init; }
    public required byte[] After { get; init; }
    /// <summary>Returns null when Address still holds this field (same player, same record), else why not.</summary>
    public required Func<GuestLayout, string?> Revalidate { get; init; }
    public bool Undone { get; set; }
    /// <summary>Player edits only: the player's name and the field values as numbers (for the persistent edit memory).</summary>
    public string? Player { get; init; }
    public int? OldValue { get; init; }
    public int? NewValue { get; init; }
}

/// <summary>Per-launch list of writes, with guarded undo. Addresses move between launches, so it is not saved.</summary>
public sealed class EditHistory
{
    private readonly List<EditRecord> _items = new();
    private readonly object _lock = new();
    public event Action? Changed;
    /// <summary>Raised after a write is recorded, and after an undo (with undone = true).</summary>
    public event Action<EditRecord, bool>? Recorded;

    public IReadOnlyList<EditRecord> Items { get { lock (_lock) return _items.ToList(); } }

    public void Add(EditRecord r)
    {
        lock (_lock) _items.Add(r);
        Log.Info($"History: {r.Target} / {r.Field}: {r.OldText} -> {r.NewText} at {r.Address:X10} ({Hex(r.Before)} -> {Hex(r.After)})");
        Recorded?.Invoke(r, false);
        Changed?.Invoke();
    }

    public void Clear() { lock (_lock) _items.Clear(); Changed?.Invoke(); }

    /// <summary>Writes the old bytes back, only if the target is unchanged since the edit. Returns null on success, else why nothing was written.</summary>
    public string? Undo(GuestLayout l, EditRecord r)
    {
        if (r.Undone) return "already undone";
        lock (_lock)
            if (_items.Any(x => !x.Undone && x != r && x.When > r.When && x.Address < r.Address + (ulong)r.Before.Length && r.Address < x.Address + (ulong)x.Before.Length))
                return "a later edit changed the same bytes; undo that one first";
        l.Memory.Refresh();
        if (r.Revalidate(l) is string why) return why;
        var now = l.Memory.ReadBytes(r.Address, r.Before.Length);
        if (now == null) return "memory read failed";
        if (!now.AsSpan().SequenceEqual(r.After)) return $"the value changed since the edit (now {Hex(now)}, expected {Hex(r.After)}); the game may have updated it";
        if (!l.Memory.Write(r.Address, r.Before)) return "memory write failed";
        var back = l.Memory.ReadBytes(r.Address, r.Before.Length);
        if (back == null || !back.AsSpan().SequenceEqual(r.Before)) return "the write did not read back";
        r.Undone = true;
        Log.Info($"Undo: {r.Target} / {r.Field} back to {r.OldText} at {r.Address:X10}");
        Recorded?.Invoke(r, true);
        Changed?.Invoke();
        return null;
    }

    public static string Hex(byte[] b) => BitConverter.ToString(b).Replace('-', ' ');
}
