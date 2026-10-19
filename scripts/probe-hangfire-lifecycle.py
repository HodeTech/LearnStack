#!/usr/bin/env python3
"""Reproduce ADR-0059's isolated Core lifecycle controls, without a database.

Package pins come from the repository. Build outputs and restore state stay in an
owned temporary tree. This is decision support, not production runtime proof.
"""

import argparse
import json
import os
import queue
from pathlib import Path
import shutil
import signal
import subprocess
import tempfile
import threading
import time
import xml.etree.ElementTree as ET


EXIT_MODES = ("exit-direct-callback", "exit-owned-relay")
BOUNDARY_MODES = (
    "announce-raw", "announce-owned", "acquire-owned", "dispose-owned",
    "normal-raw", "normal-owned", "remove-owned", "remove-acquire-owned",
    "remove-dispose-owned", "builder-owned", "drain-infinite", "drain-finite",
)
MODES = ("stock-restart", "builder-before", "builder-after", "builder-throw", "builder-throw-raw", *EXIT_MODES, *BOUNDARY_MODES)
PACKAGES = ("Hangfire.Core", "Hangfire.PostgreSql", "Npgsql", "Newtonsoft.Json")


def _terminate_and_join(child):
    if child.poll() is not None:
        child.wait(timeout=3)
        return
    if os.name == "nt":
        child.terminate()
    else:
        try:
            os.killpg(child.pid, signal.SIGTERM)
        except ProcessLookupError:
            pass
    try:
        child.wait(timeout=1)
    except subprocess.TimeoutExpired:
        if os.name == "nt":
            child.kill()
        else:
            try:
                os.killpg(child.pid, signal.SIGKILL)
            except ProcessLookupError:
                pass
        child.wait(timeout=3)


def _require(condition, message):
    if not condition:
        raise AssertionError(message)


def run_exit_owned(command, directory, environment, mode, timeout=15):
    """Require unsafe exit to stay held; require bounded relay exit to return zero.

    The actual child is killed/joined on every unfinished path. A reader drains
    stdout continuously so a probe-only exit receipt cannot block on its pipe.
    """
    _require(mode in EXIT_MODES, "Unknown process-exit probe mode")
    lines = queue.Queue()
    events = {}
    ordered = []
    child = subprocess.Popen(
        command, cwd=directory, env=environment,
        stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True,
        start_new_session=os.name != "nt",
    )

    def read_output():
        try:
            for line in child.stdout:
                lines.put(line)
        finally:
            lines.put(None)

    reader = threading.Thread(target=read_output, daemon=True)
    reader.start()
    ended = False
    dirty_held = False
    deadline = time.monotonic() + timeout
    try:
        while not ended:
            remaining = deadline - time.monotonic()
            if remaining <= 0:
                raise TimeoutError(f"{mode}: child exceeded its external watchdog")
            try:
                line = lines.get(timeout=min(0.1, remaining))
            except queue.Empty:
                continue
            if line is None:
                ended = True
                continue
            print(line, end="", flush=True)
            row = json.loads(line)
            _require(row.get("mode") == mode, "Child receipt mode mismatch")
            event = row.get("event_name")
            _require(event and event not in events, "Missing or duplicate child event")
            _require(event != "assertion_failed", f"Child assertion failed: {row}")
            events[event] = row["values"]
            ordered.append(event)
            if mode == "exit-direct-callback" and event == "unsafe_core_shutdown_callback_entered":
                _require("main_returning_unresolved" in events, "Core exit callback ran before the ready receipt")
                try:
                    child.wait(timeout=0.3)
                except subprocess.TimeoutExpired:
                    dirty_held = True
                else:
                    raise AssertionError("Unsafe direct callback did not keep the process alive")
                break

        required = ["starting", "finite_host_wait_ended", "main_returning_unresolved"]
        _require(all(name in events for name in required), "Missing normal host-stop evidence")
        _require([ordered.index(name) for name in required] == sorted(ordered.index(name) for name in required),
                 "Host-stop receipts were out of order")
        wait = events["finite_host_wait_ended"]
        _require(wait["stock_process_live"] == 1 and not wait["actual_join_completed"],
                 "Finite wait did not retain one actual stock process")
        _require(not wait["shutdown_signaled"] and wait["context_handles_accessible"],
                 "Normal finite wait disposed/signaled Core's shutdown state")
        returning = events["main_returning_unresolved"]
        _require(returning["no_cleanup_claim"] and returning["no_quiescence_claim"],
                 "Child claimed cleanup/quiescence from termination")
        if mode == "exit-direct-callback":
            _require(dirty_held and child.poll() is None, "Dirty child must still be held before owned termination")
            _require("process_exit_observed" not in events, "Unsafe control reached the post-Core exit observer")
        else:
            _require(child.wait(timeout=max(0.01, deadline - time.monotonic())) == 0,
                     "Closed relay child did not exit normally")
            _require("unsafe_core_shutdown_callback_entered" not in events, "Positive used the direct callback")
            _require("private_module_callback_held" in events and "process_exit_observed" in events,
                     "Missing private callback or actual Core exit evidence")
            _require(events["private_module_callback_held"]["background_thread"],
                     "Private callback was not background execution")
            _require(wait["module_callback_live"] == 1 and wait["relay_completed"] is False,
                     "Positive did not retain its actual module callback and relay")
            exit_row = events["process_exit_observed"]
            _require(exit_row["core_shutdown_signaled"] and exit_row["module_callback_live"] == 1
                     and exit_row["stock_process_live"] == 1 and exit_row["process_exit_is_not_cleanup"],
                     "Process exit did not preserve the declared unresolved-work distinction")
    finally:
        _terminate_and_join(child)
        reader.join(timeout=3)
        if child.stdout is not None:
            child.stdout.close()
        _require(not reader.is_alive(), "Output reader failed to join after child termination")
    print(f"{mode}: assertions passed; actual child joined", flush=True)


def run_owned(command, directory, environment, timeout):
    """Join the actual owned child on failures/signals, before deleting its tree."""
    with subprocess.Popen(command, cwd=directory, env=environment, start_new_session=os.name != "nt") as child:
        try:
            code = child.wait(timeout=timeout)
            if code:
                raise subprocess.CalledProcessError(code, command)
        finally:
            _terminate_and_join(child)


def interrupted(signum, _frame):
    raise SystemExit(128 + signum)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--mode", choices=MODES, action="append", help="Run selected modes; default: all nineteen.")
    args = parser.parse_args()
    root = Path(__file__).resolve().parent.parent
    versions = {
        item.attrib["Include"]: item.attrib["Version"]
        for item in ET.parse(root / "backend/Directory.Packages.props").iter("PackageVersion")
    }
    project = ET.Element("Project", Sdk="Microsoft.NET.Sdk")
    properties = ET.SubElement(project, "PropertyGroup")
    for key, value in {
        "OutputType": "Exe", "TargetFramework": "net10.0", "ImplicitUsings": "enable",
        "Nullable": "enable", "TreatWarningsAsErrors": "true", "UseSharedCompilation": "false",
    }.items():
        ET.SubElement(properties, key).text = value
    references = ET.SubElement(project, "ItemGroup")
    for package in PACKAGES:
        ET.SubElement(references, "PackageReference", Include=package, Version=versions[package])
    environment = os.environ.copy()
    environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1"
    environment["DOTNET_NOLOGO"] = "1"
    environment["DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER"] = "1"
    with tempfile.TemporaryDirectory(prefix="learnstack-hangfire-lifecycle-") as temporary:
        directory = Path(temporary)
        ET.ElementTree(project).write(directory / "probe.csproj", encoding="unicode")
        shutil.copy2(root / "backend/global.json", directory / "global.json")
        for name in ("Program.cs", "BuilderProbe.cs", "ExitProbe.cs", "BoundaryProbe.cs"):
            shutil.copy2(root / "scripts/probes/hangfire-lifecycle" / name, directory / name)
        run_owned(["dotnet", "build", "probe.csproj", "--configuration", "Release", "-nodeReuse:false"],
                  directory, environment, 120)
        for mode in args.mode or MODES:
            command = ["dotnet", str(directory / "bin/Release/net10.0/probe.dll"), mode]
            if mode in EXIT_MODES:
                run_exit_owned(command, directory, environment, mode)
            else:
                run_owned(command, directory, environment, 40)
                print(f"{mode}: assertions passed", flush=True)


if __name__ == "__main__":
    signal.signal(signal.SIGTERM, interrupted)
    main()
