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

        case "pipeline-log" when rest.Length == 1:
            var failure = await client.GetPipelineFailureAsync(rest[0]);
            if (failure is null)
            {
                Console.WriteLine($"No failed pipeline found for {rest[0]}");
                return 0;
            }
            Console.WriteLine(JsonSerializer.Serialize(failure, new JsonSerializerOptions { WriteIndented = true }));
            return 0;

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
            var pageId = rest[0];
            // Support full URLs: extract page ID from /pages/12345/
            if (pageId.Contains("/pages/"))
            {
                var match = Regex.Match(pageId, @"/pages/(\d+)");
                if (match.Success) pageId = match.Groups[1].Value;
            }
            bool asRaw = rest.Contains("--raw");
            var content = await client.GetConfluencePageAsync(pageId, asText: !asRaw);
            Console.WriteLine(content);
            return 0;

        default:
            return PrintUsage();
    }
}

int PrintUsage()
{
    Console.Error.WriteLine("""
    Usage: atl-cli <service> <command> [args...]

    Jira:
      atl-cli jira status PROJ-101 [PROJ-102 ...]    Batch ticket statuses (JSON)
      atl-cli jira issue PROJ-101                    Full issue details
      atl-cli jira transition PROJ-101 "In Progress" Transition ticket status

    Bitbucket:
      atl-cli bb pipeline PROJ-101 [PROJ-102 ...]    Pipeline status per branch (JSON)
      atl-cli bb pipeline-log PROJ-101                Failed step + error details

    Confluence:
      atl-cli wiki page <id-or-url>                  Get page content (text)
      atl-cli wiki page <id-or-url> --raw            Get page content (raw HTML)

    Options:
      --as-curl                                      Print the curl command instead of executing
    """);
    return 1;
}
