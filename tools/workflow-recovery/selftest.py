"""Offline production checks and strict checks of CI's supported workflow syntax."""
from pathlib import Path
import ast
import importlib.util
import itertools
import re
import tomllib

ROOT = Path(__file__).resolve().parents[2]


def load_check():
    spec = importlib.util.spec_from_file_location("recovery_check", Path(__file__).with_name("check.py"))
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def refused(action):
    try:
        action()
    except (ValueError, KeyError, AssertionError):
        return
    raise AssertionError("Invalid recovery was accepted")


def field(block, name):
    values = re.findall(r"^    " + name + r": (.+)$", block, re.M)
    assert len(values) == 1, (name, values)
    return values[0]


def condition(expression, event, results=None, dotnet="true", cancelled=False):
    values = {"github.event_name": event, "needs.changes.outputs.dotnet": dotnet}
    values.update({"needs." + key + ".result": value for key, value in (results or {}).items()})
    expression = expression.removeprefix("${{").removesuffix("}}").strip()
    for key, value in values.items():
        expression = expression.replace(key, repr(value))
    expression = expression.replace("always()", "True").replace("cancelled()", str(cancelled))
    expression = expression.replace("&&", " and ").replace("||", " or ")
    expression = re.sub(r"!(?!=)", " not ", expression).strip()
    parsed = ast.parse(expression, mode="eval")
    allowed = (ast.Expression, ast.BoolOp, ast.And, ast.Or, ast.UnaryOp, ast.Not,
               ast.Compare, ast.Eq, ast.NotEq, ast.Constant, ast.Load)
    assert all(isinstance(node, allowed) for node in ast.walk(parsed)), expression
    return eval(compile(parsed, "workflow condition", "eval"), {"__builtins__": {}})


def check_workflow(text):
    # This is deliberately a narrow syntax reader, not a YAML implementation. Multi-line
    # conditions, aliases and unexpected job layouts fail rather than silently being ignored.
    assert not re.search(r"^\s+[^#\n]*[&*][A-Za-z_]", text, re.M)
    pieces = re.split(r"^  ([a-z_]+):\n", text.split("jobs:\n", 1)[1], flags=re.M)
    jobs = dict(zip(pieces[1::2], pieces[2::2]))
    assert set(jobs) == {"changes", "test", "pack", "gates", "ci", "recovery_ci"}
    assert field(jobs["test"], "needs") == "changes"
    assert field(jobs["pack"], "needs") == "test"
    assert not re.search(r"^    needs:", jobs["gates"], re.M)
    assert field(jobs["ci"], "needs") == "[changes, test, gates]"
    assert field(jobs["recovery_ci"], "needs") == "[test, gates, pack]"
    for event in ("push", "pull_request", "workflow_dispatch"):
        assert condition(field(jobs["ci"], "if"), event) == (event != "workflow_dispatch")
        assert condition(field(jobs["recovery_ci"], "if"), event) == (event == "workflow_dispatch")
        for dotnet in ("true", "false"):
            assert condition(field(jobs["test"], "if"), event, dotnet=dotnet) == (event != "pull_request" or dotnet == "true")
        for result in ("success", "failure", "skipped", "cancelled", ""):
            assert condition(field(jobs["pack"], "if"), event, {"test": result}) == (event != "pull_request" and result == "success")
            assert not condition(field(jobs["pack"], "if"), event, {"test": result}, cancelled=True)
    for name in ("test", "gates", "pack"):
        block = jobs[name]
        assert block.count("run: python tools/workflow-recovery/check.py target") == 1
        start = block.index("      - name: Admit manual CI recovery\n")
        end = block.find("\n      - ", start + 1)
        guard = block[start:end if end >= 0 else None]
        assert "        if: github.event_name == 'workflow_dispatch'\n" in guard
        assert "          EXPECTED_SHA: ${{ inputs.expected_sha }}\n" in guard
        assert "          GH_TOKEN: ${{ github.token }}\n" in guard
        assert "continue-on-error" not in guard
        before = block[:start]
        assert before.count("      - uses:") == 1 and "      - name:" not in before
        assert "          ref: ${{ github.sha }}\n" in before
    aggregate = jobs["recovery_ci"]
    assert "run: python tools/workflow-recovery/check.py aggregate" in aggregate
    assert "continue-on-error" not in aggregate
    for name in ("test", "gates", "pack"):
        assert name.upper() + "_RESULT: ${{ needs." + name + ".result }}" in aggregate


def main():
    check = load_check()
    sha, other = "a" * 40, "b" * 40
    check.validate_target("refs/heads/main", sha, sha, sha)
    for args in (("refs/heads/topic", sha, sha, sha), ("refs/heads/main", sha, sha, other),
                 ("refs/heads/main", sha, other, sha), ("refs/heads/main", sha, "", sha)):
        refused(lambda args=args: check.validate_target(*args))
    env = dict(GITHUB_REPOSITORY="owner/repo", GITHUB_EVENT_NAME="workflow_dispatch",
               GITHUB_REF="refs/heads/main", GITHUB_SHA=sha, EXPECTED_SHA=sha)
    check.target(env, lambda request: {"object": {"sha": sha}})
    refused(lambda: check.target(env, lambda request: {"object": {"sha": other}}))
    refused(lambda: check.target(env, lambda request: {}))
    for values in itertools.product(("success", "failure", "cancelled", "skipped", None), repeat=3):
        if values == ("success",) * 3:
            check.validate_results(values)
        else:
            refused(lambda values=values: check.validate_results(values))
    text = (ROOT / ".github/workflows/ci.yml").read_text(encoding="utf-8")
    check_workflow(text)
    # Mutate the actual workflow text, not a second copy of its predicates.
    for before, after in (("needs.test.result == 'success'", "needs.test.result != 'success'"),
                          ("needs: [test, gates, pack]", "needs: [test, gates]"),
                          ("check.py target", "check.py aggregate"),
                          ("PACK_RESULT: ${{ needs.pack.result }}", "PACK_RESULT: success"),
                          ("github.event_name != 'workflow_dispatch'", "github.event_name == 'workflow_dispatch'")):
        assert before in text
        refused(lambda before=before, after=after: check_workflow(text.replace(before, after)))
    pixi = tomllib.loads((ROOT / "pixi.toml").read_text(encoding="utf-8"))
    assert pixi["tasks"]["workflow-recovery-selftest"]["cmd"] == "python tools/workflow-recovery/selftest.py"
    gates = ast.parse((ROOT / "tools/gates/gates.py").read_text(encoding="utf-8"))
    overlap = next(node.value for node in gates.body if isinstance(node, ast.Assign)
                   and any(isinstance(target, ast.Name) and target.id == "OVERLAP" for target in node.targets))
    assert "workflow-recovery-selftest" in ast.literal_eval(overlap)
    print("CI recovery production checks, workflow conditions and five mutation controls passed")


if __name__ == "__main__":
    main()
