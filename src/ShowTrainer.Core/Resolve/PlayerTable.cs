using System.Text;
using ShowTrainer.Core.Guest;
using ShowTrainer.Core.Infra;
using ShowTrainer.Core.Session;

namespace ShowTrainer.Core.Resolve;

/// <summary>One rating byte inside a player's ratings record (the record the player entry's +48 points at).</summary>
public sealed record RatingField(string Group, string Name, int Offset, bool Confirmed);

/// <summary>A packed setting in the player entry itself (not the ratings record). Value = (byte &amp; Mask) >> shift.
/// Choices, when set, name each allowed value (index = value). ProvenOnUser: survived save + restart on the RTTS player.</summary>
public sealed record EntryField(string Group, string Name, int Offset, byte Mask, string[]? Choices, bool ProvenOnUser)
{
    public int Shift => System.Numerics.BitOperations.TrailingZeroCount(Mask);
    public int MaxValue => Choices != null ? Choices.Length - 1 : Mask >> Shift;
    public bool IsFlag => Choices == null && MaxValue == 1;
    /// <summary>Unique name used by the CLI, history and snapshots, e.g. "Quirk: Dead Red", "Secondary RF".</summary>
    public string Label => Group switch { "Quirks" => "Quirk: " + Name, "Secondary positions" => "Secondary " + Name, _ => Name };
    public string Describe(int v) => Choices != null && v < Choices.Length ? Choices[v] : IsFlag ? (v == 1 ? "on" : "off") : $"other ({v})";
}

/// <summary>A read-only bit field in the player entry: Width bits starting at bit Bit of the little-endian word at Offset.
/// Decoded 2026-11-26 from 22 players with known real-life values.</summary>
public sealed record InfoField(string Name, int Offset, int Bit, int Width, Func<int, string> Format)
{
    public int Get(byte[] entry) => (int)((BitConverter.ToUInt32(entry, Offset) >> Bit) & ((1u << Width) - 1));
}

/// <summary>A player found in the league table this launch.</summary>
public sealed record PlayerRef(ulong Entry, ulong Ratings, string Name, byte Primary)
{
    public static readonly string[] Positions = { "P", "C", "1B", "2B", "3B", "SS", "LF", "CF", "RF" };
    public string Position => Primary < Positions.Length ? Positions[Primary] : "?";
}

/// <summary>The league player table: 0xB0-byte entries (+50 first name, +60 last name, +48 ratings pointer, +A2 primary position).
/// Found from the RTTS player entry, [[main+3A88798]+1E98], which sits inside it. Layout proven for build AEE20BBC6F47C0D8.</summary>
public static class PlayerTable
{
    public const int Stride = 0xB0;
    private const long UserRoot = 0x3A88798, UserEntryOffset = 0x1E98;
    private const int Before = 4500, After = 1500; // the table spans roughly 3,150 rows before the RTTS player

    public static readonly RatingField[] Fields =
    {
        // Stored overall at the primary position; the game does not recalculate it live (Trout: Reaction 85, CF stayed 77).
        new("Overall", "Overall (stored)", 0x00, true),
        new("Hitting", "Contact R", 0x02, true), new("Hitting", "Contact L", 0x01, true),
        new("Hitting", "Power R", 0x04, true), new("Hitting", "Power L", 0x03, true),
        new("Hitting", "Vision", 0x08, true), new("Hitting", "Discipline", 0x09, true),
        new("Hitting", "Clutch", 0x05, true), new("Hitting", "Sac Bunt", 0x06, true), new("Hitting", "Drag Bunt", 0x07, true),
        new("Running", "Speed", 0x0C, true), new("Running", "Steal", 0x0D, true), new("Running", "BR Agg", 0x0E, true),
        new("Fielding", "Fielding ability", 0x11, true),
        // The card's Reaction is the average of these four, rounded down (matches Judge, Rice and Trout). What each part covers is unknown.
        new("Fielding", "Reaction part 1", 0x12, true), new("Fielding", "Reaction part 2", 0x13, true),
        new("Fielding", "Reaction part 3", 0x14, true), new("Fielding", "Reaction part 4", 0x15, true),
        new("Fielding", "Arm Str", 0x0F, true), new("Fielding", "Arm Acc", 0x10, true),
        new("Fielding", "Blocking (C)", 0x16, true), new("Fielding", "Pop Time (C)", 0x17, true),
        new("Other", "Durability", 0x0B, true),
        new("Pitching", "Stamina", 0x28, true), new("Pitching", "Pitching Clutch", 0x29, true),
        new("Pitching", "H/9 vs L", 0x2A, true), new("Pitching", "H/9 vs R", 0x2B, true), new("Pitching", "HR/9", 0x2C, true),
        new("Pitching", "K/9 vs L", 0x2D, true), new("Pitching", "K/9 vs R", 0x2E, true), new("Pitching", "Control", 0x2F, true),
    };

    public static readonly string[] Roles = { "SP", "RP", "CP" };

    private static readonly string[] Hands = { "R", "L", "S", "?" };

    /// <summary>Non-rating details in the 0xB0 entry. Read-only: none of these has been write-tested.</summary>
    public static readonly InfoField[] InfoFields =
    {
        new("Jersey", 0x84, 7, 7, v => "#" + v),
        new("Age", 0x76, 0, 7, v => v.ToString()),
        new("Bats", 0x76, 7, 2, v => Hands[v]),
        new("Throws", 0x73, 6, 1, v => Hands[v]),
        new("Height", 0x74, 9, 7, v => $"{v / 12}'{v % 12}\""),
        new("Weight", 0x74, 0, 9, v => v + " lb"),
        new("Player ID", 0x98, 0, 16, v => v.ToString()), // equals the player's row in the league table
    };

    /// <summary>Label and formatted value of every info field, or null while the entry is unreadable.</summary>
    public static List<(string Name, string Value)>? ReadInfo(GuestLayout l, PlayerRef p) =>
        l.Memory.ReadBytes(p.Entry, Stride) is { } e ? InfoFields.Select(f => (f.Name, f.Format(f.Get(e)))).ToList() : null;

    /// <summary>Settings in the 0xB0 entry. Layout decoded on the RTTS player and matched against other players' cards.</summary>
    public static readonly EntryField[] EntryFields = BuildEntryFields();

    private static EntryField[] BuildEntryFields()
    {
        var f = new List<EntryField>
        {
            new("Positions", "Primary position", 0xA2, 0xFF, PlayerRef.Positions, false),
            new("Positions", "Pitching role", 0x72, 0xE0, Roles, true),
            new("Positions", "Two-way player", 0x25, 0x80, null, true),
        };
        // +84 u32 bits 21..28 = C, 1B, 2B, 3B, SS, LF, CF, RF: byte +86 bits 5-7, byte +87 bits 0-4.
        string[] sec = { "C", "1B", "2B", "3B", "SS", "LF", "CF", "RF" };
        for (int i = 0; i < sec.Length; i++)
        {
            int bit = 21 + i;
            f.Add(new("Secondary positions", sec[i], 0x84 + bit / 8, (byte)(1 << (bit % 8)), null, sec[i] == "RF"));
        }
        // +28 u32 bitmask, one bit per quirk. The user's 12 quirks survived save + restart; the other 7 only showed on the card.
        string[] q = { "Homebody", "Road Warrior", "First-Pitch Hitter", "Dead Red", "Breaking Ball Hitter", "Unfazed", "Fighter", "Pickoff Artist",
                       "Rally Monkey", "Stopper", "Bad Ball Hitter", "Situational Hitter", "Pinch Hitter", "Day Player", "Night Player",
                       "Outlier I", "Outlier II", "Break Outlier", "Table Setter" };
        int[] proven = { 0, 1, 2, 3, 5, 6, 10, 14, 15, 16, 17, 18 };
        for (int i = 0; i < q.Length; i++) f.Add(new("Quirks", q[i], 0x28 + i / 8, (byte)(1 << (i % 8)), null, proven.Contains(i)));
        return f.ToArray();
    }

    public static ulong? UserEntry(GuestLayout l) =>
        l.Main == null ? null : PointerResolver.Resolve(l, new PointerChain { BaseOffset = UserRoot, Offsets = { UserEntryOffset, 0 } });

    private static string Text(byte[] b, int at) { int n = Array.IndexOf(b, (byte)0, at, 16); return Encoding.Latin1.GetString(b, at, (n < 0 ? at + 16 : n) - at); }

    private static PlayerRef? Parse(GuestLayout l, byte[] row, ulong entry)
    {
        static bool Letter(byte c) => c is >= 0x41 and <= 0x5A;
        if (!Letter(row[0x50]) || !Letter(row[0x60])) return null;
        ulong rec = BitConverter.ToUInt64(row, 0x48);
        if (rec < l.HeapBase || rec >= l.HeapBase + l.HeapSize || row[0xA2] > 8) return null;
        return new PlayerRef(entry, rec, Text(row, 0x50) + " " + Text(row, 0x60), row[0xA2]);
    }

    /// <summary>Every named player in the table, read in one pass. Empty while the career is not loaded.</summary>
    public static List<PlayerRef> All(GuestLayout l)
    {
        var list = new List<PlayerRef>();
        if (UserEntry(l) is not ulong me) return list;
        ulong start = me - (ulong)(Stride * Before);
        var buf = l.Memory.ReadBytes(start, Stride * (Before + After));
        if (buf == null) { Log.Warn("Player table: block not readable"); return list; }
        for (int i = 0; i < Before + After; i++)
        {
            var row = buf.AsSpan(i * Stride, Stride).ToArray();
            if (Parse(l, row, start + (ulong)(i * Stride)) is PlayerRef p) list.Add(p);
        }
        return list;
    }

    public static List<PlayerRef> Find(GuestLayout l, string query) =>
        All(l).Where(p => p.Name.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();

    /// <summary>Re-reads the entry and returns the player only if it still has the same name and ratings pointer.</summary>
    public static PlayerRef? Recheck(GuestLayout l, PlayerRef p, out string why)
    {
        why = "";
        var row = l.Memory.ReadBytes(p.Entry, Stride);
        var now = row == null ? null : Parse(l, row, p.Entry);
        if (now == null || now.Name != p.Name || now.Ratings != p.Ratings) { why = "the player entry changed (loading? search again)"; return null; }
        var rec = l.Memory.ReadBytes(p.Ratings, 0x30);
        if (rec == null || Fields.Any(f => rec[f.Offset] > 125)) { why = "the ratings record does not look right (loading?)"; return null; }
        return now;
    }

    public static byte? Read(GuestLayout l, PlayerRef p, RatingField f) => l.Memory.ReadBytes(p.Ratings + (ulong)f.Offset, 1) is [var b] ? b : null;

    public static int? Read(GuestLayout l, PlayerRef p, EntryField f) =>
        l.Memory.ReadBytes(p.Entry + (ulong)f.Offset, 1) is [var b] ? (b & f.Mask) >> f.Shift : null;

    /// <summary>A rating (by Name) or a setting (by Label), case-insensitive.</summary>
    public static (RatingField? Rating, EntryField? Setting) FindField(string label) =>
        (Fields.FirstOrDefault(f => f.Name.Equals(label, StringComparison.OrdinalIgnoreCase)),
         EntryFields.FirstOrDefault(f => f.Label.Equals(label, StringComparison.OrdinalIgnoreCase)));

    public static int? ReadField(GuestLayout l, PlayerRef p, string label) => FindField(label) switch
    {
        ({ } r, _) => Read(l, p, r),
        (_, { } e) => Read(l, p, e),
        _ => null,
    };

    public static string? WriteField(GuestLayout l, PlayerRef p, string label, int value, EditHistory? history) => FindField(label) switch
    {
        ({ } r, _) => Write(l, p, r, value, history),
        (_, { } e) => Write(l, p, e, value, history),
        _ => $"unknown field {label}",
    };

    public static string DescribeField(string label, int value) => FindField(label).Setting is { } e ? e.Describe(value) : value.ToString();

    /// <summary>The RTTS player's name, used to keep edit memory per career. Null while loading.</summary>
    public static string? UserName(GuestLayout l) =>
        UserEntry(l) is ulong e && l.Memory.ReadBytes(e, Stride) is { } row && Parse(l, row, e) is { } p ? p.Name : null;

    /// <summary>True when this entry is the RTTS player's own.</summary>
    public static bool IsUser(GuestLayout l, PlayerRef p) => UserEntry(l) == p.Entry;

    private static string? Revalidate(GuestLayout l, PlayerRef p) => Recheck(l, p, out var why) == null ? why : null;

    /// <summary>Guarded single-byte write. Returns null on success, else why nothing was written.</summary>
    public static string? Write(GuestLayout l, PlayerRef p, RatingField f, int value, EditHistory? history = null)
    {
        if (value is < 0 or > 99) return "ratings must be 0..99";
        l.Memory.Refresh();
        if (Recheck(l, p, out var why) == null) return why;
        ulong at = p.Ratings + (ulong)f.Offset;
        if (l.Memory.ReadBytes(at, 1) is not [var old]) return "memory read failed";
        return WriteByte(l, p, at, old, (byte)value, f.Name, old.ToString(), value.ToString(), history, old, value);
    }

    /// <summary>Guarded masked write to the player entry: only the field's bits change. Returns null on success, else why nothing was written.</summary>
    public static string? Write(GuestLayout l, PlayerRef p, EntryField f, int value, EditHistory? history = null)
    {
        if (value < 0 || value > f.MaxValue) return $"{f.Name} must be 0..{f.MaxValue}";
        l.Memory.Refresh();
        if (Recheck(l, p, out var why) == null) return why;
        ulong at = p.Entry + (ulong)f.Offset;
        if (l.Memory.ReadBytes(at, 1) is not [var old]) return "memory read failed";
        byte now = (byte)((old & ~f.Mask) | ((value << f.Shift) & f.Mask));
        int was = (old & f.Mask) >> f.Shift;
        return WriteByte(l, p, at, old, now, f.Label, f.Describe(was), f.Describe(value), history, was, value);
    }

    private static string? WriteByte(GuestLayout l, PlayerRef p, ulong at, byte old, byte now, string field, string oldText, string newText, EditHistory? history, int oldValue, int newValue)
    {
        if (old == now) return null;
        bool ok = l.Memory.Write(at, new[] { now }) && l.Memory.ReadBytes(at, 1) is [var back] && back == now;
        Log.Info($"Player write {p.Name} {field} = {newText} ({old:X2} -> {now:X2} at {at:X10}): {(ok ? "ok" : "failed")}");
        if (!ok) return "memory write failed or did not read back";
        history?.Add(new EditRecord
        {
            Target = p.Name, Field = field, OldText = oldText, NewText = newText, Address = at,
            Before = new[] { old }, After = new[] { now }, Revalidate = gl => Revalidate(gl, p),
            Player = p.Name, OldValue = oldValue, NewValue = newValue,
        });
        return null;
    }
}
