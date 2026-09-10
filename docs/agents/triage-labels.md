# Triage labels

Read this file before routing an issue. These labels describe what can happen next, not issue type or
component.

| Label | Meaning and required next step |
| --- | --- |
| `needs-triage` | A maintainer still needs to classify the issue. |
| `needs-info` | Evidence is missing. Link an executable measurement, or name an owner and passive capture that will produce it. |
| `needs-design` | A decision is still open. Surface the decision for the operator, or name the dependency that must settle it. |
| `ready-for-agent` | The work is specified and dispatchable now. |

A new or newly updated issue starts at `needs-triage` unless its evidence and scope already make it
`ready-for-agent`. `needs-info` returns to `needs-triage` when the measurement or owned capture
arrives. `needs-design` returns when the decision is recorded or the named dependency settles.
`ready-for-agent` is not a holding state: the queue or an operator may dispatch it.

Keep type, layer, and platform labels alongside this routing label. Confirm the current repository
label set with `gh label list --repo philipreese/baton` before applying any label not listed here.
