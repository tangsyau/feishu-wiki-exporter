using System.Text.Json;

namespace FeishuExporter.Core;

internal static class ExportOrderStore
{
    private const string FileName = "order.json";
    private const int CurrentVersion = 2;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public static async Task SaveAsync(
        string sourceRoot,
        IReadOnlyList<PlannedExportItem> items,
        CancellationToken cancellationToken)
    {
        var stateDirectory = Path.Combine(sourceRoot, ".feishu-export");
        Directory.CreateDirectory(stateDirectory);
        var path = Path.Combine(stateDirectory, FileName);
        var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var document = new ExportOrderDocument
        {
            Version = CurrentVersion,
            Items = [.. items.Select(item => new ExportOrderEntry
            {
                HierarchyToken = item.Item.HierarchyToken,
                ParentHierarchyToken = item.Item.ParentHierarchyToken,
                Title = item.Item.Title,
                Type = item.Item.Type,
                RelativePath = NormalizeRelativePath(item.RelativePath),
                SiblingOrder = item.Item.SiblingOrder,
                IsFolder = item.Item.IsFolder,
                IsNavigationOnly = item.Item.IsNavigationOnly
            })]
        };

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
                await JsonSerializer.SerializeAsync(stream, document, JsonOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    public static ExportHierarchyMetadata Load(string sourceRoot)
    {
        var path = Path.Combine(sourceRoot, ".feishu-export", FileName);
        if (!File.Exists(path))
        {
            return ExportHierarchyMetadata.Empty;
        }

        try
        {
            var document = JsonSerializer.Deserialize<ExportOrderDocument>(File.ReadAllText(path), JsonOptions);
            if (document?.Items is null ||
                (document.Version != 1 && document.Version != CurrentVersion))
            {
                return ExportHierarchyMetadata.Empty;
            }

            var siblingOrders = document.Items
                .Where(item =>
                    !string.IsNullOrWhiteSpace(item.RelativePath) &&
                    item.SiblingOrder.HasValue)
                .GroupBy(item => NormalizeRelativePath(item.RelativePath), StringComparer.Ordinal)
                .ToDictionary(
                    group => group.Key,
                    group => group.First().SiblingOrder!.Value,
                    StringComparer.Ordinal);

            if (document.Version == 1)
            {
                return new ExportHierarchyMetadata(siblingOrders, []);
            }

            var hierarchyItems = document.Items
                .Where(item =>
                    !string.IsNullOrWhiteSpace(item.HierarchyToken) &&
                    !string.IsNullOrWhiteSpace(item.RelativePath))
                .Select(item => new ExportHierarchyItem(
                    item.HierarchyToken!,
                    item.ParentHierarchyToken,
                    string.IsNullOrWhiteSpace(item.Title)
                        ? Path.GetFileNameWithoutExtension(item.RelativePath)
                        : item.Title,
                    item.Type ?? string.Empty,
                    NormalizeRelativePath(item.RelativePath),
                    item.SiblingOrder,
                    item.IsFolder,
                    item.IsNavigationOnly))
                .ToList();

            return new ExportHierarchyMetadata(siblingOrders, hierarchyItems);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return ExportHierarchyMetadata.Empty;
        }
    }

    private static string NormalizeRelativePath(string path) => path.Replace('\\', '/');

    private sealed class ExportOrderDocument
    {
        public int Version { get; init; }
        public List<ExportOrderEntry>? Items { get; init; }
    }

    private sealed class ExportOrderEntry
    {
        public string? HierarchyToken { get; init; }
        public string? ParentHierarchyToken { get; init; }
        public string? Title { get; init; }
        public string? Type { get; init; }
        public string RelativePath { get; init; } = string.Empty;
        public int? SiblingOrder { get; init; }
        public bool IsFolder { get; init; }
        public bool IsNavigationOnly { get; init; }
    }
}

internal sealed record ExportHierarchyMetadata(
    IReadOnlyDictionary<string, int> SiblingOrders,
    IReadOnlyList<ExportHierarchyItem> Items)
{
    public static ExportHierarchyMetadata Empty { get; } = new(
        new Dictionary<string, int>(StringComparer.Ordinal),
        []);
}

internal sealed record ExportHierarchyItem(
    string HierarchyToken,
    string? ParentHierarchyToken,
    string Title,
    string Type,
    string RelativePath,
    int? SiblingOrder,
    bool IsFolder,
    bool IsNavigationOnly);
