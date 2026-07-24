using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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

    public async Task<JsonNode> GetIssueAsync(string key)
    {
        var pointsField = await ResolveStoryPointsFieldAsync();
        var resp = await _jiraHttp.GetAsync($"/rest/api/3/issue/{Uri.EscapeDataString(key)}?fields=status,summary,description,comment,issuetype,priority,labels,assignee,issuelinks,{pointsField}");
        resp.EnsureSuccessStatusCode();
        var node = await JsonNode.ParseAsync(await resp.Content.ReadAsStreamAsync())
                   ?? throw new HttpRequestException($"Empty response fetching issue {key}.");

        // Surface the story-point custom field under a readable name so callers can script
        // against .fields.storyPoints instead of an instance-specific customfield id. Always
        // present: null when the field is unset on the issue.
        if (node["fields"] is JsonObject fields)
        {
            // Jira omits a requested field entirely when the id is not valid for the instance.
            // Reporting that as an unset value would make a misconfigured field indistinguishable
            // from a genuinely unpointed ticket, so fail instead.
            if (!fields.ContainsKey(pointsField))
                throw new InvalidOperationException(
                    $"Jira did not return {pointsField} for {key} — it is probably not a valid " +
                    $"field id on {_config.JiraBaseUrl}. Check the Atlassian:StoryPointsField user secret.");

            fields.Remove(pointsField, out var storyPoints);
            fields["storyPoints"] = storyPoints;
        }

        return node;
    }

    public async Task<string> GetMyAccountIdAsync()
    {
        var resp = await _jiraHttp.GetAsync("/rest/api/3/myself");
        resp.EnsureSuccessStatusCode();
        var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync());
        return doc.RootElement.GetProperty("accountId").GetString()!;
    }

    public async Task<JsonElement> CreateIssueAsync(string project, string issueType, string summary, string? assignee)
    {
        var fields = new Dictionary<string, object?>
        {
            ["project"] = new { key = project },
            ["issuetype"] = new { name = issueType },
            ["summary"] = summary,
        };

        if (!string.IsNullOrEmpty(assignee))
        {
            var accountId = assignee == "@me" ? await GetMyAccountIdAsync() : assignee;
            fields["assignee"] = new { accountId };
        }

        var json = JsonSerializer.Serialize(new { fields });
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        var resp = await _jiraHttp.PostAsync("/rest/api/3/issue", content);
        if (!resp.IsSuccessStatusCode)
        {
            var errBody = await resp.Content.ReadAsStringAsync();
            throw new HttpRequestException($"Create issue failed ({(int)resp.StatusCode} {resp.ReasonPhrase}): {errBody}");
        }
        var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync());
        return doc.RootElement;
    }

    public async Task LinkIssuesAsync(string fromKey, string toKey, string linkType)
    {
        var payload = new
        {
            type = new { name = linkType },
            inwardIssue = new { key = fromKey },
            outwardIssue = new { key = toKey }
        };
        var json = JsonSerializer.Serialize(payload);
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        var resp = await _jiraHttp.PostAsync("/rest/api/3/issueLink", content);
        if (!resp.IsSuccessStatusCode)
        {
            var errBody = await resp.Content.ReadAsStringAsync();
            throw new HttpRequestException($"Link issues failed ({(int)resp.StatusCode} {resp.ReasonPhrase}): {errBody}");
        }
        // 201 Created with an empty body — nothing to parse.
    }

    public async Task<JsonElement> CreateCommentAsync(string key, string text)
    {
        // Wrap plain text in a minimal ADF document: one paragraph per line so newlines
        // render. Empty lines become empty paragraphs (an ADF text node may not be "").
        var paragraphs = text.Replace("\r\n", "\n").Split('\n')
            .Select(line => line.Length == 0
                ? (object)new { type = "paragraph" }
                : new { type = "paragraph", content = new[] { new { type = "text", text = line } } })
            .ToArray();

        var payload = new { body = new { type = "doc", version = 1, content = paragraphs } };
        var json = JsonSerializer.Serialize(payload);
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        var resp = await _jiraHttp.PostAsync($"/rest/api/3/issue/{Uri.EscapeDataString(key)}/comment", content);
        if (!resp.IsSuccessStatusCode)
        {
            var errBody = await resp.Content.ReadAsStringAsync();
            throw new HttpRequestException($"Create comment failed ({(int)resp.StatusCode} {resp.ReasonPhrase}): {errBody}");
        }
        var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync());
        return doc.RootElement;
    }

    public async Task<JsonElement> CreateCommentAdfAsync(string key, string adfDocJson)
    {
        // adfDocJson is a raw ADF document body: { "version": 1, "type": "doc", "content": [...] }.
        // Validate it parses (throws on malformed input), then wrap it as the comment "body" without
        // re-serializing so the caller's exact ADF is preserved.
        using (JsonDocument.Parse(adfDocJson)) { }

        var json = $"{{\"body\":{adfDocJson}}}";
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        var resp = await _jiraHttp.PostAsync($"/rest/api/3/issue/{Uri.EscapeDataString(key)}/comment", content);
        if (!resp.IsSuccessStatusCode)
        {
            var errBody = await resp.Content.ReadAsStringAsync();
            throw new HttpRequestException($"Create comment (ADF) failed ({(int)resp.StatusCode} {resp.ReasonPhrase}): {errBody}");
        }
        var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync());
        return doc.RootElement;
    }

    public async Task SetDescriptionAsync(string key, string text)
    {
        // Same plain-text -> ADF wrapping as CreateCommentAsync: one paragraph per line.
        var paragraphs = text.Replace("\r\n", "\n").Split('\n')
            .Select(line => line.Length == 0
                ? (object)new { type = "paragraph" }
                : new { type = "paragraph", content = new[] { new { type = "text", text = line } } })
            .ToArray();

        var adf = new { type = "doc", version = 1, content = paragraphs };
        var payload = new { fields = new { description = adf } };
        var json = JsonSerializer.Serialize(payload);
        await PutDescriptionAsync(key, json);
    }

    public async Task SetDescriptionAdfAsync(string key, string adfDocJson)
    {
        // adfDocJson is a raw ADF document body: { "version": 1, "type": "doc", "content": [...] }.
        using (JsonDocument.Parse(adfDocJson)) { }
        var json = $"{{\"fields\":{{\"description\":{adfDocJson}}}}}";
        await PutDescriptionAsync(key, json);
    }

    private async Task PutDescriptionAsync(string key, string json)
    {
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        var resp = await _jiraHttp.PutAsync($"/rest/api/3/issue/{Uri.EscapeDataString(key)}", content);
        if (!resp.IsSuccessStatusCode)
        {
            var errBody = await resp.Content.ReadAsStringAsync();
            throw new HttpRequestException($"Set description failed ({(int)resp.StatusCode} {resp.ReasonPhrase}): {errBody}");
        }
    }

    // Story points live in a custom field whose id differs per Jira instance, so it cannot be
    // hardcoded. Company-managed projects name it "Story Points"; team-managed projects use
    // "Story point estimate" — an instance may expose either or both, hence the ordered probe
    // and the config override for disambiguation.
    private static readonly string[] StoryPointsFieldNames = ["Story Points", "Story point estimate"];
    private string? _storyPointsField;

    private async Task<string> ResolveStoryPointsFieldAsync()
    {
        if (!string.IsNullOrWhiteSpace(_config.StoryPointsField))
            return _config.StoryPointsField;

        if (_storyPointsField is not null)
            return _storyPointsField;

        var resp = await _jiraHttp.GetAsync("/rest/api/3/field");
        resp.EnsureSuccessStatusCode();
        var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync());

        var candidates = doc.RootElement.EnumerateArray()
            .Select(f => (
                Id: f.TryGetProperty("id", out var id) ? id.GetString() : null,
                Name: f.TryGetProperty("name", out var name) ? name.GetString() : null))
            .Where(f => f.Id is not null && StoryPointsFieldNames.Contains(f.Name, StringComparer.OrdinalIgnoreCase))
            .ToList();

        // Probe in preference order rather than taking the first match, so an instance carrying
        // both fields resolves to "Story Points" deterministically.
        foreach (var wanted in StoryPointsFieldNames)
        {
            var match = candidates.Where(c => string.Equals(c.Name, wanted, StringComparison.OrdinalIgnoreCase)).ToList();
            if (match.Count == 1)
                return _storyPointsField = match[0].Id!;

            // Same name, several ids — only the operator can say which one they mean.
            if (match.Count > 1)
                throw new InvalidOperationException(
                    $"Ambiguous story-point field: {match.Count} fields named \"{wanted}\" " +
                    $"({string.Join(", ", match.Select(c => c.Id))}). " +
                    "Set the Atlassian:StoryPointsField user secret to pick one.");
        }

        throw new InvalidOperationException(
            $"Could not resolve a story-point field on {_config.JiraBaseUrl} (looked for " +
            $"{string.Join(" / ", StoryPointsFieldNames.Select(n => $"\"{n}\""))}). " +
            "Set the Atlassian:StoryPointsField user secret to the custom field id.");
    }

    public async Task SetStoryPointsAsync(string key, decimal points)
    {
        var field = await ResolveStoryPointsFieldAsync();
        var value = points.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var json = $"{{\"fields\":{{\"{field}\":{value}}}}}";
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        var resp = await _jiraHttp.PutAsync($"/rest/api/3/issue/{Uri.EscapeDataString(key)}", content);
        if (!resp.IsSuccessStatusCode)
        {
            var errBody = await resp.Content.ReadAsStringAsync();
            throw new HttpRequestException($"Set story points failed ({(int)resp.StatusCode} {resp.ReasonPhrase}): {errBody}");
        }
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
        // Try the draft endpoint first; fall back to current if no draft exists.
        // Confluence Cloud's v2 page-by-id returns the published "current" version by
        // default, even if a draft has more recent edits, so we need an explicit query.
        var resp = await _jiraHttp.GetAsync($"/wiki/api/v2/pages/{pageId}?body-format=storage&get-draft=true");
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            resp = await _jiraHttp.GetAsync($"/wiki/api/v2/pages/{pageId}?body-format=storage");
        }
        resp.EnsureSuccessStatusCode();
        var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync());
        var root = doc.RootElement;

        var title = root.GetProperty("title").GetString() ?? "";
        var body = root.GetProperty("body").GetProperty("storage").GetProperty("value").GetString() ?? "";

        if (asText)
            body = ConvertHtmlToText(body);

        return $"# {title}\n\n{body}";
    }

    public async Task<List<ConfluenceSearchHit>> SearchConfluenceAsync(string text, int limit = 25)
    {
        // CQL: search for the term across all current pages, ranked by relevance.
        // Quoted phrase so terms with hyphens or special chars don't break the parser.
        string cql = $"text ~ \"{text.Replace("\"", "\\\"")}\" and type = page";
        var url = $"/wiki/rest/api/search?cql={Uri.EscapeDataString(cql)}&limit={limit}";
        var resp = await _jiraHttp.GetAsync(url);
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync();
            throw new HttpRequestException($"search failed ({(int)resp.StatusCode} {resp.ReasonPhrase}): {err[..Math.Min(err.Length, 400)]}");
        }
        var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync());
        var hits = new List<ConfluenceSearchHit>();
        foreach (var r in doc.RootElement.GetProperty("results").EnumerateArray())
        {
            string? excerpt = r.TryGetProperty("excerpt", out var e) ? e.GetString() : null;
            var content = r.GetProperty("content");
            string id = content.GetProperty("id").GetString() ?? "";
            string title = content.GetProperty("title").GetString() ?? "";
            string? spaceKey = null;
            if (r.TryGetProperty("resultGlobalContainer", out var ctn) &&
                ctn.TryGetProperty("title", out var ctnt))
                spaceKey = ctnt.GetString();
            hits.Add(new ConfluenceSearchHit(id, title, spaceKey, excerpt));
        }
        return hits;
    }

    public async Task<List<ConfluenceAttachment>> ListConfluenceAttachmentsAsync(string pageId)
    {
        var resp = await _jiraHttp.GetAsync($"/wiki/api/v2/pages/{pageId}/attachments?limit=100");
        resp.EnsureSuccessStatusCode();
        var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync());
        var list = new List<ConfluenceAttachment>();
        foreach (var a in doc.RootElement.GetProperty("results").EnumerateArray())
        {
            list.Add(new ConfluenceAttachment(
                Id: a.GetProperty("id").GetString() ?? "",
                Title: a.GetProperty("title").GetString() ?? "",
                MediaType: a.GetProperty("mediaType").GetString() ?? "",
                DownloadLink: a.TryGetProperty("downloadLink", out var d) ? d.GetString() : null
            ));
        }
        return list;
    }

    public async Task DownloadConfluenceAttachmentAsync(string downloadPath, string localPath)
    {
        // Confluence v2 attachments API returns links like "/download/attachments/...".
        // Those resolve under the /wiki context root on Cloud.
        var url = downloadPath.StartsWith("/wiki") ? downloadPath : "/wiki" + downloadPath;
        var resp = await _jiraHttp.GetAsync(url);
        resp.EnsureSuccessStatusCode();
        await using var fs = File.Create(localPath);
        await resp.Content.CopyToAsync(fs);
    }

    public async Task<string?> GetDrawioDiagramAsync(string pageId, string diagramName)
    {
        // Modern Confluence Cloud stores drawio diagrams as "custom-content".
        // First read the page body to find the custContentId, then fetch that
        // custom content's body (which holds the diagram XML).
        var pageResp = await _jiraHttp.GetAsync($"/wiki/api/v2/pages/{pageId}?body-format=storage");
        pageResp.EnsureSuccessStatusCode();
        var pageDoc = await JsonDocument.ParseAsync(await pageResp.Content.ReadAsStreamAsync());
        var storage = pageDoc.RootElement.GetProperty("body").GetProperty("storage").GetProperty("value").GetString() ?? "";

        var idMatch = Regex.Match(storage, @"name=""custContentId"">(\d+)<");
        if (!idMatch.Success)
            throw new HttpRequestException("No custContentId macro parameter found on the page.");
        var custId = idMatch.Groups[1].Value;

        var resp = await _jiraHttp.GetAsync($"/wiki/rest/api/content/{custId}?expand=body.storage,body.dynamic,body.view");
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync();
            throw new HttpRequestException($"custom-content fetch failed ({(int)resp.StatusCode} {resp.ReasonPhrase}): {err[..Math.Min(err.Length, 400)]}");
        }
        var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync());
        if (doc.RootElement.TryGetProperty("body", out var bodyEl))
        {
            foreach (var key in new[] { "storage", "dynamic", "view", "raw" })
            {
                if (bodyEl.TryGetProperty(key, out var rep) &&
                    rep.TryGetProperty("value", out var v) &&
                    v.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(v.GetString()))
                {
                    return v.GetString();
                }
            }
        }
        return JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions { WriteIndented = true });
    }

    public async Task<ConfluencePageMeta> GetConfluencePageMetaAsync(string pageId)
    {
        var resp = await _jiraHttp.GetAsync($"/wiki/api/v2/pages/{pageId}");
        resp.EnsureSuccessStatusCode();
        var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync());
        var root = doc.RootElement;
        return new ConfluencePageMeta(
            Id: root.GetProperty("id").GetString() ?? "",
            Title: root.GetProperty("title").GetString() ?? "",
            SpaceId: root.GetProperty("spaceId").GetString() ?? "",
            Status: root.GetProperty("status").GetString() ?? "",
            VersionNumber: root.GetProperty("version").GetProperty("number").GetInt32(),
            ParentId: root.TryGetProperty("parentId", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null
        );
    }

    // Confluence storage bodies are XHTML: the body must begin with a markup element, not
    // stray text. The common trap is feeding `wiki page --raw` output straight back in — it
    // carries a leading "# {title}" display line, which Confluence silently wraps in a <p> and
    // applies, corrupting the top of the page. Reject it here with a clear, actionable message
    // rather than letting a "successful" update quietly prepend garbage.
    private static void RejectLeadingNonMarkup(string storageBody)
    {
        var trimmed = storageBody.TrimStart();
        if (trimmed.Length > 0 && trimmed[0] != '<')
        {
            var preview = trimmed.Length > 60 ? trimmed[..60] + "…" : trimmed;
            throw new ArgumentException(
                $"Storage body must start with an XHTML element, but starts with text: \"{preview}\". " +
                $"If this came from `wiki page --raw`, strip the leading \"# {{title}}\" line " +
                $"(everything before the first '<') before updating.");
        }
    }

    public async Task<ConfluencePageCreated> CreateConfluencePageAsync(string spaceId, string? parentId, string title, string storageBody, bool draft)
    {
        RejectLeadingNonMarkup(storageBody);
        var payload = new Dictionary<string, object?>
        {
            ["spaceId"] = spaceId,
            ["status"] = draft ? "draft" : "current",
            ["title"] = title,
            ["body"] = new { representation = "storage", value = storageBody }
        };
        if (!string.IsNullOrEmpty(parentId)) payload["parentId"] = parentId;

        var json = JsonSerializer.Serialize(payload);
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        var resp = await _jiraHttp.PostAsync("/wiki/api/v2/pages", content);
        if (!resp.IsSuccessStatusCode)
        {
            var errBody = await resp.Content.ReadAsStringAsync();
            throw new HttpRequestException($"Create page failed ({(int)resp.StatusCode} {resp.ReasonPhrase}): {errBody}");
        }
        var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync());
        var root = doc.RootElement;
        var id = root.GetProperty("id").GetString() ?? "";
        var webui = root.TryGetProperty("_links", out var links) && links.TryGetProperty("webui", out var w) ? w.GetString() : null;
        int? version = root.TryGetProperty("version", out var vEl) && vEl.TryGetProperty("number", out var vNum) ? vNum.GetInt32() : null;
        return new ConfluencePageCreated(id, title, draft ? "draft" : "current", webui, version);
    }

    public async Task<ConfluencePageCreated> UpdateConfluencePageAsync(string pageId, string title, string storageBody, bool draft)
    {
        RejectLeadingNonMarkup(storageBody);
        var meta = await GetConfluencePageMetaAsync(pageId);
        // Drafts do not support version increments — Confluence requires version: 1.
        var nextVersion = draft ? 1 : meta.VersionNumber + 1;
        var payload = new
        {
            id = pageId,
            status = draft ? "draft" : "current",
            title,
            body = new { representation = "storage", value = storageBody },
            version = new { number = nextVersion }
        };
        var json = JsonSerializer.Serialize(payload);
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        var resp = await _jiraHttp.PutAsync($"/wiki/api/v2/pages/{pageId}", content);
        if (!resp.IsSuccessStatusCode)
        {
            var errBody = await resp.Content.ReadAsStringAsync();
            throw new HttpRequestException($"Update page failed ({(int)resp.StatusCode} {resp.ReasonPhrase}): {errBody}");
        }

        // A 2xx does NOT guarantee the body was applied: Confluence can accept the request
        // and silently ignore a malformed storage body (e.g. stray leading text before the
        // root element), leaving the page unchanged at its old version. Verify against the
        // authoritative post-update state instead of trusting the request we sent.
        var after = await GetConfluencePageMetaAsync(pageId);
        if (!draft && after.VersionNumber <= meta.VersionNumber)
        {
            throw new HttpRequestException(
                $"Update page reported success but the page version did not advance " +
                $"(still v{after.VersionNumber}); Confluence did not apply the new body. " +
                $"The storage XHTML was likely rejected — check it is valid and does not " +
                $"begin with stray text before the root element.");
        }
        return new ConfluencePageCreated(pageId, after.Title, after.Status, null, after.VersionNumber);
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

    public async Task<JsonElement> TriggerPipelineAsync(string branch, string? selectorType, string? selectorPattern)
    {
        var repoPath = $"/2.0/repositories/{_config.BitbucketWorkspace}/{_config.BitbucketRepo}";

        // pipeline_ref_target with no selector runs the branch's default pipeline;
        // a "custom" selector + pattern runs a named custom: pipeline from bitbucket-pipelines.yml.
        var target = new Dictionary<string, object?>
        {
            ["type"] = "pipeline_ref_target",
            ["ref_type"] = "branch",
            ["ref_name"] = branch,
        };
        if (selectorType is not null)
        {
            target["selector"] = selectorPattern is not null
                ? new { type = selectorType, pattern = selectorPattern }
                : (object)new { type = selectorType };
        }

        var json = JsonSerializer.Serialize(new Dictionary<string, object?> { ["target"] = target });
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        var resp = await _bbHttp.PostAsync($"{repoPath}/pipelines/", content);
        if (!resp.IsSuccessStatusCode)
        {
            var errBody = await resp.Content.ReadAsStringAsync();
            throw new HttpRequestException($"Trigger pipeline failed ({(int)resp.StatusCode} {resp.ReasonPhrase}): {errBody}");
        }
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

    public async Task<List<PullRequestInfo>> GetPullRequestsAsync(string branch, string? state = null)
    {
        // Bitbucket states: OPEN, MERGED, DECLINED, SUPERSEDED. Multiple state params OR them.
        var repoPath = $"/2.0/repositories/{_config.BitbucketWorkspace}/{_config.BitbucketRepo}";
        var q = $"source.branch.name=\"{branch}\"";
        var url = $"{repoPath}/pullrequests?q={Uri.EscapeDataString(q)}&pagelen=20&sort=-updated_on";
        if (!string.IsNullOrEmpty(state))
        {
            foreach (var s in state.Split(',', StringSplitOptions.RemoveEmptyEntries))
                url += $"&state={Uri.EscapeDataString(s.Trim().ToUpper())}";
        }
        else
        {
            // Default: include all terminal states so callers see merged/declined PRs too
            url += "&state=OPEN&state=MERGED&state=DECLINED&state=SUPERSEDED";
        }

        var resp = await _bbHttp.GetAsync(url);
        resp.EnsureSuccessStatusCode();
        var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync());

        var results = new List<PullRequestInfo>();
        foreach (var pr in doc.RootElement.GetProperty("values").EnumerateArray())
        {
            var id = pr.GetProperty("id").GetInt32();
            var prState = pr.GetProperty("state").GetString() ?? "";
            var title = pr.GetProperty("title").GetString() ?? "";
            var href = pr.GetProperty("links").GetProperty("html").GetProperty("href").GetString() ?? "";
            var srcBranch = pr.GetProperty("source").GetProperty("branch").GetProperty("name").GetString() ?? "";
            string? closedOn = pr.TryGetProperty("closed_on", out var co) && co.ValueKind == JsonValueKind.String ? co.GetString() : null;
            results.Add(new PullRequestInfo(id, prState, title, href, srcBranch, closedOn));
        }
        return results;
    }

    // Fetch a single PR's current description (Bitbucket "markdown"-rendered body). Returns it verbatim
    // so it can be round-tripped straight back through pr-edit --description-file without losing edits.
    public async Task<string> GetPullRequestBodyAsync(int prId)
    {
        var repoPath = $"/2.0/repositories/{_config.BitbucketWorkspace}/{_config.BitbucketRepo}";
        var resp = await _bbHttp.GetAsync($"{repoPath}/pullrequests/{prId}");
        if (!resp.IsSuccessStatusCode)
        {
            var errBody = await resp.Content.ReadAsStringAsync();
            throw new HttpRequestException($"Fetch pull request {prId} failed ({(int)resp.StatusCode} {resp.ReasonPhrase}): {errBody}");
        }
        using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync());
        return doc.RootElement.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String
            ? d.GetString() ?? ""
            : "";
    }

    // A PR's comments — general and inline arrive in one feed. An inline comment carries an `inline`
    // object naming the file and line; a reply carries `parent.id`. Deleted comments come back as
    // tombstones with no content, so they're skipped. pagelen=100 covers any realistic review thread
    // in one call (same "fetch a generous page, don't paginate" approach as the pipeline lookups).
    public async Task<List<PullRequestComment>> GetPullRequestCommentsAsync(int prId)
    {
        var repoPath = $"/2.0/repositories/{_config.BitbucketWorkspace}/{_config.BitbucketRepo}";
        var resp = await _bbHttp.GetAsync($"{repoPath}/pullrequests/{prId}/comments?pagelen=100&sort=created_on");
        if (!resp.IsSuccessStatusCode)
        {
            var errBody = await resp.Content.ReadAsStringAsync();
            throw new HttpRequestException($"Fetch comments for pull request {prId} failed ({(int)resp.StatusCode} {resp.ReasonPhrase}): {errBody}");
        }
        using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync());

        var results = new List<PullRequestComment>();
        foreach (var c in doc.RootElement.GetProperty("values").EnumerateArray())
        {
            if (c.TryGetProperty("deleted", out var del) && del.ValueKind == JsonValueKind.True) continue;

            var id = c.GetProperty("id").GetInt32();
            var text = c.TryGetProperty("content", out var content) && content.TryGetProperty("raw", out var raw)
                ? raw.GetString() ?? "" : "";
            var author = c.TryGetProperty("user", out var u) && u.TryGetProperty("display_name", out var dn)
                ? dn.GetString() ?? "" : "";
            string? createdOn = c.TryGetProperty("created_on", out var co) && co.ValueKind == JsonValueKind.String ? co.GetString() : null;
            string? updatedOn = c.TryGetProperty("updated_on", out var uo) && uo.ValueKind == JsonValueKind.String ? uo.GetString() : null;

            string? inlinePath = null;
            int? inlineLine = null;
            if (c.TryGetProperty("inline", out var inl) && inl.ValueKind == JsonValueKind.Object)
            {
                inlinePath = inl.TryGetProperty("path", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
                // `to` is the line in the new file, `from` the old one; both are null for a file-level comment.
                if (inl.TryGetProperty("to", out var to) && to.ValueKind == JsonValueKind.Number) inlineLine = to.GetInt32();
                else if (inl.TryGetProperty("from", out var fr) && fr.ValueKind == JsonValueKind.Number) inlineLine = fr.GetInt32();
            }

            int? parentId = c.TryGetProperty("parent", out var par) && par.TryGetProperty("id", out var pid) && pid.ValueKind == JsonValueKind.Number
                ? pid.GetInt32() : null;

            results.Add(new PullRequestComment(id, author, createdOn, updatedOn, inlinePath, inlineLine, parentId, text));
        }
        return results;
    }

    // Add a general (non-inline) PR comment. Bitbucket takes the body as markdown in content.raw.
    // Pass parentId to reply to an existing comment instead of starting a new thread.
    public async Task<JsonElement> PostPullRequestCommentAsync(int prId, string text, int? parentId = null)
    {
        var repoPath = $"/2.0/repositories/{_config.BitbucketWorkspace}/{_config.BitbucketRepo}";
        var fields = new Dictionary<string, object?> { ["content"] = new { raw = text } };
        if (parentId is not null) fields["parent"] = new { id = parentId.Value };

        var json = JsonSerializer.Serialize(fields);
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        var resp = await _bbHttp.PostAsync($"{repoPath}/pullrequests/{prId}/comments", content);
        if (!resp.IsSuccessStatusCode)
        {
            var errBody = await resp.Content.ReadAsStringAsync();
            throw new HttpRequestException($"Comment on pull request {prId} failed ({(int)resp.StatusCode} {resp.ReasonPhrase}): {errBody}");
        }
        using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync());
        return doc.RootElement.Clone();
    }

    // Bitbucket only auto-applies a repo's default reviewers when a PR is created through the web UI;
    // the create-PR REST endpoint ignores them. To match the UI we resolve them ourselves. The author
    // can't be their own reviewer, so excludeUuid (the PR author) is dropped from the list.
    private async Task<List<object>> GetDefaultReviewersAsync(string? excludeUuid)
    {
        var repoPath = $"/2.0/repositories/{_config.BitbucketWorkspace}/{_config.BitbucketRepo}";
        var reviewers = new List<object>();
        var url = $"{repoPath}/default-reviewers?pagelen=100";
        while (url is not null)
        {
            var resp = await _bbHttp.GetAsync(url);
            if (!resp.IsSuccessStatusCode) break;
            using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync());
            var root = doc.RootElement;
            foreach (var v in root.GetProperty("values").EnumerateArray())
            {
                if (!v.TryGetProperty("uuid", out var uuidEl)) continue;
                var uuid = uuidEl.GetString();
                if (uuid is null || uuid == excludeUuid) continue;
                reviewers.Add(new { uuid });
            }
            url = root.TryGetProperty("next", out var next) && next.ValueKind == JsonValueKind.String ? next.GetString() : null;
        }
        return reviewers;
    }

    public async Task<JsonElement> CreatePullRequestAsync(string source, string dest, string title, string? description, bool draft)
    {
        var repoPath = $"/2.0/repositories/{_config.BitbucketWorkspace}/{_config.BitbucketRepo}";
        var fields = new Dictionary<string, object?>
        {
            ["title"] = title,
            ["source"] = new { branch = new { name = source } },
            ["destination"] = new { branch = new { name = dest } },
            ["draft"] = draft,
        };
        if (description is not null) fields["description"] = description;

        var json = JsonSerializer.Serialize(fields);
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        var resp = await _bbHttp.PostAsync($"{repoPath}/pullrequests", content);
        if (!resp.IsSuccessStatusCode)
        {
            var errBody = await resp.Content.ReadAsStringAsync();
            throw new HttpRequestException($"Create pull request failed ({(int)resp.StatusCode} {resp.ReasonPhrase}): {errBody}");
        }
        var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync());
        var created = doc.RootElement.Clone();

        // The create endpoint ignores default reviewers, so attach them in a follow-up PUT now that the
        // create response has told us the author's uuid (the one account we must exclude). Best-effort:
        // a reviewer-attach failure shouldn't sink an otherwise-created PR — report the PR either way.
        var prId = created.GetProperty("id").GetInt32();
        string? authorUuid = created.TryGetProperty("author", out var a) && a.TryGetProperty("uuid", out var au)
            ? au.GetString() : null;
        var reviewers = await GetDefaultReviewersAsync(authorUuid);
        if (reviewers.Count > 0)
        {
            try { return await SetPullRequestReviewersAsync(prId, reviewers); }
            catch (HttpRequestException ex) { Console.Error.WriteLine($"warning: PR {prId} created but adding reviewers failed: {ex.Message}"); }
        }
        return created;
    }

    // Sets the reviewers on an existing PR via PUT. Bitbucket's PUT clears omitted fields, so we re-send
    // the current title/description/draft alongside the new reviewers to leave everything else intact.
    private async Task<JsonElement> SetPullRequestReviewersAsync(int id, List<object> reviewers)
    {
        var repoPath = $"/2.0/repositories/{_config.BitbucketWorkspace}/{_config.BitbucketRepo}";
        var getResp = await _bbHttp.GetAsync($"{repoPath}/pullrequests/{id}");
        getResp.EnsureSuccessStatusCode();
        using var current = await JsonDocument.ParseAsync(await getResp.Content.ReadAsStreamAsync());
        var root = current.RootElement;

        var fields = new Dictionary<string, object?>
        {
            ["title"] = root.GetProperty("title").GetString(),
            ["description"] = root.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "",
            ["draft"] = root.TryGetProperty("draft", out var dr) && dr.GetBoolean(),
            ["reviewers"] = reviewers,
        };
        var content = new StringContent(JsonSerializer.Serialize(fields), Encoding.UTF8, "application/json");
        var resp = await _bbHttp.PutAsync($"{repoPath}/pullrequests/{id}", content);
        if (!resp.IsSuccessStatusCode)
        {
            var errBody = await resp.Content.ReadAsStringAsync();
            throw new HttpRequestException($"Set reviewers on PR {id} failed ({(int)resp.StatusCode} {resp.ReasonPhrase}): {errBody}");
        }
        var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync());
        return doc.RootElement;
    }

    public async Task<JsonElement> UpdatePullRequestAsync(int id, string? title, string? description, bool? draft)
    {
        var repoPath = $"/2.0/repositories/{_config.BitbucketWorkspace}/{_config.BitbucketRepo}";

        // Bitbucket's PUT requires title (and silently clears omitted fields like description),
        // so fetch the current PR and merge: only the explicitly-passed fields change.
        var getResp = await _bbHttp.GetAsync($"{repoPath}/pullrequests/{id}");
        if (!getResp.IsSuccessStatusCode)
        {
            var errBody = await getResp.Content.ReadAsStringAsync();
            throw new HttpRequestException($"Fetch pull request {id} failed ({(int)getResp.StatusCode} {getResp.ReasonPhrase}): {errBody}");
        }
        using var current = await JsonDocument.ParseAsync(await getResp.Content.ReadAsStreamAsync());
        var root = current.RootElement;

        var fields = new Dictionary<string, object?>
        {
            ["title"] = title ?? root.GetProperty("title").GetString(),
            ["description"] = description ?? (root.TryGetProperty("description", out var d) ? d.GetString() ?? "" : ""),
            ["draft"] = draft ?? (root.TryGetProperty("draft", out var dr) && dr.GetBoolean()),
        };

        var json = JsonSerializer.Serialize(fields);
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        var resp = await _bbHttp.PutAsync($"{repoPath}/pullrequests/{id}", content);
        if (!resp.IsSuccessStatusCode)
        {
            var errBody = await resp.Content.ReadAsStringAsync();
            throw new HttpRequestException($"Update pull request {id} failed ({(int)resp.StatusCode} {resp.ReasonPhrase}): {errBody}");
        }
        var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync());
        return doc.RootElement;
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
public record PullRequestInfo(int Id, string State, string Title, string Url, string SourceBranch, string? ClosedOn);
public record PullRequestComment(int Id, string Author, string? CreatedOn, string? UpdatedOn, string? InlinePath, int? InlineLine, int? ParentId, string Text);
public record ConfluencePageMeta(string Id, string Title, string SpaceId, string Status, int VersionNumber, string? ParentId);
public record ConfluencePageCreated(string Id, string Title, string Status, string? WebUi, int? Version = null);
public record ConfluenceAttachment(string Id, string Title, string MediaType, string? DownloadLink);
public record ConfluenceSearchHit(string Id, string Title, string? SpaceContainer, string? Excerpt);
