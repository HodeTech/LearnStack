import assert from 'node:assert/strict';
import { randomBytes, createHash } from 'node:crypto';
import { existsSync, mkdirSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { cpus, homedir, platform, release } from 'node:os';
import { join } from 'node:path';
import { setTimeout as pause } from 'node:timers/promises';
import { fileURLToPath } from 'node:url';

import { chromium } from 'playwright';

import {
  digest,
  reportAdvisories,
  reportInventory,
  validateReportPair,
} from './public-demo-contract.mjs';
import {
  PublicDemoFailure,
  publicDemoChildExited,
  publicDemoFailure,
  publicDemoOperation,
  publicDemoWorkerStage,
  publicDemoWorkerOutput,
} from './public-demo-diagnostics.mjs';
import { stopTestChild } from './stop-test-child.mjs';
import { demoRequest } from '../../../../scripts/demo-http.mjs';
import { completionDelta, finalCompletionCensus } from '../../../../scripts/demo-observation.mjs';
import { DemoCommandError } from '../../../../scripts/demo-process.mjs';
import { demoSource } from '../../../../scripts/demo-source.mjs';

const root = fileURLToPath(new URL('../../../../', import.meta.url));
const worker = fileURLToPath(new URL('./public-demo-worker.mjs', import.meta.url));
const snapshot = (state) => JSON.parse(JSON.stringify(state));
const census = JSON.parse(
  readFileSync(new URL('./public-demo-census.json', import.meta.url), 'utf8'),
);

/** Four paced batches over one unchanged production API/build/database. */
export async function verifyPublicDemo(runtime) {
  const started = Date.now();
  const deadline = started + 12 * 60 * 1000;
  const remaining = (bound) => {
    runtime.processes.assertActive();
    if (Date.now() >= deadline) throw new PublicDemoFailure('deadline');
    return Math.min(bound, deadline - Date.now());
  };
  const output = join(root, 'artifacts/public-demo');
  let outputOwned = false;
  let failure;
  let phase = 'setup';
  const identity = {
    invocation: randomBytes(16).toString('hex'),
    commit: null,
    tree: null,
    build: null,
  };
  let hosts;
  let spki;
  let expected;
  const childrenEnv = {
    PATH: process.env.PATH,
    HOME: runtime.privateRoot,
    TMPDIR: runtime.privateRoot,
    NODE_TLS_REJECT_UNAUTHORIZED: '1',
    NEXT_TELEMETRY_DISABLED: '1',
  };
  const receipt = {
    identity,
    node: process.version,
    os: platform() + ' ' + release(),
    cpus: cpus().length,
    runnerImage: process.env.ImageOS ?? null,
    versions: {
      lighthouse: census.versions.lighthouse,
      playwright: census.versions.playwright,
      chromiumRevision: census.versions.chromiumRevision,
    },
    configDigest: digest(JSON.stringify(census)),
    setupMilliseconds: runtime.setupMilliseconds,
    batches: [],
    reports: [],
    tls: [],
    proofMilliseconds: null,
    passed: false,
  };
  receipt.configuration = census;
  const save = () => {
    if (!outputOwned) return false;
    try {
      writeFileSync(join(output, 'receipt.json'), JSON.stringify(receipt, null, 2));
      return true;
    } catch (error) {
      receipt.passed = false;
      receipt.failure ??= publicDemoFailure('result', error);
      failure ??= error;
      return false;
    }
  };
  let operationIndex = 0;
  async function operation(input, trust = spki) {
    const index = operationIndex++;
    const profile = join(runtime.privateRoot, `chromium-${index}`);
    const resultFile = join(runtime.privateRoot, `browser-result-${index}.json`);
    const inputFile = join(runtime.privateRoot, `browser-input-${index}.json`);
    let browser;
    let profileOwned = false;
    return publicDemoOperation({
      input,
      signal: runtime.signal,
      child: () => browser?.child,
      record: (failure) => {
        receipt.failure ??= failure;
        save();
      },
      run: async (stage) => {
        mkdirSync(profile, { mode: 0o700 });
        profileOwned = true;
        browser = runtime.processes.start(
          chromium.executablePath(),
          [
            '--headless=new',
            '--remote-debugging-address=127.0.0.1',
            '--remote-debugging-port=0',
            `--user-data-dir=${profile}`,
            '--no-first-run',
            '--no-default-browser-check',
            '--disable-background-networking',
            '--disable-component-update',
            '--disable-sync',
            '--no-proxy-server',
            `--host-resolver-rules=${hosts.map((host) => `MAP ${host} 127.0.0.1`).join(',')}`,
            ...(trust ? [`--ignore-certificate-errors-spki-list=${trust}`] : []),
            'about:blank',
          ],
          {
            cwd: root,
            // macOS's native certificate verifier needs the real account home.
            // Chromium data stays in its explicitly owned disposable profile.
            env: { ...childrenEnv, ...(platform() === 'darwin' ? { HOME: homedir() } : {}) },
            label: 'Owned managed Chromium',
          },
        );
        stage('startup-port');
        const portFile = join(profile, 'DevToolsActivePort');
        const startupDeadline = Date.now() + remaining(10000);
        while (!existsSync(portFile)) {
          remaining(10000);
          if (browser.result().failed)
            throw new DemoCommandError(
              'Owned managed Chromium',
              browser.result().failed,
              undefined,
              {
                reason: browser.result().failureReason,
              },
            );
          if (publicDemoChildExited(browser.child)) throw new PublicDemoFailure('chromium-exited');
          if (Date.now() >= startupDeadline) throw new PublicDemoFailure('deadline');
          await pause(50, undefined, { signal: runtime.signal });
        }
        const port = Number(readFileSync(portFile, 'utf8').split('\n')[0]);
        if (!(port > 0 && port <= 65535)) throw new PublicDemoFailure('port-invalid');
        writeFileSync(
          inputFile,
          JSON.stringify({
            ...input,
            port,
            lanes: runtime.manifest,
            result: resultFile,
            output,
            identity,
          }),
          { mode: 0o600, flag: 'wx' },
        );
        stage('worker');
        try {
          await runtime.processes.run('node', [worker, inputFile], {
            cwd: root,
            env: childrenEnv,
            label: `Public ${input.mode} proof`,
            timeout: remaining(input.mode === 'browser' ? 135000 : 60000),
            // Fixed assertions/phase names only; provider/document logs stay suppressed.
            observe: (chunk) => {
              for (const line of publicDemoWorkerOutput(chunk)) console.error(line);
            },
          });
        } catch (error) {
          try {
            input.workerStage = publicDemoWorkerStage(
              JSON.parse(readFileSync(resultFile, 'utf8'))?.failure?.stage,
            );
          } catch {
            /* A missing/incomplete private result supplies no public diagnostic data. */
          }
          throw error;
        }
        stage('result');
        return JSON.parse(readFileSync(resultFile, 'utf8'));
      },
      cleanup: async () => {
        if (browser) {
          try {
            await stopTestChild(browser.child);
          } catch (error) {
            throw new PublicDemoFailure('process-cleanup', error);
          }
          // The outer process owner performs the separately bounded stream drain.
        }
        if (profileOwned) {
          try {
            rmSync(profile, { recursive: true, force: true });
          } catch (error) {
            throw new PublicDemoFailure('profile-cleanup', error);
          }
        }
      },
    });
  }
  async function quiet() {
    remaining(61000);
    if (deadline - Date.now() < 61000) throw new PublicDemoFailure('deadline');
    const before = runtime.completions.total;
    await pause(61000, undefined, { signal: runtime.signal });
    assert.equal(runtime.completions.total, before, 'Quiet window has no hidden product traffic');
  }
  async function finishBatch(before, expected, name) {
    // The API finishes before returning its document; allow only log delivery,
    // never a retry or extra request, to catch up on its pipe.
    const until = Date.now() + remaining(2000);
    while (
      (runtime.completions.total - before.total < expected || runtime.completions.pendingBytes) &&
      Date.now() < until
    )
      await pause(20, undefined, { signal: runtime.signal });
    receipt.batches.push({ name, ...completionDelta(before, runtime.completions, expected) });
    if (!save()) throw failure;
  }
  try {
    if (existsSync(output)) throw new Error('Public proof artifact directory must be fresh');
    mkdirSync(output, { recursive: true });
    outputOwned = true;
    if (!save()) throw failure;
    assert.equal(runtime.mode, 'ci');
    assert.ok(runtime.source, 'Source captured before CI setup/build');
    const source = await demoSource(runtime.processes, root, remaining, runtime.source);
    identity.commit = source.commit;
    identity.tree = source.tree;
    identity.build = readFileSync(join(root, 'frontend/apps/web/.next/BUILD_ID'), 'utf8').trim();
    assert.match(identity.commit, /^[a-f0-9]{40}$/);
    hosts = [...new Set(runtime.manifest.map((lane) => lane.host))];
    spki = createHash('sha256')
      .update(runtime.leaf.publicKey.export({ type: 'spki', format: 'der' }))
      .digest('base64');
    expected = runtime.manifest.flatMap((lane) =>
      ['catalog', 'course', 'lesson'].flatMap((kind) =>
        [1, 2, 3].map((run) => ({
          ...identity,
          url: lane[kind + 'Url'],
          run,
          configDigest: receipt.configDigest,
          browserVersion: census.versions.browserVersion,
        })),
      ),
    );
    receipt.expectedReports = expected;
    phase = 'proof';
    // Positive observer control uses already completed readiness, then a fresh
    // product document through independent CA/hostname verification.
    assert.equal(runtime.completions.total, 6, 'Three two-call readiness documents completed');
    receipt.startupCompletions = snapshot(runtime.completions);
    const beforeControl = snapshot(runtime.completions);
    const control = await demoRequest(runtime.manifest[0].lessonUrl, {
      ca: runtime.ca,
      signal: runtime.signal,
      loopback: true,
    });
    assert.equal(control.status, 200);
    await finishBatch(beforeControl, 2, 'observer-positive');
    const beforeTls = snapshot(runtime.completions);
    for (const trust of [null, randomBytes(32).toString('base64')])
      receipt.tls.push(await operation({ mode: 'tls-refusal' }, trust));
    assert.equal(
      runtime.completions.total,
      beforeTls.total,
      'Untrusted browser TLS never reaches API',
    );
    receipt.timeoutControl = await operation({ mode: 'timeout-control' });
    assert.equal(receipt.timeoutControl.refused, true);
    assert.equal(
      runtime.completions.total,
      beforeTls.total,
      'Synthetic timeout control has no API traffic',
    );
    await quiet();
    let before = snapshot(runtime.completions);
    receipt.browser = await operation({ mode: 'browser' });
    assert.equal(receipt.browser.navigations, 9);
    assert.equal(receipt.browser.visibilityControls, 24);
    await finishBatch(before, 18, 'linked-browser');
    for (const [laneIndex, lane] of runtime.manifest.entries()) {
      await quiet();
      before = snapshot(runtime.completions);
      for (const kind of ['catalog', 'course', 'lesson'])
        for (const run of [1, 2, 3]) {
          console.warn(`demo audit: lane ${laneIndex + 1}/3, ${kind}, run ${run}/3`);
          const url = lane[kind + 'Url'];
          const report = await operation({ mode: 'audit', laneIndex, lane, kind, url, run });
          receipt.reports.push(report);
          remaining(1);
          if (runtime.completions.statuses[429] || runtime.completions.total - before.total > 48)
            throw new Error('Public audit quota/status bound');
          if (!save()) throw failure;
        }
      await finishBatch(before, 36, `audit-lane-${laneIndex}`);
    }
    reportInventory(receipt.reports, expected);
    for (const row of receipt.reports) {
      const base = `audit-${runtime.manifest.findIndex((lane) => row.url.startsWith(lane.catalogUrl))}-${row.url.includes('/lessons/') ? 'lesson' : runtime.manifest.some((lane) => row.url === lane.catalogUrl) ? 'catalog' : 'course'}-${row.run}`;
      validateReportPair(
        row,
        readFileSync(join(output, base + '.json')),
        readFileSync(join(output, base + '.html')),
      );
    }
    receipt.advisories = reportAdvisories(receipt.reports);
    await demoSource(runtime.processes, root, remaining, runtime.source);
    remaining(1);
    receipt.proofMilliseconds = Date.now() - started;
    receipt.passed = true;
  } catch (error) {
    failure = error;
    receipt.failure ??= publicDemoFailure(phase, error, {}, undefined, runtime.signal);
    save();
  } finally {
    receipt.proofMilliseconds = Date.now() - started;
    const beforeCleanup = snapshot(runtime.completions);
    try {
      receipt.cleanup = await runtime.dispose();
    } catch (error) {
      receipt.cleanup = { passed: false };
      receipt.passed = false;
      receipt.failure ??= publicDemoFailure('cleanup', error);
      failure ??= new Error('Public proof owned cleanup failed');
    } finally {
      receipt.totalCompletions = snapshot(runtime.completions);
      try {
        finalCompletionCensus(
          beforeCleanup,
          runtime.completions,
          (receipt.startupCompletions?.total ?? 0) +
            receipt.batches.reduce((sum, batch) => sum + batch.count, 0),
        );
      } catch (error) {
        receipt.passed = false;
        receipt.failure ??= publicDemoFailure('proof', error);
        failure ??= error;
      }
      save();
    }
  }
  if (failure) {
    console.error('Public demo proof refused: ' + JSON.stringify(receipt.failure));
    throw failure;
  }
}
