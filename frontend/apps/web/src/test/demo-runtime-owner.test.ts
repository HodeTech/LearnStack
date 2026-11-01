// @vitest-environment node
import { once } from 'node:events';

import { expect, test } from 'vitest';

import { DemoProcesses } from '../../../../../scripts/demo-process.mjs';

test('the managed native driver waits beyond generic escalation for a graceful owner', async () => {
  const processes = new DemoProcesses(new AbortController().signal);
  const running = processes.daemon(
    process.execPath,
    [
      '-e',
      `
      const live = setInterval(() => {}, 1000);
      process.once('SIGTERM', () => {
        setTimeout(() => { clearInterval(live); process.exit(0); }, 2300);
      });
      process.stdout.write('ready');
      `,
    ],
    { label: 'Managed runtime owner control', ownedRuntime: true },
  );
  try {
    await once(running.child.stdout!, 'data');
    const started = Date.now();
    await processes.dispose();
    expect(Date.now() - started).toBeGreaterThanOrEqual(2200);
    expect(running.child.exitCode).toBe(0);
    expect(running.child.signalCode).toBeNull();
    // Teardown may be called again; it must not signal a saved PID again.
    await expect(processes.dispose()).resolves.toBeUndefined();
  } finally {
    if (running.child.exitCode === null && running.child.signalCode === null) {
      const closed = once(running.child, 'close');
      running.child.kill('SIGTERM');
      await closed;
    }
  }
}, 10_000);

test('a spontaneously completed runtime owner is not credited as requested retirement', async () => {
  const processes = new DemoProcesses(new AbortController().signal);
  const running = processes.daemon(process.execPath, ['-e', 'process.exit(0);'], {
    label: 'Unexpected runtime owner control',
    ownedRuntime: true,
  });
  await once(running.child, 'close');
  await expect(processes.dispose()).rejects.toThrow('private files retained');
  expect(running.child.exitCode).toBe(0);
  expect(running.child.signalCode).toBeNull();
});
