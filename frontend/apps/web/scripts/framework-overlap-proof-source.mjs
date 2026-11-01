// PRIVATE fixed copied-source proposal. No canonical import or runtime selector.
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';

function replaceOnce(source, before, after) {
  assert.equal(source.split(before).length, 2, 'unique copied overlap anchor');
  return source.replace(before, after);
}
const access = "Reflect.get(globalThis, Symbol.for('learnstack.proof-overlap-v1'))";

/** Inputs are already copied BFF+transport sources, before the genuine producer. */
export function instrumentCopiedOverlap({
  runtime,
  requestRuntime,
  snapshotPool,
  sessionStore,
  page,
  observer,
}) {
  runtime = replaceOnce(
    runtime,
    "import { readFileSync } from 'node:fs';",
    "import { readFileSync } from 'node:fs';\nimport { createCopiedOverlapProof } from './framework-proof-overlap.mjs';",
  );
  runtime = replaceOnce(
    runtime,
    '  let shutdownPromise;',
    '  let overlapProof;\n  let overlapStopStarted = false;\n  let shutdownPromise;',
  );
  runtime = replaceOnce(
    runtime,
    "    const dev = framework.mode === 'development';",
    `    const dev = framework.mode === 'development';
    overlapProof = createCopiedOverlapProof(framework);
    Object.defineProperty(globalThis, Symbol.for('learnstack.proof-overlap-v1'), { value: overlapProof });`,
  );
  runtime = replaceOnce(
    runtime,
    "      if (app) ownShutdown(() => observeShutdownJoin('next', app.close()));",
    "      if (app) ownShutdown(() => observeShutdownJoin('next', app.close()));\n      if (overlapProof) ownShutdown(() => overlapProof.close());",
  );
  runtime = replaceOnce(
    runtime,
    '    releaseProofRender = releaseRender;',
    '    releaseProofRender = () => { releaseRender(); overlapProof.releaseAll(); };',
  );
  runtime = replaceOnce(
    runtime,
    '      renderWork.add(release); framework.waitUntil(work);\n      return work.finally(() => renderWork.delete(release));',
    `      renderWork.add(release);
      const actualRender = work.finally(() => renderWork.delete(release));
      overlapProof.registerRender(actualRender, framework);
      return actualRender;`,
  );
  runtime = replaceOnce(
    runtime,
    "      if (value.action === 'arm') { if (renderArmed || renderWork.size) throw new Error('Owned render already held'); renderArmed = true; }",
    "      if (value.action === 'arm') { if (renderArmed || renderWork.size) throw new Error('Owned render already held'); overlapProof.arm(); renderArmed = true; }",
  );
  runtime = replaceOnce(
    runtime,
    "      if (value.action === 'release') releaseRender();",
    `      if (value.action === 'release') {
        if (!overlapStopStarted) { overlapProof.startNativeStop(bffRequests); overlapStopStarted = true; }
        else overlapProof.releaseNext(releaseRender);
      }
      if (value.action === 'status') overlapProof.snapshot();`,
  );
  runtime = replaceOnce(
    runtime,
    '    const fixtureAdministration = async (_token, binding, operation) => {',
    '    const fixtureAdministrationBase = async (_token, binding, operation) => {',
  );
  runtime = replaceOnce(
    runtime,
    '    const fixtureDependencies = { provider:',
    `    const fixtureAdministration = (_token, binding, operation) => {
      const hold = operation.kind === 'getOwnCapabilities' ? overlapProof.claimApi() : null;
      const actual = hold ? (async () => { await hold; return fixtureAdministrationBase(_token, binding, operation); })()
        : fixtureAdministrationBase(_token, binding, operation);
      if (hold) overlapProof.observeApi(actual);
      return actual;
    };
    const fixtureDependencies = { provider:`,
  );

  requestRuntime = replaceOnce(
    requestRuntime,
    '      frame.lease = result;\n      tickets.delete(ticket);',
    `      frame.lease = result;
      const overlap = ${access};
      if (overlap) overlap.reserve(frame, result.lease, frame.cleaned);
      tickets.delete(ticket);`,
  );
  requestRuntime = replaceOnce(
    requestRuntime,
    `        const settled = () => {
          handlerPending = false;
          release();
        };`,
    `        const settled = (outcome: 'fulfilled' | 'rejected') => {
          handlerPending = false;
          const overlap = ${access};
          if (overlap) overlap.handler(frame, outcome);
          release();
        };`,
  );
  requestRuntime = replaceOnce(
    requestRuntime,
    `            void Promise.resolve(result).then(settled, () => {
              frame.invalidate();
              settled();
            });
          else settled();
          return result;
        } catch (error) {
          frame.invalidate();
          settled();`,
    `            void Promise.resolve(result).then(() => settled('fulfilled'), () => {
              frame.invalidate();
              settled('rejected');
            });
          else settled('fulfilled');
          return result;
        } catch (error) {
          frame.invalidate();
          settled('rejected');`,
  );

  snapshotPool = replaceOnce(
    snapshotPool,
    '          work.add(promise);\n          ++generation;',
    `          work.add(promise);
          const overlap = ${access};
          if (overlap) overlap.track(lease, promise);
          ++generation;`,
  );
  snapshotPool = replaceOnce(
    snapshotPool,
    '          response.publishPage(value);\n          return value;',
    `          response.publishPage(value);
          const overlap = ${access};
          if (overlap) overlap.publish(lease);
          return value;`,
  );
  snapshotPool = replaceOnce(
    snapshotPool,
    '          return publish(entry);',
    `          const value = publish(entry);
          const overlap = ${access};
          if (overlap) overlap.publish(lease);
          return value;`,
  );

  sessionStore = replaceOnce(
    sessionStore,
    '      lease.track(work);\n      const value = await this.#wait(work, lease.signal);',
    `      lease.track(work);
      const overlap = ${access};
      if (overlap) overlap.bounded(lease, work);
      const value = await this.#wait(work, lease.signal);`,
  );

  page = replaceOnce(
    page,
    "import { AccountShell, accountShellMetadata } from '@/components/auth/shell';",
    "import { Suspense } from 'react';\nimport { AccountShell, accountShellMetadata } from '@/components/auth/shell';",
  );
  page = replaceOnce(
    page,
    "export const generateMetadata = () => accountShellMetadata('studio');",
    `async function OverlapHeldRender() {
  const gate = Reflect.get(globalThis, Symbol.for('learnstack.proof-render-gate'));
  if (typeof gate === 'function') await gate();
  return null;
}
export const generateMetadata = () => accountShellMetadata('studio');`,
  );
  page = replaceOnce(
    page,
    '  return <AccountShell kind="studio" />;',
    '  return <><AccountShell kind="studio" /><Suspense fallback={<span>Loading</span>}><OverlapHeldRender /></Suspense></>;',
  );

  observer = replaceOnce(
    observer,
    "        event('encoder-close', ended ? 'ended' : 'unended');\n        active.resolveEncoder();",
    `        event('encoder-close', ended ? 'ended' : 'unended');
        const overlap = ${access};
        if (overlap) overlap.encoderClose();
        active.resolveEncoder();`,
  );
  observer = replaceOnce(
    observer,
    "                event('response-close');\n                resolveResponse();",
    `                event('response-close');
                const overlap = ${access};
                if (overlap) overlap.responseClose();
                resolveResponse();`,
  );

  const coordinator = readFileSync(
    new URL('./framework-proof-overlap.mjs', import.meta.url),
    'utf8',
  );
  return Object.freeze({
    runtime,
    requestRuntime,
    snapshotPool,
    sessionStore,
    page,
    observer,
    coordinator,
  });
}
