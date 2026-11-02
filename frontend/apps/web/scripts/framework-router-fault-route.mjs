// This literal is copied to the fixed admitted App Route before a genuine build.
// It is not a production route or a runtime source rewrite.
export const copiedRouterFaultRouteSource = `import { workAsyncStorage } from 'next/dist/server/app-render/work-async-storage.external';

export const dynamic = 'force-dynamic';
export const revalidate = 0;

type FaultRoute = Readonly<{
  name: string;
  pipe: Promise<void>;
  allocation: Promise<void>;
  peerClose: Promise<void>;
  errors: Readonly<{ primary: Error; cleanup: Error; end: Error }>;
  notePull(): void;
  noteSourceFault(error: Error): void;
  bindEndWork(store: object, promise: Promise<void>): void;
  rejectEndWork(store: object, error: Error): void;
}>;

const key = Symbol.for('learnstack.routerFaultRoute');
const chunk = new Uint8Array(4096).fill(65);

export function GET() {
  const control = (globalThis as unknown as Record<symbol, { begin(): FaultRoute }>)[key];
  if (!control || typeof control.begin !== 'function') throw new Error('Copied route control absent');
  const fault = control.begin();
  let pullCount = 0;
  let endStore: ReturnType<typeof workAsyncStorage.getStore>;
  let rejectEnd: ((error: Error) => void) | undefined;
  if (fault.name === 'end-work-reject-gzip') {
    endStore = workAsyncStorage.getStore();
    if (!endStore) throw new Error('Actual Next work store absent');
    const pending = new Promise<void>((_resolve, reject) => { rejectEnd = reject; });
    endStore.pendingRevalidateWrites ??= [];
    endStore.pendingRevalidateWrites.push(pending);
    fault.bindEndWork(endStore, pending);
  }
  const stream = new ReadableStream<Uint8Array>({
    async pull(controller) {
      const index = pullCount++;
      if(index===0) fault.notePull();
      if (fault.name === 'pipe-before-selection') {
        if (index !== 0) throw new Error('Unexpected copied pull');
        await fault.pipe;
        fault.noteSourceFault(fault.errors.primary);
        controller.error(fault.errors.primary);
        return;
      }
      if (fault.name === 'abort-before-selection') {
        if (index !== 0) throw new Error('Unexpected copied pull');
        await fault.peerClose;
        controller.close();
        return;
      }
      if (index === 0) {
        controller.enqueue(chunk);
        return;
      }
      if (index !== 1) throw new Error('Unexpected copied pull');
      if (fault.name === 'source-fault-gzip' || fault.name === 'source-cleanup-fault-gzip') {
        await fault.allocation;
        fault.noteSourceFault(fault.errors.primary);
        controller.error(fault.errors.primary);
        return;
      }
      if (fault.name === 'end-work-reject-gzip') {
        await fault.allocation;
        if (!endStore || !rejectEnd) throw new Error('Actual pending end task absent');
        fault.rejectEndWork(endStore, fault.errors.end);
        rejectEnd(fault.errors.end);
        controller.close();
        return;
      }
      controller.close();
    },
  });
  return new Response(stream, {
    status: 200,
    headers: { 'content-type': 'text/plain; charset=utf-8', 'cache-control': 'no-store' },
  });
}
`;
