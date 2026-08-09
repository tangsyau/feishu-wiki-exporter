using FeishuExporter.Core;
using System.Text;
using Xunit;

namespace FeishuExporter.Core.Tests;

public sealed class PathPlannerTests
{
    [Fact]
    public void Plan_PreservesWikiHierarchy()
    {
        var items = new[]
        {
            Item("root", null, "总览", "docx"),
            Item("child", "root", "操作说明", "docx")
        };

        var planned = PathPlanner.Plan(items, "docx", true);

        Assert.Equal("总览.docx", planned[0].RelativePath);
        Assert.Equal(Path.Combine("总览", "操作说明.docx"), planned[1].RelativePath);
    }

    [Fact]
    public void MarkWikiNavigationFolders_TurnsOnlyRealWikiParentsIntoDirectories()
    {
        var items = new[]
        {
            Item("root", null, "公务用车", "docx"),
            Item("child", "root", "申请流程", "docx"),
            Item("standalone", null, "值班制度", "docx")
        };

        var marked = PathPlanner.MarkWikiNavigationFolders(
            items,
            new HashSet<string>(StringComparer.Ordinal) { "root" });
        var root = Assert.Single(marked, item => item.HierarchyToken == "root");
        var child = Assert.Single(marked, item => item.HierarchyToken == "child");
        var standalone = Assert.Single(marked, item => item.HierarchyToken == "standalone");

        Assert.True(root.IsFolder);
        Assert.True(root.IsNavigationOnly);
        Assert.False(child.IsFolder);
        Assert.False(standalone.IsFolder);

        var planned = PathPlanner.Plan(marked, "docx", true);
        Assert.Equal("公务用车", planned[0].RelativePath);
        Assert.Equal(Path.Combine("公务用车", "申请流程.docx"), planned[1].RelativePath);
        Assert.Equal("值班制度.docx", planned[2].RelativePath);
    }

    [Fact]
    public void NavigationFolder_KeepsItsAttachmentInsideFolderWhenPlacementIsAlongside()
    {
        var navigation = Item("root", null, "公务用车", "docx") with
        {
            IsFolder = true,
            IsNavigationOnly = true
        };
        var child = Item("child", "root", "申请流程", "docx");
        var attachment = new ExportItem
        {
            HierarchyToken = "embedded:block-1",
            ContentToken = "media-1",
            ParentHierarchyToken = navigation.HierarchyToken,
            Title = "用车须知.pdf",
            Type = "embedded_file",
            ModifiedTime = navigation.ModifiedTime,
            IsFolder = false
        };

        var planned = PathPlanner.Plan(
            [navigation, child, attachment],
            "docx",
            true,
            EmbeddedAttachmentPlacement.AlongsideDocument);

        Assert.Equal("公务用车", planned[0].RelativePath);
        Assert.Equal(Path.Combine("公务用车", "申请流程.docx"), planned[1].RelativePath);
        Assert.Equal(Path.Combine("公务用车", "用车须知.pdf"), planned[2].RelativePath);
    }

    [Fact]
    public void MarkWikiNavigationFolders_DoesNotTreatEmbeddedAttachmentAsWikiChild()
    {
        var document = Item("document", null, "abc", "docx");
        var attachment = new ExportItem
        {
            HierarchyToken = "embedded:block-1",
            ContentToken = "media-1",
            ParentHierarchyToken = document.HierarchyToken,
            Title = "abc.pdf",
            Type = "embedded_file",
            ModifiedTime = document.ModifiedTime,
            IsFolder = false
        };

        var marked = PathPlanner.MarkWikiNavigationFolders(
            [document, attachment],
            new HashSet<string>(StringComparer.Ordinal) { "document" });

        Assert.False(marked[0].IsFolder);
        Assert.False(marked[0].IsNavigationOnly);
    }

    [Fact]
    public void MarkWikiNavigationFolders_KeepsUnselectedParentAsDocument()
    {
        var items = new[]
        {
            Item("root", null, "公务用车", "docx"),
            Item("child", "root", "申请流程", "docx")
        };

        var marked = PathPlanner.MarkWikiNavigationFolders(
            items,
            new HashSet<string>(StringComparer.Ordinal));

        Assert.False(marked[0].IsFolder);
        Assert.False(marked[0].IsNavigationOnly);
        var planned = PathPlanner.Plan(marked, "docx", true);
        Assert.Equal("公务用车.docx", planned[0].RelativePath);
        Assert.Equal(Path.Combine("公务用车", "申请流程.docx"), planned[1].RelativePath);
    }

    [Fact]
    public void Plan_AddsNumericSuffixWhenSiblingNamesCollide()
    {
        var items = new[]
        {
            Item("abcdefgh-one", null, "周报", "docx"),
            Item("ijklmnop-two", null, "周报", "docx")
        };

        var planned = PathPlanner.Plan(items, "pdf", true);

        Assert.NotEqual(planned[0].RelativePath, planned[1].RelativePath);
        Assert.Contains("（2）", planned[1].RelativePath);
        Assert.All(planned, item => Assert.EndsWith(".pdf", item.RelativePath));
    }

    [Theory]
    [InlineData("CON", "_CON")]
    [InlineData("a/b:c", "a-b-c")]
    [InlineData("  hello. ", "hello")]
    [InlineData("中文（测试）", "中文（测试）")]
    public void SanitizeSegment_UsesPortableRules(string input, string expected)
    {
        Assert.Equal(expected, PathPlanner.SanitizeSegment(input));
    }

    [Fact]
    public void Plan_LeavesAttachmentExtensionUntouched()
    {
        var item = Item("attachment", null, "附件.pdf", "file");
        var planned = PathPlanner.Plan([item], "docx", true);
        Assert.Equal("附件.pdf", planned[0].RelativePath);
    }

    [Fact]
    public void Plan_PutsEmbeddedFileUnderItsDocument()
    {
        var document = Item("document", null, "项目说明", "docx");
        var attachment = new ExportItem
        {
            HierarchyToken = "embedded:block-1",
            ContentToken = "media-1",
            ParentHierarchyToken = document.HierarchyToken,
            Title = "参考资料.pdf",
            Type = "embedded_file",
            ModifiedTime = document.ModifiedTime,
            IsFolder = false
        };

        var planned = PathPlanner.Plan([document, attachment], "docx", true);

        Assert.Equal("项目说明.docx", planned[0].RelativePath);
        Assert.Equal(Path.Combine("项目说明", "参考资料.pdf"), planned[1].RelativePath);
    }

    [Fact]
    public void Plan_CanPutEmbeddedFileAlongsideItsDocument()
    {
        var document = Item("document", null, "abc", "docx");
        var attachment = new ExportItem
        {
            HierarchyToken = "embedded:block-1",
            ContentToken = "media-1",
            ParentHierarchyToken = document.HierarchyToken,
            Title = "abc.pdf",
            Type = "embedded_file",
            ModifiedTime = document.ModifiedTime,
            IsFolder = false
        };

        var planned = PathPlanner.Plan(
            [document, attachment],
            "docx",
            true,
            EmbeddedAttachmentPlacement.AlongsideDocument);

        Assert.Equal("abc.docx", planned[0].RelativePath);
        Assert.Equal("abc.pdf", planned[1].RelativePath);
    }

    [Fact]
    public void Plan_UsesNumericSuffixForFinalPathCollision()
    {
        var document = Item("document", null, "abc", "docx");
        var attachment = new ExportItem
        {
            HierarchyToken = "embedded:block-1",
            ContentToken = "media-1",
            ParentHierarchyToken = document.HierarchyToken,
            Title = "abc.pdf",
            Type = "embedded_file",
            ModifiedTime = document.ModifiedTime,
            IsFolder = false
        };

        var planned = PathPlanner.Plan(
            [document, attachment],
            "pdf",
            true,
            EmbeddedAttachmentPlacement.AlongsideDocument);

        Assert.Equal("abc.pdf", planned[0].RelativePath);
        Assert.Equal("abc（2）.pdf", planned[1].RelativePath);
    }

    [Fact]
    public void SanitizeSegment_TruncatesByUtf8BytesWithoutHashAndPreservesExtension()
    {
        var original = new string('长', 100) + ".pdf";

        var result = PathPlanner.SanitizeSegment(original);

        Assert.True(Encoding.UTF8.GetByteCount(result) <= 180);
        Assert.EndsWith("….pdf", result);
        Assert.DoesNotContain("[", result);
    }

    private static ExportItem Item(string token, string? parent, string title, string type) => new()
    {
        HierarchyToken = token,
        ContentToken = token,
        ParentHierarchyToken = parent,
        Title = title,
        Type = type,
        ModifiedTime = "1",
        IsFolder = type == "folder"
    };
}
