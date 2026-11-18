import * as oidc from 'openid-client';

import { FEDERATION_ALIAS, FEDERATION_TEST_SCOPE } from './federation-alias.js';
import { BffProviderError } from './provider-error.js';
import { providerOperation } from './provider-transport.js';

export type ProviderIdentity = Readonly<{
  issuer: string;
  subject: string;
  sid: string;
  actorId: string | null;
  tenantId: string | null;
  organizationId: string | null;
  displayLabel: string | null;
  /**
   * ADR-0099 § Host binding: the ID token's `idp` (the brokering alias) on a brokered authentication, null on a local
   * one. Provenance only; the session owner compares it with the host tenant's Active alias before any publication.
   */
  idp: string | null;
  /**
   * ADR-0099 Amendment 7: the ID token's `auth_time` (seconds since the epoch) when present. The federation_test
   * callback requires it at or after the test window's start; nothing else reads it.
   */
  authTime?: number;
}>;

export { FEDERATION_ALIAS, FEDERATION_TEST_SCOPE };
/**
 * The closed authorization purposes (ADR-0070 Amendments 11 and 12): `verify_email`, `federation_signin` and
 * `federation_test`. `federation_signin` is an ordinary sign-in that starts at the host tenant's Active broker (the
 * alias as `kc_idp_hint`, no forced login, the ordinary publication rules); `federation_test` is the administrator's
 * test (`prompt=login`, `max_age=0` and the test-record scope), whose callback publishes nothing. Every alias comes
 * from a server-side read, never from the browser.
 */
export type ProviderPurpose =
  | 'verify_email'
  | Readonly<{ kind: 'federation_signin'; alias: string }>
  | Readonly<{ kind: 'federation_test'; alias: string }>;

/** Server-only grant result; never a session/client/RSC projection. */
export type ProviderCredentials = Readonly<{
  accessToken: string;
  refreshToken?: string;
  idToken: string;
  identity: ProviderIdentity;
  accessExpiresAt: number;
  refreshExpiresAt: number;
}>;

export type LoginTransaction = Readonly<{
  callbackUri: string;
  state: string;
  nonce: string;
  verifier: string;
}>;

type ProviderInput = Readonly<{
  issuer: string;
  clientSecret: string;
  allowLoopbackHttp?: boolean;
}>;
type Operation = ReturnType<typeof providerOperation>;
const uuid = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const allZero = '00000000-0000-0000-0000-000000000000';

function optionalId(value: unknown): string | null {
  if (value === undefined) return null;
  if (typeof value !== 'string' || !uuid.test(value) || value === allZero)
    throw new BffProviderError('identity');
  return value.toLowerCase();
}

function provenance(value: unknown): string | null {
  if (value === undefined) return null;
  if (
    typeof value !== 'string' ||
    !value ||
    value.length > 255 ||
    // eslint-disable-next-line no-control-regex -- Reject control bytes in an opaque provider alias.
    /[\u0000-\u001f\u007f-\u009f\ud800-\udfff]/u.test(value)
  )
    throw new BffProviderError('identity');
  return value;
}

function authTime(value: unknown): number | undefined {
  if (value === undefined) return undefined;
  if (typeof value !== 'number' || !Number.isSafeInteger(value) || value <= 0)
    throw new BffProviderError('identity');
  return value;
}

function displayLabel(claims: oidc.IDToken): string | null {
  const value = claims.name ?? claims.preferred_username;
  return typeof value === 'string' &&
    value.length > 0 &&
    value.length <= 128 &&
    !/^[\s\u0085]+$/u.test(value) &&
    !/[\0\ud800-\udfff]/u.test(value)
    ? value
    : null;
}

function identity(claims: oidc.IDToken | undefined, issuer: string): ProviderIdentity {
  if (
    !claims ||
    claims.iss !== issuer ||
    typeof claims.sid !== 'string' ||
    !claims.sid ||
    Buffer.byteLength(claims.sid, 'utf8') > 255 ||
    // eslint-disable-next-line no-control-regex -- Reject control bytes in opaque provider identifiers.
    /[\u0000-\u001f\u007f-\u009f\ud800-\udfff]/u.test(claims.sid) ||
    typeof claims.sub !== 'string' ||
    !claims.sub ||
    claims.sub.length > 255 ||
    /^[\t\n\v\f\r \u0085\u00a0\u1680\u2000-\u200a\u2028\u2029\u202f\u205f\u3000]+$/u.test(
      claims.sub,
    ) ||
    /[\0\r\n\ud800-\udfff]/u.test(claims.sub)
  )
    throw new BffProviderError('identity');
  const actorId = optionalId(claims.user_id);
  const tenantId = optionalId(claims.tenant_id);
  const organizationId = optionalId(claims.organization_id);
  if (organizationId !== null && tenantId === null) throw new BffProviderError('identity');
  const authenticated = authTime(claims.auth_time);
  return Object.freeze({
    issuer,
    subject: claims.sub,
    sid: claims.sid as string,
    actorId,
    tenantId,
    organizationId,
    displayLabel: displayLabel(claims),
    idp: provenance(claims.idp),
    ...(authenticated === undefined ? {} : { authTime: authenticated }),
  });
}

function browserReturn(value: string, path: string): URL {
  let url: URL;
  try {
    url = new URL(value);
  } catch {
    throw new BffProviderError('configuration');
  }
  if (
    url.protocol !== 'https:' ||
    url.pathname !== path ||
    url.search ||
    url.hash ||
    url.username ||
    url.password
  )
    throw new BffProviderError('configuration');
  return url;
}

/** Inactive adapter: composition supplies verified configuration, never a request URL. */
export function createTenantProvider(input: ProviderInput, transport: typeof fetch) {
  let issuer: URL;
  try {
    issuer = new URL(input.issuer);
  } catch {
    throw new BffProviderError('configuration');
  }
  const insecure =
    issuer.protocol === 'http:' &&
    input.allowLoopbackHttp === true &&
    ['localhost', '127.0.0.1', '[::1]'].includes(issuer.hostname);
  if (
    (!insecure && issuer.protocol !== 'https:') ||
    issuer.pathname !== '/realms/learnstack' ||
    issuer.href !== input.issuer ||
    issuer.search ||
    issuer.hash ||
    issuer.username ||
    issuer.password ||
    typeof input.clientSecret !== 'string' ||
    !input.clientSecret ||
    /[\r\n]/.test(input.clientSecret)
  )
    throw new BffProviderError('configuration');
  const secret = input.clientSecret;
  const expectedIssuer = issuer.href;
  let metadata: oidc.ServerMetadata | undefined;
  const expected = {
    authorization_endpoint: `${expectedIssuer}/protocol/openid-connect/auth`,
    token_endpoint: `${expectedIssuer}/protocol/openid-connect/token`,
    jwks_uri: `${expectedIssuer}/protocol/openid-connect/certs`,
    end_session_endpoint: `${expectedIssuer}/protocol/openid-connect/logout`,
  };

  async function configuration(operation: Operation) {
    if (!metadata) {
      const response = await operation.fetch(`${expectedIssuer}/.well-known/openid-configuration`);
      if (response.status !== 200) throw new BffProviderError('provider_refused');
      const document = (await response.json()) as oidc.ServerMetadata;
      if (
        !document ||
        document.issuer !== expectedIssuer ||
        Object.entries(expected).some(([key, value]) => document[key] !== value)
      )
        throw new BffProviderError('configuration');
      operation.interrupted();
      metadata = Object.freeze({ ...document });
    }
    const config = new oidc.Configuration(
      metadata,
      'learnstack-web',
      {
        id_token_signed_response_alg: 'RS256',
      },
      oidc.ClientSecretPost(secret),
    );
    config[oidc.customFetch] = operation.fetch;
    if (insecure) oidc.allowInsecureRequests(config);
    oidc.enableNonRepudiationChecks(config);
    return config;
  }

  async function perform<T>(
    signal: AbortSignal | undefined,
    action: (op: Operation) => Promise<T>,
  ): Promise<T> {
    const operation = providerOperation(expectedIssuer, signal, transport);
    try {
      const result = await action(operation);
      operation.interrupted();
      return result;
    } catch (error) {
      operation.interrupted();
      if (error instanceof BffProviderError) throw error;
      if (error instanceof oidc.ClientError && error.cause instanceof BffProviderError)
        throw new BffProviderError(error.cause.reason);
      if (error instanceof oidc.ResponseBodyError)
        throw new BffProviderError(
          error.error === 'invalid_grant' ? 'invalid_grant' : 'provider_refused',
        );
      throw new BffProviderError('response');
    }
  }

  function credentials(
    result: oidc.TokenEndpointResponse & oidc.TokenEndpointResponseHelpers,
    started: number,
    previous?: ProviderIdentity,
    testRecord = false,
  ): ProviderCredentials {
    const current = identity(result.claims(), expectedIssuer);
    if (
      previous &&
      (['issuer', 'subject', 'sid', 'actorId', 'tenantId', 'organizationId'] as const).some(
        (key) => current[key] !== previous[key],
      )
    )
      throw new BffProviderError('identity');
    const seconds = (value: unknown): number => {
      if (
        typeof value !== 'number' ||
        !Number.isSafeInteger(value) ||
        value <= 0 ||
        value > Number.MAX_SAFE_INTEGER / 1000
      )
        throw new BffProviderError('response');
      return value;
    };
    const scopes = typeof result.scope === 'string' ? result.scope.split(/\s+/u) : [];
    if (scopes.includes('offline_access')) throw new BffProviderError('response');
    // ADR-0099 Amendment 7 (Step 4 review S4-2): the test-record scope belongs to the federation_test redemption
    // alone. A browser-edited `scope` on any other authorization, or a refresh that keeps it, is refused; the test
    // redemption is refused without it.
    if (scopes.includes(FEDERATION_TEST_SCOPE) !== testRecord)
      throw new BffProviderError('response');
    if (
      typeof result.id_token !== 'string' ||
      !result.id_token ||
      typeof result.access_token !== 'string' ||
      !result.access_token ||
      (!previous && !result.refresh_token) ||
      (result.refresh_token !== undefined &&
        (typeof result.refresh_token !== 'string' || !result.refresh_token))
    )
      throw new BffProviderError('response');
    const bundle = Object.freeze({
      accessToken: result.access_token,
      refreshToken: result.refresh_token,
      idToken: result.id_token,
      identity: current,
      accessExpiresAt: started + seconds(result.expires_in) * 1000,
      refreshExpiresAt: started + seconds(result.refresh_expires_in) * 1000,
    });
    if (Buffer.byteLength(JSON.stringify(bundle)) > 32 * 1024)
      throw new BffProviderError('response');
    return bundle;
  }

  return Object.freeze({
    async begin(callbackUri: string, signal?: AbortSignal, purpose?: ProviderPurpose) {
      if (
        purpose !== undefined &&
        purpose !== 'verify_email' &&
        (typeof purpose !== 'object' ||
          purpose === null ||
          (purpose.kind !== 'federation_signin' && purpose.kind !== 'federation_test') ||
          Object.keys(purpose).length !== 2 ||
          typeof purpose.alias !== 'string' ||
          !FEDERATION_ALIAS.test(purpose.alias))
      )
        throw new BffProviderError('configuration');
      const federation = typeof purpose === 'object' ? purpose : undefined;
      browserReturn(callbackUri, '/api/auth/callback/keycloak');
      return perform(signal, async (operation) => {
        const config = await configuration(operation);
        const transaction = Object.freeze({
          callbackUri,
          state: oidc.randomState(),
          nonce: oidc.randomNonce(),
          verifier: oidc.randomPKCECodeVerifier(),
        });
        const url = oidc.buildAuthorizationUrl(config, {
          redirect_uri: callbackUri,
          scope:
            federation?.kind === 'federation_test'
              ? 'openid profile email ' + FEDERATION_TEST_SCOPE
              : 'openid profile email',
          ...(purpose === 'verify_email' ? { kc_action: 'VERIFY_EMAIL' } : {}),
          ...(federation ? { kc_idp_hint: federation.alias } : {}),
          ...(federation?.kind === 'federation_test' ? { prompt: 'login', max_age: '0' } : {}),
          code_challenge_method: 'S256',
          code_challenge: await oidc.calculatePKCECodeChallenge(transaction.verifier),
          state: transaction.state,
          nonce: transaction.nonce,
        });
        return Object.freeze({ transaction, redirectUrl: url.href });
      });
    },
    /** `testRecord` is true only for the federation_test owner's redemption (ADR-0099 Amendment 7). */
    async redeem(
      transaction: LoginTransaction,
      responseUrl: string,
      signal?: AbortSignal,
      testRecord = false,
    ) {
      browserReturn(transaction.callbackUri, '/api/auth/callback/keycloak');
      let response: URL;
      try {
        response = new URL(responseUrl);
      } catch {
        throw new BffProviderError('response');
      }
      if (
        response.origin + response.pathname !== transaction.callbackUri ||
        response.hash ||
        response.searchParams.getAll('state').length !== 1 ||
        response.searchParams.get('state') !== transaction.state
      )
        throw new BffProviderError('response');
      return perform(signal, async (operation) => {
        const started = performance.now();
        const config = await configuration(operation);
        return credentials(
          await oidc.authorizationCodeGrant(config, response, {
            expectedState: transaction.state,
            expectedNonce: transaction.nonce,
            pkceCodeVerifier: transaction.verifier,
            idTokenExpected: true,
          }),
          started,
          undefined,
          testRecord,
        );
      });
    },
    /**
     * Ends the provider (SSO) session a refresh token belongs to, server-side, at the fixed end-session endpoint with
     * this client's credentials (Keycloak's refresh-token logout form). Used when a definitive host-binding mismatch
     * refuses a publication outside any browser navigation (ADR-0099 § Host binding). Never returns a token.
     */
    async endSession(refreshToken: string, signal?: AbortSignal) {
      if (typeof refreshToken !== 'string' || !refreshToken || /[\r\n]/.test(refreshToken))
        throw new BffProviderError('configuration');
      return perform(signal, async (operation) => {
        await configuration(operation);
        const response = await operation.fetch(expected.end_session_endpoint, {
          method: 'POST',
          headers: { 'content-type': 'application/x-www-form-urlencoded' },
          body: new URLSearchParams({
            client_id: 'learnstack-web',
            client_secret: secret,
            refresh_token: refreshToken,
          }).toString(),
        });
        if (response.status !== 204 && response.status !== 200)
          throw new BffProviderError('provider_refused');
      });
    },
    async refresh(refreshToken: string, previous: ProviderIdentity, signal?: AbortSignal) {
      return perform(signal, async (operation) => {
        const started = performance.now();
        return credentials(
          await oidc.refreshTokenGrant(await configuration(operation), refreshToken),
          started,
          previous,
        );
      });
    },
    logoutUrl(idToken: string, returnUri: string, state: string) {
      browserReturn(returnUri, '/');
      if (!metadata) throw new BffProviderError('configuration');
      try {
        const config = new oidc.Configuration(metadata, 'learnstack-web');
        if (insecure) oidc.allowInsecureRequests(config);
        return oidc.buildEndSessionUrl(config, {
          id_token_hint: idToken,
          post_logout_redirect_uri: returnUri,
          state,
        }).href;
      } catch {
        throw new BffProviderError('response');
      }
    },
  });
}
