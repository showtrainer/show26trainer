using System.Globalization;
using ShowTrainer.Core;
using ShowTrainer.Core.Infra;
using ShowTrainer.Core.Native;
using ShowTrainer.Core.Scan;
using ShowTrainer.Core.Session;
using ShowTrainer.Core.Resolve;
using PT = ShowTrainer.Core.Resolve.PlayerTable;
using PS = ShowTrainer.Core.Resolve.PlayerSnapshot;

// Headless front-end for the scanner core. Each invocation re-attaches and persists scan state
// to a session file, so scans can be driven step by step while playing.
Log.EchoToConsole = args.Contains("-v");
var argv = args.Where(a => a != "-v").ToList();
string sessionPath = Opt("--session") ?? Path.Combine(AppContext.BaseDirectory, "sessions", "cli.json");
string? Opt(string name) { int i = argv.IndexOf(name); if (i < 0 || i + 1 >= argv.Count) return null; var v = argv[i + 1]; argv.RemoveRange(i, 2); return v; }
string? scopeOpt = Opt("--scope"), alignOpt = Opt("--align"), pidOpt = Opt("--pid"), tolOpt = Opt("--tol");

if (argv.Count == 0) { Usage(); return 1; }
var cmd = argv[0].ToLowerInvariant();
if (cmd == "list")
{
    foreach (var c in HostProcess.FindCandidates()) Console.WriteLine($"{c.Pid,6} {c.Name,-12} \"{c.WindowTitle}\" {c.Path}");
    return 0;
}

using var ctx = new TrainerContext();
try { ctx.Attach(pidOpt != null ? int.Parse(pidOpt) : null, s => { if (cmd == "detect") Console.WriteLine("  " + s); }); }
catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 2; }
var layout = ctx.Layout;
if (layout == null) { Console.Error.WriteLine("Guest memory not found (is a game running?). Run 'detect' for details."); return 3; }

var session = File.Exists(sessionPath) ? TrainerSession.Load(sessionPath) : new TrainerSession();
var warn = session.CheckBuild(layout); if (warn != null) Console.WriteLine("WARNING: " + warn);

switch (cmd)
{
    case "detect":
        Console.WriteLine();
        Console.WriteLine($"Mode:      {layout.Memory.Describe()}");
        Console.WriteLine($"Game:      {layout.GameTitle ?? "?"}  title id {layout.TitleId ?? "?"}");
        Console.WriteLine($"Main:      {(layout.Main != null ? $"{layout.Main.Va:X}" : "not found")}  build id {layout.BuildId ?? "?"}");
        Console.WriteLine($"Heap:      {layout.HeapBase:X} ({layout.HeapSize >> 20} MB)");
        foreach (var m in layout.Modules) Console.WriteLine($"  {m}");
        break;

    case "first":
    {
        var kind = Enum.Parse<ValueKind>(argv[1], true);
        var cmp = Enum.Parse<ScanCompare>(argv[2], true);
        var scope = scopeOpt == null ? ScanScope.AllGuest : scopeOpt.ToLowerInvariant() switch { "heap" => ScanScope.HeapOnly, "main" => ScanScope.MainModule, _ => ScanScope.AllGuest };
        var res = ctx.Scanner!.FirstScan(kind, new ScanRequest(cmp, argv.ElementAtOrDefault(3), argv.ElementAtOrDefault(4), Tol()), alignOpt != null ? int.Parse(alignOpt) : 0, scope, Progress());
        Console.WriteLine();
        session.SetScan(res, sessionPath);
        session.Save(sessionPath, layout);
        PrintResults(res, 20);
        break;
    }
    case "next":
    {
        if (session.Scan == null) { Console.Error.WriteLine("No previous scan in session."); return 4; }
        var cmp = Enum.Parse<ScanCompare>(argv[1], true);
        var res = ctx.Scanner!.NextScan(session.Scan.ToResults(), new ScanRequest(cmp, argv.ElementAtOrDefault(2), argv.ElementAtOrDefault(3), Tol()), Progress());
        Console.WriteLine();
        session.SetScan(res, sessionPath);
        session.Save(sessionPath, layout);
        PrintResults(res, 30);
        break;
    }
    case "results":
        if (session.Scan == null) { Console.Error.WriteLine("No scan in session."); return 4; }
        PrintResults(session.Scan.ToResults(), argv.Count > 1 ? int.Parse(argv[1]) : 50);
        break;

    case "read":
    {
        ulong va = Hex(argv[1]); var kind = Enum.Parse<ValueKind>(argv[2], true);
        var b = layout.Memory.ReadBytes(va, ValueCodec.SizeOf(kind));
        Console.WriteLine(b == null ? "unmapped" : $"{layout.FormatAddress(va)} = {ValueCodec.Format(kind, b)}");
        break;
    }
    case "hexdump":
    {
        ulong va = Hex(argv[1]); int len = argv.Count > 2 ? int.Parse(argv[2]) : 256;
        var b = layout.Memory.ReadBytes(va, len);
        if (b == null) { Console.WriteLine("unmapped"); break; }
        for (int i = 0; i < b.Length; i += 16)
            Console.WriteLine($"{va + (ulong)i:X10}  {BitConverter.ToString(b, i, Math.Min(16, b.Length - i)).Replace('-', ' ')}");
        break;
    }
    case "check": // would each saved entry be writable right now? (no writes)
        foreach (var e in session.Entries)
        {
            var at = ShowTrainer.Core.Resolve.PointerResolver.CheckForWrite(layout, e, out var why);
            var b = at is ulong a ? layout.Memory.ReadBytes(a, ValueCodec.SizeOf(e.Kind)) : null;
            Console.WriteLine($"{e.Name,-28} {(at is ulong x ? $"OK {x:X10} = {(b == null ? "?" : ValueCodec.Format(e.Kind, e.Own(b)))}" : "REFUSED: " + why)}");
        }
        break;
    case "set": // set <entry name> <value> --backed-up
    {
        if (warn != null) { Console.Error.WriteLine("Refused: wrong game build."); return 5; }
        if (!argv.Contains("--backed-up")) { Console.Error.WriteLine("Refused: back up your save, then add --backed-up."); return 5; }
        var e = session.Entries.Single(x => x.Name.Equals(argv[1], StringComparison.OrdinalIgnoreCase));
        double v = ValueCodec.ParseDouble(argv[2]);
        if (v < (e.Min ?? v) || v > (e.Max ?? v)) { Console.Error.WriteLine($"Refused: outside {e.Min}..{e.Max}."); return 5; }
        var why = ctx.Freezer.Write(e, argv[2]);
        Console.WriteLine(why == null ? "written" : "REFUSED: " + why);
        if (why != null) return 5;
        break;
    }
    case "ptrscan": // ptrscan <va> <out.json> [depth] [firstMaxHex] [nextMaxHex]
    {
        var chains = ShowTrainer.Core.Resolve.PointerScanner.Scan(layout, Hex(argv[1]), argv.Count > 3 ? int.Parse(argv[3]) : 4,
            argv.Count > 4 ? (long)Hex(argv[4]) : 0x2000, argv.Count > 5 ? (long)Hex(argv[5]) : 0x1000);
        File.WriteAllText(argv[2], System.Text.Json.JsonSerializer.Serialize(chains, TrainerSession.Json));
        Console.WriteLine($"{chains.Count} chains -> {argv[2]}");
        foreach (var c in chains.Take(20)) Console.WriteLine("  " + c);
        break;
    }
    case "ptrcheck": // ptrcheck <chains.json>: group chains by the address they resolve to now
    {
        var chains = System.Text.Json.JsonSerializer.Deserialize<List<PointerChain>>(File.ReadAllText(argv[1]), TrainerSession.Json)!;
        foreach (var g in chains.GroupBy(c => ShowTrainer.Core.Resolve.PointerResolver.Resolve(layout, c)).OrderByDescending(g => g.Count()).Take(15))
            Console.WriteLine($"{(g.Key is ulong a ? $"{a:X10} {BitConverter.ToString(layout.Memory.ReadBytes(a, 16) ?? Array.Empty<byte>())}" : "unresolved")}  {g.Count()} chains, e.g. {g.First()}");
        break;
    }
    case "group": // group <kind> <window> <required,csv> [optional,csv]
    {
        static int[] Csv(string? t) => string.IsNullOrEmpty(t) ? Array.Empty<int>() : t.Split(',').Select(int.Parse).ToArray();
        var hits = GroupScanner.Scan(layout, Enum.Parse<ValueKind>(argv[1], true), int.Parse(argv[2]), Csv(argv[3]), Csv(argv.ElementAtOrDefault(4)), 12);
        Console.WriteLine($"{hits.Count} groups shown");
        foreach (var h in hits) Console.WriteLine($"  {layout.FormatAddress(h.Va)} score={h.Score}: {h.Values}");
        break;
    }
    case "export": // export <file>: proven entries at their current values
    {
        var items = new List<(AddressEntry, string, long)>();
        foreach (var e in session.Entries.Where(x => x.VerifiedPersists && x.Pointer != null && x.Mask == null))
        {
            if (ShowTrainer.Core.Resolve.PointerResolver.CheckForWrite(layout, e, out var why) is not ulong at) { Console.Error.WriteLine($"{e.Name}: {why}"); return 6; }
            items.Add((e, ValueCodec.Format(e.Kind, layout.Memory.ReadBytes(at, ValueCodec.SizeOf(e.Kind))!), ShowTrainer.Core.Resolve.PointerResolver.RowShift(layout, e) ?? 0));
        }
        File.WriteAllText(argv[1], ShowTrainer.Core.Export.AtmosphereCheats.Export(layout.BuildId!, items, out var words));
        Console.WriteLine($"{items.Count} cheats, {words} words -> {argv[1]}");
        break;
    }
    case "cheatsim": // cheatsim <file>: run each cheat read-only, compare its write with the guarded address and live value
        foreach (var (name, code) in ShowTrainer.Core.Export.AtmosphereCheats.Parse(File.ReadAllText(argv[1])))
        {
            var e = session.Entries.Single(x => name.StartsWith($"{x.Category}: {x.Name} = "));
            var want = ShowTrainer.Core.Resolve.PointerResolver.CheckForWrite(layout, e, out _);
            var r = ShowTrainer.Core.Export.AtmosphereCheats.Simulate(layout, code);
            var now = want is ulong w ? ValueCodec.ToDouble(e.Kind, layout.Memory.ReadBytes(w, ValueCodec.SizeOf(e.Kind))!) : -1;
            bool ok = r is { } x2 && x2.Va == want && x2.Size == ValueCodec.SizeOf(e.Kind) && x2.Value == (ulong)now;
            Console.WriteLine($"{(ok ? "MATCH " : "DIFFER")} {name} -> {(r is { } q ? $"{q.Va:X10} size {q.Size} value {q.Value}" : "no write")}");
        }
        break;
    case "players": // players <name part>: find players and show their ratings (no writes)
    {
        var found = ShowTrainer.Core.Resolve.PlayerTable.Find(layout, argv[1]);
        Console.WriteLine($"{found.Count} match(es)");
        foreach (var p in found.Take(10))
        {
            Console.WriteLine($"{p.Name} ({p.Position})  entry {p.Entry:X10}  ratings {p.Ratings:X10}");
            Console.WriteLine("  " + string.Join("  ", ShowTrainer.Core.Resolve.PlayerTable.Fields.Select(f => $"{f.Name}={ShowTrainer.Core.Resolve.PlayerTable.Read(layout, p, f)}")));
        }
        break;
    }
    case "pinfo": // pinfo <name part>: positions, role, two-way and quirks (no writes)
    {
        var found = PT.Find(layout, argv[1]);
        Console.WriteLine($"{found.Count} match(es)");
        foreach (var p in found.Take(10))
        {
            Console.WriteLine($"{p.Name} ({p.Position}, overall {PT.Read(layout, p, PT.Fields[0])}){(PT.IsUser(layout, p) ? "  [your RTTS player]" : "")}  entry {p.Entry:X10}");
            if (PT.ReadInfo(layout, p) is { } info) Console.WriteLine("  " + string.Join("  ", info.Select(x => $"{x.Name} {x.Value}")));
            foreach (var g in PT.EntryFields.GroupBy(f => f.Group))
                Console.WriteLine($"  {g.Key}: " + (g.All(f => f.IsFlag)
                    ? string.Join(", ", g.Where(f => PT.Read(layout, p, f) == 1).Select(f => f.Name)) is { Length: > 0 } on ? on : "none"
                    : string.Join("  ", g.Select(f => $"{f.Name}={(PT.Read(layout, p, f) is int v ? f.Describe(v) : "?")}"))));
        }
        break;
    }
    case "pset": // pset "<full name>" "<rating or setting>" <value> --backed-up
    {
        if (warn != null) { Console.Error.WriteLine("Refused: wrong game build."); return 5; }
        if (!argv.Contains("--backed-up")) { Console.Error.WriteLine("Refused: back up your save, then add --backed-up."); return 5; }
        if (OnePlayer(argv[1]) is not { } p) return 5;
        string? why;
        if (PT.Fields.SingleOrDefault(x => x.Name.Equals(argv[2], StringComparison.OrdinalIgnoreCase)) is { } f)
        {
            if (!int.TryParse(argv[3], out var v)) { Console.Error.WriteLine("Refused: ratings are 0..99."); return 5; }
            why = PT.Write(layout, p, f, v, Remembering());
            if (why == null) Console.WriteLine($"written; {f.Name} now {PT.Read(layout, p, f)}");
        }
        else if (PT.EntryFields.SingleOrDefault(x => x.Label.Equals(argv[2], StringComparison.OrdinalIgnoreCase)) is { } ef)
        {
            // value: a number, on/off for flags, or a choice name (SP/RP/CP, P..RF)
            var t = argv[3];
            int v = int.TryParse(t, out var n) ? n : t.ToLowerInvariant() switch { "on" or "yes" => 1, "off" or "no" => 0, _ => ef.Choices is { } c ? Array.FindIndex(c, x => x.Equals(t, StringComparison.OrdinalIgnoreCase)) : -1 };
            why = PT.Write(layout, p, ef, v, Remembering());
            if (why == null) Console.WriteLine($"written; {ef.Label} now {(PT.Read(layout, p, ef) is int r ? ef.Describe(r) : "?")}");
        }
        else
        {
            Console.Error.WriteLine($"Unknown field '{argv[2]}'. Ratings: {string.Join(", ", PT.Fields.Select(x => x.Name))}");
            Console.Error.WriteLine($"Settings: {string.Join(", ", PT.EntryFields.Select(x => x.Label))}");
            return 5;
        }
        if (why != null) { Console.WriteLine("REFUSED: " + why); return 5; }
        break;
    }
    case "psnap": // psnap "<full name>" [file]: save a player's ratings and settings
    {
        if (OnePlayer(argv[1]) is not { } p) return 5;
        var file = argv.ElementAtOrDefault(2) ?? Path.Combine(AppContext.BaseDirectory, "snapshots", $"{PS.SafeFileName(p.Name)}_{DateTime.Now:yyyyMMdd_HHmmss}.json");
        if (PS.Take(layout, p) is not { } snap) { Console.Error.WriteLine("Could not read the player."); return 6; }
        snap.Save(file);
        Console.WriteLine($"snapshot -> {file}");
        break;
    }
    case "pdiff": // pdiff "<full name>" <file>: live values vs a snapshot (no writes)
    case "prestore": // prestore "<full name>" <file> --backed-up: write the snapshot's values back
    {
        if (OnePlayer(argv[1]) is not { } p) return 5;
        var snap = PS.Load(argv[2]);
        if (!snap.Name.Equals(p.Name, StringComparison.OrdinalIgnoreCase)) { Console.Error.WriteLine($"Refused: snapshot is for {snap.Name}."); return 5; }
        var diff = snap.Compare(layout, p);
        Console.WriteLine($"{diff.Count} difference(s) from {snap.Taken:g}");
        foreach (var d in diff) Console.WriteLine($"  {d.Field,-28} now {d.NowText,-10} snapshot {d.ThenText}");
        if (cmd == "pdiff" || diff.Count == 0) break;
        if (warn != null) { Console.Error.WriteLine("Refused: wrong game build."); return 5; }
        if (!argv.Contains("--backed-up")) { Console.Error.WriteLine("Refused: back up your save, then add --backed-up."); return 5; }
        var err = snap.Restore(layout, p, Remembering(), out var written);
        Console.WriteLine(err == null ? $"restored {written} value(s)" : $"STOPPED after {written}: {err}");
        if (err != null) return 5;
        break;
    }
    case "leaguesnap": // leaguesnap <file>: every player's ratings and settings
    {
        var all = PS.TakeAll(layout);
        PS.SaveAll(argv[1], all);
        Console.WriteLine($"{all.Count} players -> {argv[1]}");
        break;
    }
    case "leaguediff": // leaguediff <file> [max]: which players changed since a league snapshot (no writes)
    {
        var then = PS.LoadAll(argv[1]);
        var now = PT.All(layout);
        int max = argv.Count > 2 ? int.Parse(argv[2]) : 200, changed = 0, missing = 0;
        foreach (var g in then.GroupBy(x => x.Name))
        {
            var live = now.Where(p => p.Name == g.Key).ToList();
            if (live.Count != 1 || g.Count() != 1) { missing++; continue; } // gone, or a shared name: can't pair safely
            var diff = g.First().Compare(layout, live[0]);
            if (diff.Count == 0) continue;
            if (++changed <= max) Console.WriteLine($"{g.Key}: " + string.Join(", ", diff.Select(d => $"{d.Field} {d.ThenText}->{d.NowText}")));
        }
        Console.WriteLine($"{changed} of {then.Count} players changed since {then.FirstOrDefault()?.Taken:g}; {missing} not compared (missing or shared name)");
        break;
    }
    case "pdump": // pdump <file.json> "<full name>"...: raw bytes of each player's entry and the records it points to (read-only)
    {
        var res = new Dictionary<string, Dictionary<string, string>>();
        foreach (var n in argv.Skip(2))
        {
            if (OnePlayer(n) is not { } p) continue;
            var ent = layout.Memory.ReadBytes(p.Entry, PT.Stride)!;
            var d = new Dictionary<string, string> { ["entry"] = Convert.ToHexString(ent), ["entry_va"] = p.Entry.ToString("X") };
            foreach (var off in new[] { 0x00, 0x08, 0x10, 0x18, 0x30, 0x38, 0x40, 0x48, 0x70, 0x78, 0x88, 0x90, 0xA8 })
            {
                ulong ptr = BitConverter.ToUInt64(ent, off);
                if (ptr - layout.HeapBase < layout.HeapSize && layout.Memory.ReadBytes(ptr, 0x200) is { } rec) d[$"p{off:X2}"] = Convert.ToHexString(rec);
            }
            res[p.Name] = d;
        }
        File.WriteAllText(argv[1], System.Text.Json.JsonSerializer.Serialize(res));
        Console.WriteLine($"{res.Count} players -> {argv[1]}");
        break;
    }
    case "near": // near <int16|int32> <window bytes> <v1,v2,...> [max]: places in the heap where all values occur within the window (read-only)
    {
        var kind = Enum.Parse<ValueKind>(argv[1], true); int win = int.Parse(argv[2]), size = ValueCodec.SizeOf(kind);
        var vals = argv[3].Split(',').Select(long.Parse).ToArray(); int max = argv.Count > 4 ? int.Parse(argv[4]) : 30, found = 0;
        layout.Memory.Refresh();
        foreach (var seg in layout.Memory.Segments.Where(x => x.Va >= layout.HeapBase && x.Va < layout.HeapBase + layout.HeapSize))
            for (ulong off = 0; off < seg.Size && found < max; off += 1 << 20)
            {
                int len = (int)Math.Min((1UL << 20) + (ulong)win, seg.Size - off);
                var buf = new byte[len];
                if (!layout.Memory.Process.Read(seg.Host + off, buf)) continue;
                long V(int i) => size == 2 ? BitConverter.ToUInt16(buf, i) : BitConverter.ToUInt32(buf, i);
                for (int i = 0; i + size <= Math.Min(len, 1 << 20) && found < max; i += size)
                {
                    if (V(i) != vals[0]) continue;
                    int lo = Math.Max(0, i - win), hi = Math.Min(len - size, i + win);
                    bool all = vals.Skip(1).All(v => { for (int j = lo - lo % size; j <= hi; j += size) if (V(j) == v) return true; return false; });
                    if (!all) continue;
                    found++;
                    ulong va = seg.Va + off + (ulong)i;
                    Console.WriteLine($"{layout.FormatAddress(va)}  " + string.Join(" ", Enumerable.Range(-8, 24).Select(k => i + k * size).Where(j => j >= 0 && j + size <= len).Select(j => V(j).ToString())));
                }
            }
        Console.WriteLine($"{found} hit(s)");
        break;
    }
    case "myedits": // myedits [--reapply --backed-up]: remembered player edits for this career, and which the game changed
    {
        if (PT.UserName(layout) is not string career) { Console.Error.WriteLine("Career not readable (loading?)."); return 6; }
        var mem = EditMemory.Open(MemoryDir_(), career, layout.BuildId);
        var st = mem.Status(layout, PT.All(layout));
        Console.WriteLine($"{career}: {st.Count} remembered edit(s) ({mem.Path})");
        foreach (var x in st) Console.WriteLine($"  {x.Edit.Player,-22} {x.Edit.Field,-26} yours {PT.DescribeField(x.Edit.Field, x.Edit.Value),-6} original {PT.DescribeField(x.Edit.Field, x.Edit.Original),-6} {x.State}");
        if (!argv.Contains("--reapply")) break;
        if (warn != null) { Console.Error.WriteLine("Refused: wrong game build."); return 5; }
        if (!argv.Contains("--backed-up")) { Console.Error.WriteLine("Refused: back up your save, then add --backed-up."); return 5; }
        var (n, err) = mem.Reapply(layout, st, Remembering());
        Console.WriteLine(err == null ? $"re-applied {n}" : $"STOPPED after {n}: {err}");
        if (err != null) return 5;
        break;
    }
    case "leaguecsv": // leaguecsv <file.csv>: every player's ratings and settings (read-only)
    {
        var all = PS.TakeAll(layout);
        File.WriteAllText(argv[1], PS.ToCsv(all), new System.Text.UTF8Encoding(true));
        Console.WriteLine($"{all.Count} players -> {argv[1]}");
        break;
    }
    case "segments":
        foreach (var s in layout.Memory.Segments) Console.WriteLine(s);
        break;
    default:
        Usage(); return 1;
}
return 0;

// Player writes from the CLI go into the same per-career edit memory as the app's.
EditHistory? Remembering()
{
    if (PT.UserName(layout!) is not string career) { Console.Error.WriteLine("Note: career not readable; this edit will not be remembered."); return null; }
    var mem = EditMemory.Open(MemoryDir_(), career, layout!.BuildId);
    var h = new EditHistory();
    h.Recorded += (r, undone) => { if (r.Player != null && r.OldValue is int o && r.NewValue is int n) mem.Remember(r.Player, r.Field, undone ? n : o, undone ? o : n); };
    return h;
}

PlayerRef? OnePlayer(string name)
{
    var ps = PT.All(layout!).Where(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToList();
    if (ps.Count == 1) return ps[0];
    Console.Error.WriteLine($"Refused: {ps.Count} players named {name}.");
    return null;
}

static string MemoryDir_() => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ShowTrainer", "edit-memory");

double? Tol() => tolOpt != null ? double.Parse(tolOpt, CultureInfo.InvariantCulture) : null;

void PrintResults(ScanResults r, int max)
{
    Console.WriteLine($"Scan #{r.ScanNumber} ({r.Description}): {r.Count:N0} results{(r.Truncated ? " (truncated)" : "")}");
    if (r.IsSnapshot) return;
    for (int i = 0; i < Math.Min(max, r.Addresses.Length); i++)
    {
        var va = r.Addresses[i];
        var cur = layout!.Memory.ReadBytes(va, r.ValueSize);
        Console.WriteLine($"  {layout.FormatAddress(va),-48} at-scan={ValueCodec.Format(r.Kind, r.ValueAt(i)),-12} now={(cur != null ? ValueCodec.Format(r.Kind, cur) : "?")}");
    }
}

IProgress<double> Progress()
{
    int last = -1;
    return new Progress<double>(p => { int pc = (int)(p * 100); if (pc / 10 != last / 10) { last = pc; Console.Write($"\r  {pc}%  "); } });
}

static ulong Hex(string s) => ulong.Parse(s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? s[2..] : s, NumberStyles.HexNumber);

static void Usage() => Console.WriteLine("""
usage: showtrainer-cli <command> [args] [--session file] [--pid n] [-v]
  list                                       list emulator processes
  detect                                     find guest memory, modules, heap, build id
  first <kind> exact <value> [--scope heap|main|all] [--align n] [--tol t]
  first <kind> between <lo> <hi>
  first <kind> unknown                       snapshot for changed/unchanged scans
  next  <exact|changed|unchanged|increased|decreased|increasedby|decreasedby|between> [v1] [v2]
  results [n]                                show results with live values
  read <va> <kind>
  check                                      test every saved entry (no writes)
  set <entry name> <value> --backed-up       guarded write to a saved entry
  hexdump <va> [len] | segments
  players <name part>                        find players, show ratings
  pinfo <name part>                          positions, role, two-way, quirks
  pset "<full name>" "<field>" <value> --backed-up   guarded write: a rating (0-99) or a setting
                                             e.g. "Quirk: Dead Red" on, "Pitching role" CP, "Secondary RF" 1
  psnap "<full name>" [file]                 save a player's ratings and settings
  pdiff "<full name>" <file>                 compare with a snapshot (no writes)
  prestore "<full name>" <file> --backed-up  write a snapshot's values back
  leaguesnap <file> | leaguediff <file> [n]  snapshot every player / list who changed since
  leaguecsv <file.csv>                       every player's ratings and settings as CSV
  myedits [--reapply --backed-up]            remembered player edits (shared with the app); re-apply ones the game changed
kinds: byte int16 int32 int64 float double
""");
