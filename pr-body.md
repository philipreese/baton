## Summary

- Clear Baton stdout/stderr inheritance for every one-shot CommandResult verb, including `cancel` and `resolve --close`.
- Preserve daemon and detached-lane lifetimes while documenting the terminal-result handle invariant.
- Add PowerShell-wrapper exit coverage and enforce the complete protected verb set.

## Verification

- `python tools/buildlock.py dotnet build -warnaserror`
- Focused CLI and architecture tests
- `pixi run fmt-check`
- `pixi run audit-recordonce`
- `pixi run audit-docsbudget`
- Pre-push `pixi run gates-lane-fast`

Closes #2030
