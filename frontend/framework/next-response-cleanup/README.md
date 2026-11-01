# Next response cleanup maintenance

[ADR-0083](../../../docs/decisions/0083-owned-next-response-compression-cleanup.md)
records the decision. [Standard 07](../../../docs/standards/07-frontend-architecture.md#version-qualified-next-response-cleanup)
owns the installation, generated-output and response-ownership contracts. This
file describes the supported commands; [Phase 03](../../../docs/roadmap/phase-03-identity-admin.md)
owns delivery and qualification state.

## Restore and run

Read the exact current Node/pnpm/Next tuple from [census.json](census.json).
The engine declarations are additional controls; the installer independently
checks the executable it invokes and the installed correction.

From the repository root:

```bash
node frontend/scripts/install-frontend.mjs
```

This performs the frozen restore under an installation owner. Raw `pnpm install`
refuses before dependency mutation. Do not disable the project hook, lifecycle
scripts or patch application to make restoration proceed.

From `frontend/`:

```bash
pnpm --filter @learnstack/web build
pnpm --filter @learnstack/web start
# Or start the owned development producer and native launcher:
pnpm --filter @learnstack/web dev
```

Production build starts from owned clean output and writes
`.next/framework-build.json` last. Start verifies the current inputs and exact
runtime graph before application/framework imports or listener creation. A
corrected package beside old `.next` output, including its old manifest, refuses.
There is no command that signs an arbitrary existing output directory.

For an intentional dependency-manifest change, from the repository root:

```bash
node frontend/scripts/install-frontend.mjs --prepare-lock
# Review the manifest and lockfile diff, then restore normally:
node frontend/scripts/install-frontend.mjs
```

`--prepare-lock` updates the lock under the same manager/installation ownership;
it does not establish a restored or qualified runtime.

## Regenerate the correction

Use a separate pristine copy of the locked original Next distribution. Keep the
installed package and shared pnpm store untouched. [originals.json](originals.json)
owns the qualified input membership and digests; [owner.mjs](owner.mjs) is the
readable ownership input. The generator refuses changed or unmatched inputs.

From the repository root, supply absolute private paths; the output directory
must not already exist:

```bash
node frontend/scripts/generate-next-response-cleanup.mjs \
  --package-root /absolute/private/pristine-next \
  --output-root /absolute/private/new-directory
```

The output contains `next@15.5.28.patch`, `census.json` and the separate original/
patched subject trees. Review the readable transformation inputs and generated
results. Reproduce generation from another clean output directory and compare
exact bytes before replacing the tracked patch and census. Then use the guarded
lock update and frozen restore above, preserving the exact patch reference.

The generated maps use `*.js.learnstack.map`; each actual source-map directive
points to its new sibling. The patch deletes the old maps and adds complete
replacement maps. Pnpm's deletion effect does not verify the old-map preimage.
Original digests are checked by generation; installation relies on frozen
package integrity and verification of the exact 30-JS/29-map patched subject set,
obsolete-map absence and distribution membership.

A changed tuple, generator, readable owner or correction needs the applicable
map/owner/install/build controls, independent reviews and complete source-bound
qualification required by Standard 07. A successful generation or restore is no
substitute for those gates. Do not hand-edit installed files, suppress a missing
patch, or rewrite the historical initial tuple in the Accepted ADR.

## Refused or older output

Preserve an unknown output or ownership record while identifying its producer
and consumers. After confirming no consumer remains, move identified older
unowned generated output to a private backup before the managed build. Do not
recursively delete unowned output, copy a manifest onto another build, or remove
an ownership record solely because it is old. The normal producer recreates
its own output; it does not repair an ambiguous installation or acknowledge
unfinished work.
