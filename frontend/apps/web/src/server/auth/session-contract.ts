import type {
  NativeConsentResponseLease,
  NativeDataRequestResponseLease,
} from './consent-response-contract.js';
import type {
  InvitationCommand,
  InvitationContinuation,
  InvitationOpening,
  InvitationResponseLease,
} from './invitation-session.js';
import type {
  NativeBinding,
  NativeScope,
  NativeSessionApi,
  SelfContexts,
} from './native-contract.js';
import type { NativeInvitationApi } from './native-invitation-contract.js';
import type {
  NativeAdministrationCaller,
  NativeRetainedConsentCaller,
  NativeRetainedDataRequestCaller,
  OwnDataRequestOperation,
  RetainedConsentOperation,
  RetainedDataRequestOperation,
  NativeOperation,
  NativeOperationResult,
} from './native-operation-contract.js';
import type { NativeActionPage, NativeEntrySnapshot, NativePageSnapshot } from './page-contract.js';
import type { NativeSnapshotTicket } from './page-snapshot-contract.js';
import type {
  LoginTransaction,
  ProviderCredentials,
  ProviderIdentity,
  ProviderPurpose,
} from './provider.js';
import type { SessionProjection } from './session-projection.js';
export type { SessionProjection } from './session-projection.js';

/** Native-only capability. Application/UI callers receive projections, never this port. */
export type SessionProvider = {
  begin: (
    uri: string,
    signal?: AbortSignal,
    purpose?: ProviderPurpose,
  ) => Promise<Readonly<{ transaction: LoginTransaction; redirectUrl: string }>>;
  /** `testRecord` is true only for the federation_test owner (ADR-0099 Amendment 7). */
  redeem: (
    transaction: LoginTransaction,
    uri: string,
    signal?: AbortSignal,
    testRecord?: boolean,
  ) => Promise<ProviderCredentials>;
  refresh: (
    token: string,
    identity: ProviderIdentity,
    signal?: AbortSignal,
  ) => Promise<ProviderCredentials>;
  logoutUrl: (idToken: string, returnUri: string, state: string) => string;
  /** Server-side end of the provider session a refresh token belongs to (ADR-0099 § Host binding). */
  endSession: (refreshToken: string, signal?: AbortSignal) => Promise<void>;
};

/** ADR-0099 § Sign-in exposure: the host tenant's Active, entitled federation option, read server-side. */
export type FederationSignInOption = Readonly<{ alias: string; displayName: string }>;
/** The closed test result the BFF reports; never a provider message. */
export type FederationTestOutcome = 'passed' | 'failed' | 'unrecorded';
/**
 * ADR-0099 Amendment 6 (S-B): the one BFF-only federation test owner. `start` begins a test window with the
 * administrator's session token and returns the attempt identifier to the BFF only; `record` reports the closed result
 * with the test-purpose access token. Neither is a browser-invocable operation.
 */
export type NativeFederationTestApi = Readonly<{
  start(
    token: string,
    binding: AccountBinding & NativeScope,
    signal: AbortSignal,
  ): Promise<Readonly<{ alias: string; attemptId: string; version: string; connectionId: string }>>;
  record(
    token: string,
    binding: AccountBinding & NativeScope,
    input: Readonly<{ version: string; attemptId: string; passed: boolean }>,
    signal: AbortSignal,
  ): Promise<Readonly<{ outcome: string }>>;
}>;

export type SessionBinding = Readonly<{
  host: string;
  browser: string;
  handle: string;
  traceparent?: string;
}>;
export type AccountWitness = Readonly<{
  actorId: string;
  tenantId: string;
  organizationId: string | null;
  displayName: string;
}>;
export type AccountBinding = NativeBinding;
export type PublishedSessionIdentity = Readonly<NativeScope & { actorId: string }>;
export type NativeReturnPath =
  | '/'
  | '/studio'
  | '/studio/members'
  | `/studio/members/${string}`
  | '/studio/invitations'
  | `/studio/invitations/${string}`
  | '/studio/invitation-policy'
  | '/studio/federation'
  | '/studio/data-requests'
  | `/studio/data-requests/${string}`
  | '/studio/custom-fields'
  | `/studio/custom-fields/${string}`
  | '/studio/roles'
  | `/studio/roles/${string}`
  | '/portal'
  | '/portal/profile';
export type EntryProjection = Readonly<{
  state: 'entry';
  displayLabel: string | null;
  csrfToken: string;
  expiresAt: number;
  identityState: 'unavailable' | 'missing' | 'active';
  contexts: SelfContexts | null;
  contextHint: string | null;
}>;
export type SessionActivity = 'passive' | 'read' | 'extend';
export type PagePreparation =
  | Readonly<{ kind: 'document'; ticket: NativeSnapshotTicket }>
  | Readonly<{ kind: 'entry' | 'scopes'; ticket: NativeSnapshotTicket; cursor?: string }>
  | Readonly<{ kind: 'create'; ticket: NativeSnapshotTicket; csrf: string; signal?: AbortSignal }>
  | Readonly<{ kind: 'response'; ticket: NativeSnapshotTicket; page: NativeActionPage }>;
export type LoginStart = Readonly<{
  state: string;
  proof: string;
  browser: string;
  redirectUrl: string;
}>;
export type SessionCompletion = Readonly<{
  kind: 'session';
  handle: string;
  browser: string;
  returnPath: NativeReturnPath;
  session: SessionProjection;
}>;
export type LoginCompletion = Readonly<{
  kind: 'entry';
  invitation?: string;
  handle: string;
  browser: string;
  entry: EntryProjection;
}>;
/** The federation test callback: nothing is published; the administrator is signed out. */
export type FederationTestCompletion = Readonly<{
  kind: 'federation_test';
  outcome: FederationTestOutcome;
  browser: string;
  logout: LogoutStart;
}>;
export type SelectionCompletion =
  | SessionCompletion
  | Readonly<{
      kind: 'destination';
      origin: string;
      returnPath: NativeReturnPath;
      contextHint: string;
    }>;
export type LogoutStart = Readonly<{ state: string; proof: string; redirectUrl: string | null }>;
export type SessionFailure =
  | 'source_capacity'
  | 'pending_capacity'
  | 'global_capacity'
  | 'subject_capacity'
  | 'transaction'
  | 'expired'
  | 'revoked'
  | 'stopped'
  | 'cancelled'
  | 'identity'
  | 'configuration'
  | 'deadline'
  | 'transport'
  | 'response'
  | 'invalid_grant'
  | 'provider_refused'
  | 'account_change'
  | 'reauthenticate'
  | 'csrf'
  /** ADR-0099 § Host binding: a definitive mismatch; the BFF and provider sessions end. */
  | 'host_binding'
  /** ADR-0099 § Host binding: the self read could not decide; nothing brokered is published. */
  | 'host_unavailable';

export class BffSessionError extends Error {
  constructor(
    readonly reason: SessionFailure,
    readonly clearTransaction: string | null = null,
  ) {
    super('BFF session: ' + reason);
    this.name = 'BffSessionError';
  }
}

/** The pinned owner's proof and, for a live session, whether its group is tenant-wide; never an identifier. */
export type DataRequestRetention = Readonly<{
  csrfToken: string;
  scope: 'tenant' | 'organization' | null;
}>;
export type SessionDependencies = Readonly<{
  provider: SessionProvider;
  account: (
    accessToken: string,
    binding: AccountBinding & NativeScope,
    signal: AbortSignal,
  ) => Promise<AccountWitness>;
  native: NativeSessionApi;
  administration: NativeAdministrationCaller;
  invitations?: NativeInvitationApi;
  retainedConsent?: NativeRetainedConsentCaller;
  /** ADR-0106 § 9: the retained data-request entry's own caller; absent means that entry is unavailable. */
  retainedDataRequest?: NativeRetainedDataRequestCaller;
  /** ADR-0099: the server-side sign-in option read and the BFF-only test owner; absent means federation is off. */
  publicSignIn?: (
    binding: NativeBinding,
    signal: AbortSignal,
  ) => Promise<FederationSignInOption | null>;
  /** The tenant realm issuer, for the IdP values the federation page shows; never a request value. */
  federationIssuer?: string;
  federationTest?: NativeFederationTestApi;
  diagnostic?: (reason: SessionFailure) => void;
  /** Internal deterministic test clock, never deployment configuration. */
  clock?: Readonly<{ monotonic: () => number; utc: () => number }>;
}>;

/** Phase 11 replaces this native default before multiple web processes serve a session. */
export interface BffSessionStore {
  openInvitation(
    input: Readonly<{
      host: string;
      source: string;
      browser?: string;
      handle?: string;
      sessionHandle?: string;
      entryHandle?: string;
    }>,
  ): InvitationOpening;
  invitation(
    binding: SessionBinding,
    source: string,
    command?: InvitationCommand,
    signal?: AbortSignal,
  ): Promise<InvitationContinuation>;
  retainInvitation(binding: SessionBinding, lease: InvitationResponseLease): void;
  invitationEntry(binding: SessionBinding, entry: SessionBinding): boolean;
  start(
    input: Readonly<{
      host: string;
      source: string;
      browser?: string;
      handle?: string;
      signal?: AbortSignal;
      returnPath: NativeReturnPath;
      contextHint?: string;
      invitation?: string;
      verification?: boolean;
      /** Start at the host tenant's Active broker; the alias is read server-side, never taken from the browser. */
      federation?: boolean;
    }>,
  ): Promise<LoginStart>;
  /** ADR-0099 § Sign-in exposure: the option the sign-in page may render, or null on any missing or failed read. */
  signInOption(
    host: string,
    source: string,
    signal?: AbortSignal,
  ): Promise<FederationSignInOption | null>;
  /** ADR-0099 § Lifecycle and test: begin the administrator's federation_test authorization. */
  startFederationTest(
    binding: SessionBinding,
    source: string,
    csrf: string,
    signal?: AbortSignal,
  ): Promise<LoginStart>;
  complete(
    input: Readonly<{
      host: string;
      source: string;
      traceparent?: string;
      browser: string;
      state: string;
      proof: string;
      responseUrl: string;
      signal?: AbortSignal;
    }>,
  ): Promise<LoginCompletion>;
  /**
   * The provider callback: an ordinary or verification owner completes as {@link complete}; only the owner that
   * {@link startFederationTest} created completes as the test. The two purposes never cross.
   */
  completeCallback(
    input: Parameters<BffSessionStore['complete']>[0],
  ): Promise<LoginCompletion | FederationTestCompletion>;
  entry(
    binding: SessionBinding,
    source: string,
    cursor?: string,
    signal?: AbortSignal,
  ): Promise<EntryProjection>;
  create(
    binding: SessionBinding,
    source: string,
    csrf: string,
    signal?: AbortSignal,
  ): Promise<EntryProjection>;
  select(
    binding: SessionBinding,
    source: string,
    csrf: string,
    scope: NativeScope,
    cursor?: string,
    signal?: AbortSignal,
  ): Promise<SelectionCompletion>;
  switchScope(
    binding: SessionBinding,
    source: string,
    csrf: string,
    scope: NativeScope,
    cursor?: string,
    signal?: AbortSignal,
  ): Promise<SelectionCompletion>;
  discardEntry(binding: SessionBinding, csrf: string): LogoutStart;
  operate(
    binding: SessionBinding,
    source: string,
    operation: NativeOperation,
    csrf?: string,
    signal?: AbortSignal,
  ): Promise<NativeOperationResult>;
  preparePage(
    binding: SessionBinding,
    source: string,
    input: PagePreparation,
  ): Promise<NativePageSnapshot | NativeEntrySnapshot>;
  retainConsent(binding: SessionBinding, lease: NativeConsentResponseLease): string;
  retainedConsent(
    binding: SessionBinding,
    source: string,
    operation: RetainedConsentOperation,
    csrf: string,
    signal?: AbortSignal,
  ): Promise<NativeOperationResult>;
  retainDataRequests(
    binding: SessionBinding,
    lease: NativeDataRequestResponseLease,
    mode: 'session' | 'entry',
  ): DataRequestRetention;
  ownDataRequest(
    binding: SessionBinding,
    source: string,
    operation: OwnDataRequestOperation,
    csrf: string,
    signal?: AbortSignal,
  ): Promise<NativeOperationResult>;
  retainedDataRequest(
    binding: SessionBinding,
    source: string,
    operation: RetainedDataRequestOperation,
    csrf: string,
    signal?: AbortSignal,
  ): Promise<NativeOperationResult>;
  peek(binding: SessionBinding): SessionProjection | null;
  admit(
    binding: SessionBinding,
    source: string,
    activity: SessionActivity,
    signal?: AbortSignal,
  ): Promise<SessionProjection>;
  signout(binding: SessionBinding, csrf: string): LogoutStart;
  consumeLogout(
    input: Readonly<{ host: string; browser: string; state: string; proof: string }>,
  ): boolean;
  stop(): Promise<void>;
}
