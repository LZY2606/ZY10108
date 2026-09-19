using System.Text.Json;
using SourceMapChain.Core;

public static class SampleData
{
    public static List<StageInput> Stages()
    {
        var original = "var x=1\n";
        var intermediate = "Avar x=1\n";
        var bundle = "Bvar x=1\n";

        var intermediateMap = new
        {
            version = 3,
            file = "intermediate.js",
            sources = new[] { "original.js" },
            sourcesContent = new[] { original },
            names = Array.Empty<string>(),
            mappings = "CAAA,BAAI"
        };

        var bundleMap = new
        {
            version = 3,
            file = "bundle.js",
            sources = new[] { "intermediate" },
            names = Array.Empty<string>(),
            mappings = "CAAA,BAAI"
        };

        return
        [
            new StageInput
            {
                Id = "intermediate",
                Label = "中间输出",
                OutputText = intermediate,
                MapJson = JsonSerializer.Serialize(intermediateMap)
            },
            new StageInput
            {
                Id = "bundle",
                Label = "最终 bundle",
                OutputText = bundle,
                MapJson = JsonSerializer.Serialize(bundleMap)
            }
        ];
    }
}
