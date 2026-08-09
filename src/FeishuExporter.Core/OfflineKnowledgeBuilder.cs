using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace FeishuExporter.Core;

public sealed partial class OfflineKnowledgeBuilder
{
    private const string FormatName = "feishu-offline-knowledge";
    private const int FormatVersion = 2;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public async Task<OfflineKnowledgeBuildResult> BuildAsync(
        string sourceDirectory,
        string outputDirectory,
        IProgress<OfflineKnowledgeProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var sourceRoot = Path.GetFullPath(sourceDirectory);
        var outputRoot = Path.GetFullPath(outputDirectory);
        if (!Directory.Exists(sourceRoot))
        {
            throw new DirectoryNotFoundException($"导出目录不存在：{sourceRoot}");
        }

        if (IsSubPath(sourceRoot, outputRoot))
        {
            throw new InvalidOperationException("离线知识库输出目录不能位于原始导出目录内部。");
        }

        EnsureReplaceableOutput(outputRoot);
        var previousDocuments = LoadPreviousDocuments(outputRoot);
        var hierarchyMetadata = ExportOrderStore.Load(sourceRoot);
        var temporaryRoot = outputRoot + ".building-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(temporaryRoot);

        try
        {
            var sourceFiles = Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories)
                .Where(path => !ShouldSkipSourceFile(sourceRoot, path))
                .OrderBy(path => Path.GetRelativePath(sourceRoot, path), StringComparer.Ordinal)
                .ToList();

            var pagesDirectory = Path.Combine(temporaryRoot, "pages");
            var filesDirectory = Path.Combine(temporaryRoot, "files");
            var indexDirectory = Path.Combine(temporaryRoot, "index");
            Directory.CreateDirectory(pagesDirectory);
            Directory.CreateDirectory(filesDirectory);
            Directory.CreateDirectory(indexDirectory);

            var documents = new List<KnowledgeDocument>();
            var postings = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            var indexedCount = 0;
            var reusedPages = 0;

            for (var index = 0; index < sourceFiles.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var sourcePath = sourceFiles[index];
                var relativePath = NormalizeRelativePath(Path.GetRelativePath(sourceRoot, sourcePath));
                progress?.Report(new OfflineKnowledgeProgress(index, sourceFiles.Count, relativePath));

                var destinationPath = Path.Combine(filesDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
                File.Copy(sourcePath, destinationPath, overwrite: true);

                var extension = Path.GetExtension(sourcePath).ToLowerInvariant();
                var title = Path.GetFileNameWithoutExtension(sourcePath);
                var documentId = CreateStableId(relativePath);
                var kind = ClassifyFile(extension);
                var sourceInfo = new FileInfo(sourcePath);
                var sourceModifiedUtc = new DateTimeOffset(sourceInfo.LastWriteTimeUtc);
                string? pagePath = null;
                string? searchableText = null;

                if (extension == ".docx")
                {
                    var expectedPagePath = $"pages/{documentId}.json";
                    if (previousDocuments.TryGetValue(relativePath, out var previous) &&
                        previous.PagePath is not null &&
                        previous.SourceSizeBytes == sourceInfo.Length &&
                        previous.SourceModifiedUtc == sourceModifiedUtc &&
                        File.Exists(Path.Combine(outputRoot, previous.PagePath.Replace('/', Path.DirectorySeparatorChar))))
                    {
                        pagePath = expectedPagePath;
                        var previousPagePath = Path.Combine(
                            outputRoot,
                            previous.PagePath.Replace('/', Path.DirectorySeparatorChar));
                        var page = await ReadJsonAsync<KnowledgePage>(previousPagePath, cancellationToken);
                        searchableText = page.Text;
                        await WriteJsonAsync(
                            Path.Combine(temporaryRoot, expectedPagePath.Replace('/', Path.DirectorySeparatorChar)),
                            page,
                            cancellationToken);
                        reusedPages++;
                    }
                    else
                    {
                        try
                        {
                            var converted = ConvertDocx(sourcePath);
                            searchableText = converted.Text;
                            pagePath = expectedPagePath;
                            var page = new KnowledgePage(title, converted.Html, converted.Text);
                            await WriteJsonAsync(
                                Path.Combine(temporaryRoot, pagePath.Replace('/', Path.DirectorySeparatorChar)),
                                page,
                                cancellationToken);
                        }
                        catch (InvalidDataException)
                        {
                            // Keep the original file available even if a malformed DOCX cannot be rendered.
                        }
                        catch (System.Xml.XmlException)
                        {
                            // Keep the original file available even if WordprocessingML cannot be parsed.
                        }
                    }
                }

                var breadcrumb = BuildBreadcrumb(relativePath);
                var document = new KnowledgeDocument(
                    documentId,
                    title,
                    kind,
                    relativePath,
                    $"files/{relativePath}",
                    pagePath,
                    breadcrumb,
                    sourceInfo.Length,
                    sourceModifiedUtc);
                documents.Add(document);

                if (!string.IsNullOrWhiteSpace(searchableText))
                {
                    AddToIndex(postings, documentId, title + " " + searchableText);
                    indexedCount++;
                }
                else
                {
                    AddToIndex(postings, documentId, title);
                }
            }

            var tree = BuildTree(new DirectoryInfo(sourceRoot).Name, documents, hierarchyMetadata);
            var manifest = new KnowledgeManifest(
                FormatName,
                FormatVersion,
                new DirectoryInfo(sourceRoot).Name,
                DateTimeOffset.UtcNow,
                documents);
            var searchIndex = new KnowledgeSearchIndex(
                FormatVersion,
                postings.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                    .ToDictionary(
                        pair => pair.Key,
                        pair => pair.Value.OrderBy(id => id, StringComparer.Ordinal).ToArray(),
                        StringComparer.Ordinal));

            await WriteJsonAsync(Path.Combine(temporaryRoot, "manifest.json"), manifest, cancellationToken);
            await WriteJsonAsync(Path.Combine(temporaryRoot, "tree.json"), tree, cancellationToken);
            await WriteJsonAsync(Path.Combine(indexDirectory, "search-index.json"), searchIndex, cancellationToken);
            await File.WriteAllTextAsync(
                Path.Combine(temporaryRoot, "README.txt"),
                "此目录由飞书知识库导出助手生成。请使用 Feishu Wiki Reader 打开本目录。\n",
                new UTF8Encoding(false),
                cancellationToken);

            if (Directory.Exists(outputRoot))
            {
                Directory.Delete(outputRoot, recursive: true);
            }
            Directory.Move(temporaryRoot, outputRoot);
            progress?.Report(new OfflineKnowledgeProgress(sourceFiles.Count, sourceFiles.Count, "完成"));
            return new OfflineKnowledgeBuildResult(outputRoot, sourceFiles.Count, indexedCount, reusedPages);
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

    private static void EnsureReplaceableOutput(string outputRoot)
    {
        if (!Directory.Exists(outputRoot))
        {
            return;
        }

        var manifestPath = Path.Combine(outputRoot, "manifest.json");
        if (!File.Exists(manifestPath))
        {
            throw new InvalidOperationException($"目标目录已经存在且不是本程序生成的离线知识库：{outputRoot}");
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
            if (!document.RootElement.TryGetProperty("format", out var format) ||
                !string.Equals(format.GetString(), FormatName, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"目标目录已经存在且格式无法识别：{outputRoot}");
            }
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"目标目录中的知识库清单损坏：{outputRoot}", ex);
        }
    }

    private static Dictionary<string, KnowledgeDocument> LoadPreviousDocuments(string outputRoot)
    {
        var manifestPath = Path.Combine(outputRoot, "manifest.json");
        if (!File.Exists(manifestPath))
        {
            return new Dictionary<string, KnowledgeDocument>(StringComparer.Ordinal);
        }

        try
        {
            var manifest = JsonSerializer.Deserialize<KnowledgeManifest>(File.ReadAllText(manifestPath), JsonOptions);
            return manifest?.Documents.ToDictionary(document => document.RelativePath, StringComparer.Ordinal)
                ?? new Dictionary<string, KnowledgeDocument>(StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            return new Dictionary<string, KnowledgeDocument>(StringComparer.Ordinal);
        }
    }

    private static bool IsSubPath(string parent, string candidate)
    {
        var relative = Path.GetRelativePath(parent, candidate);
        return relative != "." &&
               !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
               !Path.IsPathRooted(relative);
    }

    private static bool ShouldSkipSourceFile(string sourceRoot, string path)
    {
        var relative = NormalizeRelativePath(Path.GetRelativePath(sourceRoot, path));
        return relative.StartsWith(".feishu-export/", StringComparison.Ordinal) ||
               string.Equals(relative, "export-report.csv", StringComparison.OrdinalIgnoreCase) ||
               relative.EndsWith(".part", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeRelativePath(string path) => path.Replace('\\', '/');

    private static string CreateStableId(string relativePath)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(relativePath.Normalize(NormalizationForm.FormC)));
        return Convert.ToHexString(bytes.AsSpan(0, 8)).ToLowerInvariant();
    }

    private static string ClassifyFile(string extension) => extension switch
    {
        ".docx" => "docx",
        ".pdf" => "pdf",
        ".xlsx" or ".xls" => "spreadsheet",
        ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" => "image",
        _ => "file"
    };

    private static string BuildBreadcrumb(string relativePath)
    {
        var directory = Path.GetDirectoryName(relativePath.Replace('/', Path.DirectorySeparatorChar));
        return string.IsNullOrWhiteSpace(directory)
            ? string.Empty
            : directory.Replace(Path.DirectorySeparatorChar, '/').Replace("/", " / ", StringComparison.Ordinal);
    }

    private static KnowledgeTreeNode BuildTree(
        string rootName,
        IReadOnlyList<KnowledgeDocument> documents,
        ExportHierarchyMetadata hierarchyMetadata)
    {
        return hierarchyMetadata.Items.Count > 0
            ? BuildSemanticTree(rootName, documents, hierarchyMetadata)
            : BuildLegacyTree(rootName, documents, hierarchyMetadata.SiblingOrders);
    }

    private static KnowledgeTreeNode BuildSemanticTree(
        string rootName,
        IReadOnlyList<KnowledgeDocument> documents,
        ExportHierarchyMetadata hierarchyMetadata)
    {
        var documentsByPath = documents
            .GroupBy(document => NormalizeRelativePath(document.RelativePath), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var entries = hierarchyMetadata.Items
            .GroupBy(item => item.HierarchyToken, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToList();
        var parentTokens = entries
            .Where(item => !string.IsNullOrWhiteSpace(item.ParentHierarchyToken))
            .Select(item => item.ParentHierarchyToken!)
            .ToHashSet(StringComparer.Ordinal);
        var referencedDocumentIds = new HashSet<string>(StringComparer.Ordinal);
        var nodes = new Dictionary<string, MutableSemanticTreeNode>(StringComparer.Ordinal);

        foreach (var entry in entries)
        {
            documentsByPath.TryGetValue(NormalizeRelativePath(entry.RelativePath), out var document);
            var hasChildren = parentTokens.Contains(entry.HierarchyToken);
            if (document is null && !hasChildren && !entry.IsFolder && !entry.IsNavigationOnly)
            {
                continue;
            }

            if (document is not null)
            {
                referencedDocumentIds.Add(document.Id);
            }

            nodes[entry.HierarchyToken] = new MutableSemanticTreeNode(
                document?.Title ?? entry.Title,
                entry.ParentHierarchyToken,
                entry.SiblingOrder,
                document);
        }

        var root = new MutableSemanticTreeNode(rootName, null, null, null);
        foreach (var pair in nodes)
        {
            var entry = pair.Value;
            if (!string.IsNullOrWhiteSpace(entry.ParentHierarchyToken) &&
                nodes.TryGetValue(entry.ParentHierarchyToken, out var parent))
            {
                parent.Children.Add(entry);
            }
            else
            {
                root.Children.Add(entry);
            }
        }

        // Files left by an older export or added manually have no hierarchy token.
        // Keep them discoverable without guessing that a same-named file and folder
        // necessarily represent the same Feishu page.
        foreach (var document in documents.Where(document => !referencedDocumentIds.Contains(document.Id)))
        {
            root.Children.Add(new MutableSemanticTreeNode(document.Title, null, null, document));
        }

        return root.ToImmutable(isRoot: true);
    }

    private static KnowledgeTreeNode BuildLegacyTree(
        string rootName,
        IReadOnlyList<KnowledgeDocument> documents,
        IReadOnlyDictionary<string, int> siblingOrders)
    {
        var root = new MutableTreeNode(rootName);
        foreach (var document in documents)
        {
            var parts = document.RelativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var current = root;
            for (var index = 0; index < parts.Length - 1; index++)
            {
                var folderPath = string.Join("/", parts.Take(index + 1));
                var hasFolderOrder = siblingOrders.TryGetValue(folderPath, out var folderOrder);
                current = current.GetOrAddFolder(
                    parts[index],
                    hasFolderOrder ? folderOrder : null);
            }
            var hasDocumentOrder = siblingOrders.TryGetValue(document.RelativePath, out var documentOrder);
            current.Documents.Add(new OrderedDocument(
                document,
                hasDocumentOrder ? documentOrder : null));
        }
        return root.ToImmutable();
    }

    private static void AddToIndex(
        IDictionary<string, HashSet<string>> postings,
        string documentId,
        string text)
    {
        foreach (var token in Tokenize(text))
        {
            if (!postings.TryGetValue(token, out var ids))
            {
                ids = new HashSet<string>(StringComparer.Ordinal);
                postings[token] = ids;
            }
            ids.Add(documentId);
        }
    }

    private static HashSet<string> Tokenize(string text)
    {
        var normalized = text.Normalize(NormalizationForm.FormKC).ToLowerInvariant();
        var tokens = new HashSet<string>(StringComparer.Ordinal);
        var word = new StringBuilder();
        char? previousCjk = null;

        void FlushWord()
        {
            if (word.Length > 0)
            {
                tokens.Add(word.ToString());
                word.Clear();
            }
        }

        foreach (var character in normalized)
        {
            if (IsCjkSearchCharacter(character))
            {
                FlushWord();
                if (previousCjk is not null)
                {
                    tokens.Add(string.Concat(previousCjk.Value, character));
                }
                previousCjk = character;
                continue;
            }

            previousCjk = null;
            if (char.IsLetterOrDigit(character))
            {
                word.Append(character);
            }
            else
            {
                FlushWord();
            }
        }
        FlushWord();
        return tokens;
    }

    private static bool IsCjkSearchCharacter(char value) =>
        value is >= '\u3400' and <= '\u9fff' or
        >= '\uf900' and <= '\ufaff' or
        >= '\u3040' and <= '\u30ff' or
        >= '\uac00' and <= '\ud7af';

    private static async Task WriteJsonAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var stream = new FileStream(
            path,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 81920,
            useAsync: true);
        await JsonSerializer.SerializeAsync(stream, value, JsonOptions, cancellationToken);
    }

    private static async Task<T> ReadJsonAsync<T>(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 81920,
            useAsync: true);
        return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken)
            ?? throw new InvalidDataException($"JSON 文件内容为空：{path}");
    }

    private static ConvertedDocument ConvertDocx(string path)
    {
        using var archive = ZipFile.OpenRead(path);
        var documentEntry = archive.GetEntry("word/document.xml")
            ?? throw new InvalidDataException("DOCX 中缺少 word/document.xml。");

        XDocument document;
        using (var stream = documentEntry.Open())
        {
            document = XDocument.Load(stream, LoadOptions.PreserveWhitespace);
        }

        var relationships = LoadRelationships(archive);
        var body = document.Root?.Element(WordNamespace + "body")
            ?? throw new InvalidDataException("DOCX 中缺少正文。");
        var html = new StringBuilder();
        var text = new StringBuilder();

        foreach (var element in body.Elements())
        {
            if (element.Name == WordNamespace + "p")
            {
                RenderParagraph(archive, element, relationships, html, text);
            }
            else if (element.Name == WordNamespace + "tbl")
            {
                RenderTable(element, html, text);
            }
        }

        return new ConvertedDocument(html.ToString(), text.ToString().Trim());
    }

    private static Dictionary<string, RelationshipInfo> LoadRelationships(ZipArchive archive)
    {
        var result = new Dictionary<string, RelationshipInfo>(StringComparer.Ordinal);
        var entry = archive.GetEntry("word/_rels/document.xml.rels");
        if (entry is null)
        {
            return result;
        }

        XDocument document;
        using (var stream = entry.Open())
        {
            document = XDocument.Load(stream);
        }

        foreach (var relationship in document.Root?.Elements(PackageRelationshipsNamespace + "Relationship")
                     ?? Enumerable.Empty<XElement>())
        {
            var id = relationship.Attribute("Id")?.Value;
            var target = relationship.Attribute("Target")?.Value;
            if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(target))
            {
                result[id] = new RelationshipInfo(
                    target,
                    string.Equals(relationship.Attribute("TargetMode")?.Value, "External", StringComparison.OrdinalIgnoreCase));
            }
        }
        return result;
    }

    private static void RenderParagraph(
        ZipArchive archive,
        XElement paragraph,
        IReadOnlyDictionary<string, RelationshipInfo> relationships,
        StringBuilder html,
        StringBuilder text)
    {
        var plainText = string.Concat(paragraph.Descendants(WordNamespace + "t").Select(node => node.Value));
        var headingLevel = GetHeadingLevel(paragraph);
        var isList = paragraph.Descendants(WordNamespace + "numPr").Any();
        var content = new StringBuilder();

        foreach (var child in paragraph.Elements())
        {
            if (child.Name == WordNamespace + "r")
            {
                RenderRun(archive, child, relationships, content);
            }
            else if (child.Name == WordNamespace + "hyperlink")
            {
                RenderHyperlink(child, relationships, content);
            }
        }

        if (content.Length == 0 && plainText.Length > 0)
        {
            content.Append(WebUtility.HtmlEncode(plainText));
        }

        if (content.Length == 0)
        {
            return;
        }

        if (headingLevel is not null)
        {
            html.Append("<h").Append(headingLevel.Value).Append('>')
                .Append(content)
                .Append("</h").Append(headingLevel.Value).Append('>');
        }
        else if (isList)
        {
            html.Append("<div class=\"list-item\"><span class=\"bullet\">•</span><span>")
                .Append(content)
                .Append("</span></div>");
        }
        else
        {
            html.Append("<p>").Append(content).Append("</p>");
        }

        if (!string.IsNullOrWhiteSpace(plainText))
        {
            text.AppendLine(plainText);
        }
    }

    private static int? GetHeadingLevel(XElement paragraph)
    {
        var properties = paragraph.Element(WordNamespace + "pPr");
        var style = properties?.Element(WordNamespace + "pStyle")?.Attribute(WordNamespace + "val")?.Value;
        if (!string.IsNullOrWhiteSpace(style))
        {
            if (string.Equals(style, "Title", StringComparison.OrdinalIgnoreCase))
            {
                return 1;
            }
            var match = HeadingStyleRegex().Match(style);
            if (match.Success && int.TryParse(match.Groups[1].Value, out var level))
            {
                return Math.Clamp(level, 1, 6);
            }
        }

        var outline = properties?.Element(WordNamespace + "outlineLvl")?.Attribute(WordNamespace + "val")?.Value;
        return int.TryParse(outline, out var outlineLevel) ? Math.Clamp(outlineLevel + 1, 1, 6) : null;
    }

    private static void RenderRun(
        ZipArchive archive,
        XElement run,
        IReadOnlyDictionary<string, RelationshipInfo> relationships,
        StringBuilder html)
    {
        var value = string.Concat(run.Descendants(WordNamespace + "t").Select(node => node.Value));
        var encoded = WebUtility.HtmlEncode(value);
        var properties = run.Element(WordNamespace + "rPr");
        if (properties?.Element(WordNamespace + "b") is not null)
        {
            encoded = "<strong>" + encoded + "</strong>";
        }
        if (properties?.Element(WordNamespace + "i") is not null)
        {
            encoded = "<em>" + encoded + "</em>";
        }
        if (properties?.Element(WordNamespace + "u") is not null)
        {
            encoded = "<u>" + encoded + "</u>";
        }
        html.Append(encoded);

        foreach (var breakElement in run.Descendants(WordNamespace + "br"))
        {
            _ = breakElement;
            html.Append("<br>");
        }

        foreach (var blip in run.Descendants(DrawingNamespace + "blip"))
        {
            var relationshipId = blip.Attribute(RelationshipNamespace + "embed")?.Value;
            if (relationshipId is null || !relationships.TryGetValue(relationshipId, out var relationship) || relationship.External)
            {
                continue;
            }
            var image = TryReadEmbeddedImage(archive, relationship.Target);
            if (image is not null)
            {
                html.Append("<img loading=\"lazy\" src=\"")
                    .Append(image.Value.DataUri)
                    .Append("\" alt=\"\">");
            }
        }
    }

    private static void RenderHyperlink(
        XElement hyperlink,
        IReadOnlyDictionary<string, RelationshipInfo> relationships,
        StringBuilder html)
    {
        var label = WebUtility.HtmlEncode(string.Concat(hyperlink.Descendants(WordNamespace + "t").Select(node => node.Value)));
        var relationshipId = hyperlink.Attribute(RelationshipNamespace + "id")?.Value;
        if (relationshipId is not null &&
            relationships.TryGetValue(relationshipId, out var relationship) &&
            relationship.External &&
            Uri.TryCreate(relationship.Target, UriKind.Absolute, out var uri) &&
            uri.Scheme is "http" or "https")
        {
            html.Append("<a href=\"")
                .Append(WebUtility.HtmlEncode(uri.AbsoluteUri))
                .Append("\" target=\"_blank\" rel=\"noreferrer\">")
                .Append(label)
                .Append("</a>");
        }
        else
        {
            html.Append(label);
        }
    }

    private static EmbeddedImage? TryReadEmbeddedImage(ZipArchive archive, string relationshipTarget)
    {
        var normalizedTarget = relationshipTarget.Replace('\\', '/').TrimStart('/');
        while (normalizedTarget.StartsWith("../", StringComparison.Ordinal))
        {
            normalizedTarget = normalizedTarget[3..];
        }
        var entry = archive.GetEntry("word/" + normalizedTarget);
        if (entry is null)
        {
            return null;
        }

        using var stream = entry.Open();
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        var mime = Path.GetExtension(entry.Name).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".svg" => "image/svg+xml",
            _ => "application/octet-stream"
        };
        return new EmbeddedImage($"data:{mime};base64,{Convert.ToBase64String(buffer.ToArray())}");
    }

    private static void RenderTable(XElement table, StringBuilder html, StringBuilder text)
    {
        html.Append("<div class=\"table-scroll\"><table>");
        foreach (var row in table.Elements(WordNamespace + "tr"))
        {
            html.Append("<tr>");
            foreach (var cell in row.Elements(WordNamespace + "tc"))
            {
                var cellText = string.Join(
                    " ",
                    cell.Descendants(WordNamespace + "p")
                        .Select(p => string.Concat(p.Descendants(WordNamespace + "t").Select(node => node.Value)))
                        .Where(value => !string.IsNullOrWhiteSpace(value)));
                html.Append("<td>").Append(WebUtility.HtmlEncode(cellText)).Append("</td>");
                if (!string.IsNullOrWhiteSpace(cellText))
                {
                    text.Append(cellText).Append(' ');
                }
            }
            html.Append("</tr>");
            text.AppendLine();
        }
        html.Append("</table></div>");
    }

    private sealed class MutableSemanticTreeNode(
        string title,
        string? parentHierarchyToken,
        int? siblingOrder,
        KnowledgeDocument? document)
    {
        public string Title { get; } = title;
        public string? ParentHierarchyToken { get; } = parentHierarchyToken;
        public int? SiblingOrder { get; } = siblingOrder;
        public KnowledgeDocument? Document { get; } = document;
        public List<MutableSemanticTreeNode> Children { get; } = [];

        public KnowledgeTreeNode ToImmutable(bool isRoot = false)
        {
            var children = Children
                .OrderBy(item => item.SiblingOrder.HasValue ? 0 : 1)
                .ThenBy(item => item.SiblingOrder ?? int.MaxValue)
                .ThenBy(item => item.Title, StringComparer.CurrentCulture)
                .Select(item => item.ToImmutable())
                .ToList();
            return new KnowledgeTreeNode(
                Title,
                isRoot ? "folder" : "page",
                Document?.Id,
                Document?.Kind,
                children);
        }
    }

    private sealed class MutableTreeNode(string title, int? siblingOrder = null)
    {
        private readonly Dictionary<string, MutableTreeNode> _folders = new(StringComparer.CurrentCulture);

        public string Title { get; } = title;

        public int? SiblingOrder { get; private set; } = siblingOrder;

        public List<OrderedDocument> Documents { get; } = [];

        public MutableTreeNode GetOrAddFolder(string name, int? order)
        {
            if (!_folders.TryGetValue(name, out var folder))
            {
                folder = new MutableTreeNode(name, order);
                _folders[name] = folder;
            }
            else if (!folder.SiblingOrder.HasValue && order.HasValue)
            {
                folder.SiblingOrder = order;
            }
            return folder;
        }

        public KnowledgeTreeNode ToImmutable()
        {
            var children = _folders.Values
                .Select(folder => new OrderedTreeNode(
                    folder.SiblingOrder,
                    true,
                    folder.Title,
                    folder.ToImmutable()))
                .Concat(Documents
                    .Select(item => new OrderedTreeNode(
                        item.SiblingOrder,
                        false,
                        item.Document.Title,
                        new KnowledgeTreeNode(
                            item.Document.Title,
                            "document",
                            item.Document.Id,
                            item.Document.Kind,
                            []))))
                .OrderBy(item => item.SiblingOrder.HasValue ? 0 : 1)
                .ThenBy(item => item.SiblingOrder ?? int.MaxValue)
                .ThenBy(item => item.IsFolder ? 0 : 1)
                .ThenBy(item => item.Title, StringComparer.CurrentCulture)
                .Select(item => item.Node)
                .ToList();
            return new KnowledgeTreeNode(Title, "folder", null, null, children);
        }
    }

    private sealed record OrderedDocument(KnowledgeDocument Document, int? SiblingOrder);

    private sealed record OrderedTreeNode(
        int? SiblingOrder,
        bool IsFolder,
        string Title,
        KnowledgeTreeNode Node);

    private sealed record KnowledgeManifest(
        string Format,
        int Version,
        string Name,
        DateTimeOffset GeneratedUtc,
        IReadOnlyList<KnowledgeDocument> Documents);

    private sealed record KnowledgeDocument(
        string Id,
        string Title,
        string Kind,
        string RelativePath,
        string OriginalPath,
        string? PagePath,
        string Breadcrumb,
        long SourceSizeBytes,
        DateTimeOffset SourceModifiedUtc);

    private sealed record KnowledgeTreeNode(
        string Title,
        string Type,
        string? DocumentId,
        string? Kind,
        IReadOnlyList<KnowledgeTreeNode> Children);

    private sealed record KnowledgePage(string Title, string Html, string Text);

    private sealed record KnowledgeSearchIndex(int Version, IReadOnlyDictionary<string, string[]> Postings);

    private sealed record ConvertedDocument(string Html, string Text);

    private sealed record RelationshipInfo(string Target, bool External);

    private readonly record struct EmbeddedImage(string DataUri);

    private static readonly XNamespace WordNamespace = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private static readonly XNamespace RelationshipNamespace = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace DrawingNamespace = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private static readonly XNamespace PackageRelationshipsNamespace = "http://schemas.openxmlformats.org/package/2006/relationships";

    [GeneratedRegex(@"(?:Heading|heading)[^0-9]*([1-9])", RegexOptions.CultureInvariant)]
    private static partial Regex HeadingStyleRegex();
}
