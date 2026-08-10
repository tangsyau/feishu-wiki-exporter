using System.Text.Json;
using FeishuExporter.Core;
using Xunit;

namespace FeishuExporter.Core.Tests;

public sealed class FeishuBlockNormalizerTests
{
    [Fact]
    public void Normalize_PreservesSeparateBlocksAndCreatesClickableSubPages()
    {
        var children = new[]
        {
            Item("child-a", "doc-a", "法律", 0),
            Item("child-b", "doc-b", "行政法规", 1)
        };
        var pageIds = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["child-a"] = "page-a",
            ["doc-a"] = "page-a",
            ["child-b"] = "page-b",
            ["doc-b"] = "page-b"
        };
        var blocks = new[]
        {
            Block("page", 1),
            Block("heading-a", 3, "heading1", "法律"),
            Block("catalog-a", 42, "wiki_catalog"),
            Block("heading-b", 3, "heading1", "行政法规"),
            Block("catalog-b", 42, "wiki_catalog")
        };

        var page = new FeishuBlockNormalizer(
            pageIds,
            children,
            new Dictionary<string, string>(),
            "parent-page").Normalize("法律规范", blocks);

        Assert.Equal(new[] { "heading", "subpages", "heading", "subpages" },
            page.Blocks.Select(block => block.Type).ToArray());
        Assert.Contains("法律", page.Text);
        Assert.Contains(Environment.NewLine, page.Text);
        Assert.Contains("行政法规", page.Text);
        var links = Assert.Single(page.Blocks.Where(block => block.Type == "subpages" && block.Links.Count > 0)).Links;
        Assert.Equal(new[] { "page-a", "page-b" }, links.Select(link => link.TargetPageId).ToArray());
    }

    [Fact]
    public void Normalize_ResolvesMentionedDocumentToInternalPage()
    {
        using var mention = JsonDocument.Parse("""
            {"elements":[{"mention_doc":{"token":"target-doc","title":"目标页面","url":"https://example.feishu.cn/docx/target-doc"}}]}
            """);
        var block = new DocumentBlockDto
        {
            BlockId = "text",
            BlockType = 2,
            Properties = new Dictionary<string, JsonElement>
            {
                ["text"] = mention.RootElement.Clone()
            }
        };
        var page = new FeishuBlockNormalizer(
            new Dictionary<string, string> { ["target-doc"] = "target-page" },
            [],
            new Dictionary<string, string>(),
            "source-page").Normalize("来源", [Block("page", 1), block]);

        var inline = Assert.Single(Assert.Single(page.Blocks).Inlines);
        Assert.Equal("目标页面", inline.Text);
        Assert.Equal("target-page", inline.TargetPageId);
    }

    private static ExportItem Item(string hierarchyToken, string contentToken, string title, int order) => new()
    {
        HierarchyToken = hierarchyToken,
        ContentToken = contentToken,
        ParentHierarchyToken = "parent",
        Title = title,
        Type = "docx",
        SiblingOrder = order,
        IsFolder = false
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
            properties = new Dictionary<string, JsonElement>
            {
                [property] = document.RootElement.Clone()
            };
        }
        return new DocumentBlockDto
        {
            BlockId = id,
            BlockType = type,
            Properties = properties
        };
    }
}
