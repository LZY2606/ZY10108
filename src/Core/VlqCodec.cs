using System.Globalization;
using System.Text;

namespace SourceMapChain.Core;

public static class VlqCodec
{
    private const string Base64 = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
    private static readonly int[] Values = CreateValues();

    public static int[] DecodeSegment(string segment)
    {
        if (segment.Length == 0)
        {
            throw new SourceMapException("VLQ segment must not be empty.");
        }

        var values = new List<int>();
        var digitCount = 0;
        var accumulator = 0;
        var continuation = false;

        foreach (var ch in segment)
        {
            var value = ch < Values.Length ? Values[ch] : -1;
            if (value < 0)
            {
                throw new SourceMapException($"Invalid Base64 VLQ character '{ch}'.");
            }

            digitCount++;
            continuation = (value & 32) != 0;
            accumulator += (value & 31) << (5 * (digitCount - 1));
            if (!continuation)
            {
                var signed = accumulator >> 1;
                if ((accumulator & 1) != 0)
                {
                    signed = -signed;
                }

                values.Add(signed);
                accumulator = 0;
                digitCount = 0;
            }
        }

        if (continuation)
        {
            throw new SourceMapException("Truncated Base64 VLQ value.");
        }

        if (values.Count is not (1 or 4 or 5))
        {
            throw new SourceMapException(
                string.Create(CultureInfo.InvariantCulture, $"VLQ segment must contain 1, 4, or 5 fields, but contained {values.Count}."));
        }

        return values.ToArray();
    }

    public static string EncodeSegment(IEnumerable<int> values)
    {
        var builder = new StringBuilder();
        foreach (var value in values)
        {
            var signed = value < 0 ? ((-value) << 1) | 1 : value << 1;
            do
            {
                var digit = signed & 31;
                signed >>>= 5;
                if (signed > 0)
                {
                    digit |= 32;
                }

                builder.Append(Base64[digit]);
            } while (signed > 0);
        }

        return builder.ToString();
    }

    private static int[] CreateValues()
    {
        var values = Enumerable.Repeat(-1, 128).ToArray();
        for (var i = 0; i < Base64.Length; i++)
        {
            values[Base64[i]] = i;
        }

        return values;
    }
}
