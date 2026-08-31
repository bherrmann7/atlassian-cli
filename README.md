# atl-cli

Lightweight CLI for Atlassian APIs (Jira, Bitbucket). Outputs JSON for easy scripting.

## What does this cli provide? - 4 things

1) READ Jira ticket
2) WRITE Jira ticket status
3) READ bitbucket pipeline status 
4) READ wiki page

```bash
$ atl-cli jira issue PROJ-101
{
  "key": "PROJ-101",
  "fields": {
    "summary": "Fix login timeout on mobile",
    "status": { "name": "In Progress" }
  }
}
```

## How

Atlassian provides REST APIs for their products. This CLI wraps those APIs with simple commands and handles authentication via stored credentials.

- [Jira REST API v3](https://developer.atlassian.com/cloud/jira/platform/rest/v3/)
- [Confluence REST API v2](https://developer.atlassian.com/cloud/confluence/rest/v2/)
- [Bitbucket REST API 2.0](https://developer.atlassian.com/cloud/bitbucket/rest/)

Once you have credentials, you can access these APIs directly with curl. This CLI includes an `--as-curl` option to show the raw curl command for any request, so you can see exactly what's being called or use it outside the CLI.

## Setup

### 1. Create API Tokens

Two tokens are needed because Atlassian's Bitbucket and Jira/Confluence use different authentication systems. Go to [Atlassian API Tokens](https://id.atlassian.com/manage-profile/security/api-tokens) to create both.

**Jira / Confluence** — Create a classic API token (the "Create API token" button, no scopes needed). This token inherits your account permissions.

**Bitbucket** — Create a scoped API token ("Create API token with scopes"). Select these Bitbucket read scopes:
- `read:pipeline:bitbucket`
- `read:pullrequest:bitbucket`
- `read:repository:bitbucket`

### 2. Configure User Secrets

| Setting | Where to find it |
|---------|-----------------|
| `Email` | The email address you use to log in to Atlassian |
| `JiraToken` | Created in step 1 (classic API token) |
| `BitbucketToken` | Created in step 1 (scoped API token) |
| `JiraBaseUrl` | Your Atlassian site URL — visible in the browser when you open Jira (e.g., `https://yoursite.atlassian.net`) |
| `BitbucketWorkspace` | The slug in your Bitbucket URL: `bitbucket.org/{workspace}/{repo}` |
| `BitbucketRepo` | The repo slug in the same URL: `bitbucket.org/{workspace}/{repo}` |

```bash
cd ~/prj/atl-cli

dotnet user-secrets set "Atlassian:Email" "you@example.com"
dotnet user-secrets set "Atlassian:JiraToken" "ATATT3x..."
dotnet user-secrets set "Atlassian:BitbucketToken" "ATATT3x..."
dotnet user-secrets set "Atlassian:JiraBaseUrl" "https://yoursite.atlassian.net"
dotnet user-secrets set "Atlassian:BitbucketWorkspace" "your-workspace"
dotnet user-secrets set "Atlassian:BitbucketRepo" "your-repo"
```

User secrets are stored outside the project directory (in `~/.microsoft/usersecrets/`) and are never committed to source control. Works on macOS, Windows, and Linux.

### 3. Install

```bash
cd ~/prj/atl-cli
dotnet pack
dotnet tool install --global --add-source ./bin/Release/ AtlCli
```

To update after making changes:

```bash
dotnet pack && dotnet tool update --global --add-source ./bin/Release/ AtlCli
```

## Usage

```bash
# Jira — batch ticket statuses
atl-cli jira status PROJ-101 PROJ-102 PROJ-103
# {"PROJ-101":"PR Review","PROJ-102":"Done","PROJ-103":"Done"}

# Jira — full issue details
atl-cli jira issue PROJ-101
# Story points come back as fields.storyPoints (null when unset):
atl-cli jira issue PROJ-101 | jq .fields.storyPoints

# Jira — read a ticket as markdown, edit it, put it back
atl-cli jira issue PROJ-101 --text > ticket.md   # description + comments, rendered
$EDITOR ticket.md
atl-cli jira describe PROJ-101 --md-file ticket.md
atl-cli jira comment PROJ-101 --md-file note.md
# Markdown subset: headings, paragraphs, bullet/ordered lists, tables, fenced code,
# block quotes, rules, and inline `code`, **bold**, *italic*, [links](url).
# The conversion round-trips: --text of a doc written with --md-file returns the same markdown.
# Mentions render as @Name when reading; writing one still needs --adf-file with an accountId.

# Jira — attach files (screenshots, logs, exports)
atl-cli jira attach PROJ-101 screenshot.png
atl-cli jira attach PROJ-101 before.png after.png run.log
# Images are sent with a real image content type, so Jira renders them inline in the ticket
# rather than listing them as anonymous downloads. Every file is checked to exist before any
# of them upload, so a typo cannot leave a half-attached ticket behind.

# Jira — search by JQL
atl-cli jira search "project = PROJ AND status = 'In Progress'"
atl-cli jira search "assignee = currentUser() ORDER BY created DESC" --limit 5
# [{"Key":"PROJ-101","Summary":"...","Status":"In Progress","IssueType":"Task","Assignee":"..."}]
# --fields overrides what is requested (default: summary,status,issuetype,assignee).
# A malformed query returns Jira's parser error rather than an empty result.

# Jira — sprint assignment
atl-cli jira sprint --list PROJ                 # active/future sprints, with their board
atl-cli jira sprint PROJ-101 --current          # move into the active sprint
atl-cli jira sprint PROJ-101 --id 9159          # move into a specific sprint
# Adding an issue to a sprint needs the Schedule Issues permission on the project;
# without it Jira answers 403 and says so.

# Bitbucket — upload a file to repo Downloads, to embed an image in a PR description
atl-cli bb upload screenshot.png
# Prints the URL and the ready-to-paste markdown. A pull request has no attachment concept:
# a description can only reference an image already hosted somewhere the reviewer can reach,
# and Downloads is the only such place inside the repository.

# Bitbucket — trigger a pipeline (default branch pipeline, or a named custom: one)
atl-cli bb pipeline-run PROJ-101
atl-cli bb pipeline-run PROJ-101 --selector custom:deploy-to-production
# Prints the new pipeline JSON (build_number, uuid, state). Requires pipeline:write on the token.

# Bitbucket — watch a pipeline until it ends or stops at a manual gate
atl-cli bb pipeline-watch PROJ-101
atl-cli bb pipeline-watch --build 11777 --interval 30
# Streams one JSON line per state change; exits 0 successful, 2 failed, 75 waiting on a gate.
# --wait-through-gates keeps polling past a gate instead of exiting.
# Nag out loud on a Mac until someone clicks Deploy:
#   while true; do
#     atl-cli bb pipeline-watch PROJ-101; status=$?
#     [ $status -eq 75 ] || break
#     say "Need to deploy"; sleep 10
#   done

# Jira — transition a ticket
atl-cli jira transition PROJ-101 "In Progress"

# Jira — rename a ticket (summary is a plain string, not ADF)
atl-cli jira summary PROJ-101 "New title goes here"
atl-cli jira summary PROJ-101 --body-file /tmp/title.txt

# Bitbucket — pipeline status per branch
atl-cli bb pipeline PROJ-101 PROJ-102
# {"PROJ-101":{"Status":"FAILED","BuildNumber":7633}}
```

## Options

### `--as-curl`

Print the equivalent curl command instead of executing the API call. Works with any command.

```bash
atl-cli jira issue PROJ-101 --as-curl
# curl -s \
#   -H 'Authorization: Basic ...' \
#   -H 'Accept: application/json' \
#   'https://yoursite.atlassian.net/rest/api/3/issue/PROJ-101?fields=status,summary'
```

## Integration

Can be integrated with shell scripts to show pipeline and Jira status.
