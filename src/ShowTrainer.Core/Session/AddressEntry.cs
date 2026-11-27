using System.Text.Json.Serialization;
using ShowTrainer.Core.Scan;

namespace ShowTrainer.Core.Session;

/// <summary>Games often keep several copies of a value. Recording which one an address is matters for writes.</summary>
public enum CopyKind
{
    Unknown,
    /// <summary>UI/display copy: changing it changes what is shown, but it is overwritten from the real value.</summary>
    Display,
    /// <summary>Stored copy: the value that is written to the save file. Edits persist after save + reload.</summary>
    Stored,
    /// <summary>Derived value (e.g. an overall rating) recomputed from other values.</summary>
    Calculated,
}

/// <summary>A named, saved address (the "cheat table" row).</summary>
public sealed class AddressEntry
{
    public string Name { get; set; } = "";
    /// <summary>Target section, e.g. "Pitching", "Season stats", "RTTS points", "Positions".</summary>
    public string Category { get; set; } = "General";
    public ValueKind Kind { get; set; } = ValueKind.Int32;
    /// <summary>Guest virtual address resolved for the current launch.</summary>
    public ulong Address { get; set; }
    public CopyKind Copy { get; set; } = CopyKind.Unknown;
    /// <summary>True once the user confirmed a write survives save + reload.</summary>
    public bool VerifiedPersists { get; set; }
    public string Notes { get; set; } = "";
    public bool Frozen { get; set; }
    public string? FreezeValue { get; set; }
    /// <summary>Optional clamp for the UI editor, e.g. 0..99 for attributes.</summary>
    public double? Min { get; set; }
    public double? Max { get; set; }

    /// <summary>Byte entries only: the bits this entry owns. Values are shifted into them and all other bits are kept.</summary>
    public byte? Mask { get; set; }

    /// <summary>The entry's own value from its raw bytes (just the masked bits when there is a mask).</summary>
    public byte[] Own(byte[] raw) => Mask is byte m && m != 0 && raw.Length == 1
        ? new[] { (byte)((raw[0] & m) >> System.Numerics.BitOperations.TrailingZeroCount(m)) } : raw;

    /// <summary>Set for rows of a per-year table whose order shifts when a season is added.</summary>
    public SeasonRow? Row { get; set; }

    // Phase 2: how to re-resolve the address after relaunch.
    public PointerChain? Pointer { get; set; }
    /// <summary>Other chains to the same value; MinAgree of all chains must reach the address before a write.</summary>
    public List<PointerChain>? AltPointers { get; set; }
    public int MinAgree { get; set; } = 1;
    /// <summary>Byte ranges that must look plausible; catches records zeroed or reused during loading.</summary>
    public List<GuardRange>? Guards { get; set; }
    public AobSignature? Signature { get; set; }
    /// <summary>Offset from main module when the value lives in main's .data/.bss (stable across launches).</summary>
    public long? MainOffset { get; set; }
    public long? HeapOffset { get; set; }

    /// <summary>True only when Address is known good for the running game; writes and freezes require it.</summary>
    [JsonIgnore] public bool Resolved { get; set; }

    [JsonIgnore] public string? LiveValue { get; set; }
}

/// <summary>[[main + Base] + O1] + ... + Last  (all guest addresses).</summary>
public sealed class PointerChain
{
    public long BaseOffset { get; set; }
    public List<long> Offsets { get; set; } = new();
    public override string ToString() =>
        "[main+" + BaseOffset.ToString("X") + "]" + string.Concat(Offsets.Select((o, i) => (i < Offsets.Count - 1 ? " -> [+" : " -> +") + o.ToString("X") + (i < Offsets.Count - 1 ? "]" : "")));
}

/// <summary>The pointer leads to this field in row 0; the row whose year word (at value + YearOffset) equals Year is used.
/// Exactly one row must match.</summary>
public sealed class SeasonRow
{
    public int Year { get; set; }
    public int YearOffset { get; set; }
    public int Stride { get; set; }
    public int MaxRows { get; set; } = 24;
}

/// <summary>Every byte in [Address+Offset, +Length) must be within Min..Max.</summary>
public sealed record GuardRange(int Offset, int Length, byte Min, byte Max);

/// <summary>Byte signature near the value: address = match + ValueOffset. Pattern uses "??" for wildcards.</summary>
public sealed class AobSignature
{
    public string Pattern { get; set; } = "";
    public int ValueOffset { get; set; }
    public string Scope { get; set; } = "heap";
}
