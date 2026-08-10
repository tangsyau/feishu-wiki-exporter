using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;

namespace FeishuExporter.Core;

public sealed class DirectOfflineKnowledgeBuilder(FeishuApiClient apiClient)
{
    private const string FormatName = "feishu-offline-knowledge";
    private const int FormatVersion = 3;
    private const int BuildStateVersion = 2;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)
    };

    public async Task<DirectOfflineKnowledgeBuildResult> BuildAsync(
        ExportOptions options,
        ExportPreparation preparation,
        IProgress<OfflineKnowledgeProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var safeName = PathPlanner.SanitizeSegment(preparation.SourceName);
        var outputRoot = Path.Combine(options.ExportRoot, safeName + "-offline");
        var temporaryRoot = outputRoot + ".building-" + Guid.NewGuid().ToString("N");
        var stateDirectory = ExportStateLayout.GetSourceStateDirectory(options);
        var statePath = Path.Combine(stateDirectory, "reader-state.json");
        var pageCache = Path.Combine(stateDirectory, "page-cache");
        var assetCache = Path.Combine(stateDirectory, "asset-cache");
        Directory.CreateDirectory(pageCache);
        Directory.CreateDirectory(assetCache);
        EnsureReplaceableOutput(outputRoot);

        var previousState = await LoadStateAsync(statePath, cancellationToken);
        var nextState = new ReaderBuildState();
        var items = preparation.Items
            .Where(item => !string.Equals(item.Type, "embedded_file", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var pageItems = items.Where(item => !item.IsFolder).ToList();
        var pageIdByHierarchyToken = pageItems.ToDictionary(
            item => item.HierarchyToken,
            item => CreateStableId(item.HierarchyToken),
            StringComparer.Ordinal);
        var pageIdByAnyToken = new Dictionary<string, string>(pageIdByHierarchyToken, StringComparer.Ordinal);
        foreach (var item in pageItems)
        {
            pageIdByAnyToken.TryAdd(item.ContentToken, pageIdByHierarchyToken[item.HierarchyToken]);
        }
        var childrenByParent = items
            .Where(item => !string.IsNullOrWhiteSpace(item.ParentHierarchyToken))
            .GroupBy(item => item.ParentHierarchyToken!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<ExportItem>)group
                .OrderBy(item => item.SiblingOrder ?? int.MaxValue)
                .ThenBy(item => item.Title, StringComparer.CurrentCulture)
                .ToList(), StringComparer.Ordinal);

        Directory.CreateDirectory(Path.Combine(temporaryRoot, "pages"));
        Directory.CreateDirectory(Path.Combine(temporaryRoot, "index"));
        Directory.CreateDirectory(Path.Combine(temporaryRoot, "diagnostics"));
        Directory.CreateDirectory(Path.Combine(temporaryRoot, "assets", "images"));
        Directory.CreateDirectory(Path.Combine(temporaryRoot, "assets", "attachments"));
        Directory.CreateDirectory(Path.Combine(temporaryRoot, "assets", "files"));

        var documents = new List<ReaderKnowledgeDocument>();
        var postings = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var pageCount = 0;
        var attachmentCount = 0;
        var unsupportedCount = 0;
        var reusedPages = 0;
        var unsupportedBlocks = new List<ReaderUnsupportedBlock>();
        var subPageResolutionIssues = new List<ReaderPageSubPageResolutionIssue>();

        try
        {
            for (var index = 0; index < pageItems.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var item = pageItems[index];
                var id = pageIdByHierarchyToken[item.HierarchyToken];
                progress?.Report(new OfflineKnowledgeProgress(
                    index,
                    pageItems.Count,
                    $"正在生成 Reader 页面：{item.Title}"));

                string? pagePath = null;
                string? originalPath = null;
                string searchableText = string.Empty;
                if (string.Equals(item.Type, "docx", StringComparison.OrdinalIgnoreCase))
                {
                    pagePath = $"pages/{id}.json";
                    var structureSignature = CreateStructureSignature(childrenByParent.GetValueOrDefault(item.HierarchyToken, []));
                    var modifiedTime = item.ModifiedTime ?? string.Empty;
                    var cachedPagePath = Path.Combine(pageCache, id + ".json");
                    ReaderKnowledgePage page;
                    if (options.SkipUnchanged &&
                        previousState.Version == BuildStateVersion &&
                        previousState.Items.TryGetValue(item.HierarchyToken, out var previous) &&
                        string.Equals(previous.ModifiedTime, modifiedTime, StringComparison.Ordinal) &&
                        string.Equals(previous.StructureSignature, structureSignature, StringComparison.Ordinal) &&
                        File.Exists(cachedPagePath))
                    {
                        page = await ReadJsonAsync<ReaderKnowledgePage>(cachedPagePath, cancellationToken);
                        reusedPages++;
                        CopyDirectoryIfExists(
                            Path.Combine(assetCache, id),
                            Path.Combine(temporaryRoot, "assets"));
                    }
                    else if (preparation.DocumentInspections.TryGetValue(item.HierarchyToken, out var inspection))
                    {
                        var assetPaths = await DownloadDocumentAssetsAsync(
                            item,
                            id,
                            inspection,
                            temporaryRoot,
                            cancellationToken);
                        attachmentCount += assetPaths.AttachmentCount;
                        var normalizer = new FeishuBlockNormalizer(
                            pageIdByAnyToken,
                            childrenByParent.GetValueOrDefault(item.HierarchyToken, []),
                            assetPaths.Paths,
                            id);
                        page = normalizer.Normalize(item.Title, inspection.Blocks);
                        ReplaceCachedAssets(temporaryRoot, assetCache, id);
                    }
                    else
                    {
                        page = new ReaderKnowledgePage(
                            item.Title,
                            [new ReaderBlock
                            {
                                Id = "unavailable",
                                Type = "unsupported",
                                Text = "页面内容读取失败，请查看导出摘要。",
                                SourceType = -1
                            }],
                            string.Empty,
                            1,
                            []);
                    }

                    await WriteJsonAsync(Path.Combine(temporaryRoot, pagePath.Replace('/', Path.DirectorySeparatorChar)), page, cancellationToken);
                    await WriteJsonAsync(cachedPagePath, page, cancellationToken);
                    searchableText = page.Text;
                    unsupportedCount += page.UnsupportedBlockCount;
                    CollectUnsupportedBlocks(page.Blocks, id, item.Title, unsupportedBlocks);
                    subPageResolutionIssues.AddRange(page.SubPageResolutionIssues.Select(issue =>
                        new ReaderPageSubPageResolutionIssue(id, item.Title, issue)));
                    pageCount++;
                    nextState.Items[item.HierarchyToken] = new ReaderBuildStateEntry(
                        modifiedTime,
                        structureSignature,
                        pagePath,
                        page.UnsupportedBlockCount);
                }
                else
                {
                    originalPath = await DownloadNonDocumentAsync(item, id, temporaryRoot, cancellationToken);
                    if (originalPath is null)
                    {
                        unsupportedCount++;
                    }
                    else
                    {
                        attachmentCount++;
                    }
                }

                var document = new ReaderKnowledgeDocument(
                    id,
                    item.HierarchyToken,
                    item.Title,
                    ClassifyKind(item.Type, originalPath),
                    pagePath,
                    originalPath,
                    BuildBreadcrumb(item, items),
                    item.ModifiedTime);
                documents.Add(document);
                AddToIndex(postings, id, item.Title + "\n" + searchableText);
            }

            attachmentCount = CountFiles(Path.Combine(temporaryRoot, "assets", "attachments")) +
                              CountFiles(Path.Combine(temporaryRoot, "assets", "files"));
            var tree = BuildTree(preparation.SourceName, items, pageIdByHierarchyToken);
            var manifest = new ReaderKnowledgeManifest(
                FormatName,
                FormatVersion,
                preparation.SourceName,
                DateTimeOffset.UtcNow,
                documents,
                new ReaderKnowledgeStatistics(pageCount, attachmentCount, unsupportedCount));
            var searchIndex = new ReaderSearchIndex(
                FormatVersion,
                postings.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                    .ToDictionary(
                        pair => pair.Key,
                        pair => pair.Value.OrderBy(id => id, StringComparer.Ordinal).ToArray(),
                        StringComparer.Ordinal));

            await WriteJsonAsync(Path.Combine(temporaryRoot, "manifest.json"), manifest, cancellationToken);
            await WriteJsonAsync(Path.Combine(temporaryRoot, "tree.json"), tree, cancellationToken);
            await WriteJsonAsync(Path.Combine(temporaryRoot, "index", "search-index.json"), searchIndex, cancellationToken);
            await WriteJsonAsync(
                Path.Combine(temporaryRoot, "diagnostics", "unsupported-blocks.json"),
                new { version = 2, items = unsupportedBlocks },
                cancellationToken);
            await WriteJsonAsync(
                Path.Combine(temporaryRoot, "diagnostics", "subpage-link-resolution.json"),
                new { version = 1, items = subPageResolutionIssues },
                cancellationToken);
            await File.WriteAllTextAsync(
                Path.Combine(temporaryRoot, "README.txt"),
                "此目录是跨平台 Reader 数据包。请在飞书知识库离线阅读器中选择此目录。\n",
                new UTF8Encoding(false),
                cancellationToken);

            ReplaceOutputDirectory(outputRoot, temporaryRoot);
            await ExportStateLayout.WriteJsonAtomicAsync(statePath, nextState, cancellationToken);
            await ExportStateLayout.WriteJsonAtomicAsync(
                Path.Combine(stateDirectory, "order.json"),
                new
                {
                    version = 3,
                    items = items.Select(item => new
                    {
                        item.HierarchyToken,
                        item.ParentHierarchyToken,
                        item.Title,
                        item.Type,
                        item.SiblingOrder
                    })
                },
                cancellationToken);
            progress?.Report(new OfflineKnowledgeProgress(pageItems.Count, pageItems.Count, "完成"));
            return new DirectOfflineKnowledgeBuildResult(
                outputRoot,
                pageCount,
                attachmentCount,
                unsupportedCount,
                reusedPages);
        }
        catch
        {
            if (Directory.Exists(temporaryRoot))
            {
                Directory.Delete(temporaryRoot, recursive: true);
            }
            throw;
        }
    }

    private async Task<(Dictionary<string, string> Paths, int AttachmentCount)> DownloadDocumentAssetsAsync(
        ExportItem document,
        string pageId,
        DocumentInspection inspection,
        string outputRoot,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var attachments = 0;
        foreach (var block in inspection.Blocks)
        {
            string? token = null;
            string? relativePath = null;
            if (block.BlockType == 23 && !string.IsNullOrWhiteSpace(block.File?.Token))
            {
                token = block.File.Token;
                var fileName = PathPlanner.SanitizeSegment(block.File.Name ?? $"附件-{block.BlockId}");
                relativePath = NormalizePath(Path.Combine("assets", "attachments", pageId, fileName));
                attachments++;
            }
            else if (block.BlockType == 27 &&
                     block.Properties is not null &&
                     block.Properties.TryGetValue("image", out var image) &&
                     image.ValueKind == JsonValueKind.Object &&
                     image.TryGetProperty("token", out var tokenElement))
            {
                token = tokenElement.GetString();
                relativePath = NormalizePath(Path.Combine("assets", "images", pageId, block.BlockId + ".bin"));
            }

            if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(relativePath))
            {
                continue;
            }
            var destination = Path.Combine(outputRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await apiClient.DownloadMediaAsync(token, destination, cancellationToken);
            result[block.BlockId] = relativePath;
        }
        return (result, attachments);
    }

    private async Task<string?> DownloadNonDocumentAsync(
        ExportItem item,
        string id,
        string outputRoot,
        CancellationToken cancellationToken)
    {
        var extension = GetReaderExtension(item);
        if (extension is null)
        {
            return null;
        }
        var name = PathPlanner.SanitizeSegment(Path.GetFileNameWithoutExtension(item.Title));
        var relative = NormalizePath(Path.Combine("assets", "files", id, name + "." + extension));
        var destination = Path.Combine(outputRoot, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        if (string.Equals(item.Type, "file", StringComparison.OrdinalIgnoreCase))
        {
            await apiClient.DownloadFileAsync(item.ContentToken, destination, cancellationToken);
        }
        else
        {
            var fileToken = await apiClient.CreateAndWaitForExportAsync(
                extension,
                item.ContentToken,
                item.Type,
                cancellationToken);
            await apiClient.DownloadExportAsync(fileToken, destination, cancellationToken);
        }
        return relative;
    }

    private static string? GetReaderExtension(ExportItem item)
    {
        var type = item.Type.ToLowerInvariant();
        return type switch
        {
            "doc" => "docx",
            "sheet" or "bitable" => "xlsx",
            "slides" => "pptx",
            "pdf" => "pdf",
            "file" => NormalizeExistingExtension(Path.GetExtension(item.Title)),
            _ => null
        };
    }

    private static string? NormalizeExistingExtension(string extension) =>
        string.IsNullOrWhiteSpace(extension) ? null : extension.TrimStart('.').ToLowerInvariant();

    private static string ClassifyKind(string type, string? originalPath)
    {
        if (string.Equals(type, "docx", StringComparison.OrdinalIgnoreCase)) return "docx";
        var extension = Path.GetExtension(originalPath ?? string.Empty).ToLowerInvariant();
        return extension switch
        {
            ".docx" => "docx",
            ".pdf" => "pdf",
            ".xlsx" or ".xls" or ".csv" => "spreadsheet",
            ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" => "image",
            _ => "file"
        };
    }

    private static ReaderKnowledgeTreeNode BuildTree(
        string rootTitle,
        IReadOnlyList<ExportItem> items,
        IReadOnlyDictionary<string, string> pageIds)
    {
        var children = items
            .GroupBy(item => item.ParentHierarchyToken ?? string.Empty, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.OrderBy(item => item.SiblingOrder ?? int.MaxValue)
                    .ThenBy(item => item.Title, StringComparer.CurrentCulture)
                    .ToList(),
                StringComparer.Ordinal);

        ReaderKnowledgeTreeNode Create(ExportItem item)
        {
            var nested = children.GetValueOrDefault(item.HierarchyToken, [])
                .Where(child => !string.Equals(child.Type, "embedded_file", StringComparison.OrdinalIgnoreCase))
                .Select(Create)
                .ToList();
            var hasDocument = pageIds.TryGetValue(item.HierarchyToken, out var documentId);
            return new ReaderKnowledgeTreeNode(
                item.Title,
                item.IsFolder ? "folder" : "page",
                hasDocument ? documentId : null,
                item.Type,
                nested);
        }

        return new ReaderKnowledgeTreeNode(
            rootTitle,
            "folder",
            null,
            null,
            children.GetValueOrDefault(string.Empty, [])
                .Where(item => !string.Equals(item.Type, "embedded_file", StringComparison.OrdinalIgnoreCase))
                .Select(Create)
                .ToList());
    }

    private static string BuildBreadcrumb(ExportItem item, IReadOnlyList<ExportItem> items)
    {
        var byToken = items.GroupBy(value => value.HierarchyToken, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var parts = new Stack<string>();
        var current = item;
        var visited = new HashSet<string>(StringComparer.Ordinal);
        while (visited.Add(current.HierarchyToken))
        {
            parts.Push(current.Title);
            if (string.IsNullOrWhiteSpace(current.ParentHierarchyToken) ||
                !byToken.TryGetValue(current.ParentHierarchyToken, out current))
            {
                break;
            }
        }
        return string.Join(" / ", parts);
    }

    private static string CreateStableId(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant()[..20];

    private static string CreateStructureSignature(IReadOnlyList<ExportItem> children)
    {
        var value = string.Join("\n", children.Select(child =>
            $"{child.HierarchyToken}\t{child.SiblingOrder}\t{child.Title}"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    }

    private static void AddToIndex(Dictionary<string, HashSet<string>> postings, string id, string text)
    {
        foreach (var token in Tokenize(text))
        {
            if (!postings.TryGetValue(token, out var documents))
            {
                documents = new HashSet<string>(StringComparer.Ordinal);
                postings[token] = documents;
            }
            documents.Add(id);
        }
    }

    private static IEnumerable<string> Tokenize(string text)
    {
        var normalized = text.Normalize(NormalizationForm.FormKC).ToLowerInvariant();
        var result = new HashSet<string>(StringComparer.Ordinal);
        char? previousCjk = null;
        var word = new StringBuilder();
        void FlushWord()
        {
            if (word.Length == 0) return;
            result.Add(word.ToString());
            word.Clear();
        }
        foreach (var character in normalized)
        {
            if (IsCjk(character))
            {
                FlushWord();
                if (previousCjk.HasValue) result.Add(string.Concat(previousCjk.Value, character));
                previousCjk = character;
            }
            else if (char.IsLetterOrDigit(character))
            {
                previousCjk = null;
                word.Append(character);
            }
            else
            {
                previousCjk = null;
                FlushWord();
            }
        }
        FlushWord();
        return result;
    }

    private static bool IsCjk(char character) =>
        character is >= '\u3400' and <= '\u9fff' or >= '\uf900' and <= '\ufaff' or >= '\u3040' and <= '\u30ff' or >= '\uac00' and <= '\ud7af';

    private static async Task<ReaderBuildState> LoadStateAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return new ReaderBuildState();
        try
        {
            return await ReadJsonAsync<ReaderBuildState>(path, cancellationToken);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return new ReaderBuildState();
        }
    }

    private static async Task<T> ReadJsonAsync<T>(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken)
               ?? throw new InvalidDataException($"JSON 内容为空：{path}");
    }

    private static async Task WriteJsonAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);
        await JsonSerializer.SerializeAsync(stream, value, JsonOptions, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private static void EnsureReplaceableOutput(string outputRoot)
    {
        if (!Directory.Exists(outputRoot)) return;
        var manifestPath = Path.Combine(outputRoot, "manifest.json");
        if (!File.Exists(manifestPath))
        {
            throw new InvalidOperationException($"目标目录已经存在且不是本程序生成的离线知识库：{outputRoot}");
        }
        using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
        if (document.RootElement.GetProperty("format").GetString() != FormatName)
        {
            throw new InvalidOperationException($"目标目录中的离线知识库格式无法识别：{outputRoot}");
        }
    }

    private static void ReplaceOutputDirectory(string outputRoot, string temporaryRoot)
    {
        if (Directory.Exists(outputRoot)) Directory.Delete(outputRoot, recursive: true);
        Directory.Move(temporaryRoot, outputRoot);
    }

    private static void ReplaceCachedAssets(string temporaryRoot, string assetCache, string pageId)
    {
        var target = Path.Combine(assetCache, pageId);
        if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
        Directory.CreateDirectory(target);
        CopyDirectoryIfExists(Path.Combine(temporaryRoot, "assets", "images", pageId), Path.Combine(target, "images", pageId));
        CopyDirectoryIfExists(Path.Combine(temporaryRoot, "assets", "attachments", pageId), Path.Combine(target, "attachments", pageId));
    }

    private static void CopyDirectoryIfExists(string source, string destination)
    {
        if (!Directory.Exists(source)) return;
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        }
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    private static int CountFiles(string path) =>
        Directory.Exists(path) ? Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Count() : 0;

    private static void CollectUnsupportedBlocks(
        IEnumerable<ReaderBlock> blocks,
        string pageId,
        string pageTitle,
        ICollection<ReaderUnsupportedBlock> result)
    {
        foreach (var block in blocks)
        {
            if (block.Type == "unsupported")
            {
                result.Add(new ReaderUnsupportedBlock(
                    pageId,
                    pageTitle,
                    block.Id,
                    block.SourceType,
                    block.ComponentTypeId,
                    block.HasSourceRecord,
                    !string.IsNullOrWhiteSpace(block.Text) || block.Children.Count > 0));
            }
            CollectUnsupportedBlocks(block.Children, pageId, pageTitle, result);
        }
    }

    private static string NormalizePath(string path) => path.Replace('\\', '/');
}
