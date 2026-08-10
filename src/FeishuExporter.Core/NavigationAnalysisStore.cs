using System.Text.Json;
using System.Text.Json.Serialization;

namespace FeishuExporter.Core;

internal static class NavigationAnalysisStore
{
    private const string FileName = "navigation-analysis.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public static async Task SaveAsync(
        string sourceRoot,
        IReadOnlyList<NavigationPageAnalysis> analyses,
        CancellationToken cancellationToken)
    {
        var stateDirectory = Path.Combine(sourceRoot, ".feishu-export");
        Directory.CreateDirectory(stateDirectory);
        var path = Path.Combine(stateDirectory, FileName);
        var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var document = new NavigationAnalysisDocument(
            1,
            DateTimeOffset.UtcNow,
            analyses);

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

    private sealed record NavigationAnalysisDocument(
        int Version,
        DateTimeOffset GeneratedUtc,
        IReadOnlyList<NavigationPageAnalysis> Items);
}
