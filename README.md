# atl-cli

Lightweight CLI for Atlassian APIs (Jira, Bitbucket). Outputs JSON for easy scripting.

## What does this cli provide? - 4 things

1) READ Jira ticket
2) WRITE Jira ticket status
3) READ bitbucket pipeline status 
4) READ wiki page


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

### 3. Build

```bash
dotnet build
```

## Usage

```bash
# Run from the project directory
cd ~/prj/atl-cli

# Jira — batch ticket statuses
dotnet run -- jira status PROJ-101 PROJ-102 PROJ-103
# {"PROJ-101":"PR Review","PROJ-102":"Done","PROJ-103":"Done"}

# Jira — full issue details
dotnet run -- jira issue PROJ-101

# Jira — transition a ticket
dotnet run -- jira transition PROJ-101 "In Progress"

# Bitbucket — pipeline status per branch
dotnet run -- bb pipeline PROJ-101 PROJ-102
# {"PROJ-101":{"Status":"FAILED","BuildNumber":7633}}
```

## Integration

Can be integrated with shell scripts to show pipeline and Jira status.
