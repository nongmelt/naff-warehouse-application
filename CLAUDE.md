# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Context

Warehouse window application for in-house online commercial stock tracking using .NET framework inside app folder. The user will scan the tracking number to trigger the webcam record. The webcam will be recorded until the same tracking number is scanned again. Then, the webhook to n8n will be sent and keep standby for the next scan.

## MCP Tools: code-review-graph

**IMPORTANT: This project has a knowledge graph. ALWAYS use the
code-review-graph MCP tools BEFORE using Grep/Glob/Read to explore
the codebase.** The graph is faster, cheaper (fewer tokens), and gives
you structural context (callers, dependents, test coverage) that file
scanning cannot.

### When to use graph tools FIRST

- **Exploring code**: `semantic_search_nodes` or `query_graph` instead of Grep
- **Understanding impact**: `get_impact_radius` instead of manually tracing imports
- **Code review**: `detect_changes` + `get_review_context` instead of reading entire files
- **Finding relationships**: `query_graph` with callers_of/callees_of/imports_of/tests_for
- **Architecture questions**: `get_architecture_overview` + `list_communities`

Fall back to Grep/Glob/Read **only** when the graph doesn't cover what you need.

### Key Tools

| Tool | Use when |
|------|----------|
| `detect_changes` | Reviewing code changes — gives risk-scored analysis |
| `get_review_context` | Need source snippets for review — token-efficient |
| `get_impact_radius` | Understanding blast radius of a change |
| `get_affected_flows` | Finding which execution paths are impacted |
| `query_graph` | Tracing callers, callees, imports, tests, dependencies |
| `semantic_search_nodes` | Finding functions/classes by name or keyword |
| `get_architecture_overview` | Understanding high-level codebase structure |
| `refactor_tool` | Planning renames, finding dead code |

### Workflow

1. The graph auto-updates on file changes (via hooks).
2. Use `detect_changes` for code review.
3. Use `get_affected_flows` to understand impact.
4. Use `query_graph` pattern="tests_for" to check coverage.

## Repository Structure

Monorepo with two git submodules:
- `app/` — .NET 10 MAUI desktop app (Windows-only, version-controlled here)
- `backend/` — Rust API server (submodule: `naff-warehouse-backend`)
- `frontend/` — Next.js dashboard (submodule: `naff-warehouse-frontend`)
- `docker/` — Docker Compose configs for full-stack deployment
- `backend-legacy/` — Deprecated .NET backend, ignore

When working on `backend/` or `frontend/`, changes must be committed in their respective submodule repos.

## Commands

### Desktop App (app/)
```bash
# Check build compiles (Windows-only target)
dotnet build app/app.csproj -c Release -f net10.0-windows10.0.19041.0 -r win-x64

# Publish self-contained
dotnet publish app/app.csproj -c Release -f net10.0-windows10.0.19041.0 -r win-x64 --self-contained true

# Build installer (requires InnoSetup installed)
iscc setup.iss
```

### Backend (backend/)
```bash
cargo build
cargo run          # listens on 0.0.0.0:8080
cargo test
```

### Frontend (frontend/)
```bash
npm run dev        # dev server on port 3000
npm run build
npm run lint
```

### Docker (full stack)
```bash
docker compose -f docker/compose.yml -f docker/compose.db.yml up
```

## Architecture

### System Overview

The desktop app (MAUI) is the primary operator interface. The operator scans a tracking number to start webcam recording; scanning the same number again stops recording, uploads the video to MinIO, and fires an n8n webhook. The Rust backend provides REST + WebSocket APIs consumed by both the desktop app and the Next.js dashboard.

```
Desktop App (MAUI)
  → Records video to local disk
  → Uploads to MinIO via MinioUploadService
  → Calls backend REST API (ApiService.cs)
  → Triggers n8n webhooks (WebhookService.cs)

Backend (Rust / Axum)
  → PostgreSQL via SQLx (async, compile-time checked)
  → pg_notify triggers → tokio broadcast channel → WebSocket /packing-lists/events
  → Polls MinIO for upload commands (UploadCommandListener)

Frontend (Next.js App Router)
  → Fetches REST + subscribes to WebSocket for real-time updates
  → Dashboards: /packing, /qc, /logs, /settings
```

### Desktop App Internals (app/)

- `MainPage.xaml.cs` — station grid; each station is a `StationView` control
- `Controls/StationView.xaml.cs` — webcam preview, video recording lifecycle, scan handling
- `Workflows/` — internal declarative state machine (WorkflowEngine + JSON definitions in Workflows/Definitions/)
- `Services/ApiService.cs` — all HTTP calls to backend
- `Services/MinioUploadService.cs` — S3 upload; `ReuploadQueue.cs` handles retries
- `Services/AppSettings.cs` — reads/writes `appsettings.json` (apiUrl, webhookUrl, videoFolder, MinIO credentials)
- `MauiProgram.cs` — DI container setup

### Backend Internals (backend/src/)

- `main.rs` — Axum server init, state setup, router mount
- `api/mod.rs` — all routes, CORS config
- `api/packing.rs` / `api/videos.rs` — core domain handlers
- `api/events.rs` — WebSocket upgrade, broadcasts from `state.rs` channel
- `state.rs` — `AppState` struct: DB pool + broadcast sender + MinIO URL
- `notifier.rs` — PostgreSQL LISTEN loop that feeds the broadcast channel
- `migration/schema.sql` — authoritative schema; key tables: `packing_lists`, `packing_videos`, `stations`, `workflow_events`, `station_logs`, `upload_commands`

### Frontend Internals (frontend/app/)

- `hooks/usePackingSocket.ts` — WebSocket listener for real-time packing events
- `hooks/useDashboard.ts`, `usePackingDashboard.ts` — data fetching + state
- `types.ts` — shared TypeScript interfaces

## Configuration

| Layer | Config mechanism |
|-------|-----------------|
| Desktop app | `app/appsettings.json` (apiUrl, webhookUrl, videoFolder, MinIO creds) |
| Backend | Env vars: `DATABASE_URL`, `CORS_ORIGIN`, `MINIO_ENDPOINT`, `MINIO_PORT` |
| Frontend | Build-time env: `NEXT_PUBLIC_API_URL`, `NEXT_PUBLIC_WS_URL`, `NEXT_PUBLIC_MINIO_URL`, `NEXT_PUBLIC_MINIO_BUCKET` |

## Key Constraints

- Desktop app targets **Windows only** (`net10.0-windows10.0.19041.0`). Do not attempt cross-platform builds.
- Backend uses SQLx compile-time query checking — `DATABASE_URL` must be set or use `SQLX_OFFLINE=true` with a cached query file.
- Next.js 16 has breaking changes vs 15; see `frontend/AGENTS.md` before upgrading dependencies.
- The `backend/` and `frontend/` directories are git submodules — changes there need their own commits/pushes.

<!-- rtk-instructions v2 -->
# RTK (Rust Token Killer) - Token-Optimized Commands

## Golden Rule

**Always prefix commands with `rtk`**. If RTK has a dedicated filter, it uses it. If not, it passes through unchanged. This means RTK is always safe to use.

**Important**: Even in command chains with `&&`, use `rtk`:
```bash
# ❌ Wrong
git add . && git commit -m "msg" && git push

# ✅ Correct
rtk git add . && rtk git commit -m "msg" && rtk git push
```

## RTK Commands by Workflow

### Build & Compile (80-90% savings)
```bash
rtk cargo build         # Cargo build output
rtk cargo check         # Cargo check output
rtk cargo clippy        # Clippy warnings grouped by file (80%)
rtk tsc                 # TypeScript errors grouped by file/code (83%)
rtk lint                # ESLint/Biome violations grouped (84%)
rtk prettier --check    # Files needing format only (70%)
rtk next build          # Next.js build with route metrics (87%)
```

### Test (60-99% savings)
```bash
rtk cargo test          # Cargo test failures only (90%)
rtk go test             # Go test failures only (90%)
rtk jest                # Jest failures only (99.5%)
rtk vitest              # Vitest failures only (99.5%)
rtk playwright test     # Playwright failures only (94%)
rtk pytest              # Python test failures only (90%)
rtk rake test           # Ruby test failures only (90%)
rtk rspec               # RSpec test failures only (60%)
rtk test <cmd>          # Generic test wrapper - failures only
```

### Git (59-80% savings)
```bash
rtk git status          # Compact status
rtk git log             # Compact log (works with all git flags)
rtk git diff            # Compact diff (80%)
rtk git show            # Compact show (80%)
rtk git add             # Ultra-compact confirmations (59%)
rtk git commit          # Ultra-compact confirmations (59%)
rtk git push            # Ultra-compact confirmations
rtk git pull            # Ultra-compact confirmations
rtk git branch          # Compact branch list
rtk git fetch           # Compact fetch
rtk git stash           # Compact stash
rtk git worktree        # Compact worktree
```

Note: Git passthrough works for ALL subcommands, even those not explicitly listed.

### GitHub (26-87% savings)
```bash
rtk gh pr view <num>    # Compact PR view (87%)
rtk gh pr checks        # Compact PR checks (79%)
rtk gh run list         # Compact workflow runs (82%)
rtk gh issue list       # Compact issue list (80%)
rtk gh api              # Compact API responses (26%)
```

### JavaScript/TypeScript Tooling (70-90% savings)
```bash
rtk pnpm list           # Compact dependency tree (70%)
rtk pnpm outdated       # Compact outdated packages (80%)
rtk pnpm install        # Compact install output (90%)
rtk npm run <script>    # Compact npm script output
rtk npx <cmd>           # Compact npx command output
rtk prisma              # Prisma without ASCII art (88%)
```

### Files & Search (60-75% savings)
```bash
rtk ls <path>           # Tree format, compact (65%)
rtk read <file>         # Code reading with filtering (60%)
rtk grep <pattern>      # Search grouped by file (75%)
rtk find <pattern>      # Find grouped by directory (70%)
```

### Analysis & Debug (70-90% savings)
```bash
rtk err <cmd>           # Filter errors only from any command
rtk log <file>          # Deduplicated logs with counts
rtk json <file>         # JSON structure without values
rtk deps                # Dependency overview
rtk env                 # Environment variables compact
rtk summary <cmd>       # Smart summary of command output
rtk diff                # Ultra-compact diffs
```

### Infrastructure (85% savings)
```bash
rtk docker ps           # Compact container list
rtk docker images       # Compact image list
rtk docker logs <c>     # Deduplicated logs
rtk kubectl get         # Compact resource list
rtk kubectl logs        # Deduplicated pod logs
```

### Network (65-70% savings)
```bash
rtk curl <url>          # Compact HTTP responses (70%)
rtk wget <url>          # Compact download output (65%)
```

### Meta Commands
```bash
rtk gain                # View token savings statistics
rtk gain --history      # View command history with savings
rtk discover            # Analyze Claude Code sessions for missed RTK usage
rtk proxy <cmd>         # Run command without filtering (for debugging)
rtk init                # Add RTK instructions to CLAUDE.md
rtk init --global       # Add RTK to ~/.claude/CLAUDE.md
```

## Token Savings Overview

| Category | Commands | Typical Savings |
|----------|----------|-----------------|
| Tests | vitest, playwright, cargo test | 90-99% |
| Build | next, tsc, lint, prettier | 70-87% |
| Git | status, log, diff, add, commit | 59-80% |
| GitHub | gh pr, gh run, gh issue | 26-87% |
| Package Managers | pnpm, npm, npx | 70-90% |
| Files | ls, read, grep, find | 60-75% |
| Infrastructure | docker, kubectl | 85% |
| Network | curl, wget | 65-70% |

Overall average: **60-90% token reduction** on common development operations.
<!-- /rtk-instructions -->