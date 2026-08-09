using System.Text.Json;

namespace FeishuExporter.Core;

internal sealed class ExportStateStore(string statePath)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly SemaphoreSlim _gate = new(1, 1);
    private Dictionary<ExportStateKey, ExportStateEntry> _items = [];

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(statePath)!);
        if (!File.Exists(statePath))
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var stream = new FileStream(
                statePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                useAsync: true);
            var document = await JsonSerializer.DeserializeAsync<ExportStateDocument>(
                stream,
                JsonOptions,
                cancellationToken);
            _items = document?.Items.ToDictionary(
                item => new ExportStateKey(item.SourceKey, item.ItemKey, item.ExportFormat)) ?? [];
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<string?> GetCurrentPathAsync(
        string sourceKey,
        PlannedExportItem item,
        string format,
        string sourceRoot,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(item.Item.ModifiedTime))
        {
            return null;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var key = new ExportStateKey(sourceKey, item.Item.HierarchyToken, format);
            if (!_items.TryGetValue(key, out var saved))
            {
                return null;
            }

            var fullPath = Path.Combine(sourceRoot, saved.RelativePath);
            return saved.Status == ExportItemStatus.Completed &&
                   string.Equals(saved.ModifiedTime, item.Item.ModifiedTime, StringComparison.Ordinal) &&
                   string.Equals(saved.RelativePath, item.RelativePath, StringComparison.Ordinal) &&
                   File.Exists(fullPath)
                ? saved.RelativePath
                : null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(
        string sourceKey,
        PlannedExportItem item,
        string format,
        ExportItemStatus status,
        long? size,
        string? error,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var key = new ExportStateKey(sourceKey, item.Item.HierarchyToken, format);
            _items[key] = new ExportStateEntry
            {
                SourceKey = sourceKey,
                ItemKey = item.Item.HierarchyToken,
                ExportFormat = format,
                ModifiedTime = item.Item.ModifiedTime,
                RelativePath = item.RelativePath,
                Status = status,
                SizeBytes = size,
                Error = error,
                UpdatedUtc = DateTimeOffset.UtcNow
            };
            await SaveDocumentAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<string>> GetTrackedCompletedPathsAsync(
        string sourceKey,
        string itemKey,
        string sourceRoot,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var root = Path.GetFullPath(sourceRoot);
            var result = new List<string>();
            foreach (var entry in _items.Values.Where(entry =>
                         entry.Status == ExportItemStatus.Completed &&
                         string.Equals(entry.SourceKey, sourceKey, StringComparison.Ordinal) &&
                         string.Equals(entry.ItemKey, itemKey, StringComparison.Ordinal)))
            {
                var fullPath = Path.GetFullPath(Path.Combine(root, entry.RelativePath));
                var relative = Path.GetRelativePath(root, fullPath);
                if (Path.IsPathRooted(relative) ||
                    relative.Equals("..", StringComparison.Ordinal) ||
                    relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
                    !File.Exists(fullPath))
                {
                    continue;
                }

                result.Add(relative);
            }

            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ForgetItemAsync(
        string sourceKey,
        string itemKey,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var keys = _items.Keys
                .Where(key => string.Equals(key.SourceKey, sourceKey, StringComparison.Ordinal) &&
                              string.Equals(key.ItemKey, itemKey, StringComparison.Ordinal))
                .ToList();
            if (keys.Count == 0)
            {
                return;
            }

            foreach (var key in keys)
            {
                _items.Remove(key);
            }
            await SaveDocumentAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task SaveDocumentAsync(CancellationToken cancellationToken)
    {
        var temporaryPath = statePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 4096,
                             useAsync: true))
            {
                var document = new ExportStateDocument
                {
                    Items = [.. _items.Values.OrderBy(x => x.SourceKey, StringComparer.Ordinal)
                        .ThenBy(x => x.ItemKey, StringComparer.Ordinal)
                        .ThenBy(x => x.ExportFormat, StringComparer.Ordinal)]
                };
                await JsonSerializer.SerializeAsync(stream, document, JsonOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            File.Move(temporaryPath, statePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private readonly record struct ExportStateKey(string SourceKey, string ItemKey, string ExportFormat);

    private sealed class ExportStateDocument
    {
        public int Version { get; init; } = 1;
        public List<ExportStateEntry> Items { get; init; } = [];
    }

    private sealed class ExportStateEntry
    {
        public required string SourceKey { get; init; }
        public required string ItemKey { get; init; }
        public required string ExportFormat { get; init; }
        public string? ModifiedTime { get; init; }
        public required string RelativePath { get; init; }
        public ExportItemStatus Status { get; init; }
        public long? SizeBytes { get; init; }
        public string? Error { get; init; }
        public DateTimeOffset UpdatedUtc { get; init; }
    }
}
