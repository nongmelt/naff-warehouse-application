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

## Conventions

- Every new issue gets `needs-triage` label by default
- Issues must be triaged before work begins
- Cross-reference PRs with `Fixes #<number>` in commit messages
- Submodule-specific issues should be prefixed with `[backend]`, `[frontend]`, or `[app]` in the title
