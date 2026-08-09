using System.Text.Json;
using System.Text.Json.Serialization;

namespace FeishuExporter.Core;

public enum ExportSourceType
{
    Wiki,
    CloudFolder
}

public enum ExistingFilePolicy
{
    Skip,
    Overwrite,
    KeepBoth
}

public enum EmbeddedAttachmentPlacement
{
    AlongsideDocument,
    DocumentSubfolder
}

public enum ExportItemStatus
{
    Pending,
    Exporting,
    Completed,
    Skipped,
    Unsupported,
    Failed
}

public sealed record FeishuCredentials(string AppId, string AppSecret);

public sealed record ExportOptions
{
    public required FeishuCredentials Credentials { get; init; }
    public required ExportSourceType SourceType { get; init; }
    public required string SourceId { get; init; }
    public required string ExportRoot { get; init; }
    public string DocumentFormat { get; init; } = "docx";
    public ExistingFilePolicy ExistingFilePolicy { get; init; } = ExistingFilePolicy.Overwrite;
    public EmbeddedAttachmentPlacement EmbeddedAttachmentPlacement { get; init; } = EmbeddedAttachmentPlacement.AlongsideDocument;
    public bool TreatWikiParentsAsNavigationFolders { get; init; } = true;
    public bool SkipUnchanged { get; init; } = true;
    public bool DownloadAttachments { get; init; } = true;
    public int MaxParallelism { get; init; } = 2;
}

public sealed record WikiSpace(
    [property: JsonPropertyName("space_id")] string SpaceId,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string? Description);

public sealed record FolderMeta(
    [property: JsonPropertyName("token")] string Token,
    [property: JsonPropertyName("name")] string Name);

public sealed record ExportItem
{
    public required string HierarchyToken { get; init; }
    public required string ContentToken { get; init; }
    public string? ParentHierarchyToken { get; init; }
    public required string Title { get; init; }
    public required string Type { get; init; }
    public string? ModifiedTime { get; init; }
    public int? SiblingOrder { get; init; }
    public bool IsFolder { get; init; }
    public bool IsNavigationOnly { get; init; }
}

public sealed record PlannedExportItem(ExportItem Item, string RelativePath, string? ExportExtension);

public sealed record ExportProgress
{
    public required int Completed { get; init; }
    public required int Total { get; init; }
    public required int Succeeded { get; init; }
    public required int Skipped { get; init; }
    public required int Failed { get; init; }
    public required string CurrentItem { get; init; }
    public required ExportItemStatus Status { get; init; }
    public string? Message { get; init; }
}

public sealed record ExportFailure(string Title, string Token, string Error);

public sealed record DirectoryScanProgress(int DiscoveredItems, int PendingContainers);

public sealed class ExportSummary
{
    public string SourceName { get; internal set; } = string.Empty;
    public string OutputDirectory { get; internal set; } = string.Empty;
    public int Total { get; internal set; }
    public int Succeeded { get; internal set; }
    public int Skipped { get; internal set; }
    public int Unsupported { get; internal set; }
    public int Failed { get; internal set; }
    public List<ExportFailure> Failures { get; } = [];
}

public sealed record NavigationPageCandidate(
    string HierarchyToken,
    string Title,
    string HierarchyPath,
    int MaxConsecutiveBodyBlocks,
    int BodyCharacterCount,
    string Reason);

public sealed class ExportPreparation
{
    internal ExportPreparation(
        string sourceName,
        List<ExportItem> items,
        List<ExportFailure> failures,
        List<string> warnings,
        List<NavigationPageCandidate> navigationCandidates)
    {
        SourceName = sourceName;
        Items = items;
        Failures = failures;
        Warnings = warnings;
        NavigationCandidates = navigationCandidates;
    }

    public string SourceName { get; }
    public IReadOnlyList<string> Warnings { get; }
    public IReadOnlyList<NavigationPageCandidate> NavigationCandidates { get; }
    internal List<ExportItem> Items { get; }
    internal List<ExportFailure> Failures { get; }
}

public sealed record DocumentContentAnalysis(
    int MaxConsecutiveBodyBlocks,
    int BodyCharacterCount,
    bool HasRichContent,
    bool HasUnknownBlock)
{
    public bool HasSubstantiveContent =>
        MaxConsecutiveBodyBlocks >= 3 ||
        BodyCharacterCount >= 100 ||
        HasRichContent ||
        HasUnknownBlock;
}

public sealed record DocumentInspection(
    IReadOnlyList<ExportItem> EmbeddedFiles,
    DocumentContentAnalysis Content);

public sealed record OfflineKnowledgeProgress(int Completed, int Total, string CurrentItem);

public sealed record OfflineKnowledgeBuildResult(
    string OutputDirectory,
    int TotalFiles,
    int IndexedDocuments,
    int ReusedPages);

internal sealed record ApiEnvelope<T>(
    [property: JsonPropertyName("code")] int Code,
    [property: JsonPropertyName("msg")] string? Message,
    [property: JsonPropertyName("data")] T? Data);

internal sealed record PageData<T>(
    [property: JsonPropertyName("items")] List<T>? Items,
    [property: JsonPropertyName("files")] List<T>? Files,
    [property: JsonPropertyName("page_token")] string? PageToken,
    [property: JsonPropertyName("next_page_token")] string? NextPageToken,
    [property: JsonPropertyName("has_more")] bool HasMore);

internal sealed record TokenResponse(
    [property: JsonPropertyName("code")] int Code,
    [property: JsonPropertyName("msg")] string? Message,
    [property: JsonPropertyName("tenant_access_token")] string? AccessToken,
    [property: JsonPropertyName("expire")] int ExpiresIn);

internal sealed record WikiNodeDto(
    [property: JsonPropertyName("node_token")] string NodeToken,
    [property: JsonPropertyName("obj_token")] string ObjectToken,
    [property: JsonPropertyName("obj_type")] string ObjectType,
    [property: JsonPropertyName("parent_node_token")] string? ParentNodeToken,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("obj_edit_time")] string? ModifiedTime,
    [property: JsonPropertyName("has_child")] bool HasChild);

internal sealed record CloudFileDto(
    [property: JsonPropertyName("token")] string Token,
    [property: JsonPropertyName("parent_token")] string? ParentToken,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("modified_time")] string? ModifiedTime);

internal sealed class DocumentBlockDto
{
    [JsonPropertyName("block_id")]
    public string BlockId { get; init; } = string.Empty;

    [JsonPropertyName("block_type")]
    public int BlockType { get; init; }

    [JsonPropertyName("file")]
    public DocumentFileBlockDto? File { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Properties { get; init; }
}

internal sealed record DocumentFileBlockDto(
    [property: JsonPropertyName("token")] string? Token,
    [property: JsonPropertyName("name")] string? Name);

internal sealed record WikiSpaceInfoData([property: JsonPropertyName("space")] WikiSpace Space);

internal sealed record ExportTicket([property: JsonPropertyName("ticket")] string Ticket);

internal sealed record ExportTaskData([property: JsonPropertyName("result")] ExportTaskResult Result);

internal sealed record ExportTaskResult(
    [property: JsonPropertyName("file_token")] string? FileToken,
    [property: JsonPropertyName("file_size")] long FileSize,
    [property: JsonPropertyName("job_error_msg")] string? ErrorMessage,
    [property: JsonPropertyName("job_status")] int JobStatus);

public sealed class FeishuApiException(string message, int? code = null, Exception? innerException = null)
    : Exception(message, innerException)
{
    public int? Code { get; } = code;
}
