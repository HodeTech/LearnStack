import { spawn } from 'node:child_process';
import { stopTestChild } from '../frontend/apps/web/scripts/stop-test-child.mjs';

/** Only fixed caller-owned stage labels reach the foreground CLI. */
export class DemoCommandError extends Error {
  constructor(label) {
    super(`demo: ${label} failed`);
  }
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
    let output = '';
    let failed;
    for (const stream of [child.stdout, child.stderr]) {
      stream.once('error', () => {
        failed = new Error(`${label} output refused`);
        void stopTestChild(child);
      });
      stream.on('data', (chunk) => {
        try {
          observe(chunk);
          if (capture && stream === child.stdout) {
            if (output.length + chunk.length > 2 * 1024 * 1024)
              throw new Error('Command output exceeds bound');
            output += chunk.toString();
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
    return { child, exit, result: () => ({ output, failed }) };
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
