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

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(handler(request));
    }
}
