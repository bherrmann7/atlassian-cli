using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AtlCli;

/// <summary>
/// Converts between Atlassian Document Format and a markdown subset: headings, paragraphs,
/// bullet and ordered lists, tables, fenced code, block quotes, horizontal rules, and the
/// inline marks code / strong / em / link. Nodes outside the subset degrade to their text
/// rather than disappearing, so reading a ticket never silently loses content.
/// </summary>
public static class Adf
{
    // ---------------- ADF -> markdown ----------------

    public static string ToMarkdown(JsonElement doc)
    {
        var sb = new StringBuilder();
        foreach (var child in Children(doc)) Block(child, sb);
        return sb.ToString().Trim() + "\n";
    }

    private static void Block(JsonElement n, StringBuilder sb, string indent = "")
    {
        switch (Type(n))
        {
            case "heading":
                int lvl = Attr(n, "level") is { } l && l.ValueKind == JsonValueKind.Number ? l.GetInt32() : 1;
                sb.Append('\n').Append(new string('#', lvl)).Append(' ').Append(Inline(n)).Append('\n');
                break;

            case "paragraph":
                var p = Inline(n);
                if (p.Length > 0) sb.Append('\n').Append(indent).Append(p).Append('\n');
                break;

            case "bulletList":
            case "orderedList":
                bool ordered = Type(n) == "orderedList";
                int num = 1;
                sb.Append('\n');
                foreach (var li in Children(n))
                {
                    var inner = new StringBuilder();
                    foreach (var c in Children(li)) Block(c, inner, indent + "  ");
                    var lines = inner.ToString().Trim().Split('\n');
                    sb.Append(indent).Append(ordered ? $"{num++}. " : "- ").Append(lines[0]).Append('\n');
                    for (int k = 1; k < lines.Length; k++)
                        sb.Append(indent).Append("  ").Append(lines[k].TrimStart()).Append('\n');
                }
                break;

            case "codeBlock":
                var lang = Attr(n, "language")?.GetString();
                sb.Append("\n```").Append(lang).Append('\n').Append(Inline(n)).Append("\n```\n");
                break;

            case "blockquote":
                var q = new StringBuilder();
                foreach (var c in Children(n)) Block(c, q);
                sb.Append('\n');
                foreach (var line in q.ToString().Trim().Split('\n')) sb.Append("> ").Append(line).Append('\n');
                break;

            case "rule":
                sb.Append("\n---\n");
                break;

            case "table":
                Table(n, sb);
                break;

            default:
                foreach (var c in Children(n)) Block(c, sb, indent);
                break;
        }
    }

    private static void Table(JsonElement n, StringBuilder sb)
    {
        sb.Append('\n');
        bool first = true;
        foreach (var row in Children(n))
        {
            var cells = new List<string>();
            foreach (var cell in Children(row))
            {
                var inner = new StringBuilder();
                foreach (var c in Children(cell)) Block(c, inner);
                // A cell is one markdown line, so fold any internal breaks and escape delimiters.
                cells.Add(inner.ToString().Trim().Replace("\n", " ").Replace("|", "\\|"));
            }
            sb.Append("| ").Append(string.Join(" | ", cells)).Append(" |\n");
            if (first)
            {
                sb.Append('|');
                foreach (var _ in cells) sb.Append("---|");
                sb.Append('\n');
                first = false;
            }
        }
    }

    private static string Inline(JsonElement n)
    {
        var sb = new StringBuilder();
        foreach (var c in Children(n))
        {
            switch (Type(c))
            {
                case "text":
                    var s = c.TryGetProperty("text", out var tx) ? tx.GetString() ?? "" : "";
                    if (c.TryGetProperty("marks", out var marks) && marks.ValueKind == JsonValueKind.Array)
                        foreach (var m in marks.EnumerateArray())
                            s = Type(m) switch
                            {
                                "code" => $"`{s}`",
                                "strong" => $"**{s}**",
                                "em" => $"*{s}*",
                                "link" => $"[{s}]({Attr(m, "href")?.GetString()})",
                                _ => s,
                            };
                    sb.Append(s);
                    break;

                case "mention":
                    sb.Append(Attr(c, "text")?.GetString() ?? "@unknown");
                    break;

                case "inlineCard":
                    sb.Append(Attr(c, "url")?.GetString() ?? "");
                    break;

                case "hardBreak":
                    sb.Append('\n');
                    break;

                default:
                    sb.Append(Inline(c));
                    break;
            }
        }
        return sb.ToString();
    }

    private static string Type(JsonElement n) =>
        n.ValueKind == JsonValueKind.Object && n.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";

    private static JsonElement? Attr(JsonElement n, string name) =>
        n.ValueKind == JsonValueKind.Object && n.TryGetProperty("attrs", out var a)
        && a.TryGetProperty(name, out var v) ? v : null;

    private static IEnumerable<JsonElement> Children(JsonElement n) =>
        n.ValueKind == JsonValueKind.Object && n.TryGetProperty("content", out var c)
        && c.ValueKind == JsonValueKind.Array ? c.EnumerateArray() : [];

    // ---------------- markdown -> ADF ----------------

    public static string FromMarkdown(string md) =>
        new JsonObject { ["version"] = 1, ["type"] = "doc", ["content"] = Blocks(md) }.ToJsonString();

    private static readonly Regex HeadingRe = new(@"^(#{1,6})\s+(.*)$");
    private static readonly Regex ListRe = new(@"^(\s*)([-*+]|\d+\.)\s+(.*)$");
    private static readonly Regex RuleRe = new(@"^(-{3,}|\*{3,}|_{3,})$");
    private static readonly Regex SepRe = new(@"^:?-{2,}:?$");
    private static readonly Regex InlineRe =
        new(@"`([^`]+)`|\*\*([^*]+)\*\*|\*([^*]+)\*|\[([^\]]+)\]\(([^)]+)\)");

    private static JsonArray Blocks(string md)
    {
        var result = new JsonArray();
        var lines = md.Replace("\r\n", "\n").Split('\n');
        int i = 0;

        while (i < lines.Length)
        {
            var line = lines[i];
            if (line.Trim().Length == 0) { i++; continue; }

            if (line.TrimStart().StartsWith("```"))
            {
                var lang = line.Trim().TrimStart('`').Trim();
                var body = new List<string>();
                i++;
                while (i < lines.Length && !lines[i].TrimStart().StartsWith("```")) body.Add(lines[i++]);
                i++;
                var cb = new JsonObject
                {
                    ["type"] = "codeBlock",
                    ["content"] = new JsonArray(TextNode(string.Join("\n", body))),
                };
                if (lang.Length > 0) cb["attrs"] = new JsonObject { ["language"] = lang };
                result.Add(cb);
                continue;
            }

            var h = HeadingRe.Match(line);
            if (h.Success)
            {
                result.Add(new JsonObject
                {
                    ["type"] = "heading",
                    ["attrs"] = new JsonObject { ["level"] = h.Groups[1].Value.Length },
                    ["content"] = InlineNodes(h.Groups[2].Value),
                });
                i++;
                continue;
            }

            if (RuleRe.IsMatch(line.Trim())) { result.Add(new JsonObject { ["type"] = "rule" }); i++; continue; }

            if (line.TrimStart().StartsWith('|'))
            {
                var rows = new List<string>();
                while (i < lines.Length && lines[i].TrimStart().StartsWith('|')) rows.Add(lines[i++]);
                result.Add(TableNode(rows));
                continue;
            }

            if (line.TrimStart().StartsWith('>'))
            {
                var body = new List<string>();
                while (i < lines.Length && lines[i].TrimStart().StartsWith('>'))
                    body.Add(Regex.Replace(lines[i++].TrimStart(), @"^>\s?", ""));
                result.Add(new JsonObject { ["type"] = "blockquote", ["content"] = Blocks(string.Join("\n", body)) });
                continue;
            }

            var lm = ListRe.Match(line);
            if (lm.Success)
            {
                bool ordered = char.IsDigit(lm.Groups[2].Value[0]);
                var items = new JsonArray();
                while (i < lines.Length)
                {
                    var m = ListRe.Match(lines[i]);
                    if (!m.Success || char.IsDigit(m.Groups[2].Value[0]) != ordered) break;
                    var text = new List<string> { m.Groups[3].Value };
                    i++;
                    // An indented line that is not itself a bullet continues the current item.
                    while (i < lines.Length && lines[i].StartsWith("  ") && !ListRe.IsMatch(lines[i]))
                        text.Add(lines[i++].Trim());
                    items.Add(new JsonObject
                    {
                        ["type"] = "listItem",
                        ["content"] = new JsonArray(new JsonObject
                        {
                            ["type"] = "paragraph",
                            ["content"] = InlineNodes(string.Join(" ", text)),
                        }),
                    });
                }
                result.Add(new JsonObject { ["type"] = ordered ? "orderedList" : "bulletList", ["content"] = items });
                continue;
            }

            var para = new List<string>();
            while (i < lines.Length && lines[i].Trim().Length > 0
                   && !lines[i].TrimStart().StartsWith('|') && !lines[i].TrimStart().StartsWith('>')
                   && !lines[i].TrimStart().StartsWith("```")
                   && !HeadingRe.IsMatch(lines[i]) && !ListRe.IsMatch(lines[i]))
                para.Add(lines[i++]);
            result.Add(new JsonObject { ["type"] = "paragraph", ["content"] = InlineNodes(string.Join(" ", para).Trim()) });
        }
        return result;
    }

    private static JsonObject TableNode(List<string> rows)
    {
        var content = new JsonArray();
        bool header = true;
        foreach (var r in rows)
        {
            var cells = SplitRow(r.Trim());
            if (cells.All(c => SepRe.IsMatch(c.Trim()))) continue;   // the |---|---| divider
            var rowNode = new JsonArray();
            foreach (var cell in cells)
                rowNode.Add(new JsonObject
                {
                    ["type"] = header ? "tableHeader" : "tableCell",
                    ["attrs"] = new JsonObject(),
                    ["content"] = new JsonArray(new JsonObject
                    {
                        ["type"] = "paragraph",
                        ["content"] = InlineNodes(cell.Trim()),
                    }),
                });
            content.Add(new JsonObject { ["type"] = "tableRow", ["content"] = rowNode });
            header = false;
        }
        return new JsonObject
        {
            ["type"] = "table",
            ["attrs"] = new JsonObject { ["isNumberColumnEnabled"] = false, ["layout"] = "default" },
            ["content"] = content,
        };
    }

    private static List<string> SplitRow(string row)
    {
        var s = row;
        if (s.StartsWith('|')) s = s[1..];
        if (s.EndsWith('|')) s = s[..^1];
        return Regex.Split(s, @"(?<!\\)\|").Select(x => x.Replace("\\|", "|")).ToList();
    }

    private static JsonArray InlineNodes(string text)
    {
        var arr = new JsonArray();
        int pos = 0;
        foreach (Match m in InlineRe.Matches(text))
        {
            if (m.Index > pos) arr.Add(TextNode(text[pos..m.Index]));
            if (m.Groups[1].Success) arr.Add(TextNode(m.Groups[1].Value, "code"));
            else if (m.Groups[2].Success) arr.Add(TextNode(m.Groups[2].Value, "strong"));
            else if (m.Groups[3].Success) arr.Add(TextNode(m.Groups[3].Value, "em"));
            else arr.Add(LinkNode(m.Groups[4].Value, m.Groups[5].Value));
            pos = m.Index + m.Length;
        }
        if (pos < text.Length) arr.Add(TextNode(text[pos..]));
        return arr;   // an empty array is a valid empty paragraph; an empty text node is not
    }

    private static JsonObject TextNode(string s, string? mark = null)
    {
        var n = new JsonObject { ["type"] = "text", ["text"] = s };
        if (mark is not null) n["marks"] = new JsonArray(new JsonObject { ["type"] = mark });
        return n;
    }

    private static JsonObject LinkNode(string s, string href) => new()
    {
        ["type"] = "text",
        ["text"] = s,
        ["marks"] = new JsonArray(new JsonObject
        {
            ["type"] = "link",
            ["attrs"] = new JsonObject { ["href"] = href },
        }),
    };
}
