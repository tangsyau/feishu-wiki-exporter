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

    private static readonly HashSet<int> HeadingTypes = [3, 4, 5, 6, 7, 8, 9, 10, 11];
    private static readonly HashSet<int> NavigationOrDecorationTypes = [22, 42, 51];
    private static readonly HashSet<int> PassiveContainerTypes = [24, 25, 32, 34];
    private static readonly HashSet<int> RichContentTypes =
    [
        18, 19, 20, 21, 23, 26, 27, 28, 29, 30, 31, 33,
        35, 36, 37, 38, 39, 40, 41, 43
    ];

    public static DocumentContentAnalysis Analyze(IEnumerable<DocumentBlockDto> blocks)
    {
        var currentConsecutive = 0;
        var maximumConsecutive = 0;
        var characterCount = 0;
        var hasRichContent = false;
        var hasUnknownBlock = false;

        foreach (var block in blocks)
        {
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
                    hasUnknownBlock = true;
                    currentConsecutive = 0;
                    continue;
                }

                if (!textEvidence.HasContent)
                {
                    // Empty paragraphs are common editor placeholders and do not
                    // interrupt an otherwise continuous run of body paragraphs.
                    continue;
                }

                currentConsecutive++;
                maximumConsecutive = Math.Max(maximumConsecutive, currentConsecutive);
                characterCount += textEvidence.CharacterCount;
                continue;
            }

            currentConsecutive = 0;
            if (RichContentTypes.Contains(block.BlockType))
            {
                hasRichContent = true;
            }
            else
            {
                // Feishu may add new block types. Unknown content is kept rather
                // than risking the loss of a real document.
                hasUnknownBlock = true;
            }
        }

        return new DocumentContentAnalysis(
            maximumConsecutive,
            characterCount,
            hasRichContent,
            hasUnknownBlock);
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

        var characterCount = 0;
        var hasNonTextElement = false;
        foreach (var element in elements.EnumerateArray())
        {
            var before = characterCount;
            CountVisibleText(element, ref characterCount);
            if (characterCount == before &&
                element.ValueKind == JsonValueKind.Object &&
                element.EnumerateObject().Any())
            {
                // Mentions and other inline elements can be meaningful even when
                // they do not expose a text_run.content field.
                hasNonTextElement = true;
            }
        }

        return new TextEvidence(characterCount > 0 || hasNonTextElement, characterCount, false);
    }

    private static void CountVisibleText(JsonElement element, ref int count)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, "content", StringComparison.Ordinal) &&
                    property.Value.ValueKind == JsonValueKind.String)
                {
                    var value = property.Value.GetString();
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        count += value.Count(character => !char.IsWhiteSpace(character));
                    }
                    continue;
                }

                CountVisibleText(property.Value, ref count);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in element.EnumerateArray())
            {
                CountVisibleText(child, ref count);
            }
        }
    }

    private readonly record struct TextEvidence(bool HasContent, int CharacterCount, bool IsUnknown)
    {
        public static TextEvidence Unknown => new(false, 0, true);
    }
}
