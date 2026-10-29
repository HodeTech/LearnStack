import { createBffHttpProtocol } from './http-protocol.js';
import type { BffSessionStore, SessionDependencies } from './session-contract.js';
import { InMemoryBffSessionStore } from './session-store.js';

export const BFF_RUNTIME_KEY = 'learnstack.bff-sessions.v1';
export type BffSessionHolder = Readonly<{
  version: 1;
  store: BffSessionStore;
  http: ReturnType<typeof createBffHttpProtocol>;
}>;

/** Called once by the native launcher. Bundled consumers never construct a fallback. */
export function installBffSessionRuntime(dependencies: SessionDependencies) {
  const key = Symbol.for(BFF_RUNTIME_KEY);
  if (Object.getOwnPropertyDescriptor(globalThis, key))
    throw new Error('BFF session owner already installed');
  const owner = new InMemoryBffSessionStore(dependencies);
  const store: BffSessionStore = Object.freeze({
    start: owner.start.bind(owner),
    complete: owner.complete.bind(owner),
    entry: owner.entry.bind(owner),
    create: owner.create.bind(owner),
    select: owner.select.bind(owner),
    switchScope: owner.switchScope.bind(owner),
    discardEntry: owner.discardEntry.bind(owner),
    operate: owner.operate.bind(owner),
    peek: owner.peek.bind(owner),
    admit: owner.admit.bind(owner),
    signout: owner.signout.bind(owner),
    consumeLogout: owner.consumeLogout.bind(owner),
    stop: owner.stop.bind(owner),
  });
  const holder: BffSessionHolder = Object.freeze({
    version: 1,
    store,
    http: createBffHttpProtocol(store),
  });
  Object.defineProperty(globalThis, key, {
    value: holder,
    enumerable: false,
    configurable: false,
    writable: false,
  });
  return Object.freeze({ stop: store.stop, counts: owner.counts.bind(owner) });
}
