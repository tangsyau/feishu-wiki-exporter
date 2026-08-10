using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace FeishuExporter.Core;

internal static class ExportStateLayout
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static string GetSourceStateDirectory(ExportOptions options)
    {
        var sourceKey = $"{options.SourceType}:{options.SourceId}";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sourceKey)))
            .ToLowerInvariant()[..16];
        return Path.Combine(options.ExportRoot, ".feishu-exporter-state", hash);
    }

    public static async Task SaveSourceAsync(
        ExportOptions options,
        string sourceName,
        CancellationToken cancellationToken)
    {
        var directory = GetSourceStateDirectory(options);
        Directory.CreateDirectory(directory);
        var document = new
        {
            version = 1,
            sourceType = options.SourceType.ToString(),
            sourceId = options.SourceId,
            sourceName,
            updatedUtc = DateTimeOffset.UtcNow
        };
        await WriteJsonAtomicAsync(Path.Combine(directory, "source.json"), document, cancellationToken);
    }

    public static void TryMigrateFile(string oldPath, string newPath)
    {
        if (File.Exists(newPath) || !File.Exists(oldPath))
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(newPath)!);
        File.Copy(oldPath, newPath, overwrite: false);
    }

    public static async Task WriteJsonAtomicAsync<T>(
        string path,
        T value,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             81920,
                             FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(stream, value, JsonOptions, cancellationToken);
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
}
