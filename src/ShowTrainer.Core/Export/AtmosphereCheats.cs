using System.Text;
using ShowTrainer.Core.Guest;
using ShowTrainer.Core.Scan;
using ShowTrainer.Core.Session;

namespace ShowTrainer.Core.Export;

/// <summary>Writes Atmosphere (dmnt) cheats from pointer-based entries, with null-pointer and record checks.</summary>
public static class AtmosphereCheats
{
    public const int MaxWords = 0x400; // VM program limit across all enabled cheats

    public static string Export(string buildId, IEnumerable<(AddressEntry Entry, string Value, long Shift)> items, out int words)
    {
        var sb = new StringBuilder();
        words = 0;
        foreach (var (e, value, shift) in items)
        {
            var code = Encode(e, value, shift);
            words += code.SelectMany(l => l).Count();
            sb.AppendLine($"[{e.Category}: {e.Name} = {value}]");
            foreach (var line in code) sb.AppendLine(string.Join(" ", line.Select(w => w.ToString("X8"))));
            sb.AppendLine();
        }
        if (words > MaxWords) throw new InvalidOperationException($"{words} code words exceeds the {MaxWords} limit; export fewer entries.");
        return sb.ToString();
    }

    public static string FileName(string buildId) => buildId[..16].ToUpperInvariant() + ".txt";

    public static List<uint[]> Encode(AddressEntry e, string value, long shift = 0)
    {
        var p = e.Pointer ?? throw new InvalidOperationException($"{e.Name} has no pointer chain.");
        if (ValueCodec.IsFloat(e.Kind)) throw new InvalidOperationException("Float export is not supported.");
        if (e.Mask != null) throw new InvalidOperationException($"{e.Name}: single-bit values cannot be exported.");
        int size = ValueCodec.SizeOf(e.Kind);
        double d = ValueCodec.ParseDouble(value);
        if (d < (e.Min ?? d) || d > (e.Max ?? d)) throw new InvalidOperationException($"{e.Name}: {value} is outside {e.Min}..{e.Max}.");
        if (p.BaseOffset < 0 || p.Offsets.Any(o => o is < 0 or > 0xFFFFFFFF)) throw new InvalidOperationException("Negative offsets are not supported.");
        var bytes = new byte[8];
        ValueCodec.Parse(e.Kind, value).CopyTo(bytes, 0);
        ulong v = BitConverter.ToUInt64(bytes);

        var c = new List<uint[]>();
        int opened = 0;
        void NotNull() { c.Add(new[] { 0xC0810400u, 0u, 0u }); opened++; }                       // if R0 > 0
        c.Add(new[] { 0x58000000u | (uint)(p.BaseOffset >> 32), (uint)p.BaseOffset });           // R0 = [main + base]
        NotNull();
        foreach (var o in p.Offsets.SkipLast(1)) { c.Add(new[] { 0x58001000u, (uint)o }); NotNull(); } // R0 = [R0 + o]
        long last = p.Offsets[^1] + shift; // season rows: the row found for this launch
        foreach (var g in e.Guards ?? new())
        {
            long at = last + g.Offset;
            if (at < 0) throw new InvalidOperationException("Guard lies before the record.");
            c.Add(new[] { 0x40010000u, 0u, (uint)g.Min });                                       // R1 = min
            c.Add(new[] { 0xC0141200u, (uint)at }); opened++;                                    // if R1 <= byte [R0 + at]
            c.Add(new[] { 0x40010000u, 0u, (uint)g.Max });                                       // R1 = max
            c.Add(new[] { 0xC0121200u, (uint)at }); opened++;                                    // if R1 >= byte [R0 + at]
        }
        c.Add(new[] { 0x78000000u, (uint)last });                                                // R0 += last
        c.Add(new[] { 0x60000000u | ((uint)size << 24), (uint)(v >> 32), (uint)v });             // [R0] = value
        for (int i = 0; i < opened; i++) c.Add(new[] { 0x20000000u });
        return c;
    }

    /// <summary>Runs one exported cheat without writing. Returns the write it would make, or null if a condition stops it.</summary>
    public static (ulong Va, int Size, ulong Value)? Simulate(GuestLayout l, IEnumerable<uint[]> code)
    {
        var r = new ulong[16];
        foreach (var w in code)
        {
            uint op = w[0];
            switch (op >> 28)
            {
                case 5:
                {
                    ulong a = ((ulong)(op & 0xFF) << 32) | w[1];
                    int reg = (int)(op >> 16) & 0xF;
                    ulong from = ((op >> 12) & 0xF) == 1 ? r[reg] + a : l.Main!.Va + a;
                    if (l.Memory.ReadU64(from) is not ulong val) return null;
                    r[reg] = val;
                    break;
                }
                case 4: r[(op >> 16) & 0xF] = ((ulong)w[1] << 32) | w[2]; break;
                case 7: r[(op >> 16) & 0xF] += w[1]; break;
                case 0xC:
                {
                    ulong lhs = r[(op >> 12) & 0xF], rhs;
                    if (((op >> 8) & 0xF) == 4) rhs = ((ulong)w[1] << 32) | w[2];
                    else
                    {
                        var b = l.Memory.ReadBytes(r[(op >> 4) & 0xF] + w[1], 1);
                        if (b == null) return null;
                        rhs = b[0];
                    }
                    bool ok = ((op >> 16) & 0xF) switch { 1 => lhs > rhs, 2 => lhs >= rhs, 3 => lhs < rhs, 4 => lhs <= rhs, 5 => lhs == rhs, _ => lhs != rhs };
                    if (!ok) return null;
                    break;
                }
                case 6: return (r[(op >> 16) & 0xF], (int)(op >> 24) & 0xF, ((ulong)w[1] << 32) | w[2]);
            }
        }
        return null;
    }

    /// <summary>Parses exported text back into cheats, so the simulator checks the actual file contents.</summary>
    public static List<(string Name, List<uint[]> Code)> Parse(string text)
    {
        var list = new List<(string, List<uint[]>)>();
        foreach (var line in text.Split('\n').Select(x => x.Trim()).Where(x => x.Length > 0))
        {
            if (line[0] == '[') list.Add((line.Trim('[', ']'), new()));
            else list[^1].Item2.Add(line.Split(' ').Select(x => Convert.ToUInt32(x, 16)).ToArray());
        }
        return list;
    }
}
