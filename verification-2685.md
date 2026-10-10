# Issue #2685 focused verification

- Red: `python tools/buildlock.py pixi run dotnet test --project tests/Baton.Cli.Tests --filter-class Baton.Cli.Tests.AgyLifecycleTests --minimum-expected-tests 1` — exit 2, 1 test failed: named transport-only stage rejected by TaskOptionsParser before wiring.
