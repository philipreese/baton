## Summary

- Add a narrowly gated `restore-exact-file` MCP tool for restoring one tracked file from the admitted worktree base revision.
- Capture and propagate the base identity through fresh dispatches and continuations, with audit records and dirty-file safeguards.
- Add adapter wiring, parser coverage, focused restore tests, and hygiene-compliant bounded process waits.

## Verification

- `python tools/buildlock.py dotnet build -warnaserror`
- `python tools/buildlock.py dotnet test tests/Baton.Cli.Tests/Baton.Cli.Tests.csproj --filter "FullyQualifiedName~ExactFileRestoreToolTests|FullyQualifiedName~McpCommandTests"`
- `python tools/buildlock.py dotnet test tests/Baton.Vendors.Tests/Baton.Vendors.Tests.csproj --filter "FullyQualifiedName~ClaudeWorkerAdapterTests|FullyQualifiedName~AgyWorkerAdapterTests"`
- `python tools/buildlock.py dotnet format --verify-no-changes`
- `pixi run audit-recordonce`
- `pixi run audit-docsbudget`
- `pixi run audit-waitceiling`

## Protected files

- None touched.

Closes #2415
