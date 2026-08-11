using System.Text;
using System.Text.Json;

namespace FeishuExporter.Core;

internal sealed class FeishuBlockNormalizer(
    IReadOnlyDictionary<string, string> pageIdByToken,
    IReadOnlyList<ExportItem> childPages,
    IReadOnlyDictionary<string, string> assetPathByBlockId,
    string currentPageId,
    IReadOnlyDictionary<string, IReadOnlyList<ExportItem>>? childrenByParent = null,
    string? currentHierarchyToken = null)
{
    private readonly List<ReaderSubPageResolutionIssue> _subPageResolutionIssues = [];
    private readonly List<ReaderSubPageResolution> _subPageResolutions = [];
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
        return new ReaderKnowledgePage(
            title,
            normalized,
            text,
            unsupported,
            _subPageResolutionIssues.ToArray(),
            _subPageResolutions.ToArray());
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
            13 => Basic(block, "ordered", text, inlines, children) with
            {
                Sequence = GetString(payload, "style", "sequence")
            },
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
        var payload = FindPayload(block);
        var wikiToken = GetString(payload, "wiki_token");
        var links = new List<ReaderLink>();

        if (!string.IsNullOrWhiteSpace(wikiToken) &&
            TryResolveCatalogTarget(wikiToken, out var targetHierarchyToken, out var targetChildren))
        {
            foreach (var child in targetChildren)
            {
                AddChildPageLink(links, child);
            }
            return CompleteSubPageList(
                block,
                precedingHeading,
                wikiToken,
                targetHierarchyToken,
                "wiki_token",
                links,
                true,
                links.Count > 0
                    ? "已根据目录块的 wiki_token 还原目标节点的直接子页面。"
                    : "目录块的 wiki_token 已定位目标节点，但该节点没有可导出的直接子页面。");
        }

        links.AddRange(ExtractPageLinks(payload));
        if (links.Count > 0)
        {
            foreach (var link in links)
            {
                _heuristicallyAssignedPageIds.Add(link.TargetPageId);
            }
            return CompleteSubPageList(
                block,
                precedingHeading,
                wikiToken,
                null,
                "embedded_page_links",
                links,
                true,
                "已使用目录块中可直接识别的页面链接。");
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
            var match = FindUnassignedDescendantPage(nestedText);
            if (match is not null)
            {
                AddChildPageLink(links, match);
            }
        }

        if (links.Count > 0)
        {
            return CompleteSubPageList(
                block,
                precedingHeading,
                wikiToken,
                null,
                "nested_page_evidence",
                links,
                true,
                "已根据目录块的嵌套链接或标题还原子页面。");
        }

        if (links.Count == 0 && !string.IsNullOrWhiteSpace(precedingHeading))
        {
            var headingMatch = FindUnassignedChildPage(precedingHeading);
            if (headingMatch is not null)
            {
                MarkPageAssigned(headingMatch);
                var headingChildren = OrderedChildrenOf(headingMatch.HierarchyToken);
                if (headingChildren.Count > 0)
                {
                    foreach (var child in headingChildren)
                    {
                        AddChildPageLink(links, child);
                    }
                    return CompleteSubPageList(
                        block,
                        precedingHeading,
                        wikiToken,
                        headingMatch.HierarchyToken,
                        string.IsNullOrWhiteSpace(wikiToken)
                            ? "preceding_heading_target_children"
                            : "unresolved_wiki_token_then_heading_target_children",
                        links,
                        true,
                        "目录块未提供可用目标，已由前置标题定位分类节点并还原其直接子页面。");
                }

                AddChildPageLink(links, headingMatch);
                return CompleteSubPageList(
                    block,
                    precedingHeading,
                    wikiToken,
                    headingMatch.HierarchyToken,
                    string.IsNullOrWhiteSpace(wikiToken)
                        ? "preceding_heading_page"
                        : "unresolved_wiki_token_then_heading_page",
                    links,
                    true,
                    "目录块未提供可用目标，已由前置标题匹配到一个没有子页面的页面。");
            }
        }

        if (links.Count == 0 && _subPageListCount == 1)
        {
            foreach (var child in OrderedChildPages())
            {
                AddChildPageLink(links, child);
            }
            return CompleteSubPageList(
                block,
                precedingHeading,
                wikiToken,
                currentHierarchyToken,
                "single_list_current_children",
                links,
                true,
                "页面只有一个子页面目录，已使用当前页面的直接子节点。");
        }

        if (links.Count == 0 && _subPageListCount > 1)
        {
            _subPageResolutionIssues.Add(new ReaderSubPageResolutionIssue(
                block.BlockId,
                precedingHeading,
                nestedTexts.Distinct(StringComparer.CurrentCulture).ToArray(),
                OrderedChildPages().Select(item => item.Title).ToArray(),
                string.IsNullOrWhiteSpace(wikiToken)
                    ? "多个子页面列表中无法可靠确定此列表对应的页面，未生成推测性链接。"
                    : $"目录块的 wiki_token（{wikiToken}）无法映射到知识库层级，且其他证据不足，未生成推测性链接。"));
        }

        return CompleteSubPageList(
            block,
            precedingHeading,
            wikiToken,
            null,
            "unresolved",
            links,
            false,
            string.IsNullOrWhiteSpace(wikiToken)
                ? "无法可靠确定目录块对应的页面。"
                : $"wiki_token（{wikiToken}）无法映射到知识库层级。");
    }

    private ReaderBlock CompleteSubPageList(
        DocumentBlockDto block,
        string? precedingHeading,
        string? wikiToken,
        string? targetHierarchyToken,
        string strategy,
        IReadOnlyList<ReaderLink> links,
        bool resolved,
        string reason)
    {
        _subPageResolutions.Add(new ReaderSubPageResolution(
            block.BlockId,
            block.BlockType,
            precedingHeading,
            wikiToken,
            targetHierarchyToken,
            strategy,
            links.Select(link => link.Title).ToArray(),
            resolved,
            reason));
        return new ReaderBlock
        {
            Id = block.BlockId,
            Type = "subpages",
            Links = links
        };
    }

    private bool TryResolveCatalogTarget(
        string wikiToken,
        out string targetHierarchyToken,
        out IReadOnlyList<ExportItem> targetChildren)
    {
        var target = FindPageByAnyToken(wikiToken);
        targetHierarchyToken = target?.HierarchyToken ?? wikiToken;
        if (string.Equals(wikiToken, currentHierarchyToken, StringComparison.Ordinal) ||
            string.Equals(targetHierarchyToken, currentHierarchyToken, StringComparison.Ordinal))
        {
            targetChildren = OrderedChildPages();
            return true;
        }

        if (target is not null || childrenByParent?.ContainsKey(targetHierarchyToken) == true)
        {
            targetChildren = OrderedChildrenOf(targetHierarchyToken);
            return true;
        }

        targetHierarchyToken = string.Empty;
        targetChildren = [];
        return false;
    }

    private ExportItem? FindPageByAnyToken(string token)
    {
        IEnumerable<ExportItem> candidates = childrenByParent is null
            ? childPages
            : childrenByParent.Values.SelectMany(items => items);
        return candidates.FirstOrDefault(item =>
            string.Equals(item.HierarchyToken, token, StringComparison.Ordinal) ||
            string.Equals(item.ContentToken, token, StringComparison.Ordinal));
    }

    private IReadOnlyList<ExportItem> OrderedChildrenOf(string hierarchyToken)
    {
        if (childrenByParent is null || !childrenByParent.TryGetValue(hierarchyToken, out var children))
        {
            return [];
        }
        return children
            .Where(item => !string.Equals(item.Type, "embedded_file", StringComparison.OrdinalIgnoreCase))
            .OrderBy(item => item.SiblingOrder ?? int.MaxValue)
            .ThenBy(item => item.Title, StringComparer.CurrentCulture)
            .ToList();
    }

    private IReadOnlyList<ExportItem> OrderedChildPages() => childPages
        .OrderBy(item => item.SiblingOrder ?? int.MaxValue)
        .ThenBy(item => item.Title, StringComparer.CurrentCulture)
        .ToList();

    private ExportItem? FindUnassignedChildPage(string title)
        => FindUnassignedPage(title, OrderedChildPages());

    private ExportItem? FindUnassignedDescendantPage(string title)
        => FindUnassignedPage(title, OrderedDescendantPages());

    private ExportItem? FindUnassignedPage(string title, IEnumerable<ExportItem> candidates)
    {
        var comparable = NormalizeComparableTitle(title);
        if (comparable.Length == 0)
        {
            return null;
        }
        return candidates.FirstOrDefault(item =>
            pageIdByToken.TryGetValue(item.HierarchyToken, out var pageId) &&
            !_heuristicallyAssignedPageIds.Contains(pageId) &&
            string.Equals(NormalizeComparableTitle(item.Title), comparable, StringComparison.OrdinalIgnoreCase));
    }

    private IReadOnlyList<ExportItem> OrderedDescendantPages()
    {
        var result = new List<ExportItem>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        void Add(IEnumerable<ExportItem> items)
        {
            foreach (var item in items
                         .OrderBy(value => value.SiblingOrder ?? int.MaxValue)
                         .ThenBy(value => value.Title, StringComparer.CurrentCulture))
            {
                if (!visited.Add(item.HierarchyToken))
                {
                    continue;
                }
                result.Add(item);
                Add(OrderedChildrenOf(item.HierarchyToken));
            }
        }
        Add(OrderedChildPages());
        return result;
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

    private void MarkPageAssigned(ExportItem item)
    {
        if (pageIdByToken.TryGetValue(item.HierarchyToken, out var pageId))
        {
            _heuristicallyAssignedPageIds.Add(pageId);
        }
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
