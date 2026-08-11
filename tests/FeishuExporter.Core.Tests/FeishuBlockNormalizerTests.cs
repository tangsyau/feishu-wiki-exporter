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
        var lists = page.Blocks.Where(block => block.Type == "subpages").ToArray();
        Assert.Collection(
            lists,
            first => Assert.Equal(new[] { "page-a" }, first.Links.Select(link => link.TargetPageId).ToArray()),
            second => Assert.Equal(new[] { "page-b" }, second.Links.Select(link => link.TargetPageId).ToArray()));
        Assert.Empty(page.SubPageResolutionIssues);
    }

    [Fact]
    public void Normalize_MatchesNestedTitlesAcrossSeparateSubPageLists()
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
            Block("page", 1, children: ["list-a", "list-b"]),
            Block("list-a", 51, "sub_page_list", parentId: "page", children: ["text-a"]),
            Block("text-a", 2, "text", "法律", "list-a"),
            Block("list-b", 51, "sub_page_list", parentId: "page", children: ["text-b"]),
            Block("text-b", 2, "text", "行政法规", "list-b")
        };

        var page = new FeishuBlockNormalizer(
            pageIds,
            children,
            new Dictionary<string, string>(),
            "parent-page").Normalize("法律规范", blocks);

        Assert.Collection(
            page.Blocks,
            first => Assert.Equal("page-a", Assert.Single(first.Links).TargetPageId),
            second => Assert.Equal("page-b", Assert.Single(second.Links).TargetPageId));
        Assert.Empty(page.SubPageResolutionIssues);
    }

    [Fact]
    public void Normalize_UsesWikiTokenToRenderTargetNodeChildren()
    {
        var categoryB = Item("category-b", "doc-b", "B 类", 0);
        var categoryC = Item("category-c", "doc-c", "C 类", 1);
        var b1 = Item("b-1", "doc-b-1", "B1", 0) with { ParentHierarchyToken = "category-b" };
        var b2 = Item("b-2", "doc-b-2", "B2", 1) with { ParentHierarchyToken = "category-b" };
        var c1 = Item("c-1", "doc-c-1", "C1", 0) with { ParentHierarchyToken = "category-c" };
        var pageIds = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["category-b"] = "page-b",
            ["category-c"] = "page-c",
            ["b-1"] = "page-b-1",
            ["b-2"] = "page-b-2",
            ["c-1"] = "page-c-1"
        };
        var hierarchy = new Dictionary<string, IReadOnlyList<ExportItem>>(StringComparer.Ordinal)
        {
            ["category-a"] = [categoryB, categoryC],
            ["category-b"] = [b1, b2],
            ["category-c"] = [c1]
        };
        var blocks = new[]
        {
            Block("page", 1),
            Block("heading-b", 3, "heading1", "B 类"),
            Catalog("catalog-b", 42, "wiki_catalog", "category-b"),
            Block("heading-c", 3, "heading1", "C 类"),
            Catalog("catalog-c", 51, "sub_page_list", "category-c")
        };

        var page = new FeishuBlockNormalizer(
            pageIds,
            [categoryB, categoryC],
            new Dictionary<string, string>(),
            "page-a",
            hierarchy,
            "category-a").Normalize("A 大类", blocks);

        var lists = page.Blocks.Where(block => block.Type == "subpages").ToArray();
        Assert.Collection(
            lists,
            first => Assert.Equal(
                new[] { "page-b-1", "page-b-2" },
                first.Links.Select(link => link.TargetPageId).ToArray()),
            second => Assert.Equal(
                new[] { "page-c-1" },
                second.Links.Select(link => link.TargetPageId).ToArray()));
        Assert.All(page.SubPageResolutions, resolution =>
        {
            Assert.True(resolution.Resolved);
            Assert.Equal("wiki_token", resolution.Strategy);
        });
        Assert.Empty(page.SubPageResolutionIssues);
    }

    [Fact]
    public void Normalize_HeadingFallbackRendersCategoryChildrenInsteadOfCategoryPage()
    {
        var category = Item("category-b", "doc-b", "B 类", 0);
        var b1 = Item("b-1", "doc-b-1", "B1", 0) with { ParentHierarchyToken = "category-b" };
        var b2 = Item("b-2", "doc-b-2", "B2", 1) with { ParentHierarchyToken = "category-b" };
        var pageIds = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["category-b"] = "page-b",
            ["b-1"] = "page-b-1",
            ["b-2"] = "page-b-2"
        };
        var hierarchy = new Dictionary<string, IReadOnlyList<ExportItem>>(StringComparer.Ordinal)
        {
            ["category-a"] = [category],
            ["category-b"] = [b1, b2]
        };

        var page = new FeishuBlockNormalizer(
            pageIds,
            [category],
            new Dictionary<string, string>(),
            "page-a",
            hierarchy,
            "category-a").Normalize(
                "A 大类",
                [
                    Block("page", 1),
                    Block("heading-b", 3, "heading1", "B 类"),
                    Block("catalog-b", 42, "wiki_catalog"),
                    Block("catalog-unresolved", 42, "wiki_catalog")
                ]);

        var first = Assert.Single(page.Blocks, block => block.Id == "catalog-b");
        Assert.Equal(
            new[] { "page-b-1", "page-b-2" },
            first.Links.Select(link => link.TargetPageId).ToArray());
        Assert.Equal(
            "preceding_heading_target_children",
            Assert.Single(page.SubPageResolutions, resolution => resolution.BlockId == "catalog-b").Strategy);
    }

    [Fact]
    public void Normalize_TreatsViewAsContainerAndRecordsUnknownAddOn()
    {
        using var addOn = JsonDocument.Parse("""
            {"component_id":"business","component_type_id":"business-widget","record":"opaque"}
            """);
        var blocks = new[]
        {
            Block("page", 1, children: ["view", "widget"]),
            Block("view", 33, "view", parentId: "page", children: ["text"]),
            Block("text", 2, "text", "可读内容", "view"),
            new DocumentBlockDto
            {
                BlockId = "widget",
                BlockType = 40,
                ParentId = "page",
                Properties = new Dictionary<string, JsonElement>
                {
                    ["add_ons"] = addOn.RootElement.Clone()
                }
            }
        };

        var page = new FeishuBlockNormalizer(
            new Dictionary<string, string>(),
            [],
            new Dictionary<string, string>(),
            "parent-page").Normalize("组件", blocks);

        Assert.Equal("container", page.Blocks[0].Type);
        Assert.Equal("可读内容", Assert.Single(page.Blocks[0].Children).Text);
        Assert.Equal("unsupported", page.Blocks[1].Type);
        Assert.Equal("business-widget", page.Blocks[1].ComponentTypeId);
        Assert.True(page.Blocks[1].HasSourceRecord);
    }

    [Fact]
    public void Normalize_UsesAllDirectChildrenOnlyForSingleSubPageList()
    {
        var children = new[]
        {
            Item("child-a", "doc-a", "甲", 0),
            Item("child-b", "doc-b", "乙", 1)
        };
        var pageIds = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["child-a"] = "page-a",
            ["child-b"] = "page-b"
        };
        var page = new FeishuBlockNormalizer(
            pageIds,
            children,
            new Dictionary<string, string>(),
            "parent-page").Normalize("单目录", [Block("page", 1), Block("catalog", 42, "wiki_catalog")]);

        Assert.Equal(
            new[] { "page-a", "page-b" },
            Assert.Single(page.Blocks).Links.Select(link => link.TargetPageId).ToArray());
        Assert.Empty(page.SubPageResolutionIssues);
    }

    [Fact]
    public void Normalize_RecordsUnresolvedMultipleListsWithoutAssigningAllChildrenToFirst()
    {
        var child = Item("child-a", "doc-a", "实际子页面", 0);
        var page = new FeishuBlockNormalizer(
            new Dictionary<string, string> { ["child-a"] = "page-a" },
            [child],
            new Dictionary<string, string>(),
            "parent-page").Normalize(
                "多目录",
                [Block("page", 1), Block("catalog-a", 42, "wiki_catalog"), Block("catalog-b", 42, "wiki_catalog")]);

        Assert.All(page.Blocks, block => Assert.Empty(block.Links));
        Assert.Equal(2, page.SubPageResolutionIssues.Count);
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

    [Fact]
    public void Normalize_PreservesOrderedListSequence()
    {
        var page = new FeishuBlockNormalizer(
            new Dictionary<string, string>(),
            [],
            new Dictionary<string, string>(),
            "source-page").Normalize(
                "有序列表",
                [
                    Block("page", 1),
                    Ordered("ordered-1", "第一项", "1"),
                    Ordered("ordered-2", "第二项", "auto"),
                    Ordered("ordered-3", "手动第七项", "7"),
                    Ordered("ordered-4", "历史数据", null)
                ]);

        Assert.Collection(
            page.Blocks,
            first => Assert.Equal("1", first.Sequence),
            second => Assert.Equal("auto", second.Sequence),
            third => Assert.Equal("7", third.Sequence),
            fourth => Assert.Null(fourth.Sequence));
        Assert.All(page.Blocks, block => Assert.Equal("ordered", block.Type));
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

    private static DocumentBlockDto Block(
        string id,
        int type,
        string? property = null,
        string? text = null,
        string? parentId = null,
        List<string>? children = null)
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
            ParentId = parentId,
            Children = children,
            Properties = properties
        };
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

    private static DocumentBlockDto Ordered(string id, string text, string? sequence)
    {
        var payload = new Dictionary<string, object>
        {
            ["elements"] = new[] { new { text_run = new { content = text } } }
        };
        if (sequence is not null)
        {
            payload["style"] = new { sequence };
        }
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(payload));
        return new DocumentBlockDto
        {
            BlockId = id,
            BlockType = 13,
            Properties = new Dictionary<string, JsonElement>
            {
                ["ordered"] = document.RootElement.Clone()
            }
        };
    }
}
