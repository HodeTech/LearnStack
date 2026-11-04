import { randomBytes } from 'node:crypto';
import { createServer, type Socket } from 'node:net';

/** Every test cleanup is attempted; an earlier assertion remains the first cause. */
export async function withTestCleanup<T>(
  operation: () => Promise<T>,
  cleanups: (() => unknown | Promise<unknown>)[],
): Promise<T> {
  let failed = false;
  let primary: unknown;
  let value!: T;
  try {
    value = await operation();
  } catch (error) {
    failed = true;
    primary = error;
  }
  const results = await Promise.allSettled(
    cleanups.map((cleanup) => Promise.resolve().then(cleanup)),
  );
  const failures = results
    .filter((result) => result.status === 'rejected')
    .map((result) => result.reason);
  if (failed && failures.length)
    throw new AggregateError([primary, ...failures], 'Test operation and cleanup failed');
  if (failed) throw primary;
  if (failures.length) throw new AggregateError(failures, 'Test cleanup failed');
  return value;
}

async function bounded<T>(work: Promise<T>, deadline = Date.now() + 5000): Promise<T> {
  let timer: ReturnType<typeof setTimeout> | undefined;
  try {
    return await Promise.race([
      work,
      new Promise<never>((_, reject) => {
        timer = setTimeout(
          () => reject(new Error('Held test child control deadline')),
          Math.max(0, deadline - Date.now()),
        );
      }),
    ]);
  } finally {
    clearTimeout(timer);
  }
}

/** Private test-only cooperation, never a runtime family/recovery authority. */
export async function createHeldChildControl() {
  const nonce = randomBytes(32).toString('hex');
  let child: Socket | undefined;
  let pid: number | undefined;
  let acknowledged = false;
  let processRetired = false;
  let ready!: () => void;
  let rejectReady!: (error: Error) => void;
  let retired!: () => void;
  const readiness = new Promise<void>((resolve, reject) => {
    ready = resolve;
    rejectReady = reject;
  });
  void readiness.catch(() => {});
  const closure = new Promise<void>((resolve) => {
    retired = resolve;
  });
  const sockets = new Set<Socket>();
  const server = createServer((socket) => {
    sockets.add(socket);
    let input = '';
    socket.on('error', () => {});
    socket.on('data', (bytes: Buffer) => {
      input += bytes.toString();
      if (input.length > 512) {
        rejectReady(new Error('Held test child frame exceeds bound'));
        socket.destroy();
        return;
      }
      for (;;) {
        const at = input.indexOf('\n');
        if (at < 0) return;
        const frame = input.slice(0, at);
        input = input.slice(at + 1);
        try {
          const value: unknown = JSON.parse(frame);
          if (
            !value ||
            typeof value !== 'object' ||
            !('kind' in value) ||
            !('nonce' in value) ||
            value.nonce !== nonce
          )
            throw new Error('Held test child identity mismatch');
          if (
            value.kind === 'ready' &&
            !child &&
            'pid' in value &&
            typeof value.pid === 'number' &&
            Number.isSafeInteger(value.pid) &&
            value.pid > 1 &&
            Object.keys(value).sort().join(',') === 'kind,nonce,pid'
          ) {
            child = socket;
            pid = value.pid;
            ready();
          } else if (
            value.kind === 'released' &&
            socket === child &&
            !acknowledged &&
            Object.keys(value).sort().join(',') === 'kind,nonce'
          ) {
            acknowledged = true;
          } else throw new Error('Held test child frame is unowned');
        } catch (error) {
          rejectReady(error instanceof Error ? error : new Error('Held test child frame refused'));
          socket.destroy();
        }
      }
    });
    socket.once('close', () => {
      sockets.delete(socket);
      if (socket === child) retired();
      else if (!child) rejectReady(new Error('Held test child left before readiness'));
    });
  });
  await bounded(
    new Promise<void>((resolve, reject) => {
      server.once('error', reject);
      server.listen(0, '127.0.0.1', resolve);
    }),
  );
  const address = server.address();
  if (!address || typeof address === 'string') throw new Error('Held test child listener missing');
  let cleanup: Promise<void> | undefined;
  async function actualExit(deadline: number) {
    if (pid === undefined || processRetired) return;
    for (;;) {
      try {
        process.kill(pid, 0);
      } catch (error) {
        const code = (error as NodeJS.ErrnoException).code;
        if (code === 'ESRCH') {
          processRetired = true;
          return;
        }
        if (code !== 'EPERM') throw error;
      }
      if (Date.now() >= deadline) throw new Error('Held test child did not actually exit');
      await new Promise((resolve) =>
        setTimeout(resolve, Math.min(10, Math.max(0, deadline - Date.now()))),
      );
    }
  }
  return {
    source({ ignoreSigterm = false, stdoutReady = false } = {}) {
      return `
        const socket = require('node:net').connect(${address.port}, '127.0.0.1');
        const nonce = ${JSON.stringify(nonce)};
        let input = '';
        ${ignoreSigterm ? "process.on('SIGTERM', () => {});" : ''}
        socket.once('connect', () => {
          socket.write(JSON.stringify({kind:'ready',nonce,pid:process.pid})+'\\n');
          if(process.send){process.send('ready');process.disconnect();}
          ${stdoutReady ? "process.stdout.write('ready\\n');" : ''}
        });
        socket.on('data', bytes => {
          input += bytes.toString();
          if(input.length>512)process.exit(2);
          const at=input.indexOf('\\n');if(at<0)return;
          const value=JSON.parse(input.slice(0,at));
          if(value.kind!=='release'||value.nonce!==nonce||Object.keys(value).sort().join(',')!=='kind,nonce')process.exit(2);
          socket.end(JSON.stringify({kind:'released',nonce})+'\\n',()=>process.exit(0));
        });
        socket.once('error',()=>process.exit(1));
        socket.once('close',()=>process.exit(0));
      `;
    },
    async ready() {
      await bounded(readiness);
      if (pid === undefined) throw new Error('Held test child identity missing');
      return pid;
    },
    async release() {
      const deadline = Date.now() + 5000;
      await bounded(readiness, deadline);
      if (!child || child.destroyed) throw new Error('Held test child channel retired');
      child.write(JSON.stringify({ kind: 'release', nonce }) + '\n');
      await bounded(closure, deadline);
      if (!acknowledged) throw new Error('Held test child did not acknowledge release');
      await actualExit(deadline);
    },
    dispose() {
      cleanup ??= (async () => {
        const deadline = Date.now() + 5000;
        const failures: unknown[] = [];
        try {
          if (child && !child.destroyed) {
            child.write(JSON.stringify({ kind: 'release', nonce }) + '\n');
            await bounded(closure, deadline);
          }
        } catch (error) {
          failures.push(error);
        }
        for (const socket of sockets) socket.destroy();
        try {
          await actualExit(deadline);
        } catch (error) {
          failures.push(error);
        }
        try {
          await bounded(
            new Promise<void>((resolve, reject) =>
              server.close((error) => (error ? reject(error) : resolve())),
            ),
            deadline,
          );
        } catch (error) {
          failures.push(error);
        }
        if (failures.length) throw new AggregateError(failures, 'Held test child cleanup failed');
      })();
      return cleanup;
    },
  };
}
