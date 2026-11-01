// @vitest-environment node
import { once } from 'node:events';
import { existsSync, mkdtempSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

import { expect, it } from 'vitest';

import { DemoProcesses } from '../../../../../scripts/demo-process.mjs';
import { disposeDemoResources } from '../../../../../scripts/demo.mjs';

it('retains demo private roots after an already fatal outer owner and still attempts every teardown stage', async () => {
  const root = mkdtempSync(join(tmpdir(), 'learnstack-demo-fatal-cleanup-'));
  writeFileSync(join(root, 'owned-private'), 'private fixture only');
  const processes = new DemoProcesses(new AbortController().signal);
  const events: string[] = [];
  const running = processes.start(
    process.execPath,
    ['-e', 'console.log("ready");setInterval(()=>{},1000)'],
    { label: 'Private fatal owner control' },
  );
  try {
    await once(running.child.stdout!, 'data');
    running.child.kill('SIGKILL');
    expect(await running.exit).toEqual({ code: null, signal: 'SIGKILL' });
    await expect(
      disposeDemoResources({
        processes,
        finish: async () => {
          events.push('finish');
        },
        teardown: async () => {
          events.push('teardown');
        },
        removePrivate: () => {
          events.push('remove');
          rmSync(root, { recursive: true, force: true });
        },
      }),
    ).rejects.toThrow('private files retained');
    expect(events).toEqual(['finish', 'teardown']);
    expect(existsSync(root)).toBe(true);
  } finally {
    if (running.child.exitCode === null && running.child.signalCode === null)
      running.child.kill('SIGTERM');
    await running.exit;
    await processes.dispose().catch(() => {});
    rmSync(root, { recursive: true, force: true });
  }
}, 10000);

it('removes demo private roots after natural unforced command completion', async () => {
  const root = mkdtempSync(join(tmpdir(), 'learnstack-demo-complete-cleanup-'));
  const processes = new DemoProcesses(new AbortController().signal);
  const events: string[] = [];
  try {
    await expect(
      processes.run(process.execPath, ['-e', 'process.stdout.write("complete")'], {
        capture: true,
        label: 'Private natural owner control',
      }),
    ).resolves.toBe('complete');
    await disposeDemoResources({
      processes,
      finish: async () => {
        events.push('finish');
      },
      removePrivate: () => {
        events.push('remove');
        rmSync(root, { recursive: true, force: true });
      },
    });
    expect(events).toEqual(['finish', 'remove']);
    expect(existsSync(root)).toBe(false);
  } finally {
    await processes.dispose();
    rmSync(root, { recursive: true, force: true });
  }
});
