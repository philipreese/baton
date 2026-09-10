# Claude Code bridge

Read [`AGENTS.md`](AGENTS.md) before acting. It is the shared entry point for using Baton,
developing Baton, canonical project memory, and dispatched worker roles.

## Claude Code remote sandbox

The .NET 10 SDK is installed separately from pixi. On the Linux remote sandbox, run
`sudo apt-get install -y dotnet-sdk-10.0` directly, skipping `apt-get update` (or ignoring its exit
code): the sandbox's `deadsnakes` and `ondrej/php` PPAs can fail while the SDK package still resolves
from `archive.ubuntu.com` and `security.ubuntu.com`. It installs to `/usr/bin/dotnet`; no `PATH`
edit is needed.
