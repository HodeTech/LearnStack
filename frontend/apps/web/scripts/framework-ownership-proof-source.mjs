// Closed instrumentation of owned copies; never imported by the product runtime.
import assert from 'node:assert/strict';

function replace(source, needle, value) {
  assert.equal(source.split(needle).length, 2, 'unique ownership instrumentation anchor');
  return source.replace(needle, value);
}

const records = `
import { appendFileSync as appendOwnership, existsSync as ownershipExists, writeFileSync as writeOwnership } from 'node:fs';
import { resolve as ownershipResolve } from 'node:path';
const ownershipDirectory = ownershipResolve(process.cwd(), '../../../ownership-control');
function recordOwnership(event: string) {
  appendOwnership(ownershipResolve(ownershipDirectory, 'events.jsonl'), JSON.stringify({event, pid: process.pid, parentPid: process.ppid}) + '\\n', {mode: 0o600});
}
`;

/** Exactly two copied-source controls, with no caller-supplied code or capabilities. */
export function instrumentFrameworkOwnershipProof({ ownershipCase, configuration, runtime }) {
  assert.ok(
    ['prepare-sigterm', 'worker-positive'].includes(ownershipCase),
    'closed ownership case',
  );
  runtime = records.replace('event: string', 'event') + runtime;
  runtime = replace(
    runtime,
    '    stopping = true;',
    `    stopping = true;
    if (!control.stopRequested()) throw new Error('Ownership stop was not consumed');
    recordOwnership('stop-consumed');`,
  );
  runtime = replace(
    runtime,
    '    await ownSetup(app.prepare());',
    `    recordOwnership('prepare-called');
    try { await ownSetup(app.prepare()); }
    finally { recordOwnership('prepare-settled'); }`,
  );
  runtime = replace(
    runtime,
    'if (app) ownShutdown(() => app.close());',
    `if (app) ownShutdown(() => {
        recordOwnership('next-close-called');
        return Promise.resolve(app.close()).then(() => recordOwnership('next-close-settled'));
      });`,
  );
  runtime = replace(
    runtime,
    "          server.listen(0, '127.0.0.1', () => {",
    "          recordOwnership('native-listen');\n          server.listen(0, '127.0.0.1', () => {",
  );
  if (ownershipCase === 'prepare-sigterm') {
    configuration = records + configuration;
    configuration = replace(
      configuration,
      'export default nextConfig;',
      `export default async function configuration(phase: string) {
  if (phase === 'phase-production-server' && ownershipExists(ownershipResolve(ownershipDirectory, 'prepare.arm')) && !ownershipExists(ownershipResolve(ownershipDirectory, 'prepare.entered'))) {
    writeOwnership(ownershipResolve(ownershipDirectory, 'prepare.entered'), '', {flag: 'wx', mode: 0o600});
    recordOwnership('configuration-entered');
    while (!ownershipExists(ownershipResolve(ownershipDirectory, 'prepare.release'))) await new Promise((resolve) => setTimeout(resolve, 10));
    recordOwnership('configuration-released');
  }
  return nextConfig;
}`,
    );
  }
  const page =
    ownershipCase === 'worker-positive'
      ? `${records}
export async function generateStaticParams() {
  if (process.env.NODE_ENV === 'development' && ownershipExists(ownershipResolve(ownershipDirectory, 'worker.arm'))) {
    writeOwnership(ownershipResolve(ownershipDirectory, 'worker.entered'), '', {flag: 'wx', mode: 0o600});
    recordOwnership('worker-entered');
    while (!ownershipExists(ownershipResolve(ownershipDirectory, 'worker.release'))) await new Promise((resolve) => setTimeout(resolve, 10));
    recordOwnership('worker-returned');
  }
  return [{slug: 'ownership'}];
}
export default function Page() { return <main>Owned static paths worker</main>; }
`
      : undefined;
  return Object.freeze({ configuration, runtime, page });
}
