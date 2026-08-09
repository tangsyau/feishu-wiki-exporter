using System.Net;
using System.Text;
using FeishuExporter.Core;
using Xunit;

namespace FeishuExporter.Core.Tests;

public sealed class ExportEnginePreparationTests
{
    [Fact]
    public async Task PrepareAsync_OnlySuggestsContentLightWikiParents()
    {
        var handler = new StubHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/auth/v3/tenant_access_token/internal", StringComparison.Ordinal))
            {
                return Json("""{"code":0,"msg":"ok","tenant_access_token":"token","expire":7200}""");
            }

            if (path.EndsWith("/wiki/v2/spaces/space-id", StringComparison.Ordinal))
            {
                return Json("""{"code":0,"msg":"ok","data":{"space":{"space_id":"space-id","name":"测试知识库","description":""}}}""");
            }

            if (path.EndsWith("/wiki/v2/spaces/space-id/nodes", StringComparison.Ordinal))
            {
                if (request.RequestUri.Query.Contains("parent_node_token=parent", StringComparison.Ordinal))
                {
                    return Json("""
                        {"code":0,"msg":"ok","data":{"has_more":false,"items":[
                          {"node_token":"child","obj_token":"child-doc","obj_type":"docx","parent_node_token":"parent","title":"申请流程","has_child":false}
                        ]}}
                        """);
                }

                return Json("""
                    {"code":0,"msg":"ok","data":{"has_more":false,"items":[
                      {"node_token":"parent","obj_token":"parent-doc","obj_type":"docx","title":"公务用车","has_child":true},
                      {"node_token":"standalone","obj_token":"standalone-doc","obj_type":"docx","title":"值班制度","has_child":false}
                    ]}}
                    """);
            }

            if (path.EndsWith("/docx/v1/documents/parent-doc/blocks", StringComparison.Ordinal))
            {
                return Json("""
                    {"code":0,"msg":"ok","data":{"has_more":false,"items":[
                      {"block_id":"page","block_type":1},
                      {"block_id":"heading","block_type":3,"heading1":{"elements":[{"text_run":{"content":"目录"}}]}},
                      {"block_id":"subpages","block_type":51,"sub_page_list":{}}
                    ]}}
                    """);
            }

            throw new InvalidOperationException($"Unexpected request: {request.RequestUri}");
        });
        using var client = new FeishuApiClient(new FeishuCredentials("app", "secret"), handler);
        var engine = new ExportEngine(client);
        var options = new ExportOptions
        {
            Credentials = new FeishuCredentials("app", "secret"),
            SourceType = ExportSourceType.Wiki,
            SourceId = "space-id",
            ExportRoot = Path.GetTempPath(),
            DownloadAttachments = false,
            TreatWikiParentsAsNavigationFolders = true
        };

        var preparation = await engine.PrepareAsync(options, null, CancellationToken.None);

        var candidate = Assert.Single(preparation.NavigationCandidates);
        Assert.Equal("parent", candidate.HierarchyToken);
        Assert.Equal("公务用车", candidate.HierarchyPath);
        Assert.Empty(preparation.Warnings);
    }

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
