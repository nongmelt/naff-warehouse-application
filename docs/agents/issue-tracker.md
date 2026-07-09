# Issue Tracker: GitHub Issues

Issues are tracked in **GitHub Issues** at [`nongmelt/naff-warehouse-application`](https://github.com/nongmelt/naff-warehouse-application/issues).

## Creating issues

```bash
gh issue create --title "Title" --body "Description" --label "needs-triage"
```

## Listing issues

```bash
gh issue list --state open
gh issue list --label "needs-triage"
gh issue list --label "ready-for-agent"
```

## Viewing an issue

```bash
gh issue view <number>
```

## Updating issue labels

```bash
gh issue edit <number> --add-label "ready-for-agent" --remove-label "needs-triage"
```

## Closing an issue

```bash
gh issue close <number> --reason completed
gh issue close <number> --reason "not planned"
```

## Wayfinding operations

Wayfinder maps live in this root repo (features usually span both submodules).

- **Map**: issue labelled `wayfinder:map`. Tickets: `wayfinder:research|prototype|grilling|task`.
- **Children**: native sub-issues — `gh api -X POST repos/{repo}/issues/<map>/sub_issues -F sub_issue_id=<issue DB id>` (`-F`, not `-f`: the API requires an integer; get the id via `gh api repos/{repo}/issues/<n> --jq .id`).
- **Blocking**: native dependencies — `gh api -X POST repos/{repo}/issues/<n>/dependencies/blocked_by -F issue_id=<DB id>`.
- **Frontier query**: open, unassigned sub-issues of the map with no open blockers — `gh api repos/{repo}/issues/<n>/dependencies/blocked_by` returns the blockers; a ticket is takeable when that list has no open entries.
- **Claim**: assign the issue to yourself before any work.

## Conventions

- Every new issue gets `needs-triage` label by default
- Issues must be triaged before work begins
- Cross-reference PRs with `Fixes #<number>` in commit messages
- Submodule-specific issues should be prefixed with `[backend]`, `[frontend]`, or `[app]` in the title
