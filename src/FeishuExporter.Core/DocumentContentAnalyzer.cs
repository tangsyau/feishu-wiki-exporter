using System.Text;
using System.Text.Json;

namespace FeishuExporter.Core;

internal static class DocumentContentAnalyzer
{
    private static readonly IReadOnlyDictionary<int, string> BodyTextFields =
        new Dictionary<int, string>
        {
            [2] = "text",
            [12] = "bullet",
            [13] = "ordered",
            [14] = "code",
            [15] = "quote",
            [17] = "todo"
        };

    private static readonly IReadOnlyDictionary<int, string> HeadingFields =
        Enumerable.Range(3, 9).ToDictionary(type => type, type => $"heading{type - 2}");

    private static readonly HashSet<int> HeadingTypes = [3, 4, 5, 6, 7, 8, 9, 10, 11];
    private static readonly HashSet<int> NavigationOrDecorationTypes = [22, 42, 51];
    private static readonly HashSet<int> PassiveContainerTypes = [24, 25, 32, 34];
    private static readonly HashSet<int> RichContentTypes =
    [
        18, 19, 20, 21, 23, 26, 27, 28, 29, 30, 31, 33,
        35, 36, 37, 38, 39, 40, 41, 43
    ];

    public static DocumentContentAnalysis Analyze(
        IEnumerable<DocumentBlockDto> blocks,
        IReadOnlySet<string>? childPageTitles = null)
    {
        var blockList = blocks.ToList();
        var blockTypeCounts = blockList
            .GroupBy(block => block.BlockType)
            .OrderBy(group => group.Key)
            .ToDictionary(group => group.Key, group => group.Count());
        var blocksById = blockList
            .Where(block => !string.IsNullOrWhiteSpace(block.BlockId))
            .GroupBy(block => block.BlockId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var parentIds = BuildParentIds(blockList, blocksById);
        var childIds = BuildChildIds(blockList, parentIds);
        var navigationLabels = BuildNavigationLabels(blockList, childPageTitles);
        var navigationLikeTables = FindNavigationLikeTables(
            blockList,
            blocksById,
            childIds,
            navigationLabels);

        var currentConsecutive = 0;
        var maximumConsecutive = 0;
        var characterCount = 0;
        var ignoredNestedBlockCount = 0;
        var richBlockTypes = new HashSet<int>();
        var unknownBlockTypes = new HashSet<int>();

        foreach (var block in blockList)
        {
            if (IsNestedUnderExcludedContent(
                    block,
                    blocksById,
                    parentIds,
                    navigationLikeTables))
            {
                ignoredNestedBlockCount++;
                continue;
            }

            if (block.BlockType == 1 || PassiveContainerTypes.Contains(block.BlockType))
            {
                continue;
            }

            if (HeadingTypes.Contains(block.BlockType) ||
                NavigationOrDecorationTypes.Contains(block.BlockType))
            {
                currentConsecutive = 0;
                continue;
            }

            if (BodyTextFields.TryGetValue(block.BlockType, out var fieldName))
            {
                var textEvidence = ReadTextEvidence(block, fieldName);
                if (textEvidence.IsUnknown)
                {
                    unknownBlockTypes.Add(block.BlockType);
                    currentConsecutive = 0;
                    continue;
                }

                if (!textEvidence.HasContent)
                {
                    // Empty paragraphs are editor placeholders and do not
                    // interrupt an otherwise continuous run of body paragraphs.
                    continue;
                }

                currentConsecutive++;
                maximumConsecutive = Math.Max(maximumConsecutive, currentConsecutive);
                characterCount += textEvidence.CharacterCount;
                continue;
            }

            currentConsecutive = 0;
            if (block.BlockType == 31 && navigationLikeTables.Contains(block.BlockId))
            {
                continue;
            }

            if (RichContentTypes.Contains(block.BlockType))
            {
                richBlockTypes.Add(block.BlockType);
            }
            else
            {
                // New Feishu block types are reviewable uncertainties rather
                // than automatic proof that a page contains substantive text.
                unknownBlockTypes.Add(block.BlockType);
            }
        }

        return new DocumentContentAnalysis(
            maximumConsecutive,
            characterCount,
            blockTypeCounts,
            richBlockTypes.Order().ToArray(),
            unknownBlockTypes.Order().ToArray(),
            ignoredNestedBlockCount,
            navigationLikeTables.Count > 0);
    }

    private static Dictionary<string, string> BuildParentIds(
        IReadOnlyList<DocumentBlockDto> blocks,
        IReadOnlyDictionary<string, DocumentBlockDto> blocksById)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var block in blocks)
        {
            if (!string.IsNullOrWhiteSpace(block.BlockId) &&
                !string.IsNullOrWhiteSpace(block.ParentId))
            {
                result[block.BlockId] = block.ParentId;
            }
        }

        foreach (var parent in blocks)
        {
            foreach (var childId in parent.Children ?? [])
            {
                if (blocksById.ContainsKey(childId))
                {
                    result.TryAdd(childId, parent.BlockId);
                }
            }
        }
        return result;
    }

    private static Dictionary<string, List<string>> BuildChildIds(
        IReadOnlyList<DocumentBlockDto> blocks,
        IReadOnlyDictionary<string, string> parentIds)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var block in blocks)
        {
            if (block.Children is { Count: > 0 })
            {
                result[block.BlockId] = [.. block.Children];
            }
        }

        foreach (var pair in parentIds)
        {
            if (!result.TryGetValue(pair.Value, out var children))
            {
                children = [];
                result[pair.Value] = children;
            }
            if (!children.Contains(pair.Key, StringComparer.Ordinal))
            {
                children.Add(pair.Key);
            }
        }
        return result;
    }

    private static HashSet<string> BuildNavigationLabels(
        IReadOnlyList<DocumentBlockDto> blocks,
        IReadOnlySet<string>? childPageTitles)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var title in childPageTitles ?? new HashSet<string>(StringComparer.Ordinal))
        {
            AddNormalizedLabel(result, title);
        }

        foreach (var block in blocks.Where(block => HeadingFields.ContainsKey(block.BlockType)))
        {
            var evidence = ReadTextEvidence(block, HeadingFields[block.BlockType]);
            if (evidence.HasContent)
            {
                AddNormalizedLabel(result, evidence.VisibleText);
            }
        }
        return result;
    }

    private static void AddNormalizedLabel(ISet<string> labels, string value)
    {
        var normalized = NormalizeLabel(value);
        if (normalized.Length > 0)
        {
            labels.Add(normalized);
        }
    }

    private static HashSet<string> FindNavigationLikeTables(
        IReadOnlyList<DocumentBlockDto> blocks,
        IReadOnlyDictionary<string, DocumentBlockDto> blocksById,
        IReadOnlyDictionary<string, List<string>> childIds,
        IReadOnlySet<string> navigationLabels)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var table in blocks.Where(block => block.BlockType == 31))
        {
            var descendants = EnumerateDescendants(table.BlockId, blocksById, childIds).ToList();
            if (descendants.Any(block =>
                    !PassiveContainerTypes.Contains(block.BlockType) &&
                    !BodyTextFields.ContainsKey(block.BlockType) &&
                    !HeadingTypes.Contains(block.BlockType)))
            {
                continue;
            }

            var textEvidence = new List<TextEvidence>();
            foreach (var descendant in descendants)
            {
                if (TryReadTextEvidence(descendant, out var evidence) && evidence.HasContent)
                {
                    textEvidence.Add(evidence);
                }
            }
            if (textEvidence.Count < 2 || textEvidence.Any(evidence => !evidence.HasLinkLikeElement))
            {
                continue;
            }

            if (navigationLabels.Count > 0 &&
                textEvidence.Any(evidence => !navigationLabels.Contains(NormalizeLabel(evidence.VisibleText))))
            {
                continue;
            }

            result.Add(table.BlockId);
        }
        return result;
    }

    private static IEnumerable<DocumentBlockDto> EnumerateDescendants(
        string rootId,
        IReadOnlyDictionary<string, DocumentBlockDto> blocksById,
        IReadOnlyDictionary<string, List<string>> childIds)
    {
        var pending = new Stack<string>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        if (childIds.TryGetValue(rootId, out var rootChildren))
        {
            foreach (var child in rootChildren.AsEnumerable().Reverse())
            {
                pending.Push(child);
            }
        }

        while (pending.TryPop(out var blockId))
        {
            if (!visited.Add(blockId) || !blocksById.TryGetValue(blockId, out var block))
            {
                continue;
            }

            yield return block;
            if (childIds.TryGetValue(blockId, out var children))
            {
                foreach (var child in children.AsEnumerable().Reverse())
                {
                    pending.Push(child);
                }
            }
        }
    }

    private static bool IsNestedUnderExcludedContent(
        DocumentBlockDto block,
        IReadOnlyDictionary<string, DocumentBlockDto> blocksById,
        IReadOnlyDictionary<string, string> parentIds,
        IReadOnlySet<string> navigationLikeTables)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var currentId = block.BlockId;
        while (parentIds.TryGetValue(currentId, out var parentId) && visited.Add(parentId))
        {
            if (!blocksById.TryGetValue(parentId, out var parent))
            {
                break;
            }

            if (NavigationOrDecorationTypes.Contains(parent.BlockType) ||
                RichContentTypes.Contains(parent.BlockType) ||
                navigationLikeTables.Contains(parent.BlockId) ||
                !IsKnownBlockType(parent.BlockType))
            {
                return true;
            }
            currentId = parentId;
        }
        return false;
    }

    private static bool IsKnownBlockType(int blockType) =>
        blockType == 1 ||
        BodyTextFields.ContainsKey(blockType) ||
        HeadingTypes.Contains(blockType) ||
        NavigationOrDecorationTypes.Contains(blockType) ||
        PassiveContainerTypes.Contains(blockType) ||
        RichContentTypes.Contains(blockType);

    private static bool TryReadTextEvidence(DocumentBlockDto block, out TextEvidence evidence)
    {
        if (BodyTextFields.TryGetValue(block.BlockType, out var bodyField))
        {
            evidence = ReadTextEvidence(block, bodyField);
            return true;
        }
        if (HeadingFields.TryGetValue(block.BlockType, out var headingField))
        {
            evidence = ReadTextEvidence(block, headingField);
            return true;
        }

        evidence = default;
        return false;
    }

    private static TextEvidence ReadTextEvidence(DocumentBlockDto block, string fieldName)
    {
        if (block.Properties is null ||
            !block.Properties.TryGetValue(fieldName, out var textBlock) ||
            textBlock.ValueKind != JsonValueKind.Object)
        {
            return TextEvidence.Unknown;
        }

        if (!textBlock.TryGetProperty("elements", out var elements) ||
            elements.ValueKind != JsonValueKind.Array)
        {
            return TextEvidence.Unknown;
        }

        var visibleText = new StringBuilder();
        var hasNonTextElement = false;
        var hasLinkLikeElement = false;
        foreach (var element in elements.EnumerateArray())
        {
            var before = visibleText.Length;
            CollectVisibleText(element, visibleText);
            hasLinkLikeElement |= ContainsLinkLikeElement(element);
            if (visibleText.Length == before &&
                element.ValueKind == JsonValueKind.Object &&
                element.EnumerateObject().Any())
            {
                hasNonTextElement = true;
            }
        }

        var text = visibleText.ToString();
        var characterCount = text.Count(character => !char.IsWhiteSpace(character));
        return new TextEvidence(
            characterCount > 0 || hasNonTextElement,
            characterCount,
            false,
            text,
            hasLinkLikeElement);
    }

    private static void CollectVisibleText(JsonElement element, StringBuilder text)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if ((string.Equals(property.Name, "content", StringComparison.Ordinal) ||
                     string.Equals(property.Name, "title", StringComparison.Ordinal)) &&
                    property.Value.ValueKind == JsonValueKind.String)
                {
                    var value = property.Value.GetString();
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        text.Append(value);
                    }
                    continue;
                }

                CollectVisibleText(property.Value, text);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in element.EnumerateArray())
            {
                CollectVisibleText(child, text);
            }
        }
    }

    private static bool ContainsLinkLikeElement(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.Name is "mention_doc" or "link")
                {
                    return true;
                }
                if (string.Equals(property.Name, "url", StringComparison.Ordinal) &&
                    property.Value.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(property.Value.GetString()))
                {
                    return true;
                }
                if (ContainsLinkLikeElement(property.Value))
                {
                    return true;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in element.EnumerateArray())
            {
                if (ContainsLinkLikeElement(child))
                {
                    return true;
                }
            }
        }
        return false;
    }

    private static string NormalizeLabel(string value) =>
        string.Concat(value.Normalize(NormalizationForm.FormKC).Where(character => !char.IsWhiteSpace(character)));

    private readonly record struct TextEvidence(
        bool HasContent,
        int CharacterCount,
        bool IsUnknown,
        string VisibleText,
        bool HasLinkLikeElement)
    {
        public static TextEvidence Unknown => new(false, 0, true, string.Empty, false);
    }
}
