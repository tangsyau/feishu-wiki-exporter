using System.Text;
using System.Text.Json;

namespace FeishuExporter.Core;

internal sealed class FeishuBlockNormalizer(
    IReadOnlyDictionary<string, string> pageIdByToken,
    IReadOnlyList<ExportItem> childPages,
    IReadOnlyDictionary<string, string> assetPathByBlockId,
    string currentPageId)
{
    private readonly List<ReaderSubPageResolutionIssue> _subPageResolutionIssues = [];
    private readonly HashSet<string> _heuristicallyAssignedPageIds = new(StringComparer.Ordinal);
    private int _subPageListCount;

    public ReaderKnowledgePage Normalize(string title, IReadOnlyList<DocumentBlockDto> blocks)
    {
        var byId = blocks
            .Where(block => !string.IsNullOrWhiteSpace(block.BlockId))
            .GroupBy(block => block.BlockId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var page = blocks.FirstOrDefault(block => block.BlockType == 1);
        var roots = ResolveChildren(page, blocks, byId);
        _subPageListCount = blocks.Count(block => block.BlockType is 42 or 51);
        var normalized = NormalizeBlocks(roots, byId);
        var headings = new List<ReaderLink>();
        CollectHeadings(normalized, headings);
        normalized = FillTableOfContents(normalized, headings);
        var unsupported = CountUnsupported(normalized);
        var text = BuildSearchText(normalized);
        return new ReaderKnowledgePage(title, normalized, text, unsupported, _subPageResolutionIssues.ToArray());
    }

    private List<ReaderBlock> NormalizeBlocks(
        IEnumerable<DocumentBlockDto> blocks,
        IReadOnlyDictionary<string, DocumentBlockDto> byId)
    {
        var result = new List<ReaderBlock>();
        string? precedingHeading = null;
        foreach (var block in blocks)
        {
            var normalized = NormalizeBlock(block, byId, precedingHeading);
            result.Add(normalized);
            if (normalized.Type == "heading" && !string.IsNullOrWhiteSpace(normalized.Text))
            {
                precedingHeading = normalized.Text;
            }
        }
        return result;
    }

    private ReaderBlock NormalizeBlock(
        DocumentBlockDto block,
        IReadOnlyDictionary<string, DocumentBlockDto> byId,
        string? precedingHeading)
    {
        var payload = FindPayload(block);
        var inlines = ExtractInlines(payload);
        var text = string.Concat(inlines.Select(inline => inline.Text));
        var children = NormalizeBlocks(ResolveChildren(block, [], byId), byId);

        return block.BlockType switch
        {
            2 => Basic(block, "paragraph", text, inlines, children),
            >= 3 and <= 11 => Basic(block, "heading", text, inlines, children) with
            {
                Level = block.BlockType - 2
            },
            12 => Basic(block, "bullet", text, inlines, children),
            13 => Basic(block, "ordered", text, inlines, children),
            14 => Basic(block, "code", text, inlines, children) with
            {
                Language = GetString(payload, "style", "language") ?? GetString(payload, "language")
            },
            15 => Basic(block, "quote", text, inlines, children),
            17 => Basic(block, "todo", text, inlines, children) with
            {
                Checked = GetBoolean(payload, "style", "done") ?? GetBoolean(payload, "done") ?? false
            },
            19 => Basic(block, "callout", text, inlines, children),
            22 => Basic(block, "divider", null, [], children),
            23 => Basic(block, "file", text, inlines, children) with
            {
                AssetPath = assetPathByBlockId.GetValueOrDefault(block.BlockId),
                FileName = block.File?.Name ?? (string.IsNullOrWhiteSpace(text) ? "附件" : text)
            },
            27 => Basic(block, "image", text, inlines, children) with
            {
                AssetPath = assetPathByBlockId.GetValueOrDefault(block.BlockId),
                FileName = string.IsNullOrWhiteSpace(text) ? "图片" : text
            },
            31 => Basic(block, "table", text, inlines, children),
            32 => Basic(block, "tableCell", text, inlines, children),
            33 => Basic(block, "container", text, inlines, children),
            34 => Basic(block, "quoteContainer", text, inlines, children),
            40 when IsTableOfContents(payload) => CreateTableOfContents(block),
            40 => Basic(block, "unsupported", text, inlines, children) with
            {
                SourceType = block.BlockType,
                ComponentTypeId = GetString(payload, "component_type_id"),
                HasSourceRecord = HasSourceRecord(payload)
            },
            42 or 51 => CreateSubPageList(block, children, precedingHeading),
            24 or 25 => Basic(block, "container", text, inlines, children),
            _ => Basic(block, "unsupported", text, inlines, children) with
            {
                SourceType = block.BlockType
            }
        };
    }

    private ReaderBlock CreateTableOfContents(DocumentBlockDto block)
    {
        var links = new List<ReaderLink>();
        return new ReaderBlock
        {
            Id = block.BlockId,
            Type = "toc",
            Links = links
        };
    }

    private void CollectHeadings(IEnumerable<ReaderBlock> blocks, List<ReaderLink> headings)
    {
        foreach (var block in blocks)
        {
            if (block.Type == "heading" && !string.IsNullOrWhiteSpace(block.Text))
            {
                headings.Add(new ReaderLink(block.Text, currentPageId, block.Id));
            }
            CollectHeadings(block.Children, headings);
        }
    }

    private static List<ReaderBlock> FillTableOfContents(
        IEnumerable<ReaderBlock> blocks,
        IReadOnlyList<ReaderLink> headings) =>
        blocks.Select(block => block with
        {
            Links = block.Type == "toc" ? headings : block.Links,
            Children = FillTableOfContents(block.Children, headings)
        }).ToList();

    private ReaderBlock CreateSubPageList(
        DocumentBlockDto block,
        IReadOnlyList<ReaderBlock> children,
        string? precedingHeading)
    {
        var links = ExtractPageLinks(FindPayload(block));
        if (links.Count > 0)
        {
            foreach (var link in links)
            {
                _heuristicallyAssignedPageIds.Add(link.TargetPageId);
            }
            return new ReaderBlock
            {
                Id = block.BlockId,
                Type = "subpages",
                Links = links
            };
        }

        var nestedTexts = new List<string>();
        var nestedLinks = new List<ReaderLink>();
        CollectNestedSubPageEvidence(children, nestedTexts, nestedLinks);
        foreach (var nestedLink in nestedLinks)
        {
            AddDistinctLink(links, nestedLink);
            _heuristicallyAssignedPageIds.Add(nestedLink.TargetPageId);
        }
        foreach (var nestedText in nestedTexts)
        {
            var match = FindUnassignedChildPage(nestedText);
            if (match is not null)
            {
                AddChildPageLink(links, match);
            }
        }

        if (links.Count == 0 && !string.IsNullOrWhiteSpace(precedingHeading))
        {
            var headingMatch = FindUnassignedChildPage(precedingHeading);
            if (headingMatch is not null)
            {
                AddChildPageLink(links, headingMatch);
            }
        }

        if (links.Count == 0 && _subPageListCount == 1)
        {
            foreach (var child in OrderedChildPages())
            {
                AddChildPageLink(links, child);
            }
        }

        if (links.Count == 0 && _subPageListCount > 1)
        {
            _subPageResolutionIssues.Add(new ReaderSubPageResolutionIssue(
                block.BlockId,
                precedingHeading,
                nestedTexts.Distinct(StringComparer.CurrentCulture).ToArray(),
                OrderedChildPages().Select(item => item.Title).ToArray(),
                "多个子页面列表中无法可靠确定此列表对应的页面，未生成推测性链接。"));
        }

        return new ReaderBlock
        {
            Id = block.BlockId,
            Type = "subpages",
            Links = links
        };
    }

    private IReadOnlyList<ExportItem> OrderedChildPages() => childPages
        .OrderBy(item => item.SiblingOrder ?? int.MaxValue)
        .ThenBy(item => item.Title, StringComparer.CurrentCulture)
        .ToList();

    private ExportItem? FindUnassignedChildPage(string title)
    {
        var comparable = NormalizeComparableTitle(title);
        if (comparable.Length == 0)
        {
            return null;
        }
        return OrderedChildPages().FirstOrDefault(item =>
            pageIdByToken.TryGetValue(item.HierarchyToken, out var pageId) &&
            !_heuristicallyAssignedPageIds.Contains(pageId) &&
            string.Equals(NormalizeComparableTitle(item.Title), comparable, StringComparison.OrdinalIgnoreCase));
    }

    private void AddChildPageLink(ICollection<ReaderLink> links, ExportItem item)
    {
        if (!pageIdByToken.TryGetValue(item.HierarchyToken, out var pageId))
        {
            return;
        }
        AddDistinctLink(links, new ReaderLink(item.Title, pageId));
        _heuristicallyAssignedPageIds.Add(pageId);
    }

    private static void AddDistinctLink(ICollection<ReaderLink> links, ReaderLink link)
    {
        if (!links.Any(existing => string.Equals(existing.TargetPageId, link.TargetPageId, StringComparison.Ordinal)))
        {
            links.Add(link);
        }
    }

    private static void CollectNestedSubPageEvidence(
        IEnumerable<ReaderBlock> blocks,
        ICollection<string> texts,
        ICollection<ReaderLink> links)
    {
        foreach (var block in blocks)
        {
            if (!string.IsNullOrWhiteSpace(block.Text))
            {
                texts.Add(block.Text);
            }
            foreach (var inline in block.Inlines)
            {
                if (!string.IsNullOrWhiteSpace(inline.TargetPageId))
                {
                    AddDistinctLink(links, new ReaderLink(inline.Text, inline.TargetPageId));
                }
            }
            foreach (var link in block.Links)
            {
                AddDistinctLink(links, link);
            }
            CollectNestedSubPageEvidence(block.Children, texts, links);
        }
    }

    private static string NormalizeComparableTitle(string value)
    {
        var normalized = value.Normalize(NormalizationForm.FormKC);
        return string.Concat(normalized.Where(character => !char.IsWhiteSpace(character))).Trim();
    }

    private List<ReaderLink> ExtractPageLinks(JsonElement? payload)
    {
        var result = new List<ReaderLink>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (payload is not null)
        {
            Visit(payload.Value);
        }
        return result;

        void Visit(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                var token = GetString(element, "token") ?? GetString(element, "obj_token") ?? GetString(element, "node_token");
                var url = GetString(element, "url");
                var target = ResolveTargetPageId(url, token);
                if (!string.IsNullOrWhiteSpace(target) && seen.Add(target))
                {
                    var title = GetString(element, "title") ?? GetString(element, "name") ?? "子页面";
                    result.Add(new ReaderLink(title, target));
                }
                foreach (var property in element.EnumerateObject())
                {
                    Visit(property.Value);
                }
            }
            else if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in element.EnumerateArray())
                {
                    Visit(item);
                }
            }
        }
    }

    private ReaderBlock Basic(
        DocumentBlockDto block,
        string type,
        string? text,
        IReadOnlyList<ReaderInline> inlines,
        IReadOnlyList<ReaderBlock> children) => new()
    {
        Id = block.BlockId,
        Type = type,
        Text = text,
        Inlines = inlines,
        Children = children
    };

    private IReadOnlyList<ReaderInline> ExtractInlines(JsonElement? payload)
    {
        if (payload is null || payload.Value.ValueKind != JsonValueKind.Object ||
            !payload.Value.TryGetProperty("elements", out var elements) ||
            elements.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var result = new List<ReaderInline>();
        foreach (var element in elements.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (element.TryGetProperty("text_run", out var textRun))
            {
                var content = GetString(textRun, "content") ?? string.Empty;
                var style = TryGet(textRun, "text_element_style");
                var url = GetString(style, "link", "url");
                result.Add(new ReaderInline(
                    content,
                    GetBoolean(style, "bold") ?? false,
                    GetBoolean(style, "italic") ?? false,
                    GetBoolean(style, "underline") ?? false,
                    GetBoolean(style, "strikethrough") ?? false,
                    GetBoolean(style, "inline_code") ?? false,
                    url,
                    ResolveTargetPageId(url, null)));
                continue;
            }

            if (element.TryGetProperty("mention_doc", out var mentionDoc))
            {
                var token = GetString(mentionDoc, "token");
                var url = GetString(mentionDoc, "url");
                var mentionTitle = GetString(mentionDoc, "title") ?? "文档链接";
                result.Add(new ReaderInline(
                    mentionTitle,
                    false,
                    false,
                    false,
                    false,
                    false,
                    url,
                    ResolveTargetPageId(url, token)));
                continue;
            }

            if (element.TryGetProperty("equation", out var equation))
            {
                result.Add(new ReaderInline(
                    GetString(equation, "content") ?? string.Empty,
                    false,
                    false,
                    false,
                    false,
                    true,
                    null,
                    null));
                continue;
            }

            if (element.TryGetProperty("mention_user", out _))
            {
                result.Add(new ReaderInline("@用户", false, false, false, false, false, null, null));
            }
        }
        return result;
    }

    private string? ResolveTargetPageId(string? url, string? token)
    {
        if (!string.IsNullOrWhiteSpace(token) && pageIdByToken.TryGetValue(token, out var direct))
        {
            return direct;
        }
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }
        foreach (var pair in pageIdByToken)
        {
            if (url.Contains(pair.Key, StringComparison.Ordinal))
            {
                return pair.Value;
            }
        }
        return null;
    }

    private static JsonElement? FindPayload(DocumentBlockDto block)
    {
        if (block.Properties is null)
        {
            return null;
        }
        var preferred = block.BlockType switch
        {
            2 => "text",
            >= 3 and <= 11 => $"heading{block.BlockType - 2}",
            12 => "bullet",
            13 => "ordered",
            14 => "code",
            15 => "quote",
            17 => "todo",
            19 => "callout",
            27 => "image",
            33 => "view",
            40 => "add_ons",
            42 => "wiki_catalog",
            51 => "sub_page_list",
            _ => string.Empty
        };
        if (!string.IsNullOrEmpty(preferred) && block.Properties.TryGetValue(preferred, out var found))
        {
            return found;
        }
        return block.Properties.Values.FirstOrDefault(value => value.ValueKind == JsonValueKind.Object);
    }

    private static IReadOnlyList<DocumentBlockDto> ResolveChildren(
        DocumentBlockDto? parent,
        IReadOnlyList<DocumentBlockDto> allBlocks,
        IReadOnlyDictionary<string, DocumentBlockDto> byId)
    {
        if (parent?.Children is { Count: > 0 })
        {
            return parent.Children
                .Where(byId.ContainsKey)
                .Select(id => byId[id])
                .ToList();
        }
        if (parent is not null)
        {
            var nested = byId.Values
                .Where(block => string.Equals(block.ParentId, parent.BlockId, StringComparison.Ordinal))
                .ToList();
            if (nested.Count > 0 || parent.BlockType != 1 || allBlocks.Count == 0)
            {
                return nested;
            }
            return allBlocks
                .Where(block => block.BlockType != 1 && string.IsNullOrWhiteSpace(block.ParentId))
                .ToList();
        }
        return allBlocks
            .Where(block => block.BlockType != 1 && string.IsNullOrWhiteSpace(block.ParentId))
            .ToList();
    }

    private static bool IsTableOfContents(JsonElement? payload)
    {
        if (payload is null)
        {
            return false;
        }
        var component = GetString(payload, "component_type_id") ?? string.Empty;
        return component.Contains("toc", StringComparison.OrdinalIgnoreCase) ||
               component.Contains("table-of-contents", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasSourceRecord(JsonElement? payload)
    {
        if (payload is null || payload.Value.ValueKind != JsonValueKind.Object ||
            !payload.Value.TryGetProperty("record", out var record))
        {
            return false;
        }
        return record.ValueKind switch
        {
            JsonValueKind.Null or JsonValueKind.Undefined => false,
            JsonValueKind.String => !string.IsNullOrWhiteSpace(record.GetString()),
            _ => true
        };
    }

    private static int CountUnsupported(IEnumerable<ReaderBlock> blocks) =>
        blocks.Sum(block => (block.Type == "unsupported" ? 1 : 0) + CountUnsupported(block.Children));

    private static string BuildSearchText(IReadOnlyList<ReaderBlock> blocks)
    {
        var builder = new StringBuilder();
        AppendSearchText(blocks, builder);
        return builder.ToString().Trim();
    }

    private static void AppendSearchText(IEnumerable<ReaderBlock> blocks, StringBuilder builder)
    {
        foreach (var block in blocks)
        {
            if (!string.IsNullOrWhiteSpace(block.Text))
            {
                builder.AppendLine(block.Text);
            }
            foreach (var link in block.Links)
            {
                builder.AppendLine(link.Title);
            }
            AppendSearchText(block.Children, builder);
        }
    }

    private static JsonElement? TryGet(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value)
            ? value
            : null;

    private static string? GetString(JsonElement? element, params string[] path)
    {
        if (element is null)
        {
            return null;
        }
        var current = element.Value;
        foreach (var part in path)
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(part, out current))
            {
                return null;
            }
        }
        return current.ValueKind == JsonValueKind.String ? current.GetString() : null;
    }

    private static bool? GetBoolean(JsonElement? element, params string[] path)
    {
        if (element is null)
        {
            return null;
        }
        var current = element.Value;
        foreach (var part in path)
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(part, out current))
            {
                return null;
            }
        }
        return current.ValueKind is JsonValueKind.True or JsonValueKind.False ? current.GetBoolean() : null;
    }
}
