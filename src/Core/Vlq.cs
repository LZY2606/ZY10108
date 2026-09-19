namespace SourceMapChains.Core;

/// <summary>Base64 VLQ codec used by the source map "mappings" field.</summary>
public static class Vlq
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
    private static readonly Dictionary<char, int> DecodeTable = BuildTable();

    private static Dictionary<char, int> BuildTable()
    {
        var table = new Dictionary<char, int>(64);
        for (var i = 0; i < Alphabet.Length; i++) table[Alphabet[i]] = i;
        return table;
    }

    /// <summary>Decodes a run of VLQ values (already split per segment).</summary>
    public static int[] Decode(string input)
    {
        var result = new List<int>();
        var value = 0;
        var shift = 0;
        foreach (var c in input)
        {
            if (!DecodeTable.TryGetValue(c, out var digit))
                throw new FormatException($"Invalid VLQ character '{c}'.");
            var continuation = (digit & 32) != 0;
            digit &= 31;
            value += digit << shift;
            if (continuation)
            {
                shift += 5;
                continue;
            }
            var negative = (value & 1) == 1;
            value >>= 1;
            result.Add(negative ? -value : value);
            value = 0;
            shift = 0;
        }
        if (shift != 0) throw new FormatException("Truncated VLQ sequence.");
        return result.ToArray();
    }

    public static string Encode(IEnumerable<int> values)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var v in values) EncodeOne(sb, v);
        return sb.ToString();
    }

    private static void EncodeOne(System.Text.StringBuilder sb, int value)
    {
        var vlq = value < 0 ? ((-value) << 1) + 1 : value << 1;
        do
        {
            var digit = vlq & 31;
            vlq >>= 5;
            if (vlq > 0) digit |= 32;
            sb.Append(Alphabet[digit]);
        } while (vlq > 0);
    }
}
