#!/usr/bin/env python3
"""Qualify original shared assemblies, source/notices and the concrete build/publish graphs."""
import argparse
import hashlib
import json
import pathlib

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("publish", type=pathlib.Path)
parser.add_argument("--assets", required=True, type=pathlib.Path, help="Actual API obj/project.assets.json used for this publish")
parser.add_argument("--infrastructure-assets", required=True, type=pathlib.Path, help="Actual referenced Infrastructure obj/project.assets.json before API framework pruning")
parser.add_argument("--allow-replacements", action="store_true", help="Record modified library bytes instead of qualifying the original stock distribution")
args = parser.parse_args()
publish = args.publish.resolve()
kit = publish / "third-party" / "hangfire"


def digest(path):
    if not path.is_file():
        raise SystemExit(f"Missing distribution material: {path.relative_to(publish)}")
    return hashlib.sha256(path.read_bytes()).hexdigest()


expected = {"Hangfire.Core": "1.8.25", "Hangfire.NetCore": "1.8.25", "Hangfire.PostgreSql": "1.21.1", "Newtonsoft.Json": "13.0.4", "Npgsql": "10.0.0", "Microsoft.Extensions.Hosting": "10.0.12"}
source_commits = {"core": "1d4778c23410b365b5f9ce35458202efe0b0f3ea", "postgres": "e0a4001c6627695d61a0eea19d4403882e4cea34"}
material_paths = {f"{component}/{name}" for component in source_commits for name in ("COPYING", "COPYING.LESSER", "LICENSE.md")}
material_paths |= {"core/NOTICES", "dependency-notices/Apache-2.0.txt", "dependency-notices/Dapper-2.0.123-LICENSE.txt", "dependency-notices/Dapper.AOT-1.0.48-LICENSE.txt", "dependency-notices/Newtonsoft.Json-13.0.4-LICENSE.md"}
material = json.loads((kit / "material-sha256.json").read_text())
if set(material) != material_paths:
    raise SystemExit("Licence/notice manifest inventory differs from the qualified distribution")
for name, sha in material.items():
    if digest(kit / name) != sha:
        raise SystemExit(f"Changed licence/notice material: {name}")
sources = json.loads((kit / "source-sha256.json").read_text())
if set(sources) != set(source_commits):
    raise SystemExit("Corresponding source inventory differs from the qualified distribution")
for component, commit in source_commits.items():
    if sources[component]["commit"] != commit or digest(kit / component / "corresponding-source.tar.gz") != sources[component]["sha256"]:
        raise SystemExit(f"Unqualified corresponding source: {component}")

assets_bytes = args.assets.read_bytes()
assets = json.loads(assets_bytes)
resolved = assets["libraries"]
infrastructure_bytes = args.infrastructure_assets.read_bytes()
infrastructure_assets = json.loads(infrastructure_bytes)
infrastructure_resolved = infrastructure_assets["libraries"]
api_project = pathlib.Path(assets["project"]["restore"]["projectPath"])
infrastructure_project = pathlib.Path(infrastructure_assets["project"]["restore"]["projectPath"]).resolve()
references = [metadata for name, metadata in resolved.items() if name.startswith("LearnStack.Infrastructure/") and metadata["type"] == "project"]
if len(references) != 1 or (api_project.parent / references[0]["msbuildProject"]).resolve() != infrastructure_project:
    raise SystemExit("Infrastructure build graph does not match the API's actual project reference")
deps_files = list(publish.glob("*.deps.json"))
runtime_files = list(publish.glob("*.runtimeconfig.json"))
if len(deps_files) != 1 or len(runtime_files) != 1:
    raise SystemExit("Expected one published application dependency graph and runtime configuration")
shipped = json.loads(deps_files[0].read_text())["libraries"]
frameworks = json.loads(runtime_files[0].read_text())["runtimeOptions"].get("frameworks", [])
for name, version in expected.items():
    key = f"{name}/{version}"
    if key not in infrastructure_resolved or (name != "Microsoft.Extensions.Hosting" and key not in resolved):
        raise SystemExit(f"Unqualified resolved build package: {name} {version}")
    if name != "Microsoft.Extensions.Hosting" and key not in shipped:
        raise SystemExit(f"Missing published package: {name} {version}")
if "Microsoft.Extensions.Hosting/10.0.12" not in shipped and not any(f["name"] == "Microsoft.AspNetCore.App" and f["version"].startswith("10.") for f in frameworks):
    raise SystemExit("Hosting was pruned without a .NET 10 ASP.NET Core shared-framework reference")
for package in set(shipped) | set(resolved) | set(infrastructure_resolved):
    if package.startswith(("Hangfire.SqlServer/", "Hangfire.AspNetCore/", "Hangfire.Pro/", "Hangfire.Ace/")):
        raise SystemExit(f"Unexpected provider package: {package}")

original = json.loads((kit / "original-binaries.json").read_text())
required = {name + ".dll" for name in ("Hangfire.Core", "Hangfire.NetCore", "Hangfire.PostgreSql", "Newtonsoft.Json")}
if set(original) != required:
    raise SystemExit("Replaceable shared-assembly inventory differs from the qualified distribution")
assembly_hashes = {name: digest(publish / name) for name in required}
stock = {name: assembly_hashes[name] == metadata["sha256"] for name, metadata in original.items()}
if not all(stock.values()) and not args.allow_replacements:
    raise SystemExit("Modified shared assembly: use --allow-replacements to inspect a recipient-modified library distribution")
print(json.dumps({
    "resolved_build_packages": sorted(resolved),
    "published_packages": sorted(shipped),
    "infrastructure_build_packages": sorted(infrastructure_resolved),
    "infrastructure_build_assets_sha256": hashlib.sha256(infrastructure_bytes).hexdigest(),
    "shared_framework_references": frameworks,
    "hosting_asset_source": "package" if "Microsoft.Extensions.Hosting/10.0.12" in shipped else "shared-framework (build graph pin verified; runtime patch selected by .NET host)",
    "build_assets_sha256": hashlib.sha256(assets_bytes).hexdigest(),
    "original_stock_assemblies": stock,
    "shared_assembly_sha256": assembly_hashes,
    "corresponding_source_verified": source_commits,
    "licence_notice_files_verified": sorted(material),
}, indent=2))
