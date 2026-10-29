import type {
  NativeBinding,
  NativeScope,
  NativeSessionApi,
  SelfContexts,
} from './native-contract.js';
import type {
  NativeAdministrationCaller,
  NativeOperation,
  NativeOperationResult,
} from './native-operation-contract.js';
import type { LoginTransaction, ProviderCredentials, ProviderIdentity } from './provider.js';
import type { SessionProjection } from './session-projection.js';
export type { SessionProjection } from './session-projection.js';

/** Native-only capability. Application/UI callers receive projections, never this port. */
export type SessionProvider = {
  begin: (
    uri: string,
    signal?: AbortSignal,
  ) => Promise<Readonly<{ transaction: LoginTransaction; redirectUrl: string }>>;
  redeem: (
    transaction: LoginTransaction,
    uri: string,
    signal?: AbortSignal,
  ) => Promise<ProviderCredentials>;
  refresh: (
    token: string,
    identity: ProviderIdentity,
    signal?: AbortSignal,
  ) => Promise<ProviderCredentials>;
  logoutUrl: (idToken: string, returnUri: string, state: string) => string;
};

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
  handle: string;
  browser: string;
  entry: EntryProjection;
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
  | 'csrf';

export class BffSessionError extends Error {
  constructor(
    readonly reason: SessionFailure,
    readonly clearTransaction: string | null = null,
  ) {
    super('BFF session: ' + reason);
    this.name = 'BffSessionError';
  }
}

export type SessionDependencies = Readonly<{
  provider: SessionProvider;
  account: (
    accessToken: string,
    binding: AccountBinding & NativeScope,
    signal: AbortSignal,
  ) => Promise<AccountWitness>;
  native: NativeSessionApi;
  administration: NativeAdministrationCaller;
  diagnostic?: (reason: SessionFailure) => void;
  /** Internal deterministic test clock, never deployment configuration. */
  clock?: Readonly<{ monotonic: () => number; utc: () => number }>;
}>;

/** Phase 11 replaces this native default before multiple web processes serve a session. */
export interface BffSessionStore {
  start(
    input: Readonly<{
      host: string;
      source: string;
      browser?: string;
      handle?: string;
      signal?: AbortSignal;
      returnPath: NativeReturnPath;
      contextHint?: string;
    }>,
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
