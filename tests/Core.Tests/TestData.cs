using System.Text.Json;
using SourceMapChains.Core;

namespace Core.Tests;

public static class TestData
{
    public const string Ts = "export function hi() {\n  return 'hi';\n}\n";
    public const string Js = "export function hi() {\n  return 'hi';\n}\n";
    public const string Bundle = "/* banner */\nexport function hi() {\n  return 'hi';\n}\n";

    public static string MapJson(string file, string[] sources, string mappings,
        string[]? contents = null, string? sourceRoot = null, string? semantics = null)
    {
        var dict = new Dictionary<string, object?>
        {
            ["version"] = 3,
            ["file"] = file,
            ["sources"] = sources,
            ["names"] = Array.Empty<string>(),
            ["mappings"] = mappings,
        };
        if (contents is not null) dict["sourcesContent"] = contents;
        if (sourceRoot is not null) dict["sourceRoot"] = sourceRoot;
        if (semantics is not null) dict["x_columnSemantics"] = semantics;
        return JsonSerializer.Serialize(dict);
    }

    /// <summary>Identity line map: line i -> source line i.</summary>
    public static string LineMap(int lines, int genLineOffset = 0)
    {
        var parts = new List<string>();
        for (var i = 0; i < genLineOffset; i++) parts.Add("");
        for (var i = 0; i < lines; i++)
            parts.Add(i == 0 ? Vlq.Encode(new[] { 0, 0, 0, 0 }) : Vlq.Encode(new[] { 0, 0, 1, 0 }));
        return string.Join(';', parts);
    }

    public static SourceFileData File(string path, string content,
        string? root = null, ColumnSemantics sem = ColumnSemantics.Utf16) =>
        SourceFileData.Create(path, root, content, sem);

    public static MapData Map(string generatedPath, string mapJson, string? root = null) =>
        new(generatedPath, root, SourceMapDocument.Parse(mapJson));

    /// <summary>original(ts) -> transpiled(js, map1) -> bundle(bundle.js, map2).</summary>
    public static List<LevelData> ThreeLevels()
    {
        var map1 = Map("out/hello.js", MapJson("hello.js", new[] { "src/hello.ts" }, LineMap(3), new[] { Ts }));
        var map2 = Map("dist/bundle.js", MapJson("bundle.js", new[] { "out/hello.js" }, LineMap(3, genLineOffset: 1), new[] { Js }));
        return new List<LevelData>
        {
            new("original", new() { File("src/hello.ts", Ts) }, new()),
            new("transpiled", new() { File("out/hello.js", Js) }, new() { map1 }),
            new("bundle", new() { File("dist/bundle.js", Bundle) }, new() { map2 }),
        };
    }

    public static CompositionResult ComposeBase(List<LevelData> levels) =>
        Composer.Compose(levels, "base", 1, DateTimeOffset.UnixEpoch);
}
