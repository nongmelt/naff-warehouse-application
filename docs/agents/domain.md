# Domain Docs: Multi-Context Layout

This is a monorepo with three submodules, each with its own domain and tech stack. Domain context is split per-submodule.

## Context map

| Context | Path | Stack | CONTEXT.md |
|---------|------|-------|------------|
| Desktop App | `app/` | .NET 10 MAUI, C#, Windows | `app/CONTEXT.md` |
| Backend API | `backend/` | Rust, Axum, SQLx, PostgreSQL | `backend/CONTEXT.md` |
| Frontend Dashboard | `frontend/` | Next.js 16, React 19, Tailwind v4, TypeScript | `frontend/CONTEXT.md` |

## ADR locations

- `docs/adr/` — cross-cutting architectural decisions (system-wide)
- `app/docs/adr/` — desktop app decisions
- `backend/docs/adr/` — backend decisions
- `frontend/docs/adr/` — frontend decisions

## Consumer rules

When a skill needs domain context:

1. **Identify which context(s)** the current task touches
2. **Read the relevant `CONTEXT.md`** file(s) — not all of them
3. **Check cross-cutting `docs/adr/`** for system-wide decisions that may apply
4. If a `CONTEXT.md` does not yet exist for a submodule, the skill should note its absence rather than guessing domain language

## Creating CONTEXT.md files

Each `CONTEXT.md` should contain:

- **Domain language** — key terms and their definitions as used in this project
- **Invariants** — business rules that must always hold
- **Boundaries** — what this context owns vs. delegates to other contexts
- **Key entities** — the core domain objects and their relationships
