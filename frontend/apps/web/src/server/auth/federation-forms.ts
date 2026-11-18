import type { FederationConfigurationInput } from '@learnstack/sdk/administration-server';

import { BffInputError } from './input-error.js';
import type { NativeOperation } from './native-operation-contract.js';

/** One signing certificate's PEM text: the contract's `signingCertificates` item `maxLength`. */
const MAXIMUM_CERTIFICATE_LENGTH = 6144;

/** ADR-0099: the Studio federation page's closed native forms. Every result returns to the page. */
export const FEDERATION_DESTINATION = '/studio/federation';
const proof = ['csrfToken', 'version'] as const;
const configure = [
  'csrfToken',
  'version',
  'protocol',
  'displayName',
  'source',
  'documentUrl',
  'issuerOrEntityId',
  'authorizationEndpoint',
  'tokenEndpoint',
  'jwksEndpoint',
  'userInfoEndpoint',
  'singleSignOnEndpoint',
  'signingCertificate1',
  'signingCertificate2',
  'clientId',
  'clientAuthentication',
  'clientSecret',
] as const;
export const FEDERATION_FORMS: Readonly<Record<string, readonly string[]>> = Object.freeze({
  '/api/studio/federation-configure': configure,
  '/api/studio/federation-rotate-secret': [...proof, 'clientSecret'],
  '/api/studio/federation-rename': [...proof, 'displayName'],
  '/api/studio/federation-activate': proof,
  '/api/studio/federation-enable': proof,
  '/api/studio/federation-resume': proof,
  // Disable and Delete need a deliberate confirmation (ADR-0099 § User experience).
  '/api/studio/federation-disable': [...proof, 'confirm'],
  '/api/studio/federation-delete': [...proof, 'confirm'],
});
/** Fields whose value is a secret; never echoed into a recovery document. */
export const FEDERATION_SECRET_FIELDS: readonly string[] = Object.freeze(['clientSecret']);

/** Validation the user can correct; the closed codes are the page's, never the API's or the provider's. */
export class BffFederationInputError extends BffInputError {
  constructor(readonly invalidFields: readonly string[]) {
    super();
  }
}

const control = (value: string) =>
  [...value].some((character) => {
    const point = character.codePointAt(0)!;
    return (
      (point < 32 && point !== 10 && point !== 13) ||
      (point >= 127 && point <= 159) ||
      (point >= 0xd800 && point <= 0xdfff)
    );
  });

export function federationForm(
  path: string,
  fields: URLSearchParams,
): Readonly<{ operation: NativeOperation; destination: string }> {
  const allowed = FEDERATION_FORMS[path];
  if (!allowed) throw new BffInputError();
  const names = [...fields.keys()];
  if (new Set(names).size !== names.length || names.some((name) => !allowed.includes(name)))
    throw new BffInputError();
  for (const value of fields.values()) if (control(value)) throw new BffInputError();
  const value = (name: string) => fields.get(name);
  const readVersion = (optional = false): string | null => {
    const raw = value('version');
    if (raw === null || raw === '') {
      if (optional) return null;
      throw new BffInputError();
    }
    if (!/^(0|[1-9][0-9]{0,18})$/.test(raw) || (raw.length === 19 && raw > '9223372036854775807'))
      throw new BffInputError();
    return raw;
  };
  const destination = FEDERATION_DESTINATION;
  const invalid: string[] = [];
  const text = (name: string, max: number, nullable = true): string | null => {
    const raw = (value(name) ?? '').trim();
    if (!raw) {
      if (!nullable) invalid.push(name);
      return null;
    }
    if ([...raw].length > max || /[\r\n]/.test(raw)) invalid.push(name);
    return raw;
  };
  const url = (name: string): string | null => {
    const raw = text(name, 2048);
    if (raw === null) return null;
    try {
      const parsed = new URL(raw);
      if (parsed.protocol !== 'https:' || parsed.username || parsed.password || parsed.hash)
        invalid.push(name);
    } catch {
      invalid.push(name);
    }
    return raw;
  };
  const secret = (required: boolean): string | null => {
    // A secret is taken exactly as typed; it is never trimmed, echoed or stored by the BFF.
    const raw = value('clientSecret') ?? '';
    if (!raw) {
      if (required) invalid.push('clientSecret');
      return null;
    }
    if (new TextEncoder().encode(raw).length > 1024 || raw === '**********' || raw.startsWith('${'))
      invalid.push('clientSecret');
    return raw;
  };
  const confirmed = () => {
    if (value('confirm') !== 'yes') invalid.push('confirm');
  };
  let operation: NativeOperation;
  switch (path) {
    case '/api/studio/federation-configure': {
      const version = readVersion(true);
      const protocol = value('protocol');
      const source = value('source');
      if (protocol !== 'oidc' && protocol !== 'saml') throw new BffInputError();
      const sources =
        protocol === 'oidc' ? ['discovery_url', 'manual'] : ['metadata_url', 'manual'];
      if (source === null || !sources.includes(source)) throw new BffInputError();
      const manual = source === 'manual';
      const certificates = ['signingCertificate1', 'signingCertificate2']
        .map((name) => {
          const raw = (value(name) ?? '').trim();
          if (
            raw &&
            (raw.length > MAXIMUM_CERTIFICATE_LENGTH ||
              !raw.includes('-----BEGIN CERTIFICATE-----'))
          )
            invalid.push(name);
          return raw;
        })
        .filter(Boolean);
      const authentication = value('clientAuthentication');
      if (
        protocol === 'oidc'
          ? authentication !== 'client_secret_basic' && authentication !== 'client_secret_post'
          : authentication !== null && authentication !== ''
      )
        throw new BffInputError();
      const input: FederationConfigurationInput = {
        protocol,
        displayName: text('displayName', 80, version !== null),
        source: source as FederationConfigurationInput['source'],
        documentUrl: manual ? null : url('documentUrl'),
        issuerOrEntityId: manual ? text('issuerOrEntityId', 2048, false) : null,
        authorizationEndpoint: protocol === 'oidc' && manual ? url('authorizationEndpoint') : null,
        tokenEndpoint: protocol === 'oidc' && manual ? url('tokenEndpoint') : null,
        jwksEndpoint: protocol === 'oidc' && manual ? url('jwksEndpoint') : null,
        userInfoEndpoint: protocol === 'oidc' && manual ? url('userInfoEndpoint') : null,
        singleSignOnEndpoint: protocol === 'saml' && manual ? url('singleSignOnEndpoint') : null,
        signingCertificates: protocol === 'saml' && manual ? certificates : [],
        clientId: protocol === 'oidc' ? text('clientId', 255, false) : null,
        clientAuthentication:
          protocol === 'oidc'
            ? (authentication as FederationConfigurationInput['clientAuthentication'])
            : null,
        // The first OIDC configuration needs a secret; later ones may keep the stored one (ADR-0099 Amendment 3).
        clientSecret: protocol === 'oidc' ? secret(version === null) : null,
      };
      if (!manual && input.documentUrl === null) invalid.push('documentUrl');
      if (manual && protocol === 'oidc' && (!input.authorizationEndpoint || !input.tokenEndpoint))
        invalid.push(input.authorizationEndpoint ? 'tokenEndpoint' : 'authorizationEndpoint');
      if (manual && protocol === 'oidc' && !input.jwksEndpoint) invalid.push('jwksEndpoint');
      if (manual && protocol === 'saml' && !input.singleSignOnEndpoint)
        invalid.push('singleSignOnEndpoint');
      if (manual && protocol === 'saml' && certificates.length === 0)
        invalid.push('signingCertificate1');
      operation = { kind: 'configureFederation', input, version };
      break;
    }
    case '/api/studio/federation-rotate-secret':
      operation = {
        kind: 'rotateFederationSecret',
        version: readVersion()!,
        clientSecret: secret(true) ?? '',
      };
      break;
    case '/api/studio/federation-rename':
      operation = {
        kind: 'renameFederation',
        version: readVersion()!,
        displayName: text('displayName', 80, false) ?? '',
      };
      break;
    case '/api/studio/federation-disable':
    case '/api/studio/federation-delete':
      confirmed();
      operation = {
        kind: path.endsWith('-disable') ? 'disableFederation' : 'deleteFederation',
        version: readVersion()!,
      };
      break;
    default:
      operation = {
        kind: path.endsWith('-activate')
          ? 'activateFederation'
          : path.endsWith('-enable')
            ? 'enableFederation'
            : 'resumeFederationOperation',
        version: readVersion()!,
      };
  }
  if (invalid.length) throw new BffFederationInputError(Object.freeze([...new Set(invalid)]));
  return Object.freeze({ operation, destination });
}
