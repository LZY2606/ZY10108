using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SourceMapChain.Core;

public sealed class WorkspaceStore
{
    internal static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly string filePath;
    private readonly SemaphoreSlim semaphore = new(1, 1);

    public WorkspaceStore(string filePath)
    {
        this.filePath = filePath;
    }

    public async Task<WorkspaceDocument> LoadAsync(CancellationToken cancellationToken = default)
    {
        await semaphore.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(filePath))
            {
                var created = new WorkspaceDocument
                {
                    CreatedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow
                };
                await SaveUnsafeAsync(created, cancellationToken);
                return created;
            }

            await using var stream = File.OpenRead(filePath);
            var document = await JsonSerializer.DeserializeAsync<WorkspaceDocument>(stream, Options, cancellationToken);
            if (document is null)
            {
                throw new InvalidOperationException("Workspace document is invalid.");
            }

            if (document.SchemaVersion != 1)
            {
                throw new InvalidOperationException($"Unsupported workspace schema version {document.SchemaVersion}.");
            }

            return document;
        }
        finally
        {
            semaphore.Release();
        }
    }

    public async Task<T> MutateAsync<T>(Func<WorkspaceDocument, T> action, CancellationToken cancellationToken = default)
    {
        await semaphore.WaitAsync(cancellationToken);
        try
        {
            WorkspaceDocument? document;
            if (File.Exists(filePath))
            {
                await using var input = File.OpenRead(filePath);
                document = await JsonSerializer.DeserializeAsync<WorkspaceDocument>(input, Options, cancellationToken);
            }
            else
            {
                document = new WorkspaceDocument { CreatedAt = DateTimeOffset.UtcNow };
            }
            if (document is null)
            {
                throw new InvalidOperationException("Workspace document is invalid.");
            }

            var result = action(document);
            await SaveUnsafeAsync(document, cancellationToken);
            return result;
        }
        finally
        {
            semaphore.Release();
        }
    }

    public async Task SaveAsync(WorkspaceDocument document, CancellationToken cancellationToken = default)
    {
        await semaphore.WaitAsync(cancellationToken);
        try
        {
            await SaveUnsafeAsync(document, cancellationToken);
        }
        finally
        {
            semaphore.Release();
        }
    }

    private async Task SaveUnsafeAsync(WorkspaceDocument document, CancellationToken cancellationToken)
    {
        document.UpdatedAt = DateTimeOffset.UtcNow;
        var directory = Path.GetDirectoryName(Path.GetFullPath(filePath));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporaryPath = filePath + ".tmp-" + ConcurrentGuid.New();
        await using (var stream = File.Create(temporaryPath))
        {
            await JsonSerializer.SerializeAsync(stream, document, Options, cancellationToken);
        }

        File.Move(temporaryPath, filePath, overwrite: true);
    }
}

internal static class ConcurrentGuid
{
    private static long counter;

    public static string New()
    {
        var value = Interlocked.Increment(ref counter);
        return $"{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}-{value}";
    }
}
