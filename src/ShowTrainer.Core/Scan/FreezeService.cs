using System.Collections.Concurrent;
using ShowTrainer.Core.Guest;
using ShowTrainer.Core.Infra;
using ShowTrainer.Core.Resolve;
using ShowTrainer.Core.Session;

namespace ShowTrainer.Core.Scan;

/// <summary>Writes values and keeps frozen values pinned. Every write re-resolves and sanity-checks its target first.
/// This narrows, but cannot remove, the gap between the check and the write.</summary>
public sealed class FreezeService : IDisposable
{
    private readonly ConcurrentDictionary<AddressEntry, byte[]> _frozen = new();
    private readonly ConcurrentDictionary<AddressEntry, string> _paused = new();
    private readonly Timer _timer;
    private GuestLayout? _layout;
    private int _busy;
    private long _lastRefresh;

    public FreezeService(int intervalMs = 100) => _timer = new Timer(_ => Tick(), null, intervalMs, intervalMs);

    public void Attach(GuestLayout? layout) => _layout = layout;

    /// <summary>User writes (not freeze ticks) are recorded here for undo.</summary>
    public EditHistory History { get; } = new();

    /// <summary>Returns null on success, else the reason nothing was written.</summary>
    public string? Write(AddressEntry e, string value)
    {
        _layout?.Memory.Refresh();
        var why = TryWrite(e, ValueCodec.Parse(e.Kind, value), record: true);
        Log.Info($"Write {e.Name} = {value}: {why ?? "ok"}");
        return why;
    }

    private string? TryWrite(AddressEntry e, byte[] bytes, bool record = false)
    {
        var l = _layout;
        if (l == null) return "not attached";
        if (PointerResolver.CheckForWrite(l, e, out var why) is not ulong at) return why;
        var before = record ? l.Memory.ReadBytes(at, ValueCodec.SizeOf(e.Kind)) : null;
        if (record && before == null) return "memory read failed";
        if (e.Mask is byte m)
        {
            if (m == 0 || bytes.Length != 1) return "a mask needs a byte entry";
            if (l.Memory.ReadBytes(at, 1) is not [var old]) return "memory read failed";
            int v = bytes[0] << System.Numerics.BitOperations.TrailingZeroCount(m);
            if ((v & ~m) != 0) return "value does not fit the masked bits";
            bytes = new[] { (byte)((old & ~m) | v) };
        }
        if (!l.Memory.Write(at, bytes)) return "memory write failed";
        if (record && !before!.AsSpan().SequenceEqual(bytes))
            History.Add(new EditRecord
            {
                Target = e.Category, Field = e.Name, Address = at, Before = before!, After = bytes,
                OldText = ValueCodec.Format(e.Kind, e.Own(before!)), NewText = ValueCodec.Format(e.Kind, e.Own(bytes)),
                Revalidate = gl => PointerResolver.CheckForWrite(gl, e, out var w) is ulong a ? (a == at ? null : "the value has moved (new season row or relaunch)") : w,
            });
        return null;
    }

    public void Freeze(AddressEntry e, string value) { _frozen[e] = ValueCodec.Parse(e.Kind, value); Log.Info($"Freeze {e.Name} = {value}"); }
    public void Unfreeze(AddressEntry e) { _paused.TryRemove(e, out _); if (_frozen.TryRemove(e, out _)) Log.Info($"Unfreeze {e.Name}"); }
    public void Clear() { _frozen.Clear(); _paused.Clear(); }

    private void Tick()
    {
        if (_layout == null || _frozen.IsEmpty || Interlocked.Exchange(ref _busy, 1) == 1) return;
        try
        {
            if (Environment.TickCount64 - _lastRefresh > 1000) { _layout.Memory.Refresh(); _lastRefresh = Environment.TickCount64; }
            foreach (var kv in _frozen)
            {
                var why = TryWrite(kv.Key, kv.Value);
                if (why == null) { if (_paused.TryRemove(kv.Key, out _)) Log.Info($"Freeze resumed: {kv.Key.Name}"); }
                else if (_paused.TryAdd(kv.Key, why)) Log.Warn($"Freeze paused: {kv.Key.Name}: {why}");
            }
        }
        catch (Exception ex) { Log.Error("Freeze: " + ex.Message); }
        finally { Volatile.Write(ref _busy, 0); }
    }

    public void Dispose() => _timer.Dispose();
}
