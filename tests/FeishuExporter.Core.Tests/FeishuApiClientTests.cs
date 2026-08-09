using System.Net;
using System.Text;
using System.Text.Json;
using FeishuExporter.Core;
using Xunit;

namespace FeishuExporter.Core.Tests;

public sealed class FeishuApiClientTests
{
    [Fact]
    public async Task ListWikiItemsAsync_RecordsTheReturnedOrderWithinEachParent()
    {
        var handler = new StubHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/auth/v3/tenant_access_token/internal", StringComparison.Ordinal))
            {
                return Json("""
                    {"code":0,"msg":"ok","tenant_access_token":"token","expire":7200}
                    """);
            }

            var query = request.RequestUri.Query;
            if (query.Contains("parent_node_token=root-b", StringComparison.Ordinal))
            {
                return Json("""
                    {
                      "code":0,"msg":"success","data":{"has_more":false,"items":[
                        {"node_token":"child-d","obj_token":"doc-d","obj_type":"docx","parent_node_token":"root-b","title":"子文档 D","has_child":false},
                        {"node_token":"child-c","obj_token":"doc-c","obj_type":"docx","parent_node_token":"root-b","title":"子文档 C","has_child":false}
                      ]}
                    }
                    """);
            }

            return Json("""
                {
                  "code":0,"msg":"success","data":{"has_more":false,"items":[
                    {"node_token":"root-b","obj_token":"doc-b","obj_type":"docx","title":"文档 B","has_child":true},
                    {"node_token":"root-a","obj_token":"doc-a","obj_type":"docx","title":"文档 A","has_child":false}
                  ]}
                }
                """);
        });
        using var client = new FeishuApiClient(new FeishuCredentials("app", "secret"), handler);

        var items = await client.ListWikiItemsAsync("space-id");

        Assert.Equal(0, Assert.Single(items, item => item.HierarchyToken == "root-b").SiblingOrder);
        Assert.Equal(1, Assert.Single(items, item => item.HierarchyToken == "root-a").SiblingOrder);
        Assert.Equal(0, Assert.Single(items, item => item.HierarchyToken == "child-d").SiblingOrder);
        Assert.Equal(1, Assert.Single(items, item => item.HierarchyToken == "child-c").SiblingOrder);
    }

    [Fact]
    public async Task ListDocumentEmbeddedFilesAsync_ReturnsOnlyFileBlocks()
    {
        var handler = new StubHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/auth/v3/tenant_access_token/internal", StringComparison.Ordinal))
            {
                return Json("""
                    {"code":0,"msg":"ok","tenant_access_token":"token","expire":7200}
                    """);
            }

            Assert.Equal("/open-apis/docx/v1/documents/doc-token/blocks", path);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            return Json("""
                {
                  "code": 0,
                  "msg": "success",
                  "data": {
                    "has_more": false,
                    "items": [
                      {"block_id":"text-1","block_type":2},
                      {"block_id":"file-1","block_type":23,"file":{"token":"media-token","name":"材料.pdf"}}
                    ]
                  }
                }
                """);
        });
        using var client = new FeishuApiClient(new FeishuCredentials("app", "secret"), handler);
        var document = new ExportItem
        {
            HierarchyToken = "wiki-node",
            ContentToken = "doc-token",
            Title = "项目说明",
            Type = "docx",
            ModifiedTime = "123",
            IsFolder = false
        };

        var files = await client.ListDocumentEmbeddedFilesAsync(document);

        var file = Assert.Single(files);
        Assert.Equal("embedded:wiki-node:file-1", file.HierarchyToken);
        Assert.Equal("media-token", file.ContentToken);
        Assert.Equal("材料.pdf", file.Title);
        Assert.Equal("wiki-node", file.ParentHierarchyToken);
        Assert.Equal("embedded_file", file.Type);
        Assert.Equal("123", file.ModifiedTime);
    }

    [Fact]
    public async Task ListDocumentEmbeddedFilesAsync_UsesHierarchyTokenWhenDocumentsShareContentToken()
    {
        var handler = new StubHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith(
                    "/auth/v3/tenant_access_token/internal",
                    StringComparison.Ordinal))
            {
                return Json("""
                    {"code":0,"msg":"ok","tenant_access_token":"token","expire":7200}
                    """);
            }

            return Json("""
                {
                  "code": 0,
                  "msg": "success",
                  "data": {
                    "has_more": false,
                    "items": [
                      {"block_id":"file-1","block_type":23,"file":{"token":"media-token","name":"材料.pdf"}}
                    ]
                  }
                }
                """);
        });
        using var client = new FeishuApiClient(new FeishuCredentials("app", "secret"), handler);
        var firstDocument = Document("wiki-node-a", "shared-doc-token");
        var secondDocument = Document("wiki-node-b", "shared-doc-token");

        var firstFile = Assert.Single(await client.ListDocumentEmbeddedFilesAsync(firstDocument));
        var secondFile = Assert.Single(await client.ListDocumentEmbeddedFilesAsync(secondDocument));

        Assert.Equal("embedded:wiki-node-a:file-1", firstFile.HierarchyToken);
        Assert.Equal("embedded:wiki-node-b:file-1", secondFile.HierarchyToken);
        Assert.NotEqual(firstFile.HierarchyToken, secondFile.HierarchyToken);
    }

    [Fact]
    public async Task ListDocumentEmbeddedFilesAsync_IgnoresRepeatedFileBlock()
    {
        var handler = new StubHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith(
                    "/auth/v3/tenant_access_token/internal",
                    StringComparison.Ordinal))
            {
                return Json("""
                    {"code":0,"msg":"ok","tenant_access_token":"token","expire":7200}
                    """);
            }

            return Json("""
                {
                  "code": 0,
                  "msg": "success",
                  "data": {
                    "has_more": false,
                    "items": [
                      {"block_id":"file-1","block_type":23,"file":{"token":"media-token","name":"材料.pdf"}},
                      {"block_id":"file-1","block_type":23,"file":{"token":"media-token","name":"材料.pdf"}}
                    ]
                  }
                }
                """);
        });
        using var client = new FeishuApiClient(new FeishuCredentials("app", "secret"), handler);

        var file = Assert.Single(await client.ListDocumentEmbeddedFilesAsync(
            Document("wiki-node", "doc-token")));

        Assert.Equal("embedded:wiki-node:file-1", file.HierarchyToken);
    }

    [Fact]
    public async Task InspectDocumentAsync_TreatsThreeBodyBlocksSeparatedByBlankLinesAsContent()
    {
        using var client = ClientReturningBlocks("""
            {"block_id":"page","block_type":1},
            {"block_id":"text-1","block_type":2,"text":{"elements":[{"text_run":{"content":"第一段"}}]}},
            {"block_id":"blank","block_type":2,"text":{"elements":[]}},
            {"block_id":"text-2","block_type":2,"text":{"elements":[{"text_run":{"content":"第二段"}}]}},
            {"block_id":"text-3","block_type":2,"text":{"elements":[{"text_run":{"content":"第三段"}}]}}
            """);

        var inspection = await client.InspectDocumentAsync(Document("wiki-node", "doc-token"));

        Assert.Equal(3, inspection.Content.MaxConsecutiveBodyBlocks);
        Assert.True(inspection.Content.HasSubstantiveContent);
    }

    [Fact]
    public async Task InspectDocumentAsync_TreatsHeadingsCatalogsAndBlankLinesAsNavigationOnly()
    {
        using var client = ClientReturningBlocks("""
            {"block_id":"page","block_type":1},
            {"block_id":"heading","block_type":3,"heading1":{"elements":[{"text_run":{"content":"目录"}}]}},
            {"block_id":"blank","block_type":2,"text":{"elements":[]}},
            {"block_id":"catalog","block_type":42,"wiki_catalog":{}},
            {"block_id":"subpages","block_type":51,"sub_page_list":{}}
            """);

        var inspection = await client.InspectDocumentAsync(Document("wiki-node", "doc-token"));

        Assert.False(inspection.Content.HasSubstantiveContent);
        Assert.Equal(0, inspection.Content.BodyCharacterCount);
    }

    [Fact]
    public async Task InspectDocumentAsync_KeepsTwoParagraphsWhenTheirCombinedTextReachesThreshold()
    {
        var first = new string('甲', 60);
        var second = new string('乙', 40);
        var blocks = string.Join(",", new[]
        {
            """{"block_id":"page","block_type":1}""",
            SerializeTextBlock("text-1", first),
            """{"block_id":"heading","block_type":3,"heading1":{"elements":[{"text_run":{"content":"分节"}}]}}""",
            SerializeTextBlock("text-2", second)
        });
        using var client = ClientReturningBlocks(blocks);

        var inspection = await client.InspectDocumentAsync(Document("wiki-node", "doc-token"));

        Assert.Equal(1, inspection.Content.MaxConsecutiveBodyBlocks);
        Assert.Equal(100, inspection.Content.BodyCharacterCount);
        Assert.True(inspection.Content.HasSubstantiveContent);
    }

    [Fact]
    public async Task InspectDocumentAsync_KeepsRichAndUnknownBlocks()
    {
        using var richClient = ClientReturningBlocks("""
            {"block_id":"page","block_type":1},
            {"block_id":"image","block_type":27,"image":{"token":"image-token"}}
            """);
        using var unknownClient = ClientReturningBlocks("""
            {"block_id":"page","block_type":1},
            {"block_id":"future","block_type":777,"future_block":{}}
            """);

        var rich = await richClient.InspectDocumentAsync(Document("wiki-node", "doc-token"));
        var unknown = await unknownClient.InspectDocumentAsync(Document("wiki-node", "doc-token"));

        Assert.True(rich.Content.HasRichContent);
        Assert.True(rich.Content.HasSubstantiveContent);
        Assert.True(unknown.Content.HasUnknownBlock);
        Assert.True(unknown.Content.HasSubstantiveContent);
    }

    private static FeishuApiClient ClientReturningBlocks(string blocks)
    {
        var handler = new StubHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith(
                    "/auth/v3/tenant_access_token/internal",
                    StringComparison.Ordinal))
            {
                return Json("""
                    {"code":0,"msg":"ok","tenant_access_token":"token","expire":7200}
                    """);
            }

            return Json("{\"code\":0,\"msg\":\"success\",\"data\":{\"has_more\":false,\"items\":[" +
                        blocks + "]}}");
        });
        return new FeishuApiClient(new FeishuCredentials("app", "secret"), handler);
    }

    private static string SerializeTextBlock(string blockId, string content) =>
        JsonSerializer.Serialize(new
        {
            block_id = blockId,
            block_type = 2,
            text = new
            {
                elements = new[] { new { text_run = new { content } } }
            }
        });

    private static ExportItem Document(string hierarchyToken, string contentToken) => new()
    {
        HierarchyToken = hierarchyToken,
        ContentToken = contentToken,
        Title = "项目说明",
        Type = "docx",
        ModifiedTime = "123",
        IsFolder = false
    };

    private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(value, Encoding.UTF8, "application/json")
    };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(handler(request));
    }
}
