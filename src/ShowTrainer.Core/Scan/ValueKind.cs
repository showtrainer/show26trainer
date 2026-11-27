using System.Buffers.Binary;
using System.Globalization;

namespace ShowTrainer.Core.Scan;

public enum ValueKind { Byte, Int16, Int32, Int64, Float, Double }

public enum ScanCompare
{
    Exact, Changed, Unchanged, Increased, Decreased, IncreasedBy, DecreasedBy, Between, Unknown,
}

public static class ValueCodec
{
    public static int SizeOf(ValueKind k) => k switch
    {
        ValueKind.Byte => 1, ValueKind.Int16 => 2, ValueKind.Int32 => 4, ValueKind.Int64 => 8,
        ValueKind.Float => 4, ValueKind.Double => 8, _ => throw new ArgumentOutOfRangeException(nameof(k)),
    };

    public static bool IsFloat(ValueKind k) => k is ValueKind.Float or ValueKind.Double;

    /// <summary>Encode a user-entered value. Integers accept decimal, negative, or 0x hex.</summary>
    public static byte[] Parse(ValueKind k, string text)
    {
        text = text.Trim();
        var b = new byte[SizeOf(k)];
        if (IsFloat(k))
        {
            double d = double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);
            if (k == ValueKind.Float) BinaryPrimitives.WriteSingleLittleEndian(b, (float)d);
            else BinaryPrimitives.WriteDoubleLittleEndian(b, d);
            return b;
        }
        long v = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? (long)ulong.Parse(text[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture)
            : long.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture);
        switch (k)
        {
            case ValueKind.Byte: b[0] = checked((byte)v); break;
            case ValueKind.Int16: BinaryPrimitives.WriteInt16LittleEndian(b, checked((short)v)); break;
            case ValueKind.Int32: BinaryPrimitives.WriteInt32LittleEndian(b, checked((int)v)); break;
            case ValueKind.Int64: BinaryPrimitives.WriteInt64LittleEndian(b, v); break;
        }
        return b;
    }

    public static double ParseDouble(string text) =>
        text.Trim().StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? ulong.Parse(text.Trim()[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture)
            : double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);

    /// <summary>Default tolerance for float "exact": half a unit of the last typed digit (so "95" matches 94.5..95.5, "0.95" matches 0.945..0.955).</summary>
    public static double DefaultTolerance(string text)
    {
        text = text.Trim();
        int dot = text.IndexOf('.');
        int decimals = dot < 0 ? 0 : text.Length - dot - 1;
        return 0.5 * Math.Pow(10, -decimals);
    }

    public static double ToDouble(ValueKind k, ReadOnlySpan<byte> b) => k switch
    {
        ValueKind.Byte => b[0],
        ValueKind.Int16 => BinaryPrimitives.ReadInt16LittleEndian(b),
        ValueKind.Int32 => BinaryPrimitives.ReadInt32LittleEndian(b),
        ValueKind.Int64 => BinaryPrimitives.ReadInt64LittleEndian(b),
        ValueKind.Float => BinaryPrimitives.ReadSingleLittleEndian(b),
        ValueKind.Double => BinaryPrimitives.ReadDoubleLittleEndian(b),
        _ => 0,
    };

    public static string Format(ValueKind k, ReadOnlySpan<byte> b) => k switch
    {
        ValueKind.Byte => b[0].ToString(CultureInfo.InvariantCulture),
        ValueKind.Int16 => BinaryPrimitives.ReadInt16LittleEndian(b).ToString(CultureInfo.InvariantCulture),
        ValueKind.Int32 => BinaryPrimitives.ReadInt32LittleEndian(b).ToString(CultureInfo.InvariantCulture),
        ValueKind.Int64 => BinaryPrimitives.ReadInt64LittleEndian(b).ToString(CultureInfo.InvariantCulture),
        ValueKind.Float => BinaryPrimitives.ReadSingleLittleEndian(b).ToString("G7", CultureInfo.InvariantCulture),
        ValueKind.Double => BinaryPrimitives.ReadDoubleLittleEndian(b).ToString("G15", CultureInfo.InvariantCulture),
        _ => "?",
    };
}
