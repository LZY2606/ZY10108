using SourceMapChains.Core;
using Xunit;

namespace Core.Tests;

public class VlqAndParseTests
{
    [Fact]
    public void Vlq_RoundTrip()
    {
        var values = new[] { 0, 1, -1, 15, -16, 1234, -987, int.MaxValue / 2 };
        var encoded = Vlq.Encode(values);
        Assert.Equal(values, Vlq.Decode(encoded));
    }

    [Fact]
    public void Vlq_Decodes_Known_Sequence()
    {
        Assert.Equal(new[] { 0, 0, 0, 0 }, Vlq.Decode("AAAA"));
        Assert.Equal(new[] { 0, 0, 1, 0 }, Vlq.Decode("AACA"));
    }

    [Fact]
    public void Parse_Empty_Lines_And_Segments()
    {
        var map = SourceMapDocument.Parse(TestData.MapJson("f.js", new[] { "a.ts" }, "AAAA;;AACA"));
        Assert.Equal(3, map.Lines.Count);
        Assert.Single(map.Lines[0]);
        Assert.Empty(map.Lines[1]); // empty line: fixed behavior = zero segments
        Assert.Single(map.Lines[2]);
        Assert.Equal(1, map.Lines[2][0].SrcLine);
    }

    [Fact]
    public void Parse_Unmapped_Generated_Only_Segment()
    {
        var map = SourceMapDocument.Parse(TestData.MapJson("f.js", new[] { "a.ts" }, "A,AAAA"));
        Assert.Equal(2, map.Lines[0].Count);
        Assert.Equal(-1, map.Lines[0][0].SrcIndex);
        Assert.Equal(0, map.Lines[0][1].SrcIndex);
    }

    [Fact]
    public void Parse_Cross_Line_Concatenation_Keeps_Relative_Deltas_Per_Line()
    {
        // genCol resets each line; srcLine/srcCol keep running across lines.
        var map = SourceMapDocument.Parse(TestData.MapJson("f.js", new[] { "a.ts" }, "AAAA;AACA;AACA"));
        Assert.Equal(0, map.Lines[1][0].GenCol);
        Assert.Equal(1, map.Lines[1][0].SrcLine);
        Assert.Equal(2, map.Lines[2][0].SrcLine);
    }

    [Fact]
    public void Glb_Lookup_Finds_Greatest_Lower_Bound()
    {
        var map = SourceMapDocument.Parse(TestData.MapJson("f.js", new[] { "a.ts" }, "AAAA,KAAK"));
        var seg = map.FindSegment(0, 4);
        Assert.NotNull(seg);
        Assert.Equal(0, seg!.GenCol);
        var seg2 = map.FindSegment(0, 5);
        Assert.Equal(5, seg2!.GenCol);
        Assert.Null(map.FindSegment(9, 0));
    }
}
