# Hangfire distribution and replacement kit

ADR-0047 selects unmodified shared assemblies. These files ship with the ordinary
framework-dependent publish output under `third-party/hangfire/`. Keep the kit
beside redistributed binaries and in container images. No static linking,
single-file bundle, trimming or NativeAOT is qualified by this packet.

| Package | Pin | Corresponding source |
|---|---|---|
| Hangfire.Core / Hangfire.NetCore | 1.8.25 | `core/corresponding-source.tar.gz`, upstream commit `1d4778c23410b365b5f9ce35458202efe0b0f3ea` |
| Hangfire.PostgreSql | 1.21.1 | `postgres/corresponding-source.tar.gz`, upstream commit `e0a4001c6627695d61a0eea19d4403882e4cea34` |
| Cronos (internally merged into Hangfire.Core) | 0.11.1 | `cronos/corresponding-source.tar.gz`, upstream tag `v0.11.1`, commit `b2050abdf910bf5c3b7262d1a09d2c04c0f56f58`; MIT notice at `cronos/LICENSE` |
| Newtonsoft.Json | 13.0.4 | MIT; direct pin supersedes the Core dependency floor |

`core/` and `postgres/` include the exact upstream licence files, GPL and LGPL
texts, and applicable upstream third-party notices. The archives are complete
`git archive` exports of those exact source commits, including their build/project
files. The provider's install v3–v23 resources are byte-for-byte copies;
`script-sha256.json` pins their original bytes. Only migration execution substitutes
`public` and performs the stock installer's version bookkeeping. Provider code is
unmodified. The LearnStack guard triggers are separate integration code.

The pinned Core build internally merges Cronos 0.11.1 into its netstandard2.0
assembly. It therefore does not appear as a separate dependency in the application's
NuGet graph. The kit records this embedded dependency separately and includes its
complete source archive and original MIT copyright/permission notice. Its source
tag resolves to the commit above; the verifier requires that exact identity and
its relationship to the unchanged Core assembly.

The Dapper/Dapper.AOT notices and Core's third-party notices reference Apache 2.0.
The complete authoritative [Apache licence text](https://www.apache.org/licenses/LICENSE-2.0.txt)
is included once at `dependency-notices/Apache-2.0.txt` alongside the original
upstream notices. These bytes and all stock SQL resources are checkout-stable;
the narrow `.gitattributes` rules preserve their original line endings and notice
whitespace. First-party guards, manifests and documentation retain normal checks.

Recipients may modify these LGPL libraries and replace/relink the corresponding
shared assemblies for their own use. No LearnStack term restricts reverse
engineering needed to debug those modifications. Extract the matching archive,
follow its upstream build instructions/project files and build compatible shared
assemblies (Core/PostgreSql netstandard2.0; NetCore netstandard2.1). Keep the public
API/assembly identity compatible. Stop the host, back up the original assembly,
replace `Hangfire.Core.dll`, `Hangfire.NetCore.dll` and/or
`Hangfire.PostgreSql.dll` in the publish directory, and restart the framework
host. A container recipient can do the same in a derived image. Rebuilding
LearnStack against a modified compatible library is also permitted; the source
project's PackageReferences are ordinary removable references, with no signature
or checksum enforcement at runtime. Production qualification of a changed provider
remains the deployer's responsibility.

Check the shared assemblies, corresponding source, licence kit and concrete
resolved dependency graph from the repository root:

```sh
python3 third-party/hangfire/verify-distribution.py path/to/publish \
  --assets backend/src/LearnStack.Api/obj/project.assets.json \
  --infrastructure-assets backend/src/LearnStack.Infrastructure/obj/project.assets.json
```

The verifier checks both linked API/Infrastructure build graphs alongside the
published graph: ASP.NET Core prunes Hosting from the API graph and publish
output; the referenced Infrastructure graph resolves its exact pin. It records the
runtime framework reference and does not infer the installed runtime patch from a
NuGet pin. Source and licence hashes are verified. Original-distribution
qualification compares the four shared assemblies with the original NuGet bytes;
`--allow-replacements` records recipient-modified assemblies instead. This offline
check imposes no runtime restriction on replacement assemblies.

Run `verify-distribution-controls.py` with the same publish/asset arguments for
negative controls: wrong build/shipped pins, absent/corrupt source and licences,
including the internally merged Cronos source/notice/pin, missing/modified shared
assemblies, absent framework reference, and explicit
recipient-replacement inspection. It mutates only a temporary copy and emits its
receipt to stdout.

Isolated maintenance/timeout decision experiments and their prerequisites live in
[qualification/](qualification/README.md). They are not production initialization
or substitutes for the granted-role and actual-worker regression gates.
