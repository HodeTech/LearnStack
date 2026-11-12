import { randomBytes, timingSafeEqual } from 'node:crypto';

import type { OwnMembershipProfile } from '@learnstack/sdk/administration-server';

import type { VerifiedLogoutToken } from './backchannel-contract.js';
import { BackchannelState, matchesProviderLogout } from './backchannel-state.js';
import type { ProviderLogoutTarget } from './backchannel-state.js';
import type { NativeConsentResponseLease } from './consent-response-contract.js';
import { canonicalAddress, normalizeHost } from '../ingress.js';
import { openEntryCursor, sealEntryCursor } from './entry-cursor.js';
import { bffActionDocumentTarget } from './http-paths.js';
import { BffInputError } from './input-error.js';
import type {
  InvitationContinuation,
  InvitationOpening,
  InvitationResponseLease,
  InvitationCommand,
} from './invitation-session.js';
import { NativeApiError } from './native-contract.js';
import type { NativeScope, SelfContexts } from './native-contract.js';
import type {
  NativeInvitationInspection,
  NativeInvitationResult,
} from './native-invitation-contract.js';
import { NativeInvitationError } from './native-invitation-contract.js';
import type {
  NativeOperation,
  NativeOperationResult,
  RetainedConsentOperation,
} from './native-operation-contract.js';
import { nativeContextPage } from './native-session-client.js';
import type { NativeEntrySnapshot, NativePageSnapshot, ScopeChoices } from './page-contract.js';
import { nativeDocumentPlan, preloadPage } from './page-preload.js';
import { bffCursor, bffQuery } from './page-query.js';
import type {
  NativePageLease,
  NativePagePreparation,
  NativeSnapshotTicket,
} from './page-snapshot-contract.js';
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
  PagePreparation,
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
  invitation?: Owner;
};
type Owner = {
  id: string;
  admittedAt: number;
  providerTarget?: ProviderLogoutTarget;
  invitation?: Owner;
  custody?: {
    revision: object;
    code?: string;
    credentialsOwner?: Owner;
    inspection?: NativeInvitationInspection;
    result?: NativeInvitationResult;
    acceptanceUncertain?: boolean;
    state: InvitationContinuation['state'];
  };
  group: Group;
  epoch: string;
  source: string;
  phase:
    | 'invitation'
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
  verification?: true;
  proof?: string;
  returnPath: NativeReturnPath;
  contextHint?: string;
  replacement?: Owner;
  subject?: Readonly<{ key: string }>;
  credentials?: ProviderCredentials;
  account?: AccountWitness;
  identity?: PublishedSessionIdentity;
  cursorKey?: Buffer;
  pageCursorKey?: Buffer;
  pageRevision?: object;
  pageLeases?: Set<NativePageLease>;
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
  readonly #backchannel: BackchannelState;
  readonly #dependencies: SessionDependencies;
  readonly #preparation: NativePagePreparation | undefined;
  readonly #timer: ReturnType<typeof setInterval>;
  #stopped = false;
  #stopping?: Promise<void>;

  constructor(dependencies: SessionDependencies, preparation?: NativePagePreparation) {
    this.#dependencies = dependencies;
    this.#backchannel = new BackchannelState({
      monotonic: () => this.#now(),
      utc: () => this.#utc(),
    });
    this.#preparation = preparation;
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
    if (this.#now() >= deadline) {
      if (phase === 'invitation') this.#close(entry);
      throw new BffSessionError('expired');
    }
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
    this.#clearCursorKey(entry);
    this.#clearPageCursorKey(entry);
    entry.pageLeases?.clear();
    entry.pageLeases = undefined;
    entry.pageRevision = undefined;
    this.#owners.delete(entry.id);
    if (entry.transaction) this.#states.delete(entry.transaction.state);
    if (entry.logoutState) this.#logoutStates.delete(entry.logoutState);
    entry.source = '';
    entry.subject = undefined;
    entry.providerTarget = undefined;
    entry.transaction = undefined;
    entry.verification = undefined;
    entry.logoutState = undefined;
    entry.logoutProof = undefined;
    entry.replacement = undefined;
    entry.invitation = undefined;
    entry.custody = undefined;
    entry.csrf = '';
    entry.group.members.delete(entry);
    if (entry.group.current === entry) entry.group.current = undefined;
    if (entry.group.publishing === entry) entry.group.publishing = undefined;
    if (entry.group.invitation === entry) entry.group.invitation = undefined;
    if (!entry.group.members.size)
      this.#groups.delete(this.#groupKey(entry.group.host, entry.group.browser));
  }
  #close(entry: Owner) {
    if (entry.phase === 'closing') {
      this.#release(entry);
      return;
    }
    this.#abortPages(entry);
    this.#clearCursorKey(entry);
    this.#clearPageCursorKey(entry);
    entry.phase = 'closing';
    if (entry.custody) {
      for (const linked of entry.group.members)
        if (
          linked.invitation === entry &&
          ['starting', 'pending', 'exchange'].includes(linked.phase)
        )
          this.#close(linked);
      entry.custody.code = undefined;
      entry.custody.inspection = undefined;
      entry.custody.result = undefined;
      entry.custody.credentialsOwner = undefined;
      entry.custody.revision = Object.freeze({});
    }
    entry.credentials = undefined;
    entry.account = undefined;
    entry.identity = undefined;
    entry.proof = undefined;
    entry.controller.abort();
    this.#release(entry);
  }
  #clearCursorKey(entry: Owner) {
    entry.cursorKey?.fill(0);
    entry.cursorKey = undefined;
  }
  #clearPageCursorKey(entry: Owner) {
    entry.pageCursorKey?.fill(0);
    entry.pageCursorKey = undefined;
  }
  #abortPages(entry: Owner, pinnedOnly = true) {
    for (const lease of entry.pageLeases ?? []) if (!pinnedOnly || lease.pinned) lease.abort();
  }
  #pageRevision(entry: Owner) {
    return (entry.pageRevision ??= Object.freeze({}));
  }
  #pageValid(entry: Owner, phase: 'entry' | 'active', revision: object) {
    try {
      this.#assert(entry, phase);
      return this.#pageRevision(entry) === revision && (phase === 'entry' || this.#live(entry));
    } catch {
      return false;
    }
  }
  #pageCursorBinding(
    entry: Owner,
    purpose:
      | 'members'
      | 'scopes'
      | 'invitations'
      | 'custom_fields'
      | 'custom_revisions'
      | 'consent_history',
  ) {
    return { ...this.#cursorBinding(entry), purpose };
  }
  #wrapPageCursor(
    entry: Owner,
    purpose:
      | 'members'
      | 'scopes'
      | 'invitations'
      | 'custom_fields'
      | 'custom_revisions'
      | 'consent_history',
    cursor: string,
  ) {
    entry.pageCursorKey ??= randomBytes(32);
    return sealEntryCursor(entry.pageCursorKey, this.#pageCursorBinding(entry, purpose), cursor);
  }
  #openPageCursor(
    entry: Owner,
    purpose:
      | 'members'
      | 'scopes'
      | 'invitations'
      | 'custom_fields'
      | 'custom_revisions'
      | 'consent_history',
    cursor: string | undefined,
  ) {
    if (cursor === undefined) return undefined;
    if (!entry.pageCursorKey) throw new BffInputError();
    return openEntryCursor(entry.pageCursorKey, this.#pageCursorBinding(entry, purpose), cursor);
  }
  #retainPage(entry: Owner, ticket: NativeSnapshotTicket) {
    if (!this.#preparation) throw new BffSessionError('configuration');
    const lease = this.#preparation.reserve(ticket, entry);
    (entry.pageLeases ??= new Set()).add(lease);
    const hold = this.#tracked(entry, () => lease.cleaned);
    const release = () => entry.pageLeases?.delete(lease);
    void hold.then(release, release);
    return lease;
  }
  /** The only page producer. Reservation precedes authority/API work on the real owner. */
  async preparePage(
    binding: SessionBinding,
    source: string,
    input: PagePreparation,
  ): Promise<NativePageSnapshot | NativeEntrySnapshot> {
    this.#checkSource(binding.host, source);
    const ticket = input.ticket;
    if (
      !this.#preparation ||
      ticket.binding.host !== binding.host ||
      ticket.binding.peer !== source ||
      ticket.signal.aborted
    )
      throw new BffSessionError('configuration');
    if (input.kind === 'response') {
      const target = ticket.binding.target;
      const page = input.page;
      if (
        ticket.binding.method !== 'POST' ||
        !bffActionDocumentTarget(target) ||
        (page.kind === 'mutation_error'
          ? target !== page.action ||
            target === '/api/auth/switch' ||
            target === '/api/auth/entry/select'
          : page.kind !== 'account_change' ||
            !['/api/auth/switch', '/api/auth/entry/select'].includes(target))
      )
        throw new BffInputError();
      const owner = this.#find(binding);
      if (!owner) throw new BffSessionError('revoked');
      const csrf =
        page.kind === 'account_change'
          ? page.csrfToken
          : page.fields.filter(([name]) => name === 'csrfToken');
      if (
        !bffSecretEquals(
          typeof csrf === 'string' ? csrf : csrf.length === 1 ? csrf[0]![1] : undefined,
          owner.csrf,
        )
      )
        throw new BffSessionError('csrf');
      const lease = this.#retainPage(owner, ticket);
      const revision = this.#pageRevision(owner);
      lease.pin(
        () => this.#pageValid(owner, 'active', revision),
        this.#effective(owner) - this.#now(),
      );
      // Only the current actor's API-validated preference selects recovery copy.
      // Posted profile values never select it, including when editing oneself.
      const localeWork = this.#bounded(owner, lease.signal, async (signal) => {
        const result = await this.#dependencies.administration(
          owner.credentials!.accessToken,
          this.#scoped(binding, source, owner.identity!),
          { kind: 'getOwnProfile' },
          signal,
        );
        if (!this.#pageValid(owner, 'active', revision)) throw new BffSessionError('revoked');
        const preferred =
          result.kind === 'success' ? (result.data as OwnMembershipProfile).preferredLocale : null;
        return typeof preferred === 'string' && ticket.site.enabledLocales.includes(preferred)
          ? preferred
          : ticket.site.defaultLocale;
      });
      lease.track(localeWork);
      let locale = ticket.site.defaultLocale;
      try {
        locale = await this.#wait(localeWork, lease.signal);
      } catch (error) {
        if (!this.#pageValid(owner, 'active', revision) || lease.signal.aborted) throw error;
        // A missing/unavailable optional preference keeps the admitted recovery,
        // in the institution's language; it supplies no replacement authority.
      }
      return lease.publishEntry({
        site: ticket.site,
        locale,
        view: page,
      });
    }
    const path = ticket.binding.target.split('?', 1)[0];
    if (input.kind === 'document') {
      if (ticket.binding.method !== 'GET') throw new BffInputError();
    } else {
      if (
        path !== (input.kind === 'create' ? '/api/auth/entry/create' : '/api/auth/entry') ||
        ticket.binding.method !== (input.kind === 'create' ? 'POST' : 'GET')
      )
        throw new BffInputError();
      const query = bffQuery(ticket.binding.target, input.kind === 'create' ? [] : ['cursor']);
      if (input.kind !== 'create' && bffCursor(query.get('cursor')) !== input.cursor)
        throw new BffInputError();
    }
    const plan =
      input.kind === 'document' ? nativeDocumentPlan(ticket.binding.target, ticket.flight) : null;
    const isEntry = input.kind === 'entry' || input.kind === 'create';
    const owner = isEntry ? this.#findEntry(binding) : this.#find(binding);
    if (!owner) throw new BffSessionError('revoked');
    const rawCursor =
      input.kind === 'entry'
        ? input.cursor === undefined
          ? undefined
          : openEntryCursor(owner.cursorKey!, this.#cursorBinding(owner), input.cursor)
        : input.kind === 'scopes'
          ? this.#openPageCursor(owner, 'scopes', input.cursor)
          : plan?.cursor
            ? this.#openPageCursor(
                owner,
                plan.kind === 'invitations'
                  ? 'invitations'
                  : plan.kind === 'custom_fields'
                    ? 'custom_fields'
                    : plan.kind === 'custom_field'
                      ? 'custom_revisions'
                      : 'members',
                plan.cursor,
              )
            : undefined;
    if (input.kind === 'create') this.#entryProof(owner, input.csrf);
    const lease = this.#retainPage(owner, ticket);
    const pin = (phase: 'entry' | 'active') => {
      const revision = this.#pageRevision(owner);
      lease.pin(
        () => this.#pageValid(owner, phase, revision),
        (phase === 'active'
          ? this.#effective(owner)
          : Math.min(owner.deadline, owner.credentials!.refreshExpiresAt)) - this.#now(),
      );
      return revision;
    };
    const entryView = (entry: EntryProjection): NativeEntrySnapshot => ({
      site: ticket.site,
      locale: ticket.site.defaultLocale,
      view: {
        kind: 'entry',
        entry,
        cursor: input.kind === 'entry' ? (input.cursor ?? null) : null,
      },
    });
    const failure = (error: unknown): NativeEntrySnapshot => {
      if (lease.signal.aborted || this.#stopped) throw error;
      if (!lease.pinned) lease.pin(() => !this.#stopped && !ticket.signal.aborted, 10_000);
      const reason = this.#reason(error);
      const supplied = error instanceof NativeApiError ? error.status : undefined;
      const status =
        supplied ??
        (reason === 'csrf'
          ? 403
          : ['identity', 'invalid_grant', 'reauthenticate', 'expired', 'revoked'].includes(reason)
            ? 400
            : 503);
      return lease.publishEntry({
        site: ticket.site,
        locale: ticket.site.defaultLocale,
        view: {
          kind: 'failure',
          reason:
            status === 401 ||
            ['identity', 'invalid_grant', 'reauthenticate', 'expired', 'revoked'].includes(reason)
              ? 'reauthenticate'
              : status === 403 && input.kind === 'create' && error instanceof NativeApiError
                ? 'creation_disabled'
                : status === 403 || status === 404
                  ? 'denied'
                  : status === 429
                    ? 'busy'
                    : 'unavailable',
          status,
          code:
            error instanceof BffSessionError || error instanceof BffProviderError
              ? reason
              : 'unavailable',
          retryAfterSeconds:
            error instanceof NativeApiError ? (error.retryAfterSeconds ?? null) : null,
          returnPath: plan?.target ?? '/studio',
        },
      });
    };
    if (isEntry) {
      pin('entry');
      const work =
        input.kind === 'create'
          ? this.create(
              binding,
              source,
              input.csrf,
              input.signal ? AbortSignal.any([lease.signal, input.signal]) : lease.signal,
            )
          : this.#bounded(owner, lease.signal, (current) =>
              this.#entryRead(owner, binding, source, rawCursor, current),
            );
      lease.track(work);
      try {
        return lease.publishEntry(entryView(await this.#wait(work, lease.signal)));
      } catch (error) {
        return failure(error);
      }
    }
    let authorityFailure: unknown;
    try {
      const work = this.#bounded(owner, lease.signal, async (current) => {
        this.#assert(owner, 'active');
        if (owner.transition) throw new BffSessionError('transaction');
        const refreshed =
          ticket.activity === 'read'
            ? await this.#ready(owner, binding, source, false, current, undefined, (actual) =>
                lease.track(actual),
              )
            : false;
        this.#assertSignal(current);
        this.#assert(owner, 'active');
        const credentials = owner.credentials!;
        if (credentials.accessExpiresAt <= this.#now())
          throw new BffSessionError('provider_refused');
        if (!refreshed) {
          try {
            const account = await this.#dependencies.account(
              credentials.accessToken,
              this.#scoped(binding, source, owner.identity!),
              current,
            );
            this.#assertSignal(current);
            this.#assert(owner, 'active');
            if (owner.credentials !== credentials) throw new BffSessionError('transaction');
            this.#account(owner, account, owner.identity!);
          } catch (error) {
            if (this.#reason(error) === 'identity' && owner.phase !== 'logout') {
              authorityFailure = error;
              this.#close(owner);
            }
            throw error;
          }
          if (ticket.activity === 'read') owner.activity = this.#now();
        }
        const revision = pin('active');
        const read = async (operation: NativeOperation) => {
          this.#assertSignal(current);
          if (!this.#pageValid(owner, 'active', revision)) throw new BffSessionError('revoked');
          const result = await this.#dependencies.administration(
            credentials.accessToken,
            this.#scoped(binding, source, owner.identity!),
            operation,
            current,
          );
          this.#assertSignal(current);
          if (!this.#pageValid(owner, 'active', revision)) throw new BffSessionError('revoked');
          return result;
        };
        if (input.kind === 'scopes') {
          const result = await read({
            kind: 'getOwnContexts',
            limit: 20,
            ...(rawCursor ? { cursor: rawCursor } : {}),
          });
          if (result.kind === 'api-error')
            throw new NativeApiError('provider_refused', result.status);
          if (result.kind !== 'success') throw new BffSessionError('response');
          const contexts = nativeContextPage(result.data, 20);
          if (contexts.items.some((item) => item.tenantId !== owner.identity!.tenantId))
            throw new BffSessionError('response');
          const selection: ScopeChoices = {
            session: this.#project(owner),
            expiresAt: this.#project(owner).expiresAt,
            contexts: {
              items: contexts.items.map((item) => ({
                ...item,
                current:
                  item.tenantId === owner.identity!.tenantId &&
                  item.organizationId === owner.identity!.organizationId,
              })),
              endCursor:
                contexts.endCursor === null
                  ? null
                  : this.#wrapPageCursor(owner, 'scopes', contexts.endCursor),
              hasNextPage: contexts.hasNextPage,
            },
          };
          return {
            site: ticket.site,
            locale: ticket.site.defaultLocale,
            view: { kind: 'scopes', selection, cursor: input.cursor ?? null },
          } as NativeEntrySnapshot;
        }
        return preloadPage(
          { ...plan!, ...(rawCursor ? { cursor: rawCursor } : {}) },
          {
            target: plan!.target,
            site: ticket.site,
            session: this.#project(owner),
            scopeKind: owner.identity!.organizationId === null ? 'tenant' : 'organization',
            formKeys: Array.from(
              { length: plan!.kind === 'profile' || plan!.kind === 'member' ? 180 : 32 },
              opaque,
            ),
          },
          owner.identity!.organizationId,
          read,
          (cursor) =>
            this.#wrapPageCursor(
              owner,
              plan!.kind === 'invitations'
                ? 'invitations'
                : plan!.kind === 'custom_fields'
                  ? 'custom_fields'
                  : plan!.kind === 'custom_field'
                    ? 'custom_revisions'
                    : 'members',
              cursor,
            ),
        );
      });
      lease.track(work);
      const value = await this.#wait(work, lease.signal);
      return 'target' in value ? lease.publish(value) : lease.publishEntry(value);
    } catch (error) {
      // Before positive authority, only public recovery may be returned. No cached
      // session/profile/CSRF data enters this response after an uncertain refusal.
      return failure(authorityFailure ?? error);
    }
  }
  #cursorBinding(entry: Owner) {
    return { id: entry.id, host: entry.group.host, epoch: entry.epoch };
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
  #bindProviderTarget(entry: Owner, credentials: ProviderCredentials) {
    const target = Object.freeze({
      issuer: credentials.identity.issuer,
      sid: credentials.identity.sid,
      subject: credentials.identity.subject,
    });
    if (!this.#backchannel.accepts(target)) throw new BffSessionError('revoked');
    entry.providerTarget = target;
  }
  #reserveSubject(entry: Owner, credentials: ProviderCredentials) {
    this.#bindProviderTarget(entry, credentials);
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
      typeof next.identity?.sid !== 'string' ||
      !next.identity.sid ||
      Buffer.byteLength(next.identity.sid) > 255 ||
      // eslint-disable-next-line no-control-regex -- Provider-session identifiers reject controls at the custody boundary.
      /[\u0000-\u001f\u007f-\u009f\ud800-\udfff]/u.test(next.identity.sid) ||
      (!next.refreshToken && !previous?.refreshToken) ||
      !Number.isFinite(next.accessExpiresAt) ||
      !Number.isFinite(next.refreshExpiresAt) ||
      next.accessExpiresAt <= this.#now() ||
      next.refreshExpiresAt <= this.#now() ||
      (previous &&
        (['issuer', 'sid', 'subject', 'actorId', 'tenantId', 'organizationId'] as const).some(
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
        sid: next.identity.sid,
        subject: next.identity.subject,
        actorId: next.identity.actorId,
        tenantId: next.identity.tenantId,
        organizationId: next.identity.organizationId,
        displayLabel: previous ? null : next.identity.displayLabel,
      }),
      accessExpiresAt: next.accessExpiresAt,
      refreshExpiresAt: next.refreshExpiresAt,
      refreshToken: next.refreshToken ?? previous?.refreshToken,
    });
    if (Buffer.byteLength(JSON.stringify(bundle)) > BFF_LIMITS.credentialBytes)
      throw new BffSessionError('response');
    return bundle;
  }

  #findInvitation(binding: SessionBinding): Owner {
    const owner = isBffOpaque(binding.handle) ? this.#owners.get(binding.handle) : undefined;
    if (
      !owner ||
      owner.group.host !== binding.host ||
      !bffSecretEquals(owner.group.browser, binding.browser) ||
      owner.group.invitation !== owner ||
      !owner.custody
    )
      throw new BffSessionError('revoked');
    this.#assert(owner, 'invitation');
    return owner;
  }
  #pendingCapacity(source: string) {
    this.sweep();
    const pending = [...this.#owners.values()].filter((owner) => owner.pending);
    const reason =
      pending.filter((owner) => owner.source === source).length >= BFF_LIMITS.source
        ? 'source_capacity'
        : pending.length >= BFF_LIMITS.pending
          ? 'pending_capacity'
          : this.#owners.size >= BFF_LIMITS.owners
            ? 'global_capacity'
            : null;
    if (reason) throw new BffSessionError(reason);
  }
  openInvitation(input: Parameters<BffSessionStore['openInvitation']>[0]): InvitationOpening {
    this.#checkSource(input.host, input.source);
    if (input.browser !== undefined && !isBffOpaque(input.browser))
      throw new BffSessionError('configuration');
    if (input.handle && input.browser) {
      try {
        const owner = this.#findInvitation({
          host: input.host,
          browser: input.browser,
          handle: input.handle,
        });
        return Object.freeze({
          handle: owner.id,
          browser: owner.group.browser,
          csrfToken: owner.csrf,
        });
      } catch (error) {
        if (!(error instanceof BffSessionError)) throw error;
      }
    }
    this.#pendingCapacity(input.source);
    let browser = input.browser ?? opaque();
    let group = this.#groups.get(this.#groupKey(input.host, browser));
    let credentialsOwner = input.sessionHandle
      ? this.#find({ host: input.host, browser, handle: input.sessionHandle })
      : undefined;
    if (group?.current && !credentialsOwner) {
      browser = opaque();
      group = undefined;
    }
    if (!group) {
      group = { host: input.host, browser, epoch: opaque(), members: new Set() };
      this.#groups.set(this.#groupKey(input.host, browser), group);
    }
    if (!credentialsOwner && input.entryHandle) {
      try {
        credentialsOwner = this.#findEntry({
          host: input.host,
          browser,
          handle: input.entryHandle,
        });
      } catch {
        /* no borrowed authority */
      }
    }
    const now = this.#now();
    const owner: Owner = {
      id: opaque(),
      admittedAt: now,
      group,
      epoch: group.epoch,
      source: input.source,
      phase: 'invitation',
      pending: true,
      deadline: now + BFF_LIMITS.transactionMs,
      controller: new AbortController(),
      work: new Set(),
      returnPath: '/',
      absolute: now + BFF_LIMITS.transactionMs,
      activity: now,
      utcOffset: this.#utc() - now,
      csrf: opaque(),
      custody: {
        revision: Object.freeze({}),
        state: credentialsOwner ? 'inspect' : 'signin',
        credentialsOwner,
      },
    };
    if (credentialsOwner) this.#reserveSubject(owner, credentialsOwner.credentials!);
    if (credentialsOwner?.phase === 'entry') credentialsOwner.invitation = owner;
    const previous = group.invitation;
    group.invitation = owner;
    group.members.add(owner);
    this.#owners.set(owner.id, owner);
    if (previous) this.#close(previous);
    return Object.freeze({ handle: owner.id, browser, csrfToken: owner.csrf });
  }
  retainInvitation(binding: SessionBinding, lease: InvitationResponseLease): void {
    const owner = this.#findInvitation(binding);
    const revision = owner.custody!.revision;
    lease.pin(() => {
      this.#assert(owner, 'invitation');
      if (
        owner.group.invitation !== owner ||
        owner.custody?.revision !== revision ||
        lease.signal.aborted
      )
        throw new BffSessionError('revoked');
    });
    void this.#tracked(owner, () => lease.cleaned);
  }
  invitationEntry(binding: SessionBinding, entry: SessionBinding): boolean {
    try {
      const invitation = this.#findInvitation(binding);
      const credentialOwner = this.#findEntry(entry);
      return (
        credentialOwner.invitation === invitation &&
        invitation.custody!.credentialsOwner === credentialOwner
      );
    } catch {
      return false;
    }
  }
  #invitationProjection(owner: Owner): InvitationContinuation {
    this.#assert(owner, 'invitation');
    const custody = owner.custody!;
    const credentialOwner = custody.credentialsOwner;
    const live = owner.group.current;
    return Object.freeze({
      csrfToken: owner.csrf,
      state: custody.state,
      ...(custody.state === 'ready' && custody.inspection
        ? { inspection: custody.inspection }
        : {}),
      ...(custody.result
        ? {
            disposition: custody.result.disposition,
            sessionAvailable:
              !!live &&
              this.#live(live) &&
              live.identity?.tenantId === custody.result.tenantId &&
              live.identity.organizationId === custody.result.organizationId,
          }
        : {}),
      ...(credentialOwner?.phase === 'entry' ? { entryCsrfToken: credentialOwner.csrf } : {}),
      ...(live && this.#live(live) ? { sessionCsrfToken: live.csrf } : {}),
    });
  }
  async #invitationAdmission(
    owner: Owner,
    binding: SessionBinding,
    source: string,
    signal: AbortSignal,
  ): Promise<SessionCompletion | undefined> {
    const custody = owner.custody!;
    const receipt = custody.result!;
    const credentialOwner = custody.credentialsOwner!;
    const current = owner.group.current;
    const old = current && this.#live(current) ? current : undefined;
    if (old && old.subject?.key !== owner.subject?.key) throw new BffSessionError('account_change');
    if (owner.group.publishing) throw new BffSessionError('transaction');
    if (
      old &&
      old.identity?.tenantId === receipt.tenantId &&
      old.identity.organizationId === receipt.organizationId
    ) {
      // A fresh own-result/acceptance already confirmed this exact actor and active Membership.
      // Reuse the established session without resetting its account, CSRF or lifetime.
      this.#bindProviderTarget(owner, old.credentials!);
      custody.credentialsOwner = old;
      custody.code = undefined;
      if (credentialOwner !== old && credentialOwner.phase === 'entry')
        this.#close(credentialOwner);
      return undefined;
    }
    this.#pendingCapacity(source);
    const now = this.#now();
    const phase = old ? 'switching' : 'selecting';
    const entry: Owner = {
      id: opaque(),
      admittedAt: now,
      group: owner.group,
      epoch: owner.epoch,
      source,
      phase,
      pending: true,
      deadline: old ? Math.min(owner.deadline, this.#effective(old)) : owner.deadline,
      controller: new AbortController(),
      work: new Set(),
      returnPath: '/',
      invitation: owner,
      replacement: old,
      absolute: old?.absolute ?? now + BFF_LIMITS.absoluteMs,
      activity: now,
      utcOffset: owner.utcOffset,
      csrf: opaque(),
      credentials: credentialOwner.credentials,
    };
    this.#reserveSubject(entry, entry.credentials!);
    entry.group.members.add(entry);
    this.#owners.set(entry.id, entry);
    entry.group.publishing = entry;
    try {
      return await this.#bounded(entry, signal, async (current) => {
        await this.#admission(entry, binding, source, receipt, current, phase);
        this.#assert(owner, 'invitation');
        if (old) this.#assert(old, 'active');
        const completion = await this.#publish(entry, old, phase, current);
        custody.credentialsOwner = entry;
        custody.code = undefined;
        return completion;
      });
    } finally {
      if (entry.phase !== 'active') this.#close(entry);
      if (entry.group.publishing === entry) entry.group.publishing = undefined;
    }
  }
  async invitation(
    binding: SessionBinding,
    source: string,
    command?: InvitationCommand,
    signal?: AbortSignal,
  ): Promise<InvitationContinuation> {
    this.#checkSource(binding.host, source);
    const owner = this.#findInvitation(binding);
    if (!command) return this.#invitationProjection(owner);
    this.#entryProof(owner, command.csrf);
    if (command.kind === 'cancel') {
      this.#close(owner);
      return Object.freeze({ csrfToken: '', state: 'expired' });
    }
    let custody = owner.custody!;
    if (command.kind === 'capture') {
      const match =
        /^([0-9a-f]{8}-[0-9a-f]{4}-7[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12})\.([A-Za-z0-9_-]{43})$/.exec(
          command.code,
        );
      if (!match || Buffer.from(match[2]!, 'base64url').toString('base64url') !== match[2])
        throw new BffInputError();
      let capturedOwner = owner;
      let opening: InvitationOpening | undefined;
      if (custody.code || owner.busy) {
        const credentialsOwner = custody.credentialsOwner;
        const deadline = owner.deadline;
        opening = this.openInvitation({
          host: binding.host,
          source,
          browser: binding.browser,
          sessionHandle: owner.group.current?.id,
          entryHandle: credentialsOwner?.phase === 'entry' ? credentialsOwner.id : undefined,
        });
        capturedOwner = this.#findInvitation({ ...binding, handle: opening.handle });
        capturedOwner.deadline = deadline;
        custody = capturedOwner.custody!;
      }
      custody.code = command.code;
      custody.inspection = undefined;
      custody.result = undefined;
      custody.acceptanceUncertain = false;
      custody.state = custody.credentialsOwner ? 'inspect' : 'signin';
      custody.revision = Object.freeze({});
      return Object.freeze({
        ...this.#invitationProjection(capturedOwner),
        ...(opening ? { opening } : {}),
      });
    }
    if (owner.busy) throw new BffSessionError('transaction');
    if (!custody.code) {
      if (custody.result) return this.#invitationProjection(owner);
      throw new BffSessionError('expired');
    }
    if (command.kind === 'verify') {
      if (custody.state !== 'forbidden') throw new BffSessionError('transaction');
      const login = await this.start({
        host: binding.host,
        source,
        browser: binding.browser,
        handle: owner.group.current?.id,
        returnPath: '/',
        invitation: owner.id,
        verification: true,
        signal,
      });
      return Object.freeze({ ...this.#invitationProjection(owner), login });
    }
    const credentialOwner = custody.credentialsOwner;
    if (!credentialOwner || !['entry', 'active'].includes(credentialOwner.phase)) {
      custody.state = 'signin';
      return this.#invitationProjection(owner);
    }
    const revision = custody.revision;
    const work = this.#bounded(owner, signal, async (current) => {
      try {
        await this.#tracked(credentialOwner, async () => {
          const credentialSignal = AbortSignal.any([current, credentialOwner.controller.signal]);
          if (credentialOwner.phase === 'active')
            await this.#ready(credentialOwner, binding, source, false, credentialSignal);
          else this.#assert(credentialOwner, 'entry');
          this.#assert(owner, 'invitation');
          const credentials = credentialOwner.credentials!;
          if (!credentials || credentials.accessExpiresAt <= this.#now())
            throw new BffSessionError('reauthenticate');
          if (
            owner.group.current &&
            this.#live(owner.group.current) &&
            owner.group.current.subject?.key !== owner.subject?.key
          )
            throw new BffSessionError('account_change');
          const nativeBinding = this.#binding(binding, source);
          const self = await this.#dependencies.native.self(
            credentials.accessToken,
            nativeBinding,
            credentialSignal,
          );
          this.#assertSignal(credentialSignal);
          this.#assert(owner, 'invitation');
          if (self.state === 'missing') {
            custody.state = 'bootstrap';
            return;
          }
          this.#dependencies.native.verifyAssociation(credentials.identity, self.association);
          const api = this.#dependencies.invitations;
          if (!api) throw new BffSessionError('configuration');
          const code = custody.code!;
          const input = { invitationId: code.slice(0, 36), code };
          if (command.kind === 'inspect') {
            const inspected = await api.inspect(
              credentials.accessToken,
              nativeBinding,
              input,
              credentialSignal,
            );
            this.#assertSignal(credentialSignal);
            this.#assert(owner, 'invitation');
            if (custody.revision !== revision) throw new BffSessionError('revoked');
            custody.inspection = inspected;
            custody.state = 'ready';
          } else {
            if (
              command.kind === 'accept' &&
              (custody.state !== 'ready' || custody.inspection?.version !== command.version)
            )
              throw new BffSessionError('transaction');
            if (command.kind === 'accept') {
              // Issuing the API write creates uncertainty even if this HTTP request is cancelled.
              custody.acceptanceUncertain = true;
              custody.inspection = undefined;
              custody.state = 'unknown';
            }
            const receipt =
              command.kind === 'accept'
                ? await api.accept(
                    credentials.accessToken,
                    nativeBinding,
                    { ...input, version: command.version },
                    credentialSignal,
                  )
                : await api.result(credentials.accessToken, nativeBinding, input, credentialSignal);
            this.#assertSignal(credentialSignal);
            this.#assert(owner, 'invitation');
            if (custody.revision !== revision) throw new BffSessionError('revoked');
            custody.result = receipt;
            custody.acceptanceUncertain = false;
            custody.inspection = undefined;
            custody.state = 'accepted';
          }
          this.#assertSignal(credentialSignal);
          this.#assert(owner, 'invitation');
          if (custody.revision !== revision || credentialOwner.credentials !== credentials)
            throw new BffSessionError('revoked');
        });
        if (custody.result && custody.state === 'accepted') {
          try {
            const completion = await this.#invitationAdmission(owner, binding, source, current);
            custody.revision = Object.freeze({});
            return Object.freeze({ ...this.#invitationProjection(owner), completion });
          } catch {
            this.#assert(owner, 'invitation'); /* committed receipt stays truthful */
          }
        }
      } catch (error) {
        this.#assert(owner, 'invitation');
        if (current.aborted) throw error;
        custody.inspection = undefined;
        // A missing own result is not evidence of expiry: an earlier accept may still commit.
        if (
          command.kind === 'result' &&
          error instanceof NativeInvitationError &&
          error.status === 404
        )
          custody.acceptanceUncertain = true;
        custody.state =
          error instanceof NativeInvitationError && error.status === 403
            ? 'forbidden'
            : error instanceof NativeInvitationError && error.status === 404
              ? command.kind === 'result' ||
                (command.kind === 'inspect' && custody.acceptanceUncertain)
                ? 'unknown'
                : 'expired'
              : error instanceof BffSessionError && error.reason === 'account_change'
                ? 'account_change'
                : error instanceof BffSessionError && error.reason === 'reauthenticate'
                  ? 'signin'
                  : command.kind === 'accept' ||
                      command.kind === 'result' ||
                      custody.acceptanceUncertain
                    ? 'unknown'
                    : 'unavailable';
      }
      custody.revision = Object.freeze({});
      return this.#invitationProjection(owner);
    });
    owner.busy = work;
    try {
      return await work;
    } finally {
      if (owner.busy === work) owner.busy = undefined;
    }
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
    this.#sweepBackchannel();
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
    const invitation =
      input.invitation === undefined
        ? undefined
        : this.#findInvitation({
            host: input.host,
            browser: input.browser ?? '',
            handle: input.invitation,
          });
    if (input.verification && (!invitation?.subject || !invitation.custody?.credentialsOwner))
      throw new BffSessionError('transaction');
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
      admittedAt: now,
      group,
      epoch: group.epoch,
      source: input.source,
      phase: 'starting',
      pending: true,
      deadline: Math.min(now + BFF_LIMITS.transactionMs, invitation?.deadline ?? Infinity),
      controller: new AbortController(),
      work: new Set(),
      returnPath: input.returnPath,
      contextHint: input.contextHint,
      invitation,
      verification: input.verification ? true : undefined,
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
        const callbackUri = 'https://' + input.host + '/api/auth/callback/keycloak';
        const start = await (input.verification
          ? this.#dependencies.provider.begin(callbackUri, entry.controller.signal, 'verify_email')
          : this.#dependencies.provider.begin(callbackUri, entry.controller.signal));
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
    // Keycloak's finite action result belongs only to this owned VERIFY_EMAIL transaction.
    // It is never evidence of verified email; the invitation still requires fresh inspection.
    const callback = new URL(input.responseUrl).searchParams;
    const actions = callback.getAll('kc_action');
    const actionStatuses = callback.getAll('kc_action_status');
    if (
      (actions.length || actionStatuses.length) &&
      (!entry.verification ||
        actions.length !== 1 ||
        actions[0] !== 'VERIFY_EMAIL' ||
        actionStatuses.length !== 1 ||
        !['success', 'cancelled', 'error'].includes(actionStatuses[0]!))
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
        if (entry.invitation) {
          const offer = entry.invitation;
          this.#assert(offer, 'invitation');
          const key = entry.subject!.key;
          const current = offer.group.current;
          if (
            (offer.subject && offer.subject.key !== key) ||
            (current && this.#live(current) && current.subject?.key !== key)
          ) {
            offer.custody!.state = 'account_change';
            throw new BffSessionError('account_change');
          }
          if (!offer.subject) this.#reserveSubject(offer, credentials);
          else this.#bindProviderTarget(offer, credentials);
          const previousCredentials = offer.custody!.credentialsOwner;
          offer.custody!.credentialsOwner = entry;
          if (previousCredentials?.phase === 'entry') this.#close(previousCredentials);
          offer.custody!.inspection = undefined;
          offer.custody!.state = 'inspect';
          offer.custody!.revision = Object.freeze({});
        }
        entry.cursorKey = randomBytes(32);
        entry.credentials = credentials;
        entry.transaction = undefined;
        entry.verification = undefined;
        entry.proof = undefined;
        entry.phase = 'entry';
        return Object.freeze({
          kind: 'entry',
          ...(entry.invitation ? { invitation: entry.invitation.id } : {}),
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
      contexts:
        contexts === null
          ? null
          : Object.freeze({
              ...contexts,
              endCursor:
                contexts.endCursor === null
                  ? null
                  : sealEntryCursor(
                      entry.cursorKey!,
                      this.#cursorBinding(entry),
                      contexts.endCursor,
                    ),
            }),
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
    const rawCursor =
      cursor === undefined
        ? undefined
        : openEntryCursor(owner.cursorKey!, this.#cursorBinding(owner), cursor);
    return this.#bounded(owner, signal, (current) =>
      this.#entryRead(owner, binding, source, rawCursor, current),
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
      // The existing global bootstrap finishes before the invitation's separate tenant operation.
      if (owner.invitation) {
        this.#assert(owner.invitation, 'invitation');
        owner.invitation.custody!.state = 'inspect';
        return this.#entryProjection(owner, 'active');
      }
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
    if (entry.invitation) this.#close(entry.invitation);
    this.#abortPages(entry);
    this.#clearPageCursorKey(entry);
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
      this.#clearCursorKey(entry);
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
    for (const competing of entry.group.members) {
      if (competing === entry.invitation) competing.epoch = epoch;
      else if (competing !== entry && competing !== old && competing.pending)
        this.#close(competing);
    }
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
    entry.credentials = Object.freeze({
      ...entry.credentials!,
      identity: Object.freeze({ ...entry.credentials!.identity, displayLabel: null }),
    });
    this.#clearCursorKey(entry);
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
    const rawCursor =
      cursor === undefined
        ? undefined
        : openEntryCursor(entry.cursorKey!, this.#cursorBinding(entry), cursor);
    if (entry.busy || entry.group.publishing) throw new BffSessionError('transaction');
    if (!entry.credentials!.identity.displayLabel) throw new BffSessionError('reauthenticate');
    const old = entry.replacement && this.#live(entry.replacement) ? entry.replacement : undefined;
    if (old && old.subject?.key !== entry.subject?.key) throw new BffSessionError('account_change');
    entry.phase = 'selecting';
    entry.group.publishing = entry;
    this.#abortPages(entry, false);
    const oldPages = [...(entry.pageLeases ?? [])].map((lease) => lease.cleaned);
    try {
      return await this.#bounded(entry, signal, async (current) => {
        await Promise.allSettled(oldPages);
        this.#assertSignal(current);
        const selected = await this.#chosen(entry, binding, source, scope, rawCursor, current);
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
    const rawCursor = this.#openPageCursor(old, 'scopes', cursor);
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
      admittedAt: now,
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
        { kind: 'getOwnContexts', limit: 20, ...(rawCursor ? { cursor: rawCursor } : {}) },
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
      'listInvitations',
      'getInvitation',
      'listInvitableRoles',
      'getInvitationPolicy',
      'listOrganizations',
      'getOrganization',
      'listCustomFieldDefinitions',
      'getCustomFieldDefinition',
      'listCustomFieldRevisions',
      'getMembershipCustomFields',
      'getOwnCustomFields',
      'getOwnFieldConsent',
      'listOwnConsentScopes',
      'listOwnConsentHistory',
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
        throw new BffSessionError('response');
      // Operation refusals withhold their bytes, never reinterpret 401/403/404 as session revocation.
      if (result.kind === 'success') entry.activity = this.#now();
      return result;
    });
  }

  retainConsent(binding: SessionBinding, lease: NativeConsentResponseLease): string {
    const owner = this.#findEntry(binding);
    lease.pin(
      () => {
        this.#assert(owner, 'entry');
        if (lease.signal.aborted) throw new BffSessionError('revoked');
      },
      owner,
      Math.max(1, Math.min(owner.deadline, owner.credentials!.refreshExpiresAt) - this.#now()),
    );
    void this.#tracked(owner, () => lease.cleaned);
    return owner.csrf;
  }
  async retainedConsent(
    binding: SessionBinding,
    source: string,
    operation: RetainedConsentOperation,
    csrf: string,
    signal?: AbortSignal,
  ): Promise<NativeOperationResult> {
    this.#checkSource(binding.host, source);
    const owner = this.#findEntry(binding);
    this.#entryProof(owner, csrf);
    if (!this.#dependencies.retainedConsent) throw new BffSessionError('configuration');
    let captured: RetainedConsentOperation;
    try {
      const raw = JSON.stringify(operation);
      if (Buffer.byteLength(raw) > 16 * 1024) throw new Error();
      captured = JSON.parse(raw) as RetainedConsentOperation;
      if (
        !captured ||
        !['listOwnConsentScopes', 'listOwnConsentHistory', 'withdrawOwnFieldConsent'].includes(
          captured.kind,
        )
      )
        throw new Error();
    } catch {
      throw new BffSessionError('configuration');
    }
    return this.#bounded(owner, signal, async (current) => {
      this.#assert(owner, 'entry');
      if (owner.credentials!.accessExpiresAt - this.#now() <= BFF_LIMITS.refreshAheadMs) {
        if (!owner.refresh) {
          const previous = owner.credentials!;
          const work = this.#tracked(owner, async () => {
            const next = this.#bundle(
              await this.#dependencies.provider.refresh(
                previous.refreshToken!,
                previous.identity,
                current,
              ),
              previous,
            );
            this.#assertSignal(current);
            this.#assert(owner, 'entry');
            if (owner.credentials !== previous) throw new BffSessionError('transaction');
            owner.credentials = next;
          });
          owner.refresh = work;
          void work
            .finally(() => {
              if (owner.refresh === work) owner.refresh = undefined;
            })
            .catch(() => undefined);
        }
        await this.#wait(owner.refresh, current);
      }
      const credentials = owner.credentials!;
      const result = await this.#dependencies.retainedConsent!(
        credentials.accessToken,
        this.#binding(binding, source),
        captured,
        current,
      );
      this.#assertSignal(current);
      this.#assert(owner, 'entry');
      if (owner.credentials !== credentials) throw new BffSessionError('response');
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
    track?: (work: Promise<void>) => void,
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
      let failure: SessionFailure | undefined;
      const work = this.#bounded(entry, undefined, async (current) => {
        try {
          this.#assert(entry, 'active');
          const next = this.#bundle(
            await this.#dependencies.provider.refresh(
              previous.refreshToken!,
              previous.identity,
              current,
            ),
            previous,
          );
          this.#assert(entry, 'active');
          this.#assertSignal(current);
          const self = await this.#dependencies.native.self(
            next.accessToken,
            this.#binding(binding, source),
            current,
          );
          this.#assert(entry, 'active');
          this.#assertSignal(current);
          if (self.state !== 'active') throw new BffSessionError('identity');
          this.#dependencies.native.verifyAssociation(next.identity, self.association);
          if (self.association.actorId !== entry.identity!.actorId)
            throw new BffSessionError('identity');
          const witnessed = await this.#dependencies.account(
            next.accessToken,
            this.#scoped(binding, source, entry.identity!),
            current,
          );
          this.#assert(entry, 'active');
          this.#assertSignal(current);
          this.#account(entry, witnessed, entry.identity!);
          entry.credentials = next;
          entry.pageRevision = Object.freeze({});
          this.#abortPages(entry, true);
          entry.activity = this.#now();
          this.#assert(entry, 'active');
        } catch (error) {
          failure = this.#reason(error);
          throw error;
        }
      }).catch((error) => {
        // Classify before owner closure can turn a specific failure into cancellation.
        const bounded = this.#reason(error);
        const reason =
          failure === 'revoked' || failure === 'stopped'
            ? failure
            : bounded === 'deadline'
              ? bounded
              : (failure ?? bounded);
        if (entry.phase !== 'logout') this.#close(entry);
        this.#diagnostic(reason);
        throw new BffSessionError(reason);
      });
      entry.refresh = work;
      const settled = () => {
        if (entry.refresh === work) entry.refresh = undefined;
      };
      void work.then(settled, settled);
    }
    if (entry.refresh) {
      track?.(entry.refresh);
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
    let failure: SessionFailure | undefined;
    const work = this.#bounded(entry, undefined, async (current) => {
      try {
        this.#assert(entry, 'active');
        const account = await this.#dependencies.account(
          credentials.accessToken,
          this.#scoped(binding, source, entry.identity!),
          current,
        );
        this.#assert(entry, 'active');
        this.#assertSignal(current);
        if (entry.credentials !== credentials) throw new BffSessionError('transaction');
        this.#account(entry, account, entry.identity!);
      } catch (error) {
        failure = this.#reason(error);
        throw error;
      }
    }).catch((error) => {
      const bounded = this.#reason(error);
      const reason =
        failure === 'revoked' || failure === 'stopped'
          ? failure
          : bounded === 'deadline'
            ? bounded
            : (failure ?? bounded);
      // An ordinary read consumes no provider grant. Only proven identity refusal
      // revokes it; waiter cancellation never cancels the independently owned work.
      if (reason === 'identity' && entry.phase !== 'logout') this.#close(entry);
      this.#diagnostic(reason);
      throw new BffSessionError(reason);
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
    this.#abortPages(entry);
    this.#clearPageCursorKey(entry);
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
      this.#clearCursorKey(entry);
      entry.credentials = undefined;
      entry.account = undefined;
      entry.identity = undefined;
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
    this.#backchannel.stop();
    clearInterval(this.#timer);
    const entries = [...this.#owners.values()];
    const work = entries.flatMap((entry) => [...entry.work]);
    for (const entry of entries) {
      this.#abortPages(entry, false);
      this.#close(entry);
    }
    void Promise.allSettled(work).then(() => {
      for (const entry of entries) this.#release(entry);
      this.#sweepBackchannel();
      completed();
    });
    return this.#stopping;
  }

  #sweepBackchannel() {
    // Only fences eligible for retirement need owner evidence; snapshot once per sweep.
    let retained:
      | { earliestUnknown: number; sessions: Set<string>; identities: Set<string> }
      | undefined;
    this.#backchannel.sweep((fence) => {
      if (!retained) {
        retained = { earliestUnknown: Infinity, sessions: new Set(), identities: new Set() };
        for (const entry of this.#owners.values()) {
          const target = entry.providerTarget;
          if (!target)
            retained.earliestUnknown = Math.min(retained.earliestUnknown, entry.admittedAt);
          else {
            retained.sessions.add(JSON.stringify([target.issuer, target.sid]));
            retained.identities.add(JSON.stringify([target.issuer, target.sid, target.subject]));
          }
        }
      }
      return (
        retained.earliestUnknown <= fence.horizon ||
        (fence.subject === null
          ? retained.sessions.has(JSON.stringify([fence.issuer, fence.sid]))
          : retained.identities.has(JSON.stringify([fence.issuer, fence.sid, fence.subject])))
      );
    });
  }
  /** Native installation capability; deliberately absent from BffSessionStore. */
  applyProviderLogout(token: VerifiedLogoutToken): 'accepted' | 'duplicate' {
    this.#sweepBackchannel();
    return this.#backchannel.apply(token, () => {
      for (const owner of [...this.#owners.values()])
        if (owner.providerTarget && matchesProviderLogout(owner.providerTarget, token)) {
          for (const linked of [...this.#owners.values()])
            if (linked !== owner && linked.custody?.credentialsOwner === owner) this.#close(linked);
          this.#close(owner);
        }
    });
  }
  backchannelCounts() {
    return this.#backchannel.counts();
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
      identityRecords: [...this.#owners.values()].filter((entry) => entry.identity).length,
      pageCursorKeyRecords: [...this.#owners.values()].filter((entry) => entry.pageCursorKey)
        .length,
      pageSnapshots: [...this.#owners.values()].reduce(
        (count, entry) => count + (entry.pageLeases?.size ?? 0),
        0,
      ),
      cursorKeyRecords: [...this.#owners.values()].filter((entry) => entry.cursorKey).length,
      displayLabelRecords: [...this.#owners.values()].filter(
        (entry) => typeof entry.credentials?.identity.displayLabel === 'string',
      ).length,
      transactionRecords: [...this.#owners.values()].filter((entry) => entry.transaction).length,
      proofRecords: [...this.#owners.values()].filter((entry) => entry.proof || entry.logoutProof)
        .length,
      csrfRecords: [...this.#owners.values()].filter((entry) => entry.csrf).length,
      loginStates: this.#states.size,
      logoutStates: this.#logoutStates.size,
    });
  }
}
