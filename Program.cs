using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using AtlCli;

var config = new ConfigurationBuilder()
    .AddUserSecrets<AtlassianConfig>()
    .Build();

var atlConfig = new AtlassianConfig();
config.GetSection("Atlassian").Bind(atlConfig);

if (string.IsNullOrEmpty(atlConfig.Email) || string.IsNullOrEmpty(atlConfig.JiraBaseUrl))
{
    Console.Error.WriteLine("Missing user-secrets. See README.md for setup instructions.");
    return 1;
}

var asCurl = args.Contains("--as-curl");
var filteredArgs = args.Where(a => a != "--as-curl").ToArray();

var client = new AtlassianClient(atlConfig, asCurl);

if (filteredArgs.Length == 0)
{
    PrintUsage();
    return 1;
}

var command = filteredArgs[0].ToLower();

try
{
    return command switch
    {
        "jira" => await HandleJira(filteredArgs[1..]),
        "bb" => await HandleBitbucket(filteredArgs[1..]),
        "wiki" => await HandleWiki(filteredArgs[1..]),
        _ => PrintUsage()
    };
}
catch (HttpRequestException ex)
{
    Console.Error.WriteLine($"API error: {ex.Message}");
    return 1;
}
catch (ArgumentException ex)
{
    Console.Error.WriteLine($"Invalid input: {ex.Message}");
    return 1;
}

async Task<int> HandleJira(string[] args)
{
    if (args.Length == 0) return PrintUsage();

    var sub = args[0].ToLower();
    var rest = args[1..];

    switch (sub)
    {
        case "status" when rest.Length > 0:
            var statuses = await client.GetIssueStatusesAsync(rest);
            Console.WriteLine(JsonSerializer.Serialize(statuses));
            return 0;

        case "issue" when rest.Length == 1:
            var issue = await client.GetIssueAsync(rest[0]);
            Console.WriteLine(JsonSerializer.Serialize(issue, new JsonSerializerOptions { WriteIndented = true }));
            return 0;

        case "transition" when rest.Length == 2:
            await client.TransitionIssueAsync(rest[0], rest[1]);
            Console.WriteLine($"{rest[0]} -> {rest[1]}");
            return 0;

        case "create":
        {
            string? project = null, type = null, summary = null, assignee = null;
            for (int i = 0; i < rest.Length; i++)
            {
                switch (rest[i])
                {
                    case "--project" when i + 1 < rest.Length: project = rest[++i]; break;
                    case "--type" when i + 1 < rest.Length: type = rest[++i]; break;
                    case "--summary" when i + 1 < rest.Length: summary = rest[++i]; break;
                    case "--assignee" when i + 1 < rest.Length: assignee = rest[++i]; break;
                }
            }
            if (project is null || type is null || summary is null)
            {
                Console.Error.WriteLine("Usage: atl-cli jira create --project KEY --type Task --summary \"...\" [--assignee @me]");
                return 1;
            }
            var created = await client.CreateIssueAsync(project, type, summary, assignee);
            Console.WriteLine(JsonSerializer.Serialize(created, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }

        case "comment" when rest.Length >= 2:
        {
            var key = rest[0];
            string? text = null;
            string? adfFile = null;
            for (int i = 1; i < rest.Length; i++)
            {
                if (rest[i] == "--body-file" && i + 1 < rest.Length)
                {
                    var file = rest[++i];
                    if (!File.Exists(file)) { Console.Error.WriteLine($"Body file not found: {file}"); return 1; }
                    text = await File.ReadAllTextAsync(file);
                }
                else if (rest[i] == "--adf-file" && i + 1 < rest.Length)
                {
                    adfFile = rest[++i];
                    if (!File.Exists(adfFile)) { Console.Error.WriteLine($"ADF file not found: {adfFile}"); return 1; }
                }
                else if (!rest[i].StartsWith("--"))
                {
                    text ??= rest[i];
                }
            }

            JsonElement posted;
            if (adfFile is not null)
            {
                var adfJson = await File.ReadAllTextAsync(adfFile);
                posted = await client.CreateCommentAdfAsync(key, adfJson);
            }
            else if (!string.IsNullOrEmpty(text))
            {
                posted = await client.CreateCommentAsync(key, text);
            }
            else
            {
                Console.Error.WriteLine("Usage: atl-cli jira comment KEY \"text\"  |  KEY --body-file FILE (plain text)  |  KEY --adf-file FILE (raw ADF JSON)");
                return 1;
            }
            Console.WriteLine(JsonSerializer.Serialize(posted, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }

        case "describe" when rest.Length >= 2:
        {
            var key = rest[0];
            string? text = null;
            string? adfFile = null;
            for (int i = 1; i < rest.Length; i++)
            {
                if (rest[i] == "--body-file" && i + 1 < rest.Length)
                {
                    var file = rest[++i];
                    if (!File.Exists(file)) { Console.Error.WriteLine($"Body file not found: {file}"); return 1; }
                    text = await File.ReadAllTextAsync(file);
                }
                else if (rest[i] == "--adf-file" && i + 1 < rest.Length)
                {
                    adfFile = rest[++i];
                    if (!File.Exists(adfFile)) { Console.Error.WriteLine($"ADF file not found: {adfFile}"); return 1; }
                }
                else if (!rest[i].StartsWith("--"))
                {
                    text ??= rest[i];
                }
            }

            if (adfFile is not null)
            {
                var adfJson = await File.ReadAllTextAsync(adfFile);
                await client.SetDescriptionAdfAsync(key, adfJson);
            }
            else if (text is not null)
            {
                await client.SetDescriptionAsync(key, text);
            }
            else
            {
                Console.Error.WriteLine("Usage: atl-cli jira describe KEY \"text\"  |  KEY --body-file FILE (plain text)  |  KEY --adf-file FILE (raw ADF JSON)");
                return 1;
            }
            Console.WriteLine($"{key} description updated");
            return 0;
        }

        case "link" when rest.Length >= 2:
        {
            var fromKey = rest[0];
            var toKey = rest[1];
            string linkType = "Relates";
            for (int i = 2; i < rest.Length; i++)
            {
                if (rest[i] == "--type" && i + 1 < rest.Length) linkType = rest[++i];
            }
            await client.LinkIssuesAsync(fromKey, toKey, linkType);
            Console.WriteLine($"{fromKey} {linkType} {toKey}");
            return 0;
        }

        default:
            return PrintUsage();
    }
}

async Task<int> HandleBitbucket(string[] args)
{
    if (args.Length == 0) return PrintUsage();

    var sub = args[0].ToLower();
    var rest = args[1..];

    switch (sub)
    {
        case "pipeline" when rest.Length > 0:
            var statuses = await client.GetPipelineStatusesAsync(rest);
            Console.WriteLine(JsonSerializer.Serialize(statuses));
            return 0;

        case "pr" when rest.Length >= 1:
            var prBranch = rest[0];
            string? prState = null;
            for (int i = 1; i < rest.Length; i++)
            {
                if (rest[i] == "--state" && i + 1 < rest.Length) { prState = rest[++i]; }
                else if (rest[i].StartsWith("--state=")) { prState = rest[i].Substring("--state=".Length); }
            }
            var prs = await client.GetPullRequestsAsync(prBranch, prState);
            Console.WriteLine(JsonSerializer.Serialize(prs, new JsonSerializerOptions { WriteIndented = true }));
            return 0;

        case "pr-body" when rest.Length >= 1:
        {
            if (!int.TryParse(rest[0], out int bodyPrId))
            {
                Console.Error.WriteLine("Usage: atl-cli bb pr-body PR_ID   (prints the PR's current description to stdout)");
                return 1;
            }
            var body = await client.GetPullRequestBodyAsync(bodyPrId);
            Console.WriteLine(body);
            return 0;
        }

        case "pr-create":
        {
            string? source = null, dest = "develop", title = null, description = null;
            bool draft = false;
            for (int i = 0; i < rest.Length; i++)
            {
                switch (rest[i])
                {
                    case "--source" when i + 1 < rest.Length: source = rest[++i]; break;
                    case "--dest" when i + 1 < rest.Length: dest = rest[++i]; break;
                    case "--title" when i + 1 < rest.Length: title = rest[++i]; break;
                    case "--description" when i + 1 < rest.Length: description = rest[++i]; break;
                    case "--description-file" when i + 1 < rest.Length:
                    {
                        var file = rest[++i];
                        if (!File.Exists(file)) { Console.Error.WriteLine($"Description file not found: {file}"); return 1; }
                        description = await File.ReadAllTextAsync(file);
                        break;
                    }
                    case "--draft": draft = true; break;
                }
            }
            if (source is null || title is null)
            {
                Console.Error.WriteLine("Usage: atl-cli bb pr-create --source BRANCH --title \"...\" [--dest develop] [--description \"...\" | --description-file FILE] [--draft]");
                return 1;
            }
            var created = await client.CreatePullRequestAsync(source, dest, title, description, draft);
            Console.WriteLine(JsonSerializer.Serialize(created, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }

        case "pr-edit" when rest.Length >= 1:
        {
            if (!int.TryParse(rest[0], out int prId))
            {
                Console.Error.WriteLine("Usage: atl-cli bb pr-edit PR_ID [--title \"...\"] [--description \"...\" | --description-file FILE] [--draft true|false]");
                return 1;
            }
            string? editTitle = null, editDescription = null;
            bool? editDraft = null;
            for (int i = 1; i < rest.Length; i++)
            {
                switch (rest[i])
                {
                    case "--title" when i + 1 < rest.Length: editTitle = rest[++i]; break;
                    case "--description" when i + 1 < rest.Length: editDescription = rest[++i]; break;
                    case "--description-file" when i + 1 < rest.Length:
                    {
                        var file = rest[++i];
                        if (!File.Exists(file)) { Console.Error.WriteLine($"Description file not found: {file}"); return 1; }
                        editDescription = await File.ReadAllTextAsync(file);
                        break;
                    }
                    case "--draft" when i + 1 < rest.Length && bool.TryParse(rest[i + 1], out var dv): editDraft = dv; i++; break;
                }
            }
            var updated = await client.UpdatePullRequestAsync(prId, editTitle, editDescription, editDraft);
            Console.WriteLine(JsonSerializer.Serialize(updated, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }

        case "pipeline-log" when rest.Length == 1:
            var failure = await client.GetPipelineFailureAsync(rest[0]);
            if (failure is null)
            {
                Console.WriteLine($"No failed pipeline found for {rest[0]}");
                return 0;
            }
            Console.WriteLine(JsonSerializer.Serialize(failure, new JsonSerializerOptions { WriteIndented = true }));
            return 0;

        case "pipeline-run" when rest.Length >= 1:
        {
            var runBranch = rest[0];
            string? selType = null, selPattern = null;
            for (int i = 1; i < rest.Length; i++)
            {
                string? selVal = null;
                if (rest[i] == "--selector" && i + 1 < rest.Length) selVal = rest[++i];
                else if (rest[i].StartsWith("--selector=")) selVal = rest[i].Substring("--selector=".Length);
                if (selVal is not null)
                {
                    var idx = selVal.IndexOf(':');
                    if (idx > 0) { selType = selVal.Substring(0, idx); selPattern = selVal.Substring(idx + 1); }
                    else { selType = selVal; }
                }
            }
            var triggered = await client.TriggerPipelineAsync(runBranch, selType, selPattern);
            Console.WriteLine(JsonSerializer.Serialize(triggered, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }

        default:
            return PrintUsage();
    }
}

async Task<int> HandleWiki(string[] args)
{
    if (args.Length == 0) return PrintUsage();

    var sub = args[0].ToLower();
    var rest = args[1..];

    switch (sub)
    {
        case "page" when rest.Length >= 1:
            {
                var pageId = ExtractPageId(rest[0]);
                bool asRaw = rest.Contains("--raw");
                var content = await client.GetConfluencePageAsync(pageId, asText: !asRaw);
                Console.WriteLine(content);
                return 0;
            }

        case "create" when rest.Length >= 3:
            {
                var parentId = ExtractPageId(rest[0]);
                var title = rest[1];
                var bodyFile = rest[2];
                bool draft = rest.Contains("--draft");

                if (!File.Exists(bodyFile))
                {
                    Console.Error.WriteLine($"Body file not found: {bodyFile}");
                    return 1;
                }
                var storageBody = await File.ReadAllTextAsync(bodyFile);
                var parent = await client.GetConfluencePageMetaAsync(parentId);
                var created = await client.CreateConfluencePageAsync(parent.SpaceId, parentId, title, storageBody, draft);
                Console.WriteLine(JsonSerializer.Serialize(created, new JsonSerializerOptions { WriteIndented = true }));
                return 0;
            }

        case "search" when rest.Length >= 1:
            {
                string text = rest[0];
                int limit = 25;
                for (int i = 1; i < rest.Length; i++)
                {
                    if (rest[i] == "--limit" && i + 1 < rest.Length && int.TryParse(rest[++i], out int n)) limit = n;
                }
                var hits = await client.SearchConfluenceAsync(text, limit);
                Console.WriteLine(JsonSerializer.Serialize(hits, new JsonSerializerOptions { WriteIndented = true }));
                return 0;
            }

        case "attachments" when rest.Length >= 1:
            {
                var pageId = ExtractPageId(rest[0]);
                var atts = await client.ListConfluenceAttachmentsAsync(pageId);
                Console.WriteLine(JsonSerializer.Serialize(atts, new JsonSerializerOptions { WriteIndented = true }));
                return 0;
            }

        case "download" when rest.Length >= 2:
            {
                var pageId = ExtractPageId(rest[0]);
                var pattern = rest[1];
                var outDir = rest.Length >= 3 && !rest[2].StartsWith("--") ? rest[2] : ".";
                var atts = await client.ListConfluenceAttachmentsAsync(pageId);
                int n = 0;
                foreach (var a in atts)
                {
                    if (!a.Title.Contains(pattern, StringComparison.OrdinalIgnoreCase)) continue;
                    if (string.IsNullOrEmpty(a.DownloadLink)) continue;
                    var local = System.IO.Path.Combine(outDir, a.Title);
                    Console.WriteLine($"downloading: {a.Title} -> {local}");
                    await client.DownloadConfluenceAttachmentAsync(a.DownloadLink!, local);
                    n++;
                }
                Console.WriteLine($"Downloaded {n} attachment(s).");
                return 0;
            }

        case "diagram" when rest.Length >= 1:
            {
                var pageId = ExtractPageId(rest[0]);
                // Either accept a diagram name or auto-derive from the page body.
                string? diagramName = rest.Length >= 2 && !rest[1].StartsWith("--") ? rest[1] : null;
                if (diagramName is null)
                {
                    var raw = await client.GetConfluencePageAsync(pageId, asText: false);
                    var m = Regex.Match(raw, @"name=""diagramName"">([^<]+)<");
                    if (m.Success) diagramName = m.Groups[1].Value;
                }
                if (string.IsNullOrEmpty(diagramName))
                {
                    Console.Error.WriteLine("Could not derive diagram name from the page; pass it as the second argument.");
                    return 1;
                }
                var xml = await client.GetDrawioDiagramAsync(pageId, diagramName);
                Console.Write(xml);
                return 0;
            }

        case "update" when rest.Length >= 3:
            {
                var pageId = ExtractPageId(rest[0]);
                var title = rest[1];
                var bodyFile = rest[2];
                bool draft = rest.Contains("--draft");

                if (!File.Exists(bodyFile))
                {
                    Console.Error.WriteLine($"Body file not found: {bodyFile}");
                    return 1;
                }
                var storageBody = await File.ReadAllTextAsync(bodyFile);
                var updated = await client.UpdateConfluencePageAsync(pageId, title, storageBody, draft);
                Console.WriteLine(JsonSerializer.Serialize(updated, new JsonSerializerOptions { WriteIndented = true }));
                return 0;
            }

        default:
            return PrintUsage();
    }
}

static string ExtractPageId(string idOrUrl)
{
    if (idOrUrl.Contains("/pages/"))
    {
        var match = Regex.Match(idOrUrl, @"/pages/(?:edit-v2/)?(\d+)");
        if (match.Success) return match.Groups[1].Value;
    }
    return idOrUrl;
}

int PrintUsage()
{
    Console.Error.WriteLine("""
    Usage: atl-cli <service> <command> [args...]

    Jira:
      atl-cli jira status PROJ-101 [PROJ-102 ...]    Batch ticket statuses (JSON)
      atl-cli jira issue PROJ-101                    Full issue details
      atl-cli jira transition PROJ-101 "In Progress" Transition ticket status
      atl-cli jira create --project KEY --type Task --summary "..." [--assignee @me]
                                                     Create an issue (prints JSON incl. key)
      atl-cli jira comment PROJ-101 "text"           Add a comment (plain text -> ADF)
      atl-cli jira comment PROJ-101 --body-file FILE Add a comment from a plain-text file
      atl-cli jira comment PROJ-101 --adf-file FILE  Add a comment from a raw ADF JSON doc (rich formatting)
      atl-cli jira describe PROJ-101 "text"          Set the description (plain text -> ADF; replaces existing)
      atl-cli jira describe PROJ-101 --body-file FILE Set the description from a plain-text file
      atl-cli jira describe PROJ-101 --adf-file FILE Set the description from a raw ADF JSON doc (rich formatting)
      atl-cli jira link PROJ-101 PROJ-102 [--type Relates]
                                                     Link two issues ("relates to" by default)

    Bitbucket:
      atl-cli bb pipeline PROJ-101 [PROJ-102 ...]    Pipeline status per branch (JSON)
      atl-cli bb pipeline-log PROJ-101                Failed step + error details
      atl-cli bb pipeline-run BRANCH [--selector custom:PATTERN]
                                                     Trigger a pipeline (default branch pipeline, or a custom: one)
      atl-cli bb pr PS-2904 [--state OPEN|MERGED|...] PRs for a source branch (JSON)
      atl-cli bb pr-body PR_ID                        Print a PR's current description (for get-then-edit round-tripping)
      atl-cli bb pr-create --source BRANCH --title "..." [--dest develop] [--description... | --description-file FILE] [--draft]
                                                     Create a pull request (prints JSON incl. id)
      atl-cli bb pr-edit PR_ID [--title "..."] [--description... | --description-file FILE] [--draft true|false]
                                                     Update a PR's title/description/draft state

    Confluence:
      atl-cli wiki page <id-or-url>                  Get page content (text)
      atl-cli wiki page <id-or-url> --raw            Get page content (raw HTML)
      atl-cli wiki create <parent-id> <title> <body.xhtml> [--draft]
                                                     Create child page (storage XHTML body)
      atl-cli wiki update <page-id> <title> <body.xhtml> [--draft]
                                                     Update existing page
      atl-cli wiki diagram <page-id> [diagram-name]  Fetch the draw.io diagram XML
      atl-cli wiki search "text" [--limit N]         Search Confluence pages (JSON results)

    Options:
      --as-curl                                      Print the curl command instead of executing
    """);
    return 1;
}
