using System.Text.Json;

namespace SourceMapChains.Core;

/// <summary>
/// File-backed store. Each project is one JSON document; a save is a single
/// atomic commit (temp file + rename), so a batch of mutations either all
/// lands or nothing changes.
/// </summary>
public sealed class ProjectStore
{
    private readonly string _dir;
    private readonly Dictionary<string, ProjectDocument> _cache = new();
    private readonly object _gate = new();

    public ProjectStore(string dir)
    {
        _dir = dir;
        Directory.CreateDirectory(dir);
    }

    private string PathFor(string id) => Path.Combine(_dir, $"{id}.json");

    public IReadOnlyList<ProjectDocument> List()
    {
        lock (_gate)
        {
            EnsureLoaded();
            return _cache.Values.OrderBy(p => p.Id, StringComparer.Ordinal).ToList();
        }
    }

    public ProjectDocument? Get(string id)
    {
        lock (_gate)
        {
            EnsureLoaded();
            return _cache.TryGetValue(id, out var doc) ? doc : null;
        }
    }

    /// <summary>Atomically persist the document (single commit boundary).</summary>
    public void Save(ProjectDocument doc)
    {
        lock (_gate)
        {
            var tmp = PathFor(doc.Id) + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(doc, JsonOptions.Pretty));
            File.Move(tmp, PathFor(doc.Id), overwrite: true);
            _cache[doc.Id] = doc;
        }
    }

    /// <summary>Execute a mutation as one atomic commit.</summary>
    public T Mutate<T>(string id, Func<ProjectDocument, T> mutation)
    {
        lock (_gate)
        {
            var doc = Get(id) ?? throw new KeyNotFoundException($"Project '{id}' not found.");
            var result = mutation(doc);
            Save(doc);
            return result;
        }
    }

    private bool _loaded;

    private void EnsureLoaded()
    {
        if (_loaded) return;
        foreach (var file in Directory.EnumerateFiles(_dir, "*.json"))
        {
            var doc = JsonSerializer.Deserialize<ProjectDocument>(File.ReadAllText(file), JsonOptions.Default);
            if (doc is not null) _cache[doc.Id] = doc;
        }
        _loaded = true;
    }
}
