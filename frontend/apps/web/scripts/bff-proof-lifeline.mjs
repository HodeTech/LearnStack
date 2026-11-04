import { writeSync } from 'node:fs';
import { Worker, isMainThread } from 'node:worker_threads';

// Node may preload this module in ordinary worker threads even after execArgv
// is stripped. Those threads stay silent and receive no group authority.
export let proofParentWatcher;
if (isMainThread) {
  const field = 'LEARNSTACK_TEST_LIFELINE_PARENT';
  const raw = process.env[field];
  delete process.env[field];
  const expected = typeof raw === 'string' && /^[1-9][0-9]*$/.test(raw) ? Number(raw) : NaN;
  const matches = process.execArgv.flatMap((value, index, args) =>
    value === '--import' && args[index + 1] === import.meta.url ? [index] : [],
  );
  if (
    process.platform === 'win32' ||
    !Number.isSafeInteger(expected) ||
    expected <= 1 ||
    matches.length !== 1 ||
    process.ppid !== expected
  )
    throw new Error('Invalid BFF lifeline ownership');
  process.execArgv.splice(matches[0], 2);
  const watcher = new Worker(new URL('./bff-proof-parent-watch.mjs', import.meta.url), {
    execArgv: [],
    workerData: { parentPid: expected, groupPid: process.pid },
  });
  let ready = false;
  let failed = false;
  await new Promise((resolve, reject) => {
    const fail = () => {
      // Failure notification needs the leader's event loop. Parent-loss
      // protection assumes a live, runnable watcher; losing that watcher while
      // the leader blocks and then losing the owner is outside this control.
      if (failed) return;
      failed = true;
      if (!ready) {
        // An invalid readiness message must not leave the worker referenced.
        void watcher.terminate();
        watcher.unref();
        reject(new Error('BFF parent watcher failed'));
      } else {
        try {
          writeSync(2, 'BFF parent watcher failed\n');
        } finally {
          process.exit(1);
        }
      }
    };
    watcher.once('error', fail);
    watcher.once('exit', fail);
    watcher.once('message', (value) => {
      if (value !== 'ready') {
        fail();
        return;
      }
      ready = true;
      resolve();
    });
  });
  watcher.unref();
  // Exported for the real post-ready termination control.
  proofParentWatcher = watcher;
}
