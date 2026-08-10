using System.Text;
using System.Text.Json;

namespace FeishuExporter.Core;

internal sealed class FeishuBlockNormalizer(
    IReadOnlyDictionary<string, string> pageIdByToken,
    IReadOnlyList<ExportItem> childPages,
    IReadOnlyDictionary<string, string> assetPathByBlockId,
    string currentPageId)
{
    private bool _subPagesEmitted;

    public ReaderKnowledgePage Normalize(string title, IReadOnlyList<DocumentBlockDto> blocks)
    {
        var byId = blocks
            .Where(block => !string.IsNullOrWhiteSpace(block.BlockId))
            .GroupBy(block => block.BlockId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var page = blocks.FirstOrDefault(block => block.BlockType == 1);
        var roots = ResolveChildren(page, blocks, byId);
        var normalized = roots.Select(block => NormalizeBlock(block, byId)).ToList();
        var headings = new List<ReaderLink>();
        CollectHeadings(normalized, headings);
        normalized = FillTableOfContents(normalized, headings);
        var unsupported = CountUnsupported(normalized);
        var text = BuildSearchText(normalized);
        return new ReaderKnowledgePage(title, normalized, text, unsupported);
    }

    private ReaderBlock NormalizeBlock(
        DocumentBlockDto block,
        IReadOnlyDictionary<string, DocumentBlockDto> byId)
    {
        var payload = FindPayload(block);
        var inlines = ExtractInlines(payload);
        var text = string.Concat(inlines.Select(inline => inline.Text));
        var children = ResolveChildren(block, [], byId)
            .Select(child => NormalizeBlock(child, byId))
            .ToList();

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
            34 => Basic(block, "quoteContainer", text, inlines, children),
            40 when IsTableOfContents(payload) => CreateTableOfContents(block),
            42 or 51 => CreateSubPageList(block),
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

    private ReaderBlock CreateSubPageList(DocumentBlockDto block)
    {
        var links = ExtractPageLinks(FindPayload(block));
        if (links.Count > 0)
        {
            return new ReaderBlock
            {
                Id = block.BlockId,
                Type = "subpages",
                Links = links
            };
        }

        if (_subPagesEmitted)
        {
            links = [];
        }
        else
        {
            links = childPages
                .OrderBy(item => item.SiblingOrder ?? int.MaxValue)
                .ThenBy(item => item.Title, StringComparer.CurrentCulture)
                .Where(item => pageIdByToken.ContainsKey(item.HierarchyToken))
                .Select(item => new ReaderLink(item.Title, pageIdByToken[item.HierarchyToken]))
                .ToList();
        }
        _subPagesEmitted = true;
        return new ReaderBlock
        {
            Id = block.BlockId,
            Type = "subpages",
            Links = links
        };
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
