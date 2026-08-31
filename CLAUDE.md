# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What This Is

A lightweight .NET CLI for Atlassian APIs (Jira, Bitbucket, Confluence). Outputs JSON for easy scripting and dashboard integration.

## Build & Run

```bash
# Build and install as global tool
dotnet pack
dotnet tool install --global --add-source ./bin/Release/ AtlCli

# Then use from anywhere
atl-cli jira status PROJ-101
atl-cli bb pipeline PROJ-101
atl-cli wiki page 12345
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
- A pipeline parked on a manual gate reports pipeline-level `state.stage.name == "PAUSED"`. That is what `pipeline-watch` keys on. A step's `trigger.type == "manual"` plus `state.name == "PENDING"` is NOT a gate: every later manual step looks like that from the moment the pipeline starts.
- The steps endpoint pages (default 10, max 100). Deploy pipelines run to 45+ steps, so step scans must follow `next` — see `GetPipelineStepsAsync`.
- The step-log endpoint serves plain text and answers `Accept: application/json` with 406, then 307s to a storage host. It needs its own `Accept: */*` rather than the Bitbucket client's JSON default.
- The `pipeline-log` command parses raw build logs for `error CS` / `error TS` patterns to extract compiler errors.
- Confluence `wiki page` converts storage-format HTML to markdown-like text by default; `--raw` returns the original HTML.
