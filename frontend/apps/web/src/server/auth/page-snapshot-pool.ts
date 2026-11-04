import type { NativeEntrySnapshot, NativePageSnapshot } from './page-contract.js';
import type { NativePageLease } from './page-snapshot-contract.js';

const maximumSnapshots = 128;
const maximumOwnerSnapshots = 8;
const maximumBytes = 256 * 1024;

export class SnapshotAdmissionError extends Error {
  readonly status: 429 | 503;
  constructor(
    readonly reason: 'owner_capacity' | 'global_capacity' | 'snapshot_size' | 'unavailable',
  ) {
    super('Native page snapshot unavailable');
    this.name = 'SnapshotAdmissionError';
    this.status = reason === 'owner_capacity' ? 429 : 503;
  }
}

/** Internal native response capability, supplied only after the request ticket check. */
export type SnapshotResponseLifetime = Readonly<{
  signal: AbortSignal;
  cleaned: Promise<void>;
  assertCurrent: () => void;
  invalidate: () => void;
  publishPage: (page: NativePageSnapshot) => void;
}>;

export type ResponsePageLease = Readonly<{
  lease: NativePageLease;
  assertOutput: () => void;
}>;

function unavailable(): never {
  throw new SnapshotAdmissionError('unavailable');
}

/** No source aliases, getters, toJSON hooks, callbacks or non-JSON values survive publication. */
function immutableJson<T>(input: T): Readonly<{ value: T; bytes: number }> {
  const path = new Set<object>();
  const inspect = (value: unknown): void => {
    if (value === null || typeof value === 'string' || typeof value === 'boolean') return;
    if (typeof value === 'number' && Number.isFinite(value)) return;
    if (typeof value !== 'object' || path.has(value)) return unavailable();
    if (
      !Array.isArray(value) &&
      Object.getPrototypeOf(value) !== Object.prototype &&
      Object.getPrototypeOf(value) !== null
    )
      return unavailable();
    // JSON.stringify looks up toJSON even when it is non-enumerable or inherited.
    // Inspect descriptors only: testing the property itself could invoke a getter.
    for (
      let prototype: object | null = value;
      prototype !== null;
      prototype = Object.getPrototypeOf(prototype) as object | null
    )
      if (Object.getOwnPropertyDescriptor(prototype, 'toJSON')) return unavailable();
    path.add(value);
    // Array serialization also reads non-enumerable numeric properties.
    for (const key of Object.getOwnPropertyNames(value)) {
      const descriptor = Object.getOwnPropertyDescriptor(value, key)!;
      if (!('value' in descriptor)) return unavailable();
      // Optional object fields have the ordinary JSON omission semantics.
      if (descriptor.value === undefined && !Array.isArray(value)) continue;
      inspect(descriptor.value);
    }
    path.delete(value);
  };
  try {
    inspect(input);
    const serialized = JSON.stringify(input);
    if (serialized === undefined) return unavailable();
    const bytes = Buffer.byteLength(serialized, 'utf8');
    if (bytes > maximumBytes) throw new SnapshotAdmissionError('snapshot_size');
    const value = JSON.parse(serialized) as T;
    const freeze = (part: unknown): void => {
      if (part === null || typeof part !== 'object') return;
      for (const nested of Object.values(part)) freeze(nested);
      Object.freeze(part);
    };
    freeze(value);
    return { value, bytes };
  } catch (error) {
    if (error instanceof SnapshotAdmissionError) throw error;
    return unavailable();
  }
}

/** Separate snapshot credit; invalidation never substitutes for actual cleanup. */
export function createPageSnapshotPool() {
  const active = new Set<NativePageLease>();
  const owners = new Map<object, Set<NativePageLease>>();
  let retainedBytes = 0;
  let stopped = false;
  return Object.freeze({
    reserve(actualOwner: object, response: SnapshotResponseLifetime): ResponsePageLease {
      if (stopped || actualOwner === null || typeof actualOwner !== 'object') return unavailable();
      response.assertCurrent();
      response.signal.throwIfAborted();
      if (active.size >= maximumSnapshots) throw new SnapshotAdmissionError('global_capacity');
      const group = owners.get(actualOwner) ?? new Set<NativePageLease>();
      if (group.size >= maximumOwnerSnapshots) throw new SnapshotAdmissionError('owner_capacity');
      const controller = new AbortController();
      const work = new Set<Promise<unknown>>();
      let pinned = false;
      let published = false;
      let invalidated = false;
      let released = false;
      let responseCleaned = false;
      let bytes = 0;
      let validate: (() => boolean) | undefined;
      let expiry: ReturnType<typeof setTimeout> | undefined;
      let resolveCleaned!: () => void;
      const cleaned = new Promise<void>((resolve) => {
        resolveCleaned = resolve;
      });
      let scheduled = false;
      let generation = 0;
      const settle = () => {
        if (released || scheduled || !responseCleaned || work.size) return;
        scheduled = true;
        const version = generation;
        // Nested completion work can register another actual promise before the
        // fixed-point check. Neither abort nor response finish enters this branch.
        queueMicrotask(() =>
          queueMicrotask(() => {
            scheduled = false;
            if (released || !responseCleaned || work.size) return;
            if (version !== generation) {
              settle();
              return;
            }
            released = true;
            clearTimeout(expiry);
            validate = undefined;
            response.signal.removeEventListener('abort', abort);
            retainedBytes -= bytes;
            bytes = 0;
            active.delete(lease);
            group.delete(lease);
            if (group.size === 0) owners.delete(actualOwner);
            resolveCleaned();
          }),
        );
      };
      const abort = () => {
        if (invalidated || released) return;
        invalidated = true;
        clearTimeout(expiry);
        controller.abort('Native page snapshot closed');
        response.invalidate();
        settle();
      };
      const assertLive = (output = false) => {
        if (
          released ||
          invalidated ||
          response.signal.aborted ||
          !pinned ||
          !validate ||
          (output && !published)
        ) {
          abort();
          return unavailable();
        }
        let valid = false;
        try {
          valid = validate() === true;
        } catch {
          /* Fixed refusal; no owner data crosses this boundary. */
        }
        if (!valid) {
          abort();
          return unavailable();
        }
      };
      const publish = <T extends NativePageSnapshot | NativeEntrySnapshot>(value: T): T => {
        response.assertCurrent();
        assertLive();
        if (published) return unavailable();
        let result: Readonly<{ value: T; bytes: number }>;
        try {
          result = immutableJson(value);
        } catch (error) {
          abort();
          throw error;
        }
        assertLive();
        bytes = result.bytes;
        retainedBytes += bytes;
        published = true;
        return result.value;
      };
      const lease: NativePageLease = Object.freeze({
        signal: controller.signal,
        cleaned,
        get pinned() {
          return pinned;
        },
        pin(check: () => boolean, expiresInMs: number) {
          response.assertCurrent();
          if (
            released ||
            invalidated ||
            pinned ||
            typeof check !== 'function' ||
            !Number.isFinite(expiresInMs) ||
            expiresInMs <= 0 ||
            expiresInMs > 2147483647
          )
            return unavailable();
          pinned = true;
          validate = check;
          assertLive();
          expiry = setTimeout(abort, Math.max(1, Math.ceil(expiresInMs)));
          expiry.unref();
        },
        track(actualWork: PromiseLike<unknown>) {
          if (released || !actualWork || typeof actualWork.then !== 'function')
            return unavailable();
          const promise = Promise.resolve(actualWork);
          work.add(promise);
          ++generation;
          const complete = () => {
            work.delete(promise);
            ++generation;
            settle();
          };
          void promise.then(complete, complete);
        },
        publish(page: NativePageSnapshot) {
          const value = publish(page);
          response.publishPage(value);
          return value;
        },
        publishEntry(entry: NativeEntrySnapshot) {
          return publish(entry);
        },
        abort,
      });
      active.add(lease);
      group.add(lease);
      owners.set(actualOwner, group);
      response.signal.addEventListener('abort', abort, { once: true });
      void response.cleaned.then(() => {
        responseCleaned = true;
        abort();
        settle();
      });
      if (response.signal.aborted) abort();
      return Object.freeze({ lease, assertOutput: () => assertLive(true) });
    },
    async stop() {
      stopped = true;
      for (const lease of active) lease.abort();
      while (active.size) await Promise.all([...active].map((lease) => lease.cleaned));
    },
    counts() {
      return Object.freeze({ active: active.size, owners: owners.size, bytes: retainedBytes });
    },
  });
}
