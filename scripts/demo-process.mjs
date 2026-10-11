import { spawn } from 'node:child_process';
import { stopTestChild } from '../frontend/apps/web/scripts/stop-test-child.mjs';

/** Only fixed caller-owned stage labels reach the foreground CLI. */
export class DemoCommandError extends Error {
  constructor(label) {
    super(`demo: ${label} failed`);
  }
}

/** Terminal hangup and parent loss must enter the same owned teardown path. */
export function installDemoCancellation(cancel) {
  const removers = [];
  const parent = process.ppid;
  for (const event of ['SIGINT', 'SIGTERM', 'SIGHUP']) {
    process.on(event, cancel);
    removers.push(() => process.off(event, cancel));
  }
  for (const stream of [process.stdout, process.stderr]) {
    stream.on('error', cancel);
    removers.push(() => stream.off('error', cancel));
  }
  const parentWatch = setInterval(() => {
    if (process.ppid !== parent) cancel();
  }, 500);
  return () => {
    clearInterval(parentWatch);
    for (const remove of removers) remove();
  };
}

/** Own every command tree, including setup commands, from spawn until confirmed exit. */
export class DemoProcesses {
  #children = new Set();
  #signal;
  #failed;
  #failure;
  #closed = false;
  constructor(signal) {
    this.#signal = signal;
    this.failure = new Promise((resolve) => {
      this.#failure = resolve;
    });
  }
  assertActive() {
    if (this.#closed) throw new Error('Demo process owner is closed');
    this.#signal.throwIfAborted();
    if (this.#failed) throw this.#failed;
  }
  start(command, args, { cwd, env, label, observe = () => {}, capture = false } = {}) {
    this.assertActive();
    const child = spawn(command, args, {
      cwd,
      env,
      detached: process.platform !== 'win32',
      stdio: ['ignore', 'pipe', 'pipe'],
    });
    this.#children.add(child);
    const output = [];
    let outputBytes = 0;
    let failed;
    for (const stream of [child.stdout, child.stderr]) {
      stream.once('error', () => {
        failed = new Error(`${label} output refused`);
        void stopTestChild(child);
      });
      stream.on('data', (chunk) => {
        try {
          observe(chunk, stream === child.stdout ? 'stdout' : 'stderr');
          if (capture && stream === child.stdout) {
            outputBytes += chunk.length;
            if (outputBytes > 2 * 1024 * 1024) throw new Error('Command output exceeds bound');
            output.push(chunk);
          }
        } catch {
          failed = new Error(`${label} output refused`);
          void stopTestChild(child);
        }
      });
    }
    const exit = new Promise((resolve) => {
      child.once('error', () => resolve({ code: 1 }));
      child.once('close', (code) => resolve({ code: code ?? 1 }));
    });
    return {
      child,
      exit,
      result: () => ({ output: Buffer.concat(output).toString('utf8'), failed }),
    };
  }
  daemon(command, args, options) {
    const running = this.start(command, args, options);
    void running.exit.then(() => {
      this.#failed = new DemoCommandError(`${options.label} exited`);
      this.#failure(this.#failed);
    });
    return running;
  }
  async run(command, args, options = {}) {
    const running = this.start(command, args, options);
    const timeout = AbortSignal.timeout(options.timeout ?? 180000);
    const signal = AbortSignal.any([this.#signal, timeout]);
    let cancelled;
    const aborted = new Promise((resolve) => {
      cancelled = () => resolve({ code: 1, cancelled: true });
      signal.addEventListener('abort', cancelled, { once: true });
      if (signal.aborted) cancelled();
    });
    try {
      const result = await Promise.race([
        running.exit,
        aborted,
        this.failure.then(() => ({ code: 1 })),
      ]);
      if (result.code !== 0 || running.result().failed)
        throw new DemoCommandError(options.label ?? command);
      this.assertActive();
      return running.result().output;
    } finally {
      signal.removeEventListener('abort', cancelled);
      await stopTestChild(running.child);
    }
  }
  async dispose() {
    this.#closed = true;
    const results = await Promise.allSettled(
      [...this.#children].map((child) => stopTestChild(child)),
    );
    if (results.some((row) => row.status === 'rejected'))
      throw new Error('Owned process cleanup failed; private files retained');
  }
}
