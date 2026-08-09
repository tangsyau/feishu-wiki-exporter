using System.Text;
using System.Text.RegularExpressions;

namespace FeishuExporter.Core;

public static partial class PathPlanner
{
    private const int MaximumSegmentUtf8Bytes = 180;

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    public static IReadOnlyList<ExportItem> MarkWikiNavigationFolders(
        IReadOnlyList<ExportItem> items,
        IReadOnlySet<string> navigationPageTokens)
    {
        var parentTokens = items
            .Where(item =>
                !string.Equals(item.Type, "embedded_file", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(item.ParentHierarchyToken))
            .Select(item => item.ParentHierarchyToken!)
            .ToHashSet(StringComparer.Ordinal);

        return items
            .Select(item =>
                parentTokens.Contains(item.HierarchyToken) &&
                navigationPageTokens.Contains(item.HierarchyToken)
                ? item with { IsFolder = true, IsNavigationOnly = true }
                : item)
            .ToList();
    }

    public static IReadOnlyList<PlannedExportItem> Plan(
        IReadOnlyList<ExportItem> items,
        string documentFormat,
        bool downloadAttachments,
        EmbeddedAttachmentPlacement embeddedAttachmentPlacement = EmbeddedAttachmentPlacement.DocumentSubfolder)
    {
        if (documentFormat is not ("docx" or "pdf"))
        {
            throw new ArgumentOutOfRangeException(nameof(documentFormat), "文档格式只能是 docx 或 pdf。");
        }

        var byToken = items.ToDictionary(x => x.HierarchyToken, StringComparer.Ordinal);

        string? GetEffectiveParentToken(ExportItem item)
        {
            if (embeddedAttachmentPlacement == EmbeddedAttachmentPlacement.AlongsideDocument &&
                string.Equals(item.Type, "embedded_file", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(item.ParentHierarchyToken) &&
                byToken.TryGetValue(item.ParentHierarchyToken, out var document))
            {
                // A navigation-only Wiki node has no exported document for an attachment
                // to sit alongside. Keep its attachments inside the navigation directory.
                return document.IsNavigationOnly ? document.HierarchyToken : document.ParentHierarchyToken;
            }

            return item.ParentHierarchyToken;
        }

        var safeNames = BuildSafeNames(items, GetEffectiveParentToken);
        var stems = new Dictionary<string, string>(StringComparer.Ordinal);

        string GetStem(ExportItem item, HashSet<string> visiting)
        {
            if (stems.TryGetValue(item.HierarchyToken, out var existing))
            {
                return existing;
            }
            if (!visiting.Add(item.HierarchyToken))
            {
                throw new InvalidOperationException($"检测到循环目录关系：{item.Title}");
            }

            var name = safeNames[item.HierarchyToken];
            var parentToken = GetEffectiveParentToken(item);
            var stem = !string.IsNullOrWhiteSpace(parentToken) &&
                       byToken.TryGetValue(parentToken, out var parent)
                ? Path.Combine(GetStem(parent, visiting), name)
                : name;

            visiting.Remove(item.HierarchyToken);
            stems[item.HierarchyToken] = stem;
            return stem;
        }

        var result = new List<PlannedExportItem>(items.Count);
        foreach (var item in items)
        {
            var stem = GetStem(item, new HashSet<string>(StringComparer.Ordinal));
            var extension = GetExportExtension(item, documentFormat, downloadAttachments);
            var relativePath = item.IsFolder || extension is null or ""
                ? stem
                : stem + "." + extension;

            result.Add(new PlannedExportItem(item, relativePath, extension));
        }

        return ResolveFinalPathCollisions(result);
    }

    public static string SanitizeSegment(string value)
    {
        var normalized = value.Normalize(NormalizationForm.FormC).Trim();
        normalized = InvalidCharactersRegex().Replace(normalized, "-");
        normalized = WhitespaceRegex().Replace(normalized, " ").Trim(' ', '.');
        if (string.IsNullOrWhiteSpace(normalized))
        {
            normalized = "未命名";
        }

        var baseName = Path.GetFileNameWithoutExtension(normalized);
        if (ReservedNames.Contains(baseName))
        {
            normalized = "_" + normalized;
        }

        return FitSegment(normalized);
    }

    private static Dictionary<string, string> BuildSafeNames(
        IReadOnlyList<ExportItem> items,
        Func<ExportItem, string?> getParentToken)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var usedNames = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            var parentKey = getParentToken(item) ?? string.Empty;
            if (!usedNames.TryGetValue(parentKey, out var used))
            {
                used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                usedNames[parentKey] = used;
            }

            var original = SanitizeSegment(item.Title);
            var candidate = original;
            var counter = 2;
            while (!used.Add(candidate))
            {
                candidate = AppendNumericSuffix(original, counter++);
            }

            result[item.HierarchyToken] = candidate;
        }

        return result;
    }

    private static IReadOnlyList<PlannedExportItem> ResolveFinalPathCollisions(
        IReadOnlyList<PlannedExportItem> items)
    {
        var result = new List<PlannedExportItem>(items.Count);
        var usedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            var original = item.RelativePath;
            var candidate = original;
            var counter = 2;
            while (!usedPaths.Add(candidate))
            {
                var directory = Path.GetDirectoryName(original);
                var fileName = AppendNumericSuffix(Path.GetFileName(original), counter++);
                candidate = string.IsNullOrEmpty(directory) ? fileName : Path.Combine(directory, fileName);
            }

            result.Add(item with { RelativePath = candidate });
        }

        return result;
    }

    private static string AppendNumericSuffix(string name, int number)
    {
        var extension = GetShortExtension(name);
        var stem = string.IsNullOrEmpty(extension) ? name : name[..^extension.Length];
        var suffix = $"（{number}）";
        return FitSegment(stem, suffix, extension);
    }

    private static string? GetExportExtension(ExportItem item, string documentFormat, bool downloadAttachments)
    {
        if (item.IsFolder)
        {
            return null;
        }

        return item.Type.ToLowerInvariant() switch
        {
            "doc" or "docx" => documentFormat,
            "sheet" or "bitable" => "xlsx",
            "file" or "embedded_file" when downloadAttachments => string.Empty,
            _ => null
        };
    }

    private static string FitSegment(string value)
    {
        if (Encoding.UTF8.GetByteCount(value) <= MaximumSegmentUtf8Bytes)
        {
            return value;
        }

        var extension = GetShortExtension(value);
        var stem = string.IsNullOrEmpty(extension) ? value : value[..^extension.Length];
        return FitSegment(stem, "…", extension);
    }

    private static string FitSegment(string stem, string suffix, string extension)
    {
        var reservedBytes = Encoding.UTF8.GetByteCount(suffix) + Encoding.UTF8.GetByteCount(extension);
        var stemBudget = Math.Max(1, MaximumSegmentUtf8Bytes - reservedBytes);
        var builder = new StringBuilder();
        var usedBytes = 0;
        foreach (var rune in stem.EnumerateRunes())
        {
            if (usedBytes + rune.Utf8SequenceLength > stemBudget)
            {
                break;
            }

            builder.Append(rune.ToString());
            usedBytes += rune.Utf8SequenceLength;
        }

        var shortenedStem = builder.ToString().TrimEnd(' ', '.');
        return shortenedStem + suffix + extension;
    }

    private static string GetShortExtension(string value)
    {
        var extension = Path.GetExtension(value);
        return !string.IsNullOrEmpty(extension) &&
               extension.Length <= 16 &&
               Encoding.UTF8.GetByteCount(extension) <= 32
            ? extension
            : string.Empty;
    }

    [GeneratedRegex("[\\\\/:*?\"<>|\\p{Cc}]", RegexOptions.CultureInvariant)]
    private static partial Regex InvalidCharactersRegex();

    [GeneratedRegex("\\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespaceRegex();
}
