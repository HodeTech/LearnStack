import { spawn } from 'node:child_process';
import { stopTestChild } from '../frontend/apps/web/scripts/stop-test-child.mjs';

const commandReasons = new Set([
  'exit',
  'deadline',
  'cancelled',
  'spawn',
  'output',
  'cleanup',
  'unknown',
]);

/** Only fixed caller-owned stage labels reach the foreground CLI. */
export class DemoCommandError extends Error {
  constructor(label, cause, status, { reason = 'unknown', origin = 'command' } = {}) {
    super(`demo: ${label} failed`, { cause });
    this.reason = commandReasons.has(reason) ? reason : 'unknown';
    this.origin = origin === 'daemon' ? 'daemon' : 'command';
    // Only an observed spontaneous exit owns native status. A subsequent stop
    // cannot supply the status of a deadline, spawn, output or cleanup failure.
    this.exitCode = this.reason === 'exit' ? (status?.code ?? null) : null;
    this.signalCode = this.reason === 'exit' ? (status?.signal ?? null) : null;
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
  #children = new Map();
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
    if (this.#signal.aborted)
      throw new DemoCommandError('Owned command cancellation', this.#signal.reason, undefined, {
        reason: 'cancelled',
      });
    if (this.#failed) throw this.#failed;
  }
  start(command, args, { cwd, env, label, observe = () => {}, capture = false } = {}) {
    this.assertActive();
    let child;
    try {
      child = spawn(command, args, {
        cwd,
        env,
        detached: process.platform !== 'win32',
        stdio: ['ignore', 'pipe', 'pipe'],
      });
    } catch (cause) {
      throw new DemoCommandError(label ?? command, cause, undefined, { reason: 'spawn' });
    }
    const closed = new Promise((resolve) => child.once('close', resolve));
    const output = [];
    let outputBytes = 0;
    let failed;
    let failureReason;
    let publishRefusal;
    const refusal = new Promise((resolve) => { publishRefusal = resolve; });
    const refuseOutput = (cause) => {
      if (!failed) {
        failed = new Error(`${label} output refused`, { cause });
        failureReason = 'output';
        publishRefusal({ cause: failed, reason: failureReason });
      }
      // dispose still owns a failed stop; do not create an unhandled rejection.
      void stopTestChild(child).catch(() => {});
    };
    for (const stream of [child.stdout, child.stderr]) {
      stream.once('error', refuseOutput);
      stream.on('data', (chunk) => {
        try {
          observe(chunk, stream === child.stdout ? 'stdout' : 'stderr');
          if (capture && stream === child.stdout) {
            outputBytes += chunk.length;
            if (outputBytes > 2 * 1024 * 1024) throw new Error('Command output exceeds bound');
            output.push(chunk);
          }
        } catch (cause) {
          refuseOutput(cause);
        }
      });
    }
    const exit = new Promise((resolve) => {
      child.once('error', (cause) => {
        if (!failed) {
          failed = cause;
          failureReason = 'spawn';
          publishRefusal({ cause: failed, reason: failureReason });
        }
        resolve({ code: null, signal: null });
      });
      child.once('exit', (code, signal) => {
        // A failed leader is already authoritative even if inherited/paused
        // streams delay close. Successful captured output still waits for EOF.
        if (code !== 0 || signal !== null) resolve({ code, signal });
      });
      child.once('close', (code, signal) => resolve({ code, signal }));
    });
    const running = {
      child,
      exit,
      refusal,
      result: () => ({
        output: Buffer.concat(output).toString('utf8'),
        failed,
        failureReason,
      }),
    };
    this.#children.set(child, {
      closed,
      result: running.result,
      daemon: false,
    });
    return running;
  }
  daemon(command, args, options) {
    const running = this.start(command, args, options);
    this.#children.get(running.child).daemon = true;
    const fail = (cause, status, reason) => {
      if (this.#failed) return;
      this.#failed = new DemoCommandError(
        `${options.label} ${reason === 'exit' ? 'exited' : 'refused'}`,
        cause,
        status,
        { reason, origin: 'daemon' },
      );
      this.#failure(this.#failed);
    };
    void running.refusal.then(({ cause, reason }) => fail(cause, undefined, reason));
    void running.exit.then((status) => {
      fail(running.result().failed, status, running.result().failureReason ?? 'exit');
    });
    return running;
  }
  async run(command, args, options = {}) {
    const running = this.start(command, args, options);
    const timeout = AbortSignal.timeout(options.timeout ?? 180000);
    const signal = AbortSignal.any([this.#signal, timeout]);
    let cancelled;
    const aborted = new Promise((resolve) => {
      cancelled = () => resolve({ kind: 'aborted' });
      signal.addEventListener('abort', cancelled, { once: true });
      if (signal.aborted) cancelled();
    });
    let failure;
    try {
      const result = await Promise.race([
        running.exit.then((status) => ({ kind: 'exit', status })),
        running.refusal.then((outcome) => ({ kind: 'refusal', outcome })),
        aborted,
        this.failure.then((error) => ({ kind: 'daemon', error })),
      ]);
      if (result.kind === 'daemon') throw result.error;
      if (result.kind === 'refusal')
        throw new DemoCommandError(options.label ?? command, result.outcome.cause, undefined, {
          reason: result.outcome.reason,
        });
      if (result.kind === 'aborted')
        throw new DemoCommandError(
          options.label ?? command,
          this.#signal.aborted ? this.#signal.reason : timeout.reason,
          undefined,
          { reason: this.#signal.aborted ? 'cancelled' : 'deadline' },
        );
      const outcome = running.result();
      if (result.status.code !== 0 || outcome.failed)
        throw new DemoCommandError(options.label ?? command, outcome.failed, result.status, {
          reason: outcome.failureReason ?? 'exit',
        });
      this.assertActive();
      return running.result().output;
    } catch (error) {
      failure = error;
      throw error;
    } finally {
      signal.removeEventListener('abort', cancelled);
      try {
        await stopTestChild(running.child);
      } catch (error) {
        // Retain this handle for dispose's cleanup failure; do not replace the
        // triggering command failure or attribute teardown's signal to it.
        if (!failure)
          throw new DemoCommandError(options.label ?? command, error, undefined, {
            reason: 'cleanup',
          });
      }
    }
  }
  async dispose() {
    this.#closed = true;
    const results = await Promise.allSettled(
      [...this.#children].map(async ([child, { closed, result, daemon }]) => {
        const deadline = Date.now() + 5000;
        await stopTestChild(child);
        // Group absence does not establish stream EOF. A closed child has also
        // delivered its buffered stdout/stderr; bound this separate drain so
        // inherited/leaked pipes cannot hang teardown.
        let timer;
        try {
          await Promise.race([
            closed,
            new Promise((_, reject) => {
              timer = setTimeout(
                () => reject(new Error('Owned output did not close')),
                Math.max(0, deadline - Date.now()),
              );
            }),
          ]);
        } finally {
          clearTimeout(timer);
        }
        if (daemon && result().failureReason === 'output')
          throw new Error('Owned output was refused', {
            cause: result().failed,
          });
      }),
    );
    if (results.some((row) => row.status === 'rejected'))
      throw new AggregateError(
        results.filter((row) => row.status === 'rejected').map((row) => row.reason),
        'Owned process cleanup failed; private files retained',
      );
  }
}
