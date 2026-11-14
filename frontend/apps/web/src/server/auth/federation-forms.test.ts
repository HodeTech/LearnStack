import { describe, expect, it } from 'vitest';

import { BffFederationInputError, FEDERATION_FORMS, federationForm } from './federation-forms';
import { bffAdministrationForm, BFF_ADMIN_FORMS } from './http-forms';

const csrf = 'c'.repeat(43);
const form = (values: Record<string, string>) => new URLSearchParams(values);
const oidc = {
  csrfToken: csrf,
  protocol: 'oidc',
  displayName: 'Acme SSO',
  source: 'discovery_url',
  documentUrl: 'https://idp.example/realms/acme/.well-known/openid-configuration',
  clientId: 'learnstack',
  clientAuthentication: 'client_secret_basic',
  clientSecret: 'first-secret',
};

describe('ADR-0099 closed federation forms', () => {
  it('registers every federation form with the admin form census and no idempotency key', () => {
    for (const [path, fields] of Object.entries(FEDERATION_FORMS)) {
      expect(BFF_ADMIN_FORMS[path]).toEqual(fields);
      expect(fields).not.toContain('idempotencyKey');
    }
  });

  it('maps the first OIDC configuration and requires its secret', () => {
    expect(federationForm('/api/studio/federation-configure', form(oidc))).toEqual({
      operation: {
        kind: 'configureFederation',
        version: null,
        input: {
          protocol: 'oidc',
          displayName: 'Acme SSO',
          source: 'discovery_url',
          documentUrl: oidc.documentUrl,
          issuerOrEntityId: null,
          authorizationEndpoint: null,
          tokenEndpoint: null,
          jwksEndpoint: null,
          userInfoEndpoint: null,
          singleSignOnEndpoint: null,
          signingCertificates: [],
          clientId: 'learnstack',
          clientAuthentication: 'client_secret_basic',
          clientSecret: 'first-secret',
        },
      },
      destination: '/studio/federation',
    });
    expect(() =>
      federationForm('/api/studio/federation-configure', form({ ...oidc, clientSecret: '' })),
    ).toThrow(BffFederationInputError);
    // A later configuration may omit the secret: the API decides whether the stored one may be kept.
    expect(
      federationForm(
        '/api/studio/federation-configure',
        form({ ...oidc, clientSecret: '', version: '4' }),
      ).operation,
    ).toMatchObject({ version: '4', input: { clientSecret: null } });
  });

  it('maps a manual SAML configuration with at most two certificates', () => {
    const pem = '-----BEGIN CERTIFICATE-----\nMIIB\n-----END CERTIFICATE-----';
    const result = federationForm(
      '/api/studio/federation-configure',
      form({
        csrfToken: csrf,
        protocol: 'saml',
        displayName: 'Acme SAML',
        source: 'manual',
        issuerOrEntityId: 'https://idp.example/entity',
        singleSignOnEndpoint: 'https://idp.example/sso',
        signingCertificate1: pem,
        signingCertificate2: '',
      }),
    );
    expect(result.operation).toMatchObject({
      kind: 'configureFederation',
      input: {
        protocol: 'saml',
        source: 'manual',
        signingCertificates: [pem],
        clientId: null,
        clientAuthentication: null,
        clientSecret: null,
      },
    });
  });

  it.each([
    ['an http URL', { documentUrl: 'http://idp.example/x' }, 'documentUrl'],
    ['the mask literal', { clientSecret: '**********' }, 'clientSecret'],
    ['a vault reference', { clientSecret: '${vault.key}' }, 'clientSecret'],
    ['an empty name', { displayName: '' }, 'displayName'],
    ['an empty client', { clientId: '' }, 'clientId'],
  ])('reports %s as a field to correct', (_name, patch, field) => {
    try {
      federationForm('/api/studio/federation-configure', form({ ...oidc, ...patch }));
      throw new Error('accepted');
    } catch (error) {
      expect(error).toBeInstanceOf(BffFederationInputError);
      expect((error as BffFederationInputError).invalidFields).toContain(field);
    }
  });

  it('refuses unknown, duplicate and structural values outright', () => {
    for (const fields of [
      form({ ...oidc, idempotencyKey: 'k'.repeat(43) }),
      form({ ...oidc, alias: 'lsf-' + 'a'.repeat(32) }),
      form({ ...oidc, protocol: 'ldap' }),
      form({ ...oidc, source: 'metadata_url' }),
      form({ ...oidc, version: '01' }),
      new URLSearchParams('csrfToken=a&csrfToken=b'),
    ])
      expect(() => federationForm('/api/studio/federation-configure', fields)).toThrow();
  });

  it('requires a deliberate confirmation for Disable and Delete', () => {
    for (const path of ['/api/studio/federation-disable', '/api/studio/federation-delete']) {
      expect(() => federationForm(path, form({ csrfToken: csrf, version: '4' }))).toThrow(
        BffFederationInputError,
      );
      expect(
        federationForm(path, form({ csrfToken: csrf, version: '4', confirm: 'yes' })).operation,
      ).toEqual({
        kind: path.endsWith('-disable') ? 'disableFederation' : 'deleteFederation',
        version: '4',
      });
    }
  });

  it('dispatches federation forms through the closed admin form entry point', () => {
    expect(
      bffAdministrationForm(
        '/api/studio/federation-activate',
        form({ csrfToken: csrf, version: '7' }),
      ),
    ).toEqual({
      operation: { kind: 'activateFederation', version: '7' },
      destination: '/studio/federation',
    });
    expect(
      bffAdministrationForm(
        '/api/studio/federation-rotate-secret',
        form({ csrfToken: csrf, version: '7', clientSecret: 'fresh' }),
      ).operation,
    ).toEqual({ kind: 'rotateFederationSecret', version: '7', clientSecret: 'fresh' });
  });
});
