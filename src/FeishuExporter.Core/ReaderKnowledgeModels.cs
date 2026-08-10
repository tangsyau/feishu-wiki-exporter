namespace FeishuExporter.Core;

internal sealed record ReaderKnowledgeManifest(
    string Format,
    int Version,
    string Name,
    DateTimeOffset GeneratedUtc,
    IReadOnlyList<ReaderKnowledgeDocument> Documents,
    ReaderKnowledgeStatistics Statistics);

internal sealed record ReaderKnowledgeStatistics(
    int Pages,
    int Attachments,
    int UnsupportedBlocks);

internal sealed record ReaderKnowledgeDocument(
    string Id,
    string HierarchyToken,
    string Title,
    string Kind,
    string? PagePath,
    string? OriginalPath,
    string Breadcrumb,
    string? ModifiedTime);

internal sealed record ReaderKnowledgeTreeNode(
    string Title,
    string Type,
    string? DocumentId,
    string? Kind,
    IReadOnlyList<ReaderKnowledgeTreeNode> Children);

internal sealed record ReaderKnowledgePage(
    string Title,
    IReadOnlyList<ReaderBlock> Blocks,
    string Text,
    int UnsupportedBlockCount,
    IReadOnlyList<ReaderSubPageResolutionIssue> SubPageResolutionIssues);

internal sealed record ReaderBlock
{
    public required string Id { get; init; }
    public required string Type { get; init; }
    public string? Text { get; init; }
    public int? Level { get; init; }
    public bool? Checked { get; init; }
    public string? Language { get; init; }
    public string? AssetPath { get; init; }
    public string? FileName { get; init; }
    public string? Url { get; init; }
    public int? SourceType { get; init; }
    public string? ComponentTypeId { get; init; }
    public bool HasSourceRecord { get; init; }
    public IReadOnlyList<ReaderInline> Inlines { get; init; } = [];
    public IReadOnlyList<ReaderLink> Links { get; init; } = [];
    public IReadOnlyList<ReaderBlock> Children { get; init; } = [];
}

internal sealed record ReaderInline(
    string Text,
    bool Bold,
    bool Italic,
    bool Underline,
    bool Strike,
    bool Code,
    string? Url,
    string? TargetPageId);

internal sealed record ReaderLink(string Title, string TargetPageId, string? Anchor = null);

internal sealed record ReaderSearchIndex(
    int Version,
    IReadOnlyDictionary<string, string[]> Postings);

internal sealed record ReaderUnsupportedBlock(
    string PageId,
    string PageTitle,
    string BlockId,
    int? SourceType,
    string? ComponentTypeId,
    bool HasSourceRecord,
    bool HasRecoverableContent);

internal sealed record ReaderSubPageResolutionIssue(
    string BlockId,
    string? PrecedingHeading,
    IReadOnlyList<string> NestedTexts,
    IReadOnlyList<string> CandidateChildPages,
    string Reason);

internal sealed record ReaderPageSubPageResolutionIssue(
    string PageId,
    string PageTitle,
    ReaderSubPageResolutionIssue Issue);

internal sealed record ReaderBuildState
{
    public int Version { get; init; } = 2;
    public Dictionary<string, ReaderBuildStateEntry> Items { get; init; } = new(StringComparer.Ordinal);
}

internal sealed record ReaderBuildStateEntry(
    string ModifiedTime,
    string StructureSignature,
    string PagePath,
    int UnsupportedBlockCount);

public sealed record DirectOfflineKnowledgeBuildResult(
    string OutputDirectory,
    int Pages,
    int Attachments,
    int UnsupportedBlocks,
    int ReusedPages);
