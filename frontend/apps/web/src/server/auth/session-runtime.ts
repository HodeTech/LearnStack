import { createBffHttpProtocol } from './http-protocol.js';
import type { NativePagePreparation } from './page-snapshot-contract.js';
import type { BffSessionStore, SessionDependencies } from './session-contract.js';
import { InMemoryBffSessionStore } from './session-store.js';

export const BFF_RUNTIME_KEY = 'learnstack.bff-sessions.v1';
export type BffSessionHolder = Readonly<{
  version: 1;
  store: BffSessionStore;
  http: ReturnType<typeof createBffHttpProtocol>;
}>;

/** Called once by the native launcher. Bundled consumers never construct a fallback. */
export function installBffSessionRuntime(
  dependencies: SessionDependencies,
  preparation: NativePagePreparation,
) {
  const key = Symbol.for(BFF_RUNTIME_KEY);
  if (Object.getOwnPropertyDescriptor(globalThis, key))
    throw new Error('BFF session owner already installed');
  if (
    !preparation ||
    typeof preparation.current !== 'function' ||
    typeof preparation.reserve !== 'function'
  )
    throw new Error('BFF page owner unavailable');
  const owner = new InMemoryBffSessionStore(dependencies, preparation);
  const store: BffSessionStore = Object.freeze({
    openInvitation: owner.openInvitation.bind(owner),
    invitation: owner.invitation.bind(owner),
    retainInvitation: owner.retainInvitation.bind(owner),
    invitationEntry: owner.invitationEntry.bind(owner),
    start: owner.start.bind(owner),
    complete: owner.complete.bind(owner),
    entry: owner.entry.bind(owner),
    create: owner.create.bind(owner),
    select: owner.select.bind(owner),
    switchScope: owner.switchScope.bind(owner),
    discardEntry: owner.discardEntry.bind(owner),
    operate: owner.operate.bind(owner),
    preparePage: owner.preparePage.bind(owner),
    retainConsent: owner.retainConsent.bind(owner),
    retainedConsent: owner.retainedConsent.bind(owner),
    peek: owner.peek.bind(owner),
    admit: owner.admit.bind(owner),
    signout: owner.signout.bind(owner),
    consumeLogout: owner.consumeLogout.bind(owner),
    stop: owner.stop.bind(owner),
  });
  const holder: BffSessionHolder = Object.freeze({
    version: 1,
    store,
    http: createBffHttpProtocol(store, preparation),
  });
  Object.defineProperty(globalThis, key, {
    value: holder,
    enumerable: false,
    configurable: false,
    writable: false,
  });
  return Object.freeze({
    store,
    stop: store.stop,
    counts: owner.counts.bind(owner),
    applyProviderLogout: owner.applyProviderLogout.bind(owner),
    backchannelCounts: owner.backchannelCounts.bind(owner),
  });
}
