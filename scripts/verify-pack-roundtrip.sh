#!/usr/bin/env bash
# M13 Phase 4 (#110), the milestone's completion gate: proves the packed nupkg is actually
# installable and runnable, unattended, with no live vendor auth. Unlike M11/M12's gated
# record-once-ok: #2204 docs/agents/developing-baton.md
# smoke-claude/smoke-mixed-vendor runbooks (live-vendor policy:
# docs/agents/developing-baton.md#live-vendor-smoke-tests), nothing here needs a live vendor: the
# `claude` binary the installed `baton` shells out to is a local stub that just satisfies the
# declared output contract, so this can run unattended in default CI. Invoked as
# `pixi run verify-pack` (depends on `pack`).
set -euo pipefail

PACK_DIR="$(cd bin/pack && pwd)"
STUB_DIR="$(mktemp -d)"
TASK_ROOT="$(mktemp -d)"
TASK_DIR="$TASK_ROOT/task"

cleanup() {
  dotnet tool uninstall --global baton >/dev/null 2>&1 || true
  rm -rf "$STUB_DIR" "$TASK_ROOT"
}
trap cleanup EXIT

# A stub `claude` binary ahead of the real one (if any) on PATH: it reads BATON_OUTPUT_DIR from
# the real process environment and satisfies the declared output contract without touching the
# network or needing vendor auth -- proving the packaged native-core dispatch works end to end
# from the installed global tool, the same proof-of-dispatch goal Phases 1/3 used ExitCode:127
# for, but this time settling the step Succeeded instead of Failed.
#
# The stub MUST be a real .exe (#1468): since #1424 removed shell wrapping, the adapter's direct
# `claude` invocation is spawned by BatonTask's managed spawn path (.NET's Process,
# UseShellExecute=false), which resolves only `claude` and `claude.exe` on PATH -- never
# `claude.cmd`/`.bat` (no PATHEXT consulted; CreateProcess's own resolution only ever appends
# `.exe`, see CVE-2024-24576 -- this held for aer-core's Rust `Command::new` pre-#1474 and was
# measured to hold identically for the managed port that replaced it). The `.cmd` stub #1405 chose
# for the old shell-wrapped spawn was invisible to it, which kept the pack job red on every main
# push from #1424 until this fix. The
# dotnet SDK is already a hard dependency of this script (it installs a dotnet tool), so publish a
# three-line C# stub as `claude.exe`.
STUB_SRC="$TASK_ROOT/stub-src"
mkdir -p "$STUB_SRC"
cat > "$STUB_SRC/claude-stub.csproj" <<'STUB'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <AssemblyName>claude</AssemblyName>
    <Nullable>disable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
</Project>
STUB
cat > "$STUB_SRC/Program.cs" <<'STUB'
var dir = Environment.GetEnvironmentVariable("BATON_OUTPUT_DIR")
          ?? throw new InvalidOperationException("BATON_OUTPUT_DIR not set");
Directory.CreateDirectory(dir);
File.WriteAllText(Path.Combine(dir, "greeting"), "stub greeting from the pack round-trip check\n");
STUB
dotnet publish "$STUB_SRC/claude-stub.csproj" -c Release -o "$STUB_DIR" --nologo -v quiet
test -f "$STUB_DIR/claude.exe" || { echo "stub publish produced no claude.exe in $STUB_DIR" >&2; exit 1; }

WORKFLOW_FILE="$TASK_ROOT/workflow.json"
BINDINGS_FILE="$TASK_ROOT/bindings.json"

cat > "$WORKFLOW_FILE" <<'EOF'
{
  "WorkflowTemplateId": "pack-roundtrip",
  "WorkflowTemplateVersion": 1,
  "Steps": [
    {
      "StepId": "greet",
      "Worker": "greeter",
      "Inputs": [],
      "Outputs": ["greeting"],
      "DependsOn": [],
      "RetryPolicy": { "MaxAttempts": 1 }
    }
  ]
}
EOF

cat > "$BINDINGS_FILE" <<'EOF'
{
  "greeter": {
    "Adapter": "claude",
    "Contract": {
      "WorkerName": "greeter",
      "RequiredInputs": [],
      "ProducedOutputs": [{ "Name": "greeting" }],
      "OptionalMetadata": []
    },
    "PromptTemplate": "Write a one-sentence greeting.",
    "Timeout": "00:02:00"
  }
}
EOF

dotnet tool uninstall --global baton >/dev/null 2>&1 || true
dotnet tool install --global --add-source "$PACK_DIR" baton

export PATH="$HOME/.dotnet/tools:$STUB_DIR:$PATH"

baton run "$WORKFLOW_FILE" --bindings "$BINDINGS_FILE" --room-dir "$TASK_DIR"

OUTPUT_FILE=$(find "$TASK_DIR/artifacts" -type f -name greeting -print -quit)
if [ -z "$OUTPUT_FILE" ] || [ ! -s "$OUTPUT_FILE" ]; then
  echo "Expected a non-empty 'greeting' output under $TASK_DIR/artifacts -- found none." >&2
  exit 1
fi

echo "Pack round-trip check passed: $OUTPUT_FILE"
