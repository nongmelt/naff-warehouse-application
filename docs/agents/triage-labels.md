# Triage Labels

Label vocabulary for the triage state machine. These labels must exist in GitHub Issues.

## Label mapping

| Role | Label | Description |
|------|-------|-------------|
| Needs evaluation | `needs-triage` | Maintainer has not yet reviewed this issue |
| Waiting on reporter | `needs-info` | Blocked on additional information from the reporter |
| Agent-ready | `ready-for-agent` | Fully specified; an AFK agent can implement without human context |
| Human-required | `ready-for-human` | Needs human judgment, design decisions, or context that an agent cannot provide |
| Won't fix | `wontfix` | Will not be actioned — out of scope, duplicate, or invalid |

## State transitions

```
new issue → needs-triage
needs-triage → needs-info       (missing information)
needs-triage → ready-for-agent  (clear spec, agent can handle)
needs-triage → ready-for-human  (needs human judgment)
needs-triage → wontfix          (out of scope)
needs-info   → needs-triage     (reporter replied)
needs-info   → wontfix          (no response, stale)
```

## Creating labels

If labels don't exist yet:

```bash
gh label create "needs-triage" --color "FBCA04" --description "Maintainer needs to evaluate"
gh label create "needs-info" --color "D93F0B" --description "Waiting on reporter"
gh label create "ready-for-agent" --color "0E8A16" --description "Fully specified, agent can pick up"
gh label create "ready-for-human" --color "1D76DB" --description "Needs human implementation"
gh label create "wontfix" --color "FFFFFF" --description "Will not be actioned"
```
