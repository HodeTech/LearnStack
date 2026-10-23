import * as oidc from 'openid-client';

import { BffProviderError } from './provider-error.js';
import { providerOperation } from './provider-transport.js';

export type ProviderIdentity = Readonly<{
  issuer: string;
  subject: string;
  actorId: string;
  tenantId: string;
  organizationId: string | null;
}>;

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
const actor = /^[0-9a-f]{8}-[0-9a-f]{4}-7[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i;
const allZero = '00000000-0000-0000-0000-000000000000';

function identity(claims: oidc.IDToken | undefined, issuer: string): ProviderIdentity {
  if (
    !claims ||
    claims.iss !== issuer ||
    typeof claims.sub !== 'string' ||
    !claims.sub ||
    claims.sub.length > 1024 ||
    /[\r\n]/.test(claims.sub) ||
    typeof claims.user_id !== 'string' ||
    !actor.test(claims.user_id) ||
    typeof claims.tenant_id !== 'string' ||
    !uuid.test(claims.tenant_id) ||
    claims.tenant_id === allZero ||
    (claims.organization_id !== undefined &&
      (typeof claims.organization_id !== 'string' ||
        !uuid.test(claims.organization_id) ||
        claims.organization_id === allZero))
  )
    throw new BffProviderError('identity');
  return Object.freeze({
    issuer,
    subject: claims.sub,
    actorId: claims.user_id.toLowerCase(),
    tenantId: claims.tenant_id.toLowerCase(),
    organizationId:
      typeof claims.organization_id === 'string' ? claims.organization_id.toLowerCase() : null,
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
  ): ProviderCredentials {
    const current = identity(result.claims(), expectedIssuer);
    if (
      previous &&
      Object.keys(current).some(
        (key) => current[key as keyof ProviderIdentity] !== previous[key as keyof ProviderIdentity],
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
    async begin(callbackUri: string, signal?: AbortSignal) {
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
          scope: 'openid profile email',
          code_challenge_method: 'S256',
          code_challenge: await oidc.calculatePKCECodeChallenge(transaction.verifier),
          state: transaction.state,
          nonce: transaction.nonce,
        });
        return Object.freeze({ transaction, redirectUrl: url.href });
      });
    },
    async redeem(transaction: LoginTransaction, responseUrl: string, signal?: AbortSignal) {
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
        );
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
