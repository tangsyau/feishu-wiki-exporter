using System.Text.Json;
using FeishuExporter.Core;
using Xunit;

namespace FeishuExporter.Core.Tests;

public sealed class DirectOfflineKnowledgeBuilderTests
{
    [Fact]
    public async Task BuildAsync_CreatesCompactVersion3PackageWithoutOfficeCopies()
    {
        var root = Path.Combine(Path.GetTempPath(), "feishu-reader-v3-" + Guid.NewGuid().ToString("N"));
        try
        {
            var parent = Item("parent", "parent-doc", null, "导航", 0);
            var child = Item("child", "child-doc", "parent", "正文", 0);
            var items = new List<ExportItem> { parent, child };
            var inspections = new Dictionary<string, DocumentInspection>(StringComparer.Ordinal)
            {
                ["parent"] = Inspection(
                    Block("page", 1),
                    Block("heading", 3, "heading1", "子页面"),
                    Block("subpages", 51, "sub_page_list")),
                ["child"] = Inspection(
                    Block("page", 1),
                    Block("text", 2, "text", "第一段"),
                    Block("text-2", 2, "text", "第二段"))
            };
            var preparation = new ExportPreparation(
                "测试知识库",
                items,
                [],
                [],
                [],
                [],
                inspections);
            var options = new ExportOptions
            {
                Credentials = new FeishuCredentials("app", "secret"),
                SourceType = ExportSourceType.Wiki,
                SourceId = "space",
                ExportRoot = root,
                OutputMode = ExportOutputMode.Reader,
                SkipUnchanged = true
            };
            using var client = new FeishuApiClient(
                options.Credentials,
                new StubHandler(_ => throw new InvalidOperationException("No HTTP request expected.")));

            var result = await new DirectOfflineKnowledgeBuilder(client).BuildAsync(options, preparation);

            Assert.Equal(2, result.Pages);
            Assert.False(Directory.Exists(Path.Combine(result.OutputDirectory, "files")));
            Assert.False(Directory.Exists(Path.Combine(root, "测试知识库")));
            using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(
                Path.Combine(result.OutputDirectory, "manifest.json")));
            Assert.Equal(3, manifest.RootElement.GetProperty("version").GetInt32());
            Assert.Equal(2, manifest.RootElement.GetProperty("statistics").GetProperty("pages").GetInt32());

            var parentDocument = manifest.RootElement.GetProperty("documents").EnumerateArray()
                .Single(document => document.GetProperty("hierarchyToken").GetString() == "parent");
            var pagePath = parentDocument.GetProperty("pagePath").GetString()!;
            using var page = JsonDocument.Parse(await File.ReadAllTextAsync(
                Path.Combine(result.OutputDirectory, pagePath.Replace('/', Path.DirectorySeparatorChar))));
            var subpages = page.RootElement.GetProperty("blocks").EnumerateArray()
                .Single(block => block.GetProperty("type").GetString() == "subpages");
            Assert.Equal("正文", Assert.Single(subpages.GetProperty("links").EnumerateArray().ToArray())
                .GetProperty("title").GetString());
            Assert.True(Directory.Exists(Path.Combine(root, ".feishu-exporter-state")));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task BuildAsync_RestoresNestedCatalogFromWikiToken()
    {
        var root = Path.Combine(Path.GetTempPath(), "feishu-reader-catalog-" + Guid.NewGuid().ToString("N"));
        try
        {
            var categoryA = Item("category-a", "doc-a", null, "A 大类", 0);
            var categoryB = Item("category-b", "doc-b", "category-a", "B 类", 0);
            var b1 = Item("b-1", "doc-b-1", "category-b", "B1", 0);
            var b2 = Item("b-2", "doc-b-2", "category-b", "B2", 1);
            var items = new List<ExportItem> { categoryA, categoryB, b1, b2 };
            var inspections = new Dictionary<string, DocumentInspection>(StringComparer.Ordinal)
            {
                ["category-a"] = Inspection(
                    Block("page-a", 1),
                    Block("heading-b", 3, "heading1", "B 类"),
                    Catalog("catalog-b", 51, "sub_page_list", "category-b")),
                ["category-b"] = Inspection(Block("page-b", 1)),
                ["b-1"] = Inspection(Block("page-b-1", 1), Block("text-b-1", 2, "text", "正文一")),
                ["b-2"] = Inspection(Block("page-b-2", 1), Block("text-b-2", 2, "text", "正文二"))
            };
            var preparation = new ExportPreparation(
                "多级目录测试",
                items,
                [],
                [],
                [],
                [],
                inspections);
            var options = new ExportOptions
            {
                Credentials = new FeishuCredentials("app", "secret"),
                SourceType = ExportSourceType.Wiki,
                SourceId = "space",
                ExportRoot = root,
                OutputMode = ExportOutputMode.Reader,
                SkipUnchanged = true
            };
            using var client = new FeishuApiClient(
                options.Credentials,
                new StubHandler(_ => throw new InvalidOperationException("No HTTP request expected.")));

            var result = await new DirectOfflineKnowledgeBuilder(client).BuildAsync(options, preparation);

            using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(
                Path.Combine(result.OutputDirectory, "manifest.json")));
            var categoryDocument = manifest.RootElement.GetProperty("documents").EnumerateArray()
                .Single(document => document.GetProperty("hierarchyToken").GetString() == "category-a");
            using var page = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(
                result.OutputDirectory,
                categoryDocument.GetProperty("pagePath").GetString()!.Replace('/', Path.DirectorySeparatorChar))));
            var subpages = page.RootElement.GetProperty("blocks").EnumerateArray()
                .Single(block => block.GetProperty("type").GetString() == "subpages");
            Assert.Equal(
                new[] { "B1", "B2" },
                subpages.GetProperty("links").EnumerateArray()
                    .Select(link => link.GetProperty("title").GetString()).ToArray());

            using var diagnostic = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(
                result.OutputDirectory,
                "diagnostics",
                "subpage-link-resolution.json")));
            Assert.Equal(2, diagnostic.RootElement.GetProperty("version").GetInt32());
            var resolution = Assert.Single(diagnostic.RootElement.GetProperty("items").EnumerateArray().ToArray());
            Assert.Equal("wiki_token", resolution.GetProperty("resolution").GetProperty("strategy").GetString());
            Assert.Equal("category-b", resolution.GetProperty("resolution").GetProperty("targetHierarchyToken").GetString());

            var statePath = Assert.Single(Directory.GetFiles(
                Path.Combine(root, ".feishu-exporter-state"),
                "reader-state.json",
                SearchOption.AllDirectories));
            using var state = JsonDocument.Parse(await File.ReadAllTextAsync(statePath));
            Assert.Equal(3, state.RootElement.GetProperty("version").GetInt32());

            var b3 = Item("b-3", "doc-b-3", "category-b", "B3", 2);
            items.Add(b3);
            inspections["b-3"] = Inspection(
                Block("page-b-3", 1),
                Block("text-b-3", 2, "text", "正文三"));
            var updatedPreparation = new ExportPreparation(
                "多级目录测试",
                items,
                [],
                [],
                [],
                [],
                inspections);

            var updated = await new DirectOfflineKnowledgeBuilder(client).BuildAsync(options, updatedPreparation);

            Assert.Equal(2, updated.ReusedPages);
            using var updatedManifest = JsonDocument.Parse(await File.ReadAllTextAsync(
                Path.Combine(updated.OutputDirectory, "manifest.json")));
            var updatedCategoryDocument = updatedManifest.RootElement.GetProperty("documents").EnumerateArray()
                .Single(document => document.GetProperty("hierarchyToken").GetString() == "category-a");
            using var updatedPage = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(
                updated.OutputDirectory,
                updatedCategoryDocument.GetProperty("pagePath").GetString()!
                    .Replace('/', Path.DirectorySeparatorChar))));
            var updatedSubpages = updatedPage.RootElement.GetProperty("blocks").EnumerateArray()
                .Single(block => block.GetProperty("type").GetString() == "subpages");
            Assert.Equal(
                new[] { "B1", "B2", "B3" },
                updatedSubpages.GetProperty("links").EnumerateArray()
                    .Select(link => link.GetProperty("title").GetString()).ToArray());
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static ExportItem Item(
        string hierarchyToken,
        string contentToken,
        string? parent,
        string title,
        int order) => new()
    {
        HierarchyToken = hierarchyToken,
        ContentToken = contentToken,
        ParentHierarchyToken = parent,
        Title = title,
        Type = "docx",
        ModifiedTime = "1",
        SiblingOrder = order,
        IsFolder = false
    };

    private static DocumentInspection Inspection(params DocumentBlockDto[] blocks) => new(
        [],
        new DocumentContentAnalysis(0, 0, new Dictionary<int, int>(), [], [], 0, false, false, []))
    {
        Blocks = blocks
    };

    private static DocumentBlockDto Block(string id, int type, string? property = null, string? text = null)
    {
        Dictionary<string, JsonElement>? properties = null;
        if (property is not null)
        {
            var json = text is null
                ? "{}"
                : JsonSerializer.Serialize(new
                {
                    elements = new[] { new { text_run = new { content = text } } }
                });
            using var document = JsonDocument.Parse(json);
            properties = new Dictionary<string, JsonElement> { [property] = document.RootElement.Clone() };
        }
        return new DocumentBlockDto { BlockId = id, BlockType = type, Properties = properties };
    }

    private static DocumentBlockDto Catalog(
        string id,
        int type,
        string property,
        string wikiToken)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(new { wiki_token = wikiToken }));
        return new DocumentBlockDto
        {
            BlockId = id,
            BlockType = type,
            Properties = new Dictionary<string, JsonElement>
            {
                [property] = document.RootElement.Clone()
            }
        };
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(handler(request));
    }
}
