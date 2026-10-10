// Test-only development recompile and live admission proof over the shipped launcher.
import assert from 'node:assert/strict';
import { readFileSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';

export async function verifyHmrAdmission({
  app,
  configuration,
  checkpoint,
  startNative,
  stopNative,
  call,
  observe,
  watchSource,
}) {
  const assertReleased = async (child) => {
    const counts = await observe(child);
    assert.equal(counts.active, 0, 'Completed development render releases its admission');
    assert.equal(counts.snapshots, 0, 'Completed development render releases its snapshot');
  };
  const path = join(app, 'src/app/(studio)/studio/page.tsx');
  const original = readFileSync(path, 'utf8');
  const page = (
    marker,
  ) => `import { getPublicRequest, assertPublicRequestActive } from '@/server/public-request';
export const dynamic = 'force-dynamic';
export default async function HmrAdmissionProbe() {
  const request = await getPublicRequest();
  if (!request) throw new Error('HMR admission unavailable');
  assertPublicRequestActive(request);
  return <main><h1>{request.site.displayName}</h1><p>${marker}</p></main>;
}
`;
  let child;
  let watcher;
  let changed = false;
  let stage = 'hmr-setup';
  try {
    writeFileSync(path, page('admission-hmr-before'));
    child = await startNative(true);
    const pid = child.pid;
    await checkpoint('hmr-before');
    stage = 'hmr-initial-request';
    const before = await call('/studio');
    stage = 'hmr-initial-status';
    assert.equal(before.status, 200, 'Development admission positive control');
    stage = 'hmr-initial-source';
    assert.match(before.body, /admission-hmr-before/);
    stage = 'hmr-initial-site';
    assert.ok(before.body.includes(configuration.tenants[0].name), 'Original live site name');
    stage = 'hmr-initial-release';
    await assertReleased(child);
    stage = 'hmr-watch-connect';
    watcher = await watchSource();
    stage = 'hmr-change';
    await checkpoint('hmr-change');
    changed = true;
    stage = 'hmr-source-barrier';
    await watcher.afterWrite(() => writeFileSync(path, page('admission-hmr-after')));
    stage = 'hmr-refresh-request';
    const after = await call('/studio', {
      headers: { RSC: '1', 'Next-Hmr-Refresh': '1' },
    });
    stage = 'hmr-refresh-status';
    assert.equal(after.status, 200, 'Development HMR render succeeds');
    stage = 'hmr-refresh-type';
    assert.match(after.headers['content-type'], /^text\/x-component/);
    stage = 'hmr-refresh-source';
    assert.match(
      after.body,
      /admission-hmr-after/,
      'Changed source recompiles in the same process',
    );
    assert.equal(after.body.includes('admission-hmr-before'), false, 'Old source is not reused');
    stage = 'hmr-refresh-site';
    assert.ok(
      after.body.includes(configuration.tenants[0].name + ' HMR refresh'),
      'HMR reads the changed live API site instead of retaining the old snapshot',
    );
    assert.equal(child.pid, pid, 'Both renders use the same native development process');
    assert.equal(child.exitCode, null);
    stage = 'hmr-refresh-release';
    await assertReleased(child);
    stage = 'hmr-after';
    await checkpoint('hmr-after');
  } catch {
    // Closed diagnostic token only; never forward request, response or provider text.
    const failure = new Error('Development admission proof failed');
    Object.defineProperty(failure, 'admissionStage', { value: stage });
    throw failure;
  } finally {
    watcher?.close();
    try {
      writeFileSync(path, original);
    } finally {
      try {
        if (child) await stopNative(child);
      } finally {
        if (changed) await checkpoint('hmr-restore');
      }
    }
  }
}
