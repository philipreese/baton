"""Hermetic Windows cmd boundary measurement; no real gh or network-capable fixture.

Run: pixi run python tests/Baton.Vendors.Tests/Fixtures/native_cmd.py
The executable records raw GetCommandLineW text and argv, then exits. The wrapper
delivery arm uses a fake cmd.exe; the execution arm uses a byte copy of real cmd,
adding /d at the nested layer to disable ambient AutoRun (not command parsing).
"""

import json
import hashlib
import os
from pathlib import Path
import shutil
import subprocess
import tempfile


RECORDER = r'''
using System;
using System.Runtime.InteropServices;
using System.Web.Script.Serialization;
class Recorder {
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr GetCommandLineW();
    static void Main(string[] args) {
        Console.WriteLine(new JavaScriptSerializer().Serialize(new {
            raw = Marshal.PtrToStringUni(GetCommandLineW()), argv = args
        }));
    }
}
'''


def main():
    if os.name != "nt":
        raise RuntimeError("This fixture requires real Windows cmd.exe")
    system_root = Path(os.environ["SystemRoot"])
    native_cmd = system_root / "System32" / "cmd.exe"
    compiler = system_root / "Microsoft.NET" / "Framework64" / "v4.0.30319" / "csc.exe"
    artifact_root = Path(__file__).resolve().parents[3] / "artifacts"
    artifact_root.mkdir(exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="native-cmd-", dir=artifact_root) as temp:
        work = Path(temp)
        source = work / "recorder.cs"
        source.write_text(RECORDER, encoding="utf-8")
        subprocess.run([str(compiler), "/nologo", "/reference:System.Web.Extensions.dll",
                        "/out:" + str(work / "gh.exe"), str(source)], check=True,
                       capture_output=True, text=True)
        env = {"SystemRoot": str(system_root), "WINDIR": str(system_root),
               "PATH": str(work), "PATHEXT": ".EXE", "COMSPEC": str(work / "cmd.exe"),
               "TEMP": str(work), "TMP": str(work)}

        def run(command):
            # list2cmdline supplies the same enclosing quotes as .NET ArgumentList
            # for these quote-free command strings: /d /s /c "<command>".
            argv = [str(native_cmd), "/d", "/s", "/c", command]
            result = subprocess.run(argv, cwd=work, env=env, capture_output=True,
                                    text=True, timeout=20)
            records = [json.loads(line) for line in result.stdout.splitlines()
                       if line.startswith('{"raw":')]
            assert result.returncode == 0, (command, result.stderr)
            assert result.stderr == "", (command, result.stderr)
            return {"command": command, "launch": subprocess.list2cmdline(argv),
                    "exit": result.returncode, "stdout": result.stdout,
                    "records": records}

        cases = [
            ("cmd /c echo safe ^& gh pr create --fill", ["pr", "create", "--fill"]),
            ("@ cmd /c gh pr create --fill", ["pr", "create", "--fill"]),
            ("@\tcmd /c gh pr create --fill", ["pr", "create", "--fill"]),
            ("@@ @ cmd /c gh pr create --fill", ["pr", "create", "--fill"]),
            ("cmd /c echo safe ^& gh --version", ["--version"]),
            ("@ cmd /c gh --version", ["--version"]),
            ("cmd /c echo gh pr create --fill", None),
            ("@ cmd /c echo gh pr create --fill", None),
        ]
        evidence = []
        for command, expected in cases:
            shutil.copyfile(work / "gh.exe", work / "cmd.exe")
            delivered = run(command)
            # Capture the EXACT requested command using a recording-only cmd.exe.
            # Then run the real nested shell with /d to suppress this host's
            # conda AutoRun. No registry or installed executable is modified.
            shutil.copyfile(native_cmd, work / "cmd.exe")
            assert (work / "cmd.exe").read_bytes() == native_cmd.read_bytes()
            actual = run(command.replace("cmd /c", "cmd /d /c", 1))
            assert len(delivered["records"]) == 1, delivered
            expected_tail = command[command.index("/c"):].replace("^&", "&")
            assert delivered["records"][0] == {
                "raw": "cmd  " + expected_tail, "argv": expected_tail.split()
            }, delivered
            assert [r["argv"] for r in actual["records"]] == (
                [] if expected is None else [expected]), actual
            evidence.append({"delivered_to_wrapper": delivered, "native_execution": actual})
        print(json.dumps({"native_cmd": str(native_cmd), "path_contains_only_fixture": True,
                          "native_cmd_sha256": hashlib.sha256(native_cmd.read_bytes()).hexdigest(),
                          "autorun_disabled_per_native_layer": True, "cases": evidence}, indent=2))


if __name__ == "__main__":
    main()
