using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AtlCli;

public partial class AtlassianClient
{
    private readonly HttpClient _jiraHttp;
    private readonly HttpClient _bbHttp;
    private readonly AtlassianConfig _config;

    public AtlassianClient(AtlassianConfig config, bool asCurl = false)
    {
        _config = config;

        _jiraHttp = asCurl ? new HttpClient(new CurlHandler()) : new HttpClient();
        _jiraHttp.BaseAddress = new Uri(config.JiraBaseUrl);
        _jiraHttp.DefaultRequestHeaders.Authorization = BasicAuth(config.Email, config.JiraToken);
        _jiraHttp.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        _bbHttp = asCurl ? new HttpClient(new CurlHandler()) : new HttpClient();
        _bbHttp.BaseAddress = new Uri("https://api.bitbucket.org");
        _bbHttp.DefaultRequestHeaders.Authorization = BasicAuth(config.Email, config.BitbucketToken);
        _bbHttp.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    private static AuthenticationHeaderValue BasicAuth(string user, string token) =>
        new("Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes($"{user}:{token}")));

    // --- Jira ---

    public async Task<JsonElement> GetIssueAsync(string key)
    {
        var resp = await _jiraHttp.GetAsync($"/rest/api/3/issue/{Uri.EscapeDataString(key)}?fields=status,summary");
        resp.EnsureSuccessStatusCode();
        var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync());
        return doc.RootElement;
    }

    public async Task<Dictionary<string, IssueStatusInfo>> GetIssueStatusesAsync(IEnumerable<string> keys)
    {
        var keyList = string.Join(",", keys);
        var jql = Uri.EscapeDataString($"key in ({keyList})");
        var resp = await _jiraHttp.GetAsync($"/rest/api/3/search/jql?jql={jql}&fields=status,statuscategorychangedate&maxResults=50");
        resp.EnsureSuccessStatusCode();
        var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync());

        var result = new Dictionary<string, IssueStatusInfo>();
        foreach (var issue in doc.RootElement.GetProperty("issues").EnumerateArray())
        {
            var key = issue.GetProperty("key").GetString()!;
            var fields = issue.GetProperty("fields");
            var status = fields.GetProperty("status").GetProperty("name").GetString()!;
            string? dateStr = null;
            if (fields.TryGetProperty("statuscategorychangedate", out var dateProp) && dateProp.ValueKind == JsonValueKind.String)
                dateStr = dateProp.GetString();
            result[key] = new IssueStatusInfo(status, dateStr);
        }
        return result;
    }

    public async Task<JsonElement> GetTransitionsAsync(string key)
    {
        var resp = await _jiraHttp.GetAsync($"/rest/api/3/issue/{Uri.EscapeDataString(key)}/transitions");
        resp.EnsureSuccessStatusCode();
        var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync());
        return doc.RootElement;
    }

    public async Task TransitionIssueAsync(string key, string targetStatus)
    {
        var transitions = await GetTransitionsAsync(key);
        string? transitionId = null;

        foreach (var t in transitions.GetProperty("transitions").EnumerateArray())
        {
            if (string.Equals(t.GetProperty("name").GetString(), targetStatus, StringComparison.OrdinalIgnoreCase))
            {
                transitionId = t.GetProperty("id").GetString();
                break;
            }
        }

        if (transitionId is null)
        {
            var available = transitions.GetProperty("transitions").EnumerateArray()
                .Select(t => t.GetProperty("name").GetString());
            throw new InvalidOperationException(
                $"No transition to '{targetStatus}' found. Available: {string.Join(", ", available)}");
        }

        var body = JsonSerializer.Serialize(new { transition = new { id = transitionId } });
        var content = new StringContent(body, Encoding.UTF8, "application/json");
        var resp = await _jiraHttp.PostAsync($"/rest/api/3/issue/{Uri.EscapeDataString(key)}/transitions", content);
        resp.EnsureSuccessStatusCode();
    }

    // --- Confluence ---

    public async Task<string> GetConfluencePageAsync(string pageId, bool asText = false)
    {
        var resp = await _jiraHttp.GetAsync($"/wiki/api/v2/pages/{pageId}?body-format=storage");
        resp.EnsureSuccessStatusCode();
        var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync());
        var root = doc.RootElement;

        var title = root.GetProperty("title").GetString() ?? "";
        var body = root.GetProperty("body").GetProperty("storage").GetProperty("value").GetString() ?? "";

        if (asText)
            body = ConvertHtmlToText(body);

        return $"# {title}\n\n{body}";
    }

    private static string ConvertHtmlToText(string html)
    {
        var sb = new StringBuilder();
        var pos = 0;

        while (pos < html.Length)
        {
            // Look for the next table
            var tableStart = html.IndexOf("<table", pos, StringComparison.OrdinalIgnoreCase);
            if (tableStart < 0)
            {
                // No more tables — convert the rest as inline content
                AppendInlineHtml(sb, html[pos..]);
                break;
            }

            // Convert everything before the table
            if (tableStart > pos)
                AppendInlineHtml(sb, html[pos..tableStart]);

            // Find the closing </table>
            var tableEnd = html.IndexOf("</table>", tableStart, StringComparison.OrdinalIgnoreCase);
            if (tableEnd < 0) tableEnd = html.Length;
            else tableEnd += "</table>".Length;

            // Render the table as aligned ASCII
            AppendTable(sb, html[tableStart..tableEnd]);
            pos = tableEnd;
        }

        // Clean up: collapse multiple blank lines
        var text = BlankLinesRegex().Replace(sb.ToString(), "\n\n");
        return text.Trim();
    }

    private static void AppendTable(StringBuilder sb, string tableHtml)
    {
        var rows = new List<List<string>>();
        int headerRowCount = 0;

        // Parse rows
        var rowMatches = TableRowRegex().Matches(tableHtml);

        foreach (Match rowMatch in rowMatches)
        {
            var cells = new List<string>();
            bool rowIsHeader = rowMatch.Value.Contains("<th", StringComparison.OrdinalIgnoreCase);

            var cellMatches = TableCellRegex().Matches(rowMatch.Groups[1].Value);

            foreach (Match cellMatch in cellMatches)
            {
                var cellText = StripTags(cellMatch.Groups[1].Value).Trim();
                cells.Add(cellText);
            }

            if (cells.Count > 0)
            {
                rows.Add(cells);
                if (rowIsHeader) headerRowCount++;
            }
        }

        if (rows.Count == 0) return;

        // Determine column count and widths
        int colCount = rows.Max(r => r.Count);
        var widths = new int[colCount];
        foreach (var row in rows)
        {
            for (int c = 0; c < row.Count; c++)
                widths[c] = Math.Max(widths[c], row[c].Length);
        }

        // Clamp column widths: min 3 (for separator), max 50 (for terminal readability)
        const int maxColWidth = 50;
        for (int c = 0; c < colCount; c++)
            widths[c] = Math.Clamp(widths[c], 3, maxColWidth);

        if (sb.Length > 0 && sb[^1] != '\n') sb.AppendLine();
        sb.AppendLine();

        for (int r = 0; r < rows.Count; r++)
        {
            var row = rows[r];
            sb.Append('|');
            for (int c = 0; c < colCount; c++)
            {
                var cell = c < row.Count ? row[c] : "";
                if (cell.Length > maxColWidth)
                    cell = cell[..(maxColWidth - 1)] + "…";
                sb.Append(' ');
                sb.Append(cell.PadRight(widths[c]));
                sb.Append(" |");
            }
            sb.AppendLine();

            // Separator after the first row
            if (r == 0)
            {
                sb.Append('|');
                for (int c = 0; c < colCount; c++)
                {
                    sb.Append(' ');
                    sb.Append(new string('-', widths[c]));
                    sb.Append(" |");
                }
                sb.AppendLine();
            }
        }
        sb.AppendLine();
    }

    private static string StripTags(string html)
    {
        var sb = new StringBuilder();
        bool inTag = false;

        for (int i = 0; i < html.Length; i++)
        {
            if (html[i] == '<') { inTag = true; continue; }
            if (html[i] == '>') { inTag = false; continue; }
            if (inTag) continue;

            if (html[i] == '&')
            {
                int semi = html.IndexOf(';', i);
                if (semi > i && semi - i < 12)
                {
                    var entity = html[i..(semi + 1)];
                    sb.Append(DecodeEntity(entity));
                    i = semi;
                    continue;
                }
            }
            sb.Append(html[i]);
        }
        return sb.ToString();
    }

    private static void AppendInlineHtml(StringBuilder sb, string html)
    {
        int i = 0;
        bool inTag = false;
        string currentTag = "";
        bool isClosing = false;

        while (i < html.Length)
        {
            if (html[i] == '<')
            {
                inTag = true;
                int tagStart = i + 1;
                isClosing = tagStart < html.Length && html[tagStart] == '/';
                if (isClosing) tagStart++;
                int tagEnd = tagStart;
                while (tagEnd < html.Length && html[tagEnd] != ' ' && html[tagEnd] != '>' && html[tagEnd] != '/')
                    tagEnd++;
                currentTag = html[tagStart..tagEnd].ToLower();
                i++;
                continue;
            }

            if (html[i] == '>')
            {
                inTag = false;

                if (!isClosing)
                {
                    // Opening tags
                    if (currentTag is "h1" or "h2" or "h3" or "h4")
                    {
                        if (sb.Length > 0 && sb[^1] != '\n') sb.AppendLine();
                        sb.AppendLine();
                        sb.Append(currentTag switch
                        {
                            "h1" => "# ",
                            "h2" => "## ",
                            "h3" => "### ",
                            _ => "#### "
                        });
                    }
                    else if (currentTag is "li")
                    {
                        if (sb.Length > 0 && sb[^1] != '\n') sb.AppendLine();
                        sb.Append("- ");
                    }
                    else if (currentTag is "strong" or "b")
                    {
                        sb.Append("**");
                    }
                    else if (currentTag is "code")
                    {
                        sb.Append('`');
                    }
                    else if (currentTag is "br")
                    {
                        sb.AppendLine();
                    }
                }
                else
                {
                    // Closing tags — only add newline if not already on a fresh line
                    if (currentTag is "h1" or "h2" or "h3" or "h4" or "li" or "p")
                    {
                        if (sb.Length > 0 && sb[^1] != '\n')
                            sb.AppendLine();
                    }
                    else if (currentTag is "strong" or "b")
                    {
                        sb.Append("**");
                    }
                    else if (currentTag is "code")
                    {
                        sb.Append('`');
                    }
                    else if (currentTag is "ul" or "ol")
                    {
                        if (sb.Length > 0 && sb[^1] != '\n') sb.AppendLine();
                    }
                }

                i++;
                continue;
            }

            if (inTag) { i++; continue; }

            // Decode HTML entities
            if (html[i] == '&')
            {
                int semi = html.IndexOf(';', i);
                if (semi > i && semi - i < 12)
                {
                    sb.Append(DecodeEntity(html[i..(semi + 1)]));
                    i = semi + 1;
                    continue;
                }
            }

            sb.Append(html[i]);
            i++;
        }
    }

    private static string DecodeEntity(string entity) => entity switch
    {
        "&amp;" => "&",
        "&lt;" => "<",
        "&gt;" => ">",
        "&nbsp;" => " ",
        "&ldquo;" or "&rdquo;" => "\"",
        "&lsquo;" or "&rsquo;" => "'",
        "&rarr;" => "→",
        "&mdash;" => "—",
        "&ndash;" => "–",
        _ => entity
    };

    // --- Bitbucket ---

    public async Task<JsonElement> GetPipelinesAsync(int pagelen = 50)
    {
        var path = $"/2.0/repositories/{_config.BitbucketWorkspace}/{_config.BitbucketRepo}" +
                   $"/pipelines/?sort=-created_on&pagelen={pagelen}";
        var resp = await _bbHttp.GetAsync(path);
        resp.EnsureSuccessStatusCode();
        var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync());
        return doc.RootElement;
    }

    public async Task<Dictionary<string, PipelineStatus>> GetPipelineStatusesAsync(IEnumerable<string> branches)
    {
        var branchSet = new HashSet<string>(branches);
        var result = new Dictionary<string, PipelineStatus>();
        var data = await GetPipelinesAsync();

        foreach (var p in data.GetProperty("values").EnumerateArray())
        {
            var target = p.GetProperty("target");
            var refName = target.TryGetProperty("source", out var src) ? src.GetString()
                        : target.TryGetProperty("ref_name", out var rn) ? rn.GetString()
                        : null;

            if (refName is null || !branchSet.Contains(refName) || result.ContainsKey(refName))
                continue;

            var state = p.GetProperty("state");
            var stateName = state.GetProperty("name").GetString()!;
            var resultName = stateName == "COMPLETED"
                ? state.GetProperty("result").GetProperty("name").GetString()!
                : stateName;

            var buildNum = p.GetProperty("build_number").GetInt32();

            result[refName] = new PipelineStatus(resultName, buildNum);

            if (result.Count == branchSet.Count)
                break;
        }

        return result;
    }

    public async Task<PipelineFailure?> GetPipelineFailureAsync(string branch)
    {
        var data = await GetPipelinesAsync();
        string? pipelineUuid = null;
        int buildNumber = 0;

        foreach (var p in data.GetProperty("values").EnumerateArray())
        {
            var target = p.GetProperty("target");
            var refName = target.TryGetProperty("source", out var src) ? src.GetString()
                        : target.TryGetProperty("ref_name", out var rn) ? rn.GetString()
                        : null;

            if (refName != branch) continue;

            var state = p.GetProperty("state");
            if (state.GetProperty("name").GetString() != "COMPLETED") continue;
            if (state.GetProperty("result").GetProperty("name").GetString() != "FAILED") continue;

            pipelineUuid = p.GetProperty("uuid").GetString();
            buildNumber = p.GetProperty("build_number").GetInt32();
            break;
        }

        if (pipelineUuid is null) return null;

        // Get steps to find the failed one
        var repoPath = $"/2.0/repositories/{_config.BitbucketWorkspace}/{_config.BitbucketRepo}";
        var stepsResp = await _bbHttp.GetAsync($"{repoPath}/pipelines/{Uri.EscapeDataString(pipelineUuid)}/steps/?pagelen=30");
        stepsResp.EnsureSuccessStatusCode();
        var stepsDoc = await JsonDocument.ParseAsync(await stepsResp.Content.ReadAsStreamAsync());

        string? failedStepUuid = null;
        string? failedStepName = null;

        foreach (var step in stepsDoc.RootElement.GetProperty("values").EnumerateArray())
        {
            var state = step.GetProperty("state");
            if (state.GetProperty("name").GetString() == "COMPLETED" &&
                state.TryGetProperty("result", out var result) &&
                result.GetProperty("name").GetString() == "FAILED")
            {
                failedStepUuid = step.GetProperty("uuid").GetString();
                failedStepName = step.GetProperty("name").GetString();
                break;
            }
        }

        if (failedStepUuid is null) return null;

        // Get the log
        var logResp = await _bbHttp.GetAsync(
            $"{repoPath}/pipelines/{Uri.EscapeDataString(pipelineUuid)}/steps/{Uri.EscapeDataString(failedStepUuid)}/log");
        logResp.EnsureSuccessStatusCode();
        var log = await logResp.Content.ReadAsStringAsync();

        // Extract error lines from the log
        var errors = log.Split('\n')
            .Where(l => l.Contains("error CS") || l.Contains("error TS"))
            .Select(l =>
            {
                // Extract "FileName.cs(line,col): error CSXXXX: message" from long Windows paths
                var errorIdx = l.IndexOf("): error ");
                if (errorIdx < 0) return l.Trim();

                // Walk backwards from "): error" to find the filename
                var pathPart = l[..errorIdx];
                var lastSlash = pathPart.LastIndexOf('\\');
                var fileName = lastSlash >= 0 ? pathPart[(lastSlash + 1)..] : pathPart;

                // Get the error message, strip the trailing [project.csproj] part
                var messagePart = l[(errorIdx + 2)..]; // skip ):
                var bracketIdx = messagePart.LastIndexOf(" [");
                if (bracketIdx >= 0) messagePart = messagePart[..bracketIdx];

                return $"{fileName}): {messagePart}".Trim();
            })
            .Distinct()
            .ToList();

        return new PipelineFailure(buildNumber, failedStepName!, errors);
    }
}

public partial class AtlassianClient
{
    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex BlankLinesRegex();

    [GeneratedRegex(@"<tr[^>]*>(.*?)</tr>", RegexOptions.Singleline)]
    private static partial Regex TableRowRegex();

    [GeneratedRegex(@"<t[hd][^>]*>(.*?)</t[hd]>", RegexOptions.Singleline)]
    private static partial Regex TableCellRegex();
}

public record IssueStatusInfo(string Status, string? StatusDate);
public record PipelineStatus(string Status, int BuildNumber);
public record PipelineFailure(int BuildNumber, string StepName, List<string> Errors);
