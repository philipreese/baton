"""Admission and complete-coverage checks for manual CI recovery."""
import json
import os
import re
import sys
import urllib.request


def validate_target(ref, sha, expected, live_sha):
    if (ref != "refs/heads/main" or not re.fullmatch(r"[0-9a-f]{40}", expected)
            or sha != expected or live_sha != expected):
        raise ValueError("Recovery requires the selected revision to match the live main head at job admission")


def validate_results(results):
    if len(results) != 3 or any(value != "success" for value in results):
        raise ValueError("Recovery requires successful test, gates and pack jobs")


def target(env, fetch=None):
    repository = env.get("GITHUB_REPOSITORY", "")
    if not re.fullmatch(r"[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+", repository):
        raise ValueError("Invalid repository")
    if env.get("GITHUB_EVENT_NAME") != "workflow_dispatch":
        raise ValueError("Recovery admission is only for workflow_dispatch")
    request = urllib.request.Request(
        "https://api.github.com/repos/" + repository + "/git/ref/heads/main",
        headers={"Authorization": "Bearer " + env.get("GH_TOKEN", ""),
                 "Accept": "application/vnd.github+json"})
    if fetch is None:
        with urllib.request.urlopen(request, timeout=20) as response:
            payload = json.load(response)
    else:
        payload = fetch(request)
    validate_target(env.get("GITHUB_REF"), env.get("GITHUB_SHA"),
                    env.get("EXPECTED_SHA", ""), payload["object"]["sha"])


def main():
    if sys.argv[1:] == ["target"]:
        target(os.environ)
    elif sys.argv[1:] == ["aggregate"]:
        validate_results([os.environ.get(key) for key in
                          ("TEST_RESULT", "GATES_RESULT", "PACK_RESULT")])
    else:
        raise ValueError("Expected target or aggregate")


if __name__ == "__main__":
    try:
        main()
    except Exception as error:
        print("::error::" + str(error), file=sys.stderr)
        sys.exit(1)
