using SourceMapChains.Core;
using Xunit;

namespace Core.Tests;

public class ValidationTests
{
    private static List<LevelData> TwoLevels(string mappings, string sourceContent = "ab\ncd\n",
        string[]? contents = null, string? sourceRoot = null, string? fileRoot = null,
        ColumnSemantics fileSem = ColumnSemantics.Utf16, string? mapSemantics = null)
    {
        var map = TestData.Map("out/x.js", TestData.MapJson("x.js", new[] { "src/a.ts" }, mappings,
            contents, sourceRoot, mapSemantics));
        return new List<LevelData>
        {
            new("original", new() { TestData.File("src/a.ts", sourceContent, fileRoot, fileSem) }, new()),
            new("out", new() { TestData.File("out/x.js", "xy\nzz\n") }, new() { map }),
        };
    }

    [Fact]
    public void Monotonic_Violation_Is_Reported()
    {
        var levels = TwoLevels("AAAA,AAAA"); // duplicate genCol 0
        var issues = Composer.Validate(levels);
        Assert.Contains(issues, i => i.Code == "GeneratedColumnNotMonotonic");
    }

    [Fact]
    public void Source_Index_Out_Of_Range()
    {
        var levels = TwoLevels("ACAA"); // srcIndex delta +1, only one source
        var issues = Composer.Validate(levels);
        Assert.Contains(issues, i => i.Code == "SourceIndexOutOfRange");
    }

    [Fact]
    public void Source_Line_And_Column_Bounds()
    {
        var lineViolation = TwoLevels(Vlq.Encode(new[] { 0, 0, 9, 0 }));
        Assert.Contains(Composer.Validate(lineViolation), i => i.Code == "SourceLineOutOfRange");
        var colViolation = TwoLevels(Vlq.Encode(new[] { 0, 0, 0, 99 }));
        Assert.Contains(Composer.Validate(colViolation), i => i.Code == "SourceColumnOutOfRange");
    }

    [Fact]
    public void SourcesContent_Fingerprint_Mismatch()
    {
        var levels = TwoLevels("AAAA", contents: new[] { "tampered" });
        Assert.Contains(Composer.Validate(levels), i => i.Code == "SourcesContentFingerprintMismatch");
        var ok = TwoLevels("AAAA", contents: new[] { "ab\ncd\n" });
        Assert.DoesNotContain(Composer.Validate(ok), i => i.Code == "SourcesContentFingerprintMismatch");
    }

    [Fact]
    public void Same_Path_Different_SourceRoot_Is_Not_Merged()
    {
        // File lives under sourceRoot "vendor", map references root "lib": no false merge.
        var levels = TwoLevels("AAAA", sourceRoot: "lib", fileRoot: "vendor");
        var issues = Composer.Validate(levels);
        Assert.Contains(issues, i => i.Code == "SourceMissing");
        // Same root resolves fine.
        var ok = TwoLevels("AAAA", sourceRoot: "vendor", fileRoot: "vendor");
        Assert.DoesNotContain(Composer.Validate(ok), i => i.Code == "SourceMissing");
    }

    [Fact]
    public void Utf16_And_UnicodeScalar_Are_Not_Mixed()
    {
        var levels = TwoLevels("AAAA", fileSem: ColumnSemantics.UnicodeScalar);
        Assert.Contains(Composer.Validate(levels), i => i.Code == "ColumnSemanticsMismatch");
    }

    [Fact]
    public void UnicodeScalar_Columns_Count_Scalars_Not_Utf16_Units()
    {
        // "😀x" is 3 UTF-16 units but 2 scalars; column 3 is valid only in UTF-16 semantics.
        var content = "😀x\n";
        var utf16 = TwoLevels(Vlq.Encode(new[] { 0, 0, 0, 3 }), sourceContent: content);
        Assert.DoesNotContain(Composer.Validate(utf16), i => i.Code == "SourceColumnOutOfRange");
        var scalar = TwoLevels(Vlq.Encode(new[] { 0, 0, 0, 3 }), sourceContent: content,
            fileSem: ColumnSemantics.UnicodeScalar, mapSemantics: "unicodeScalar");
        Assert.Contains(Composer.Validate(scalar), i => i.Code == "SourceColumnOutOfRange");
    }
}
