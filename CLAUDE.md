# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What This Is

A lightweight .NET CLI for Atlassian APIs (Jira, Bitbucket, Confluence). Outputs JSON for easy scripting and dashboard integration.

## Build & Run

```bash
dotnet build
dotnet run -- jira status PS-2811
dotnet run -- bb pipeline PS-2811
dotnet run -- wiki page 12345
```

No tests exist in this project. Configuration uses `dotnet user-secrets` (see README.md for setup).

## Architecture

Single-project CLI (`net10.0`), three files:

- **Program.cs** — Entry point with top-level statements. Parses CLI args into three command groups (`jira`, `bb`, `wiki`), each handled by a local function. Returns exit codes.
- **AtlassianClient.cs** — HTTP client wrapper with three `HttpClient` instances (Jira, Confluence, Bitbucket) using Basic Auth. Contains all API calls and an HTML-to-text converter for Confluence pages.
- **AtlassianConfig.cs** — POCO bound from `Atlassian:*` user secrets via `Microsoft.Extensions.Configuration`.

## Key Details

- Jira and Confluence share the same base URL and token (`JiraToken`). Bitbucket uses a separate scoped token (`BitbucketToken`).
- Pipeline lookups fetch the 50 most recent pipelines and filter client-side by branch name — there is no server-side branch filter.
- The `pipeline-log` command parses raw build logs for `error CS` / `error TS` patterns to extract compiler errors.
- Confluence `wiki page` converts storage-format HTML to markdown-like text by default; `--raw` returns the original HTML.
