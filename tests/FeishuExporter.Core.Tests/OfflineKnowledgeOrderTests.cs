using System.Text.Json;
using FeishuExporter.Core;
using Xunit;

namespace FeishuExporter.Core.Tests;

public sealed class OfflineKnowledgeOrderTests
{
    [Fact]
    public async Task BuildAsync_MergesAContentPageAndItsChildrenIntoOneSemanticNode()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), "feishu-semantic-tree-test-" + Guid.NewGuid().ToString("N"));
        var sourceRoot = Path.Combine(testRoot, "知识库");
        var outputRoot = Path.Combine(testRoot, "知识库-offline");

        try
        {
            Directory.CreateDirectory(Path.Combine(sourceRoot, "公务用车"));
            Directory.CreateDirectory(Path.Combine(sourceRoot, ".feishu-export"));
            await File.WriteAllTextAsync(Path.Combine(sourceRoot, "公务用车.txt"), "parent");
            await File.WriteAllTextAsync(Path.Combine(sourceRoot, "公务用车", "申请流程.txt"), "child");
            await File.WriteAllTextAsync(Path.Combine(sourceRoot, "值班制度.txt"), "sibling");
            await File.WriteAllTextAsync(
                Path.Combine(sourceRoot, ".feishu-export", "order.json"),
                """
                {
                  "version": 2,
                  "items": [
                    {"hierarchyToken":"parent","title":"公务用车","type":"docx","relativePath":"公务用车.txt","siblingOrder":0,"isFolder":false,"isNavigationOnly":false},
                    {"hierarchyToken":"child","parentHierarchyToken":"parent","title":"申请流程","type":"docx","relativePath":"公务用车/申请流程.txt","siblingOrder":0,"isFolder":false,"isNavigationOnly":false},
                    {"hierarchyToken":"sibling","title":"值班制度","type":"docx","relativePath":"值班制度.txt","siblingOrder":1,"isFolder":false,"isNavigationOnly":false}
                  ]
                }
                """);

            var builder = new OfflineKnowledgeBuilder();
            await builder.BuildAsync(sourceRoot, outputRoot);

            using var tree = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(outputRoot, "tree.json")));
            var rootChildren = tree.RootElement.GetProperty("children").EnumerateArray().ToArray();
            Assert.Equal(new string?[] { "公务用车", "值班制度" },
                rootChildren.Select(node => node.GetProperty("title").GetString()).ToArray());

            var parent = rootChildren[0];
            Assert.Equal("page", parent.GetProperty("type").GetString());
            Assert.NotNull(parent.GetProperty("documentId").GetString());
            var child = Assert.Single(parent.GetProperty("children").EnumerateArray().ToArray());
            Assert.Equal("申请流程", child.GetProperty("title").GetString());
            Assert.NotNull(child.GetProperty("documentId").GetString());
        }
        finally
        {
            if (Directory.Exists(testRoot))
            {
                Directory.Delete(testRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task BuildAsync_KeepsNavigationPageWithoutDocumentIdAndWithChildren()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), "feishu-navigation-tree-test-" + Guid.NewGuid().ToString("N"));
        var sourceRoot = Path.Combine(testRoot, "知识库");
        var outputRoot = Path.Combine(testRoot, "知识库-offline");

        try
        {
            Directory.CreateDirectory(Path.Combine(sourceRoot, "公务用车"));
            Directory.CreateDirectory(Path.Combine(sourceRoot, ".feishu-export"));
            await File.WriteAllTextAsync(Path.Combine(sourceRoot, "公务用车", "申请流程.txt"), "child");
            await File.WriteAllTextAsync(
                Path.Combine(sourceRoot, ".feishu-export", "order.json"),
                """
                {
                  "version": 2,
                  "items": [
                    {"hierarchyToken":"parent","title":"公务用车","type":"docx","relativePath":"公务用车","siblingOrder":0,"isFolder":true,"isNavigationOnly":true},
                    {"hierarchyToken":"child","parentHierarchyToken":"parent","title":"申请流程","type":"docx","relativePath":"公务用车/申请流程.txt","siblingOrder":0,"isFolder":false,"isNavigationOnly":false}
                  ]
                }
                """);

            var builder = new OfflineKnowledgeBuilder();
            await builder.BuildAsync(sourceRoot, outputRoot);

            using var tree = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(outputRoot, "tree.json")));
            var parent = Assert.Single(tree.RootElement.GetProperty("children").EnumerateArray().ToArray());
            Assert.Equal("公务用车", parent.GetProperty("title").GetString());
            Assert.Equal(JsonValueKind.Null, parent.GetProperty("documentId").ValueKind);
            Assert.Single(parent.GetProperty("children").EnumerateArray().ToArray());
        }
        finally
        {
            if (Directory.Exists(testRoot))
            {
                Directory.Delete(testRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task BuildAsync_PreservesMixedFolderAndDocumentOrderFromExportMetadata()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), "feishu-order-test-" + Guid.NewGuid().ToString("N"));
        var sourceRoot = Path.Combine(testRoot, "知识库");
        var outputRoot = Path.Combine(testRoot, "知识库-offline");

        try
        {
            Directory.CreateDirectory(Path.Combine(sourceRoot, "中间目录"));
            Directory.CreateDirectory(Path.Combine(sourceRoot, ".feishu-export"));
            await File.WriteAllTextAsync(Path.Combine(sourceRoot, "前一个.txt"), "first");
            await File.WriteAllTextAsync(Path.Combine(sourceRoot, "后一个.txt"), "last");
            await File.WriteAllTextAsync(Path.Combine(sourceRoot, "中间目录", "子文档一.txt"), "child-1");
            await File.WriteAllTextAsync(Path.Combine(sourceRoot, "中间目录", "子文档二.txt"), "child-2");
            await File.WriteAllTextAsync(
                Path.Combine(sourceRoot, ".feishu-export", "order.json"),
                """
                {
                  "version": 1,
                  "items": [
                    {"relativePath":"前一个.txt","siblingOrder":0},
                    {"relativePath":"中间目录","siblingOrder":1},
                    {"relativePath":"后一个.txt","siblingOrder":2},
                    {"relativePath":"中间目录/子文档二.txt","siblingOrder":0},
                    {"relativePath":"中间目录/子文档一.txt","siblingOrder":1}
                  ]
                }
                """);

            var builder = new OfflineKnowledgeBuilder();
            await builder.BuildAsync(sourceRoot, outputRoot);

            using var tree = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(outputRoot, "tree.json")));
            var rootChildren = tree.RootElement.GetProperty("children").EnumerateArray().ToArray();
            Assert.Equal(new string?[] { "前一个", "中间目录", "后一个" },
                rootChildren.Select(node => node.GetProperty("title").GetString()).ToArray());

            var folderChildren = rootChildren[1].GetProperty("children").EnumerateArray().ToArray();
            Assert.Equal(new string?[] { "子文档二", "子文档一" },
                folderChildren.Select(node => node.GetProperty("title").GetString()).ToArray());
        }
        finally
        {
            if (Directory.Exists(testRoot))
            {
                Directory.Delete(testRoot, recursive: true);
            }
        }
    }
}
