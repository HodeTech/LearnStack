#!/usr/bin/env python3
"""Isolated exact-pin qualification, not production runtime or a worker proof.

Requires Python 3, Docker and .NET 10. Builds only in a disposable directory.
The database uses trust authentication in an owned, loopback-only container;
no application configuration, database credentials or connection strings enter
the receipt. A 180-second whole-run deadline includes build and execution;
cleanup has its own finite allowance and runs on failure or interruption.
"""

import json
import os
from pathlib import Path
import shutil
import signal
import subprocess
import tempfile
import time
import uuid


IMAGE = "postgres:18.4"
PACKAGES = {"Hangfire.Core": "1.8.25", "Hangfire.PostgreSql": "1.21.1",
            "Npgsql": "10.0.0", "Newtonsoft.Json": "13.0.4"}


def main():
    def interrupted(_signal, _frame):
        raise KeyboardInterrupt

    signal.signal(signal.SIGTERM, interrupted)
    deadline = time.monotonic() + 180
    container_id = None
    container_start_requested = False
    container_name = "learnstack-maintenance-probe-" + uuid.uuid4().hex
    receipt = {"pass": False, "kind": "isolated_qualification", "packages": PACKAGES}
    # Deliberately exclude PG*, connection strings, GIT*, DOTNET_STARTUP_HOOKS,
    # NUGET_*, and application settings from child processes.
    environment = {key: os.environ[key] for key in ("PATH", "HOME", "USER", "SYSTEMROOT")
                   if key in os.environ}
    environment.update(DOTNET_CLI_TELEMETRY_OPTOUT="1", DOTNET_NOLOGO="1",
                       DOTNET_SKIP_FIRST_TIME_EXPERIENCE="1")

    def command(arguments, directory, limit=60, cleanup=False):
        remaining = limit if cleanup else min(limit, deadline - time.monotonic())
        if remaining <= 0:
            raise TimeoutError("whole_deadline")
        # File-backed output prevents unbounded pipe capture. Never expose tool
        # diagnostics: build errors can contain private paths or environment data.
        with tempfile.TemporaryFile() as output:
            process = subprocess.Popen(arguments, cwd=directory, env=environment,
                                       stdout=output, stderr=subprocess.STDOUT,
                                       start_new_session=True)
            try:
                status = process.wait(timeout=remaining)
            except BaseException:
                try:
                    os.killpg(process.pid, signal.SIGKILL)
                except ProcessLookupError:
                    pass
                process.wait(timeout=5)
                raise
            output.seek(0)
            data = output.read(32769)
            if status != 0 or len(data) > 32768:
                raise RuntimeError("subprocess_failed_or_output_limit")
            return data.decode("utf-8").strip()

    with tempfile.TemporaryDirectory(prefix="learnstack-maintenance-probe-") as temporary:
        directory = Path(temporary)
        environment["TMPDIR"] = temporary
        phase = "prepare"
        try:
            shutil.copyfile(Path(__file__).with_name("MaintenanceProbe.cs"), directory / "Program.cs")
            references = "\n".join(
                f'<PackageReference Include="{name}" Version="[{version}]" />'
                for name, version in PACKAGES.items())
            (directory / "Probe.csproj").write_text(
                '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup>'
                '<OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework>'
                '<ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable>'
                '<ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally>'
                '<NuGetAudit>false</NuGetAudit></PropertyGroup><ItemGroup>' + references +
                '</ItemGroup></Project>', encoding="utf-8")
            (directory / "global.json").write_text(
                '{"sdk":{"version":"10.0.100","rollForward":"latestFeature",'
                '"allowPrerelease":false}}', encoding="utf-8")
            (directory / "NuGet.Config").write_text(
                '<configuration><packageSources><clear />'
                '<add key="nuget.org" value="https://api.nuget.org/v3/index.json" />'
                '</packageSources></configuration>', encoding="utf-8")
            phase = "standalone_build"
            command(["dotnet", "build", "Probe.csproj", "--configuration", "Release",
                     "--nologo", "--verbosity", "quiet", "--configfile", "NuGet.Config",
                     "-p:ImportDirectoryBuildProps=false", "-p:ImportDirectoryBuildTargets=false"],
                    temporary, limit=90)
            phase = "container_start"
            container_start_requested = True
            started = command(
                ["docker", "run", "--detach", "--name", container_name,
                 "--label", "learnstack.qualification=hangfire-maintenance",
                 "--publish", "127.0.0.1::5432", "--env", "POSTGRES_HOST_AUTH_METHOD=trust",
                 IMAGE], temporary, limit=45)
            candidate_id = started.splitlines()[-1]
            if not (len(candidate_id) == 64 and all(c in "0123456789abcdef" for c in candidate_id)):
                raise RuntimeError("invalid_container_id")
            container_id = candidate_id
            port = int(command(["docker", "inspect", "--format",
                                '{{(index (index .NetworkSettings.Ports "5432/tcp") 0).HostPort}}',
                                container_id], temporary, limit=5))
            # The harness retries only the disposable server's readiness with a
            # finite deadline; no user database is read or selected.
            phase = "actual_component_probe"
            raw = command(["dotnet", str(directory / "bin/Release/net10.0/Probe.dll"), str(port)],
                          temporary, limit=75)
            result = json.loads(raw)
            if not isinstance(result, dict) or result.get("pass") is not True:
                raise RuntimeError("qualification_failed")
            receipt.update(result)
        except KeyboardInterrupt:
            receipt["failure"] = "interrupted"
        except TimeoutError:
            receipt["failure"] = "whole_deadline"
        except subprocess.TimeoutExpired:
            receipt["failure"] = "subprocess_deadline"
        except Exception:
            receipt["failure"] = "qualification_failed"
        finally:
            # Preserve cleanup if a second interrupt arrives during teardown.
            signal.signal(signal.SIGINT, signal.SIG_IGN)
            signal.signal(signal.SIGTERM, signal.SIG_IGN)
            if not receipt["pass"]:
                receipt["failure_phase"] = phase
            # The name also covers an interrupted docker run whose ID did not
            # reach stdout. Both identifiers belong exclusively to this run.
            try:
                if container_start_requested:
                    command(["docker", "rm", "--force", container_id or container_name],
                            temporary, limit=10, cleanup=True)
                receipt["owned_container_removed"] = True
            except Exception:
                receipt["owned_container_removed"] = False
                receipt["pass"] = False
                receipt["failure"] = "owned_container_cleanup_failed"
    receipt["temporary_directory_removed"] = True
    print(json.dumps(receipt, separators=(",", ":"), sort_keys=True))
    return 0 if receipt["pass"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
