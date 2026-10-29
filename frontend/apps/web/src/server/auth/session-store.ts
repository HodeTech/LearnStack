import { randomBytes, timingSafeEqual } from 'node:crypto';

import { canonicalAddress, normalizeHost } from '../ingress.js';
import { NativeApiError } from './native-contract.js';
import type { NativeScope, SelfContexts } from './native-contract.js';
import type { NativeOperation, NativeOperationResult } from './native-operation-contract.js';
import { nativeContextPage } from './native-session-client.js';
import { BffProviderError } from './provider-error.js';
import type { LoginTransaction, ProviderCredentials } from './provider.js';
import { BffSessionError } from './session-contract.js';
import type {
  AccountWitness,
  BffSessionStore,
  LoginCompletion,
  LoginStart,
  EntryProjection,
  SelectionCompletion,
  SessionCompletion,
  PublishedSessionIdentity,
  NativeReturnPath,
  LogoutStart,
  SessionActivity,
  SessionBinding,
  SessionDependencies,
  SessionFailure,
  SessionProjection,
} from './session-contract.js';
import { isNativeReturnPath } from './session-path.js';

export const BFF_LIMITS = Object.freeze({
  owners: 1024,
  pending: 128,
  source: 16,
  subject: 8,
  transactionMs: 5 * 60_000,
  idleMs: 30 * 60_000,
  absoluteMs: 8 * 60 * 60_000,
  sweepMs: 30_000,
  refreshAheadMs: 60_000,
  credentialBytes: 32 * 1024,
});
const opaque = () => randomBytes(32).toString('base64url');
export const isBffOpaque = (value: unknown): value is string =>
  typeof value === 'string' && /^[A-Za-z0-9_-]{43}$/.test(value);
export function bffSecretEquals(left: unknown, right: unknown): boolean {
  return (
    isBffOpaque(left) &&
    isBffOpaque(right) &&
    timingSafeEqual(Buffer.from(left), Buffer.from(right))
  );
}

type Group = {
  host: string;
  browser: string;
  epoch: string;
  members: Set<Owner>;
  current?: Owner;
  publishing?: Owner;
};
type Owner = {
  id: string;
  group: Group;
  epoch: string;
  source: string;
  phase:
    | 'starting'
    | 'pending'
    | 'exchange'
    | 'entry'
    | 'selecting'
    | 'active'
    | 'switching'
    | 'logout'
    | 'closing';
  pending: boolean;
  deadline: number;
  controller: AbortController;
  work: Set<Promise<unknown>>;
  transaction?: LoginTransaction;
  proof?: string;
  returnPath: NativeReturnPath;
  contextHint?: string;
  replacement?: Owner;
  subject?: Readonly<{ key: string }>;
  credentials?: ProviderCredentials;
  account?: AccountWitness;
  identity?: PublishedSessionIdentity;
  busy?: Promise<unknown>;
  transition?: Promise<SelectionCompletion>;
  absolute: number;
  activity: number;
  utcOffset: number;
  csrf: string;
  refresh?: Promise<void>;
  logoutState?: string;
  logoutProof?: string;
};

/** One native owner. No request constructs it; no credential-returning API exists. */
export class InMemoryBffSessionStore implements BffSessionStore {
  readonly #owners = new Map<string, Owner>();
  readonly #states = new Map<string, Owner>();
  readonly #logoutStates = new Map<string, Owner>();
  readonly #groups = new Map<string, Group>();
  readonly #dependencies: SessionDependencies;
  readonly #timer: ReturnType<typeof setInterval>;
  #stopped = false;
  #stopping?: Promise<void>;

  constructor(dependencies: SessionDependencies) {
    this.#dependencies = dependencies;
    this.#timer = setInterval(() => this.sweep(), BFF_LIMITS.sweepMs);
    this.#timer.unref();
  }

  #now() {
    return this.#dependencies.clock?.monotonic() ?? performance.now();
  }
  #utc() {
    return this.#dependencies.clock?.utc() ?? Date.now();
  }
  #groupKey(host: string, browser: string) {
    return JSON.stringify([host, browser]);
  }
  #checkAddress(host: string) {
    if (typeof host !== 'string' || normalizeHost(host) !== host)
      throw new BffSessionError('configuration');
    if (this.#stopped) throw new BffSessionError('stopped');
  }
  #checkSource(host: string, source: string) {
    this.#checkAddress(host);
    if (typeof source !== 'string' || canonicalAddress(source) !== source)
      throw new BffSessionError('configuration');
  }
  #effective(entry: Owner) {
    return Math.min(
      entry.absolute,
      entry.activity + BFF_LIMITS.idleMs,
      entry.credentials?.refreshExpiresAt ?? entry.deadline,
    );
  }
  #live(entry: Owner) {
    return (
      !this.#stopped &&
      entry.phase === 'active' &&
      entry.group.current === entry &&
      entry.epoch === entry.group.epoch &&
      this.#now() < this.#effective(entry)
    );
  }
  #assert(entry: Owner, phase: Owner['phase']) {
    if (this.#stopped) throw new BffSessionError('stopped');
    if (entry.phase !== phase || entry.epoch !== entry.group.epoch)
      throw new BffSessionError('revoked');
    const deadline =
      phase === 'active'
        ? this.#effective(entry)
        : Math.min(entry.deadline, entry.credentials?.refreshExpiresAt ?? entry.deadline);
    if (this.#now() >= deadline) throw new BffSessionError('expired');
  }
  #find(binding: SessionBinding) {
    const entry = isBffOpaque(binding.handle) ? this.#owners.get(binding.handle) : undefined;
    return entry &&
      entry.group.host === binding.host &&
      bffSecretEquals(entry.group.browser, binding.browser) &&
      this.#live(entry)
      ? entry
      : undefined;
  }
  #release(entry: Owner) {
    if (entry.phase !== 'closing' || entry.work.size || this.#owners.get(entry.id) !== entry)
      return;
    this.#owners.delete(entry.id);
    if (entry.transaction) this.#states.delete(entry.transaction.state);
    if (entry.logoutState) this.#logoutStates.delete(entry.logoutState);
    entry.source = '';
    entry.subject = undefined;
    entry.transaction = undefined;
    entry.logoutState = undefined;
    entry.logoutProof = undefined;
    entry.replacement = undefined;
    entry.csrf = '';
    entry.group.members.delete(entry);
    if (entry.group.current === entry) entry.group.current = undefined;
    if (entry.group.publishing === entry) entry.group.publishing = undefined;
    if (!entry.group.members.size)
      this.#groups.delete(this.#groupKey(entry.group.host, entry.group.browser));
  }
  #close(entry: Owner) {
    entry.phase = 'closing';
    entry.credentials = undefined;
    entry.account = undefined;
    entry.identity = undefined;
    entry.proof = undefined;
    entry.controller.abort();
    this.#release(entry);
  }
  #tracked<T>(entry: Owner, action: () => Promise<T>): Promise<T> {
    // Assign ownership before any action or continuation can execute.
    const work = Promise.resolve().then(action);
    entry.work.add(work);
    const settled = () => {
      entry.work.delete(work);
      this.#release(entry);
    };
    void work.then(settled, settled);
    return work;
  }
  #exclusive<T>(entry: Owner, signal: AbortSignal | undefined, action: () => Promise<T>) {
    const abort = () => this.#close(entry);
    signal?.addEventListener('abort', abort, { once: true });
    if (signal?.aborted) abort();
    return this.#tracked(entry, action).finally(() => signal?.removeEventListener('abort', abort));
  }
  #reason(error: unknown): SessionFailure {
    return error instanceof BffSessionError || error instanceof BffProviderError
      ? error.reason
      : 'response';
  }
  #diagnostic(reason: SessionFailure) {
    // Diagnostics cannot acquire lifecycle ownership or replace the original outcome.
    try {
      this.#dependencies.diagnostic?.(reason);
    } catch {
      /* fixed observer boundary */
    }
  }
  #project(entry: Owner): SessionProjection {
    if (!this.#live(entry) || !entry.account) throw new BffSessionError('revoked');
    return Object.freeze({
      state: 'authenticated',
      displayName: entry.account.displayName,
      // Anchor the clock conversion once: projecting an unchanged monotonic
      // deadline must not manufacture an extension from wall-clock sampling noise.
      expiresAt: Math.round(entry.utcOffset + this.#effective(entry)),
      limitedByAbsolute: this.#effective(entry) === entry.absolute,
      csrfToken: entry.csrf,
    });
  }
  #account(entry: Owner, account: AccountWitness, expected: PublishedSessionIdentity) {
    if (
      !account ||
      typeof account.displayName !== 'string' ||
      account.displayName.length > 1024 ||
      account.actorId !== expected.actorId ||
      account.tenantId !== expected.tenantId ||
      account.organizationId !== expected.organizationId
    )
      throw new BffSessionError('identity');
    entry.account = Object.freeze({
      actorId: account.actorId,
      tenantId: account.tenantId,
      organizationId: account.organizationId,
      displayName: account.displayName,
    });
  }
  #reserveSubject(entry: Owner, credentials: ProviderCredentials) {
    const key = JSON.stringify([credentials.identity.issuer, credentials.identity.subject]);
    if (
      [...this.#owners.values()].filter((owner) => owner.subject?.key === key).length >=
      BFF_LIMITS.subject
    )
      throw new BffSessionError('subject_capacity');
    // Every retained owner consumes its own credit, including same-person replacement.
    entry.subject = Object.freeze({ key });
  }
  #binding(binding: SessionBinding, source: string) {
    return {
      host: binding.host,
      source,
      ...(binding.traceparent ? { traceparent: binding.traceparent } : {}),
    };
  }
  #scoped(binding: SessionBinding, source: string, scope: NativeScope) {
    return {
      ...this.#binding(binding, source),
      tenantId: scope.tenantId,
      organizationId: scope.organizationId,
    };
  }
  #bundle(next: ProviderCredentials, previous?: ProviderCredentials) {
    if (
      !next ||
      (!next.refreshToken && !previous?.refreshToken) ||
      !Number.isFinite(next.accessExpiresAt) ||
      !Number.isFinite(next.refreshExpiresAt) ||
      next.accessExpiresAt <= this.#now() ||
      next.refreshExpiresAt <= this.#now() ||
      (previous &&
        (['issuer', 'subject', 'actorId', 'tenantId', 'organizationId'] as const).some(
          (key) =>
            previous.identity[key as keyof typeof previous.identity] !==
            next.identity[key as keyof typeof next.identity],
        ))
    )
      throw new BffSessionError('identity');
    const bundle = Object.freeze({
      accessToken: next.accessToken,
      idToken: next.idToken,
      identity: Object.freeze({
        issuer: next.identity.issuer,
        subject: next.identity.subject,
        actorId: next.identity.actorId,
        tenantId: next.identity.tenantId,
        organizationId: next.identity.organizationId,
        displayLabel: next.identity.displayLabel,
      }),
      accessExpiresAt: next.accessExpiresAt,
      refreshExpiresAt: next.refreshExpiresAt,
      refreshToken: next.refreshToken ?? previous?.refreshToken,
    });
    if (Buffer.byteLength(JSON.stringify(bundle)) > BFF_LIMITS.credentialBytes)
      throw new BffSessionError('response');
    return bundle;
  }

  /** Finite full scan; unsettled expired owners continue consuming all their credits. */
  sweep() {
    const now = this.#now();
    for (const entry of this.#owners.values()) {
      const expired =
        entry.phase === 'active' ? now >= this.#effective(entry) : now >= entry.deadline;
      if (expired && entry.phase !== 'closing') {
        this.#close(entry);
        this.#diagnostic('expired');
      }
      this.#release(entry);
    }
  }

  async start(input: Parameters<BffSessionStore['start']>[0]): Promise<LoginStart> {
    this.#checkSource(input.host, input.source);
    if (input.signal?.aborted) throw new BffSessionError('cancelled');
    if (
      !isNativeReturnPath(input.returnPath) ||
      (input.browser !== undefined && !isBffOpaque(input.browser)) ||
      (input.contextHint !== undefined &&
        input.contextHint !== 'tenant' &&
        (!/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/.test(
          input.contextHint,
        ) ||
          input.contextHint === '00000000-0000-0000-0000-000000000000'))
    )
      throw new BffSessionError('configuration');
    this.sweep();
    const pending = [...this.#owners.values()].filter((entry) => entry.pending);
    const refusal =
      pending.filter((entry) => entry.source === input.source).length >= BFF_LIMITS.source
        ? 'source_capacity'
        : pending.length >= BFF_LIMITS.pending
          ? 'pending_capacity'
          : this.#owners.size >= BFF_LIMITS.owners
            ? 'global_capacity'
            : null;
    if (refusal) {
      this.#diagnostic(refusal);
      throw new BffSessionError(refusal);
    }
    let browser = input.browser ?? opaque();
    let group = this.#groups.get(this.#groupKey(input.host, browser));
    const replacement = input.handle
      ? this.#find({ host: input.host, browser, handle: input.handle })
      : undefined;
    if (group?.publishing) throw new BffSessionError('transaction');
    // A binding alone never authorizes eviction of a lost/other primary cookie.
    if (group?.current && !replacement) {
      browser = opaque();
      group = undefined;
    }
    if (!group) {
      group = { host: input.host, browser, epoch: opaque(), members: new Set() };
      this.#groups.set(this.#groupKey(input.host, browser), group);
    }
    const now = this.#now();
    const entry: Owner = {
      id: opaque(),
      group,
      epoch: group.epoch,
      source: input.source,
      phase: 'starting',
      pending: true,
      deadline: now + BFF_LIMITS.transactionMs,
      controller: new AbortController(),
      work: new Set(),
      returnPath: input.returnPath,
      contextHint: input.contextHint,
      replacement,
      absolute: now + BFF_LIMITS.absoluteMs,
      activity: now,
      utcOffset: this.#utc() - now,
      csrf: opaque(),
    };
    group.members.add(entry);
    this.#owners.set(entry.id, entry);
    return this.#exclusive(entry, input.signal, async () => {
      try {
        this.#assert(entry, 'starting');
        const start = await this.#dependencies.provider.begin(
          'https://' + input.host + '/api/auth/callback/keycloak',
          entry.controller.signal,
        );
        this.#assert(entry, 'starting');
        if (!isBffOpaque(start.transaction.state) || this.#states.has(start.transaction.state))
          throw new BffSessionError('response');
        entry.transaction = Object.freeze({ ...start.transaction });
        entry.proof = opaque();
        entry.phase = 'pending';
        this.#states.set(start.transaction.state, entry);
        return Object.freeze({
          state: start.transaction.state,
          proof: entry.proof,
          browser,
          redirectUrl: start.redirectUrl,
        });
      } catch (error) {
        const reason = this.#stopped
          ? 'stopped'
          : input.signal?.aborted
            ? 'cancelled'
            : this.#reason(error);
        this.#close(entry);
        this.#diagnostic(reason);
        throw new BffSessionError(reason);
      }
    });
  }

  async complete(input: Parameters<BffSessionStore['complete']>[0]): Promise<LoginCompletion> {
    this.#checkSource(input.host, input.source);
    const entry = isBffOpaque(input.state) ? this.#states.get(input.state) : undefined;
    if (
      !entry ||
      entry.phase !== 'pending' ||
      entry.group.host !== input.host ||
      !bffSecretEquals(input.browser, entry.group.browser) ||
      !bffSecretEquals(input.proof, entry.proof)
    )
      throw new BffSessionError('transaction');
    // Single-use before exchange; an unrelated/invalid proof never consumes it.
    entry.phase = 'exchange';
    this.#states.delete(input.state);
    return this.#exclusive(entry, input.signal, async () => {
      try {
        this.#assert(entry, 'exchange');
        const credentials = this.#bundle(
          await this.#dependencies.provider.redeem(
            entry.transaction!,
            input.responseUrl,
            entry.controller.signal,
          ),
        );
        this.#assert(entry, 'exchange');
        this.#reserveSubject(entry, credentials);
        entry.credentials = credentials;
        entry.transaction = undefined;
        entry.proof = undefined;
        entry.phase = 'entry';
        return Object.freeze({
          kind: 'entry',
          handle: entry.id,
          browser: entry.group.browser,
          entry: this.#entryProjection(entry),
        });
      } catch (error) {
        const reason = this.#stopped
          ? 'stopped'
          : input.signal?.aborted
            ? 'cancelled'
            : this.#reason(error);
        this.#close(entry);
        this.#diagnostic(reason);
        throw new BffSessionError(reason, input.state);
      }
    });
  }

  #findEntry(binding: SessionBinding, allowSelecting = false) {
    const entry = isBffOpaque(binding.handle) ? this.#owners.get(binding.handle) : undefined;
    if (
      !entry ||
      entry.group.host !== binding.host ||
      !bffSecretEquals(entry.group.browser, binding.browser)
    )
      throw new BffSessionError('revoked');
    if (entry.phase !== 'entry' && !(allowSelecting && entry.phase === 'selecting'))
      throw new BffSessionError('revoked');
    this.#assert(entry, entry.phase);
    return entry;
  }
  #entryProjection(
    entry: Owner,
    identityState: EntryProjection['identityState'] = 'unavailable',
    contexts: SelfContexts | null = null,
  ): EntryProjection {
    this.#assert(entry, 'entry');
    return Object.freeze({
      state: 'entry',
      displayLabel: entry.credentials!.identity.displayLabel,
      csrfToken: entry.csrf,
      expiresAt: Math.round(
        entry.utcOffset + Math.min(entry.deadline, entry.credentials!.refreshExpiresAt),
      ),
      identityState,
      contexts,
      contextHint: entry.contextHint ?? null,
    });
  }
  #entryProof(entry: Owner, csrf: string) {
    if (!bffSecretEquals(csrf, entry.csrf)) throw new BffSessionError('csrf');
  }
  #assertSignal(signal: AbortSignal) {
    if (signal.aborted) throw new BffSessionError('cancelled');
  }
  async #bounded<T>(
    entry: Owner,
    signal: AbortSignal | undefined,
    action: (signal: AbortSignal) => Promise<T>,
  ): Promise<T> {
    const controller = new AbortController();
    let timedOut = false;
    const abort = () => controller.abort();
    entry.controller.signal.addEventListener('abort', abort, { once: true });
    signal?.addEventListener('abort', abort, { once: true });
    const timer = setTimeout(() => {
      timedOut = true;
      controller.abort();
    }, 10_000);
    timer.unref();
    if (entry.controller.signal.aborted || signal?.aborted) abort();
    const work = this.#tracked(entry, async () => {
      this.#assertSignal(controller.signal);
      const result = await action(controller.signal);
      this.#assertSignal(controller.signal);
      return result;
    });
    const settled = () => {
      clearTimeout(timer);
      signal?.removeEventListener('abort', abort);
      entry.controller.signal.removeEventListener('abort', abort);
    };
    void work.then(settled, settled);
    try {
      return await work;
    } catch (error) {
      if (controller.signal.aborted) throw new BffSessionError(timedOut ? 'deadline' : 'cancelled');
      throw error;
    }
  }
  async #entryRead(
    entry: Owner,
    binding: SessionBinding,
    source: string,
    cursor: string | undefined,
    signal: AbortSignal,
  ): Promise<EntryProjection> {
    this.#assert(entry, 'entry');
    const credentials = entry.credentials!;
    const self = await this.#dependencies.native.self(
      credentials.accessToken,
      this.#binding(binding, source),
      signal,
    );
    this.#assertSignal(signal);
    this.#assert(entry, 'entry');
    if (self.state === 'missing') return this.#entryProjection(entry, 'missing');
    this.#dependencies.native.verifyAssociation(credentials.identity, self.association);
    const contexts = await this.#dependencies.native.contexts(
      credentials.accessToken,
      this.#binding(binding, source),
      { ...(cursor ? { cursor } : {}), limit: 20 },
      signal,
    );
    this.#assertSignal(signal);
    this.#assert(entry, 'entry');
    return this.#entryProjection(entry, 'active', contexts);
  }
  async entry(
    binding: SessionBinding,
    source: string,
    cursor?: string,
    signal?: AbortSignal,
  ): Promise<EntryProjection> {
    this.#checkSource(binding.host, source);
    const owner = this.#findEntry(binding);
    return this.#bounded(owner, signal, (current) =>
      this.#entryRead(owner, binding, source, cursor, current),
    );
  }
  async create(
    binding: SessionBinding,
    source: string,
    csrf: string,
    signal?: AbortSignal,
  ): Promise<EntryProjection> {
    this.#checkSource(binding.host, source);
    const owner = this.#findEntry(binding);
    this.#entryProof(owner, csrf);
    if (owner.busy) throw new BffSessionError('transaction');
    const work = this.#bounded(owner, signal, async (current) => {
      const credentials = owner.credentials!;
      const self = await this.#dependencies.native.self(
        credentials.accessToken,
        this.#binding(binding, source),
        current,
      );
      this.#assertSignal(current);
      this.#assert(owner, 'entry');
      const association =
        self.state === 'active'
          ? self.association
          : await this.#dependencies.native.bootstrap(
              credentials.accessToken,
              this.#binding(binding, source),
              current,
            );
      this.#assertSignal(current);
      this.#assert(owner, 'entry');
      this.#dependencies.native.verifyAssociation(credentials.identity, association);
      // A deliberately repeated create first resolves an existing binding; never retry POST automatically.
      return this.#entryRead(owner, binding, source, undefined, current);
    });
    owner.busy = work;
    try {
      return await work;
    } finally {
      if (owner.busy === work) owner.busy = undefined;
    }
  }
  discardEntry(binding: SessionBinding, csrf: string): LogoutStart {
    this.#checkAddress(binding.host);
    const entry = this.#findEntry(binding, true);
    this.#entryProof(entry, csrf);
    const idToken = entry.credentials!.idToken;
    // This entry owns its provider logout; an unrelated live destination remains published.
    entry.phase = 'logout';
    entry.controller.abort();
    entry.csrf = '';
    entry.deadline = this.#now() + BFF_LIMITS.transactionMs;
    entry.logoutState = opaque();
    entry.logoutProof = opaque();
    this.#logoutStates.set(entry.logoutState, entry);
    let redirectUrl: string | null = null;
    try {
      redirectUrl = this.#dependencies.provider.logoutUrl(
        idToken,
        'https://' + binding.host + '/',
        entry.logoutState,
      );
    } catch (error) {
      this.#diagnostic(this.#reason(error));
    } finally {
      entry.credentials = undefined;
      entry.account = undefined;
      entry.identity = undefined;
    }
    return Object.freeze({ state: entry.logoutState, proof: entry.logoutProof, redirectUrl });
  }
  #validateProviderScope(credentials: ProviderCredentials, scope: NativeScope) {
    const identity = credentials.identity;
    if (
      identity.tenantId !== null &&
      (identity.tenantId !== scope.tenantId || identity.organizationId !== scope.organizationId)
    )
      throw new BffSessionError('identity');
  }
  async #chosen(
    entry: Owner,
    binding: SessionBinding,
    source: string,
    scope: NativeScope,
    cursor: string | undefined,
    signal: AbortSignal,
  ) {
    const credentials = entry.credentials!;
    const contexts = await this.#dependencies.native.contexts(
      credentials.accessToken,
      this.#binding(binding, source),
      { ...(cursor ? { cursor } : {}), limit: 20 },
      signal,
    );
    this.#assertSignal(signal);
    const selected = contexts.items.find(
      (item) => item.tenantId === scope.tenantId && item.organizationId === scope.organizationId,
    );
    if (!selected) throw new BffSessionError('identity');
    return selected;
  }
  async #admission(
    entry: Owner,
    binding: SessionBinding,
    source: string,
    scope: NativeScope,
    signal: AbortSignal,
    phase: Owner['phase'],
  ) {
    this.#assert(entry, phase);
    const credentials = entry.credentials!;
    this.#validateProviderScope(credentials, scope);
    const witness = await this.#dependencies.native.admit(
      credentials.accessToken,
      this.#scoped(binding, source, scope),
      signal,
    );
    this.#assertSignal(signal);
    this.#assert(entry, phase);
    this.#dependencies.native.verifyAssociation(credentials.identity, witness);
    if (witness.tenantId !== scope.tenantId || witness.organizationId !== scope.organizationId)
      throw new BffSessionError('identity');
    const identity = Object.freeze({
      actorId: witness.actorId,
      tenantId: scope.tenantId,
      organizationId: scope.organizationId,
    });
    const account = await this.#dependencies.account(
      credentials.accessToken,
      this.#scoped(binding, source, scope),
      signal,
    );
    this.#assertSignal(signal);
    this.#assert(entry, phase);
    this.#account(entry, account, identity);
    entry.identity = identity;
  }
  async #publish(
    entry: Owner,
    old: Owner | undefined,
    phase: 'selecting' | 'switching',
    signal: AbortSignal,
  ): Promise<SessionCompletion> {
    this.#assertSignal(signal);
    this.#assert(entry, phase);
    const epoch = opaque();
    entry.group.epoch = epoch;
    entry.epoch = epoch;
    for (const competing of entry.group.members)
      if (competing !== entry && competing !== old && competing.pending) this.#close(competing);
    if (old) {
      this.#close(old);
      await Promise.allSettled([...old.work]);
      try {
        this.#assertSignal(signal);
        this.#assert(entry, phase);
      } catch (error) {
        if (entry.phase !== 'logout') this.#close(entry);
        throw error;
      }
    }
    entry.pending = false;
    entry.source = '';
    entry.replacement = undefined;
    entry.phase = 'active';
    // Switch inherits its source absolute cap; initial publication starts its own eight-hour cap.
    if (phase === 'selecting') entry.absolute = this.#now() + BFF_LIMITS.absoluteMs;
    entry.activity = this.#now();
    entry.csrf = opaque();
    entry.group.current = entry;
    entry.group.publishing = undefined;
    this.#assert(entry, 'active');
    return Object.freeze({
      kind: 'session',
      handle: entry.id,
      browser: entry.group.browser,
      returnPath: entry.returnPath,
      session: this.#project(entry),
    });
  }
  async select(
    binding: SessionBinding,
    source: string,
    csrf: string,
    scope: NativeScope,
    cursor?: string,
    signal?: AbortSignal,
  ): Promise<SelectionCompletion> {
    scope = Object.freeze({ tenantId: scope.tenantId, organizationId: scope.organizationId });
    this.#checkSource(binding.host, source);
    const entry = this.#findEntry(binding);
    this.#entryProof(entry, csrf);
    if (entry.busy || entry.group.publishing) throw new BffSessionError('transaction');
    if (!entry.credentials!.identity.displayLabel) throw new BffSessionError('reauthenticate');
    const old = entry.replacement && this.#live(entry.replacement) ? entry.replacement : undefined;
    if (old && old.subject?.key !== entry.subject?.key) throw new BffSessionError('account_change');
    entry.phase = 'selecting';
    entry.group.publishing = entry;
    try {
      return await this.#bounded(entry, signal, async (current) => {
        const selected = await this.#chosen(entry, binding, source, scope, cursor, current);
        this.#assert(entry, 'selecting');
        if (!selected.canUseCurrentHost) {
          return Object.freeze({
            kind: 'destination',
            origin: selected.entryOrigin,
            returnPath: entry.returnPath,
            contextHint: scope.organizationId ?? 'tenant',
          });
        }
        await this.#admission(entry, binding, source, scope, current, 'selecting');
        return this.#publish(entry, old, 'selecting', current);
      });
    } finally {
      if (entry.phase === 'selecting') entry.phase = 'entry';
      if (entry.group.publishing === entry) entry.group.publishing = undefined;
    }
  }
  async switchScope(
    binding: SessionBinding,
    source: string,
    csrf: string,
    scope: NativeScope,
    cursor?: string,
    signal?: AbortSignal,
  ): Promise<SelectionCompletion> {
    scope = Object.freeze({ tenantId: scope.tenantId, organizationId: scope.organizationId });
    this.#checkSource(binding.host, source);
    const old = this.#find(binding);
    if (!old || !bffSecretEquals(csrf, old.csrf)) throw new BffSessionError('revoked');
    if (old.transition || old.group.publishing) throw new BffSessionError('transaction');
    this.sweep();
    const pending = [...this.#owners.values()].filter((owner) => owner.pending);
    const refusal =
      pending.filter((owner) => owner.source === source).length >= BFF_LIMITS.source
        ? 'source_capacity'
        : pending.length >= BFF_LIMITS.pending
          ? 'pending_capacity'
          : this.#owners.size >= BFF_LIMITS.owners
            ? 'global_capacity'
            : null;
    if (refusal) throw new BffSessionError(refusal);
    const now = this.#now();
    const entry: Owner = {
      id: opaque(),
      group: old.group,
      epoch: old.epoch,
      source,
      phase: 'switching',
      pending: true,
      deadline: Math.min(now + BFF_LIMITS.transactionMs, this.#effective(old)),
      controller: new AbortController(),
      work: new Set(),
      returnPath: old.returnPath === '/' ? '/studio' : old.returnPath,
      replacement: old,
      absolute: old.absolute,
      activity: old.activity,
      utcOffset: old.utcOffset,
      csrf: opaque(),
    };
    entry.group.members.add(entry);
    this.#owners.set(entry.id, entry);
    try {
      this.#reserveSubject(entry, old.credentials!);
    } catch (error) {
      this.#close(entry);
      throw error;
    }
    old.group.publishing = entry;
    const work = this.#bounded(entry, signal, async (current) => {
      await this.#ready(old, binding, source, false, current, entry);
      this.#assertSignal(current);
      this.#assert(old, 'active');
      this.#assert(entry, 'switching');
      entry.credentials = old.credentials;
      const choice = await this.#dependencies.administration(
        entry.credentials!.accessToken,
        this.#scoped(binding, source, old.identity!),
        { kind: 'getOwnContexts', limit: 20, ...(cursor ? { cursor } : {}) },
        current,
      );
      this.#assertSignal(current);
      this.#assert(entry, 'switching');
      this.#assert(old, 'active');
      if (choice.kind === 'api-error') throw new NativeApiError('provider_refused', choice.status);
      if (choice.kind !== 'success')
        throw new BffSessionError(
          choice.kind === 'cancelled'
            ? 'cancelled'
            : choice.kind === 'transport-error'
              ? 'transport'
              : 'response',
        );
      const contexts = nativeContextPage(choice.data, 20);
      if (contexts.items.some((item) => item.tenantId !== old.identity!.tenantId))
        throw new BffSessionError('response');
      const selected = contexts.items.find(
        (item) => item.tenantId === scope.tenantId && item.organizationId === scope.organizationId,
      );
      if (!selected) throw new BffSessionError('identity');
      this.#assert(entry, 'switching');
      this.#assert(old, 'active');
      if (!selected.canUseCurrentHost)
        return Object.freeze({
          kind: 'destination',
          origin: selected.entryOrigin,
          returnPath: entry.returnPath,
          contextHint: scope.organizationId ?? 'tenant',
        });
      await this.#admission(entry, binding, source, scope, current, 'switching');
      this.#assert(old, 'active');
      return this.#publish(entry, old, 'switching', current);
    });
    old.transition = work;
    try {
      return await work;
    } finally {
      if (old.transition === work) old.transition = undefined;
      if (entry.phase !== 'active') this.#close(entry);
      if (old.group.publishing === entry) old.group.publishing = undefined;
    }
  }
  async operate(
    binding: SessionBinding,
    source: string,
    operation: NativeOperation,
    csrf?: string,
    signal?: AbortSignal,
  ): Promise<NativeOperationResult> {
    this.#checkSource(binding.host, source);
    const entry = this.#find(binding);
    if (!entry) throw new BffSessionError('revoked');
    if (entry.transition) throw new BffSessionError('transaction');
    let captured: NativeOperation;
    try {
      const serialized = JSON.stringify(operation);
      if (Buffer.byteLength(serialized) > 16 * 1024) throw new Error();
      captured = JSON.parse(serialized) as NativeOperation;
      if (!captured || typeof captured !== 'object') throw new Error();
    } catch {
      throw new BffSessionError('configuration');
    }
    const read = [
      'listMemberships',
      'listOrganizationMemberships',
      'getMembership',
      'getOwnProfile',
      'getOwnContexts',
      'getOwnCapabilities',
      'listRoles',
      'getRole',
      'getPermissions',
      'listOrganizations',
      'getOrganization',
    ].includes(captured.kind);
    if (!read && !bffSecretEquals(csrf, entry.csrf)) throw new BffSessionError('csrf');
    return this.#bounded(entry, signal, async (current) => {
      // One ten-second deadline begins before refresh, including shared refresh wait and body cleanup.
      await this.#ready(entry, binding, source, false, current);
      this.#assertSignal(current);
      this.#assert(entry, 'active');
      const credentials = entry.credentials!;
      const identity = entry.identity!;
      const result = await this.#dependencies.administration(
        credentials.accessToken,
        this.#scoped(binding, source, identity),
        captured,
        current,
      );
      this.#assertSignal(current);
      this.#assert(entry, 'active');
      if (entry.credentials !== credentials || entry.identity !== identity)
        throw new BffSessionError('revoked');
      // Operation refusals withhold their bytes, never reinterpret 401/403/404 as session revocation.
      if (result.kind === 'success') entry.activity = this.#now();
      return result;
    });
  }

  peek(binding: SessionBinding): SessionProjection | null {
    const entry = this.#find(binding);
    return entry ? this.#project(entry) : null;
  }

  async #wait<T>(work: Promise<T>, signal?: AbortSignal): Promise<T> {
    if (!signal) return work;
    if (signal.aborted) throw new BffSessionError('cancelled');
    let cancel!: () => void;
    const interrupted = new Promise<never>((_, reject) => {
      cancel = () => reject(new BffSessionError('cancelled'));
      signal.addEventListener('abort', cancel, { once: true });
    });
    try {
      return await Promise.race([work, interrupted]);
    } finally {
      signal.removeEventListener('abort', cancel);
    }
  }

  async #ready(
    entry: Owner,
    binding: SessionBinding,
    source: string,
    force: boolean,
    signal?: AbortSignal,
    transitionOwner?: Owner,
  ): Promise<boolean> {
    this.#assert(entry, 'active');
    if (
      entry.transition &&
      (!transitionOwner ||
        entry.group.publishing !== transitionOwner ||
        transitionOwner.replacement !== entry)
    )
      throw new BffSessionError('transaction');
    if (
      !entry.refresh &&
      (force || entry.credentials!.accessExpiresAt - this.#now() <= BFF_LIMITS.refreshAheadMs)
    ) {
      const previous = entry.credentials!;
      const work = this.#tracked(entry, async () => {
        try {
          this.#assert(entry, 'active');
          const next = this.#bundle(
            await this.#dependencies.provider.refresh(
              previous.refreshToken!,
              previous.identity,
              entry.controller.signal,
            ),
            previous,
          );
          this.#assert(entry, 'active');
          const self = await this.#dependencies.native.self(
            next.accessToken,
            this.#binding(binding, source),
            entry.controller.signal,
          );
          this.#assert(entry, 'active');
          if (self.state !== 'active') throw new BffSessionError('identity');
          this.#dependencies.native.verifyAssociation(next.identity, self.association);
          if (self.association.actorId !== entry.identity!.actorId)
            throw new BffSessionError('identity');
          const witnessed = await this.#dependencies.account(
            next.accessToken,
            this.#scoped(binding, source, entry.identity!),
            entry.controller.signal,
          );
          this.#assert(entry, 'active');
          this.#account(entry, witnessed, entry.identity!);
          entry.credentials = next;
          entry.activity = this.#now();
          this.#assert(entry, 'active');
        } catch (error) {
          const reason = this.#reason(error);
          if (entry.phase !== 'logout') this.#close(entry);
          this.#diagnostic(reason);
          throw new BffSessionError(reason);
        }
      });
      entry.refresh = work;
      const settled = () => {
        if (entry.refresh === work) entry.refresh = undefined;
      };
      void work.then(settled, settled);
    }
    if (entry.refresh) {
      await this.#wait(entry.refresh, signal);
      this.#assert(entry, 'active');
      return true;
    }
    return false;
  }

  async admit(
    binding: SessionBinding,
    source: string,
    activity: SessionActivity,
    signal?: AbortSignal,
  ): Promise<SessionProjection> {
    this.#checkSource(binding.host, source);
    const entry = this.#find(binding);
    if (!entry) throw new BffSessionError('revoked');
    if (signal?.aborted) throw new BffSessionError('cancelled');
    if (activity === 'passive') return this.#project(entry);
    if (activity !== 'read' && activity !== 'extend') throw new BffSessionError('configuration');
    if (entry.transition) throw new BffSessionError('transaction');
    if (await this.#ready(entry, binding, source, activity === 'extend', signal))
      return this.#project(entry);
    this.#assert(entry, 'active');
    // A normal authenticated read always uses the API's current authority too.
    const credentials = entry.credentials!;
    const work = this.#tracked(entry, async () => {
      try {
        this.#assert(entry, 'active');
        const account = await this.#dependencies.account(
          credentials.accessToken,
          this.#scoped(binding, source, entry.identity!),
          entry.controller.signal,
        );
        this.#assert(entry, 'active');
        if (entry.credentials !== credentials) throw new BffSessionError('transaction');
        this.#account(entry, account, entry.identity!);
      } catch (error) {
        const reason = this.#reason(error);
        // An ordinary read consumes no provider grant. A failed authority check
        // denies this response, but only a proven identity refusal revokes the
        // live session. Refresh above retains its stricter uncertainty fence.
        if (reason === 'identity' && entry.phase !== 'logout') this.#close(entry);
        this.#diagnostic(reason);
        throw new BffSessionError(reason);
      }
    });
    await this.#wait(work, signal);
    this.#assert(entry, 'active');
    entry.activity = this.#now();
    return this.#project(entry);
  }

  signout(binding: SessionBinding, csrf: string): LogoutStart {
    this.#checkAddress(binding.host);
    const entry = this.#find(binding);
    if (!entry || !bffSecretEquals(csrf, entry.csrf)) throw new BffSessionError('revoked');
    const idToken = entry.credentials!.idToken;
    entry.group.epoch = opaque();
    entry.group.current = undefined;
    for (const pending of entry.group.members)
      if (pending !== entry && pending.pending) this.#close(pending);
    entry.phase = 'logout';
    entry.controller.abort();
    entry.csrf = '';
    entry.returnPath = '/';
    entry.deadline = this.#now() + BFF_LIMITS.transactionMs;
    entry.logoutState = opaque();
    entry.logoutProof = opaque();
    this.#logoutStates.set(entry.logoutState, entry);
    let redirectUrl: string | null = null;
    try {
      redirectUrl = this.#dependencies.provider.logoutUrl(
        idToken,
        'https://' + binding.host + '/',
        entry.logoutState,
      );
    } catch (error) {
      this.#diagnostic(this.#reason(error));
    } finally {
      entry.credentials = undefined;
      entry.account = undefined;
    }
    this.#diagnostic('revoked');
    return Object.freeze({ state: entry.logoutState, proof: entry.logoutProof, redirectUrl });
  }

  consumeLogout(input: Parameters<BffSessionStore['consumeLogout']>[0]): boolean {
    const entry = isBffOpaque(input.state) ? this.#logoutStates.get(input.state) : undefined;
    if (
      this.#stopped ||
      !entry ||
      entry.phase !== 'logout' ||
      this.#now() >= entry.deadline ||
      entry.group.host !== input.host ||
      !bffSecretEquals(input.browser, entry.group.browser) ||
      !bffSecretEquals(input.proof, entry.logoutProof)
    )
      return false;
    this.#logoutStates.delete(input.state);
    this.#close(entry);
    return true;
  }

  stop(): Promise<void> {
    if (this.#stopping) return this.#stopping;
    let completed!: () => void;
    this.#stopping = new Promise<void>((resolve) => {
      completed = resolve;
    });
    this.#stopped = true;
    clearInterval(this.#timer);
    const entries = [...this.#owners.values()];
    const work = entries.flatMap((entry) => [...entry.work]);
    for (const entry of entries) this.#close(entry);
    void Promise.allSettled(work).then(() => {
      for (const entry of entries) this.#release(entry);
      completed();
    });
    return this.#stopping;
  }

  /** Scalar observation only: tests/health never acquire tokens or indexes. */
  counts() {
    return Object.freeze({
      owners: this.#owners.size,
      pending: [...this.#owners.values()].filter((entry) => entry.pending).length,
      groups: this.#groups.size,
      subjects: new Set([...this.#owners.values()].map((entry) => entry.subject).filter(Boolean))
        .size,
      operations: [...this.#owners.values()].reduce((count, entry) => count + entry.work.size, 0),
      credentialRecords: [...this.#owners.values()].filter((entry) => entry.credentials).length,
      transactionRecords: [...this.#owners.values()].filter((entry) => entry.transaction).length,
      proofRecords: [...this.#owners.values()].filter((entry) => entry.proof || entry.logoutProof)
        .length,
      csrfRecords: [...this.#owners.values()].filter((entry) => entry.csrf).length,
      loginStates: this.#states.size,
      logoutStates: this.#logoutStates.size,
    });
  }
}
