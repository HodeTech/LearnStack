import { randomBytes, timingSafeEqual } from 'node:crypto';

import { canonicalAddress, normalizeHost } from '../ingress.js';
import { BffProviderError } from './provider-error.js';
import type { LoginTransaction, ProviderCredentials } from './provider.js';
import { BffSessionError } from './session-contract.js';
import type {
  AccountWitness,
  BffSessionStore,
  LoginCompletion,
  LoginStart,
  LogoutStart,
  SessionActivity,
  SessionBinding,
  SessionDependencies,
  SessionFailure,
  SessionProjection,
} from './session-contract.js';

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
  phase: 'starting' | 'pending' | 'exchange' | 'active' | 'logout' | 'closing';
  pending: boolean;
  deadline: number;
  controller: AbortController;
  work: Set<Promise<unknown>>;
  transaction?: LoginTransaction;
  proof?: string;
  returnPath: '/' | '/studio' | '/portal';
  replacement?: Owner;
  subject?: Readonly<{ key: string }>;
  credentials?: ProviderCredentials;
  account?: AccountWitness;
  absolute: number;
  activity: number;
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
    const deadline = phase === 'active' ? this.#effective(entry) : entry.deadline;
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
      expiresAt: this.#utc() + Math.max(0, this.#effective(entry) - this.#now()),
      limitedByAbsolute: this.#effective(entry) === entry.absolute,
      csrfToken: entry.csrf,
    });
  }
  #account(
    entry: Owner,
    credentials: ProviderCredentials,
    account: AccountWitness,
    previous?: AccountWitness,
  ) {
    const identity = credentials.identity;
    if (
      !account ||
      typeof account.displayName !== 'string' ||
      account.displayName.length > 1024 ||
      (account.organizationId !== null &&
        (typeof account.organizationId !== 'string' ||
          !/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/.test(
            account.organizationId,
          ) ||
          account.organizationId === '00000000-0000-0000-0000-000000000000')) ||
      account.actorId !== identity.actorId ||
      account.tenantId !== identity.tenantId ||
      (identity.organizationId !== null && account.organizationId !== identity.organizationId) ||
      (previous &&
        (account.actorId !== previous.actorId ||
          account.tenantId !== previous.tenantId ||
          account.organizationId !== previous.organizationId))
    )
      throw new BffSessionError('identity');
    // Only closed fields are retained; additive provider/API objects cannot enter projections.
    entry.account = Object.freeze({
      actorId: account.actorId,
      tenantId: account.tenantId,
      organizationId: account.organizationId,
      displayName: account.displayName,
    });
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
        Object.keys(previous.identity).some(
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
      if (expired && entry.phase !== 'closing') this.#close(entry);
      this.#release(entry);
    }
  }

  async start(input: Parameters<BffSessionStore['start']>[0]): Promise<LoginStart> {
    this.#checkSource(input.host, input.source);
    if (input.signal?.aborted) throw new BffSessionError('cancelled');
    if (
      !['/', '/studio', '/portal'].includes(input.returnPath) ||
      (input.browser !== undefined && !isBffOpaque(input.browser))
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
      replacement,
      absolute: now + BFF_LIMITS.absoluteMs,
      activity: now,
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
        const account = await this.#dependencies.account(
          credentials.accessToken,
          {
            host: input.host,
            source: input.source,
            ...(input.traceparent ? { traceparent: input.traceparent } : {}),
          },
          entry.controller.signal,
        );
        this.#assert(entry, 'exchange');
        this.#account(entry, credentials, account);
        this.sweep();
        this.#assert(entry, 'exchange');
        const subject = JSON.stringify([credentials.identity.issuer, credentials.identity.subject]);
        const old =
          entry.replacement && this.#live(entry.replacement) ? entry.replacement : undefined;
        const retained = new Set(
          [...this.#owners.values()]
            .map((value) => value.subject)
            .filter((credit) => credit?.key === subject),
        ).size;
        if (retained >= BFF_LIMITS.subject && old?.subject?.key !== subject)
          throw new BffSessionError('subject_capacity');
        // Reserve a new subject credit before retiring a different subject. A same-
        // subject replacement borrows its old credit until actual old I/O settles.
        entry.subject =
          old?.subject?.key === subject ? old.subject : Object.freeze({ key: subject });
        entry.group.publishing = entry;
        const epoch = opaque();
        entry.group.epoch = epoch;
        entry.epoch = epoch;
        for (const competing of entry.group.members)
          if (competing !== entry && competing !== old && competing.pending) this.#close(competing);
        if (old) {
          this.#close(old);
          await Promise.allSettled([...old.work]);
          this.#assert(entry, 'exchange');
        }
        entry.credentials = credentials;
        entry.pending = false;
        entry.source = '';
        entry.replacement = undefined;
        entry.phase = 'active';
        entry.absolute = this.#now() + BFF_LIMITS.absoluteMs;
        entry.activity = this.#now();
        entry.proof = undefined;
        entry.transaction = undefined;
        entry.group.current = entry;
        entry.group.publishing = undefined;
        this.#assert(entry, 'active');
        return Object.freeze({
          handle: entry.id,
          browser: entry.group.browser,
          returnPath: entry.returnPath,
          session: this.#project(entry),
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
    if (
      !entry.refresh &&
      (activity === 'extend' ||
        entry.credentials!.accessExpiresAt - this.#now() <= BFF_LIMITS.refreshAheadMs)
    ) {
      const previous = entry.credentials!;
      const account = entry.account!;
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
          const witnessed = await this.#dependencies.account(
            next.accessToken,
            {
              host: binding.host,
              source,
              ...(binding.traceparent ? { traceparent: binding.traceparent } : {}),
            },
            entry.controller.signal,
          );
          this.#assert(entry, 'active');
          this.#account(entry, next, witnessed, account);
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
      entry.activity = this.#now();
      return this.#project(entry);
    }
    this.#assert(entry, 'active');
    // A normal authenticated read always uses the API's current authority too.
    const credentials = entry.credentials!;
    const work = this.#tracked(entry, async () => {
      try {
        this.#assert(entry, 'active');
        const account = await this.#dependencies.account(
          credentials.accessToken,
          {
            host: binding.host,
            source,
            ...(binding.traceparent ? { traceparent: binding.traceparent } : {}),
          },
          entry.controller.signal,
        );
        this.#assert(entry, 'active');
        this.#account(entry, credentials, account, entry.account);
      } catch (error) {
        const reason = this.#reason(error);
        if (entry.phase !== 'logout') this.#close(entry);
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
    });
  }
}
