#!/usr/bin/env python3
"""Reproduce ADR-0059's isolated Core lifecycle controls, without a database.

Package pins come from the repository. Build outputs and restore state stay in an
owned temporary tree. This is decision support, not production runtime proof.
"""

import argparse
import os
from pathlib import Path
import shutil
import signal
import subprocess
import tempfile
import xml.etree.ElementTree as ET


MODES = ("stock-restart", "builder-before", "builder-after", "builder-throw", "builder-throw-raw")
PACKAGES = ("Hangfire.Core", "Hangfire.PostgreSql", "Npgsql", "Newtonsoft.Json")


def run_owned(command, directory, environment, timeout):
    """Join the actual owned child on failures/signals, before deleting its tree."""
    with subprocess.Popen(command, cwd=directory, env=environment, start_new_session=os.name != "nt") as child:
        try:
            code = child.wait(timeout=timeout)
            if code:
                raise subprocess.CalledProcessError(code, command)
        finally:
            if child.poll() is None:
                if os.name == "nt":
                    child.terminate()
                else:
                    try:
                        os.killpg(child.pid, signal.SIGTERM)
                    except ProcessLookupError:
                        pass
                try:
                    child.wait(timeout=5)
                except subprocess.TimeoutExpired:
                    if os.name == "nt":
                        child.kill()
                    else:
                        try:
                            os.killpg(child.pid, signal.SIGKILL)
                        except ProcessLookupError:
                            pass
                    child.wait(timeout=5)


def interrupted(signum, _frame):
    raise SystemExit(128 + signum)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--mode", choices=MODES, action="append", help="Run selected modes; default: all five.")
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
        for name in ("Program.cs", "BuilderProbe.cs"):
            shutil.copy2(root / "scripts/probes/hangfire-lifecycle" / name, directory / name)
        run_owned(["dotnet", "build", "probe.csproj", "--configuration", "Release", "-nodeReuse:false"],
                  directory, environment, 120)
        for mode in args.mode or MODES:
            run_owned(["dotnet", str(directory / "bin/Release/net10.0/probe.dll"), mode],
                      directory, environment, 40)
            print(f"{mode}: assertions passed", flush=True)


if __name__ == "__main__":
    signal.signal(signal.SIGTERM, interrupted)
    main()
