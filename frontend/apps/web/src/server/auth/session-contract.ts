import type { LoginTransaction, ProviderCredentials, ProviderIdentity } from './provider.js';

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

export type SessionBinding = Readonly<{ host: string; browser: string; handle: string }>;
export type AccountWitness = Readonly<{
  actorId: string;
  tenantId: string;
  organizationId: string | null;
  displayName: string;
}>;
export type AccountBinding = Readonly<{ host: string; source: string }>;
export type SessionActivity = 'passive' | 'read' | 'extend';
export type SessionProjection = Readonly<{
  state: 'authenticated';
  displayName: string;
  expiresAt: number;
  csrfToken: string;
}>;
export type LoginStart = Readonly<{
  state: string;
  proof: string;
  browser: string;
  redirectUrl: string;
}>;
export type LoginCompletion = Readonly<{
  handle: string;
  browser: string;
  returnPath: '/' | '/studio' | '/portal';
  session: SessionProjection;
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
  | 'provider_refused';

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
    binding: AccountBinding,
    signal: AbortSignal,
  ) => Promise<AccountWitness>;
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
      returnPath: '/' | '/studio' | '/portal';
    }>,
  ): Promise<LoginStart>;
  complete(
    input: Readonly<{
      host: string;
      source: string;
      browser: string;
      state: string;
      proof: string;
      responseUrl: string;
    }>,
  ): Promise<LoginCompletion>;
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
