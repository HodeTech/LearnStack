import assert from 'node:assert/strict';
import test from 'node:test';
import { randomUUID } from 'node:crypto';
import { developmentSeederRuntimeEnvironment, publicSeederEnvironment } from './demo-database.mjs';

// Per-run synthetic credentials exercise byte-preserving runtime transport;
// they are never usable against a database or published as a credential fixture.
const credentialCanary = randomUUID();
const connection = (database, application) => Object.entries({
  Host: 'selected', Database: database, Username: 'learnstack_app',
  Password: credentialCanary, 'Application Name': application,
}).map(([key, value]) => `${key}="${value.replaceAll('"', '""')}"`).join(';');

const selected = {
  ConnectionStrings__Default: connection('business', 'selected runtime'),
  Identity__AuthorityId: '01930000-0000-7000-8000-000000009001',
  Authentication__Tenant__Issuer: 'http://localhost:8080/realms/learnstack',
};
const hostileAmbient = {
  PATH: '/owned/bin', HTTPS_PROXY: 'http://owned-proxy', SSL_CERT_FILE: '/owned/trust.pem',
  ConnectionStrings__Default: 'unselected-private',
  ConnectionStrings__Migration: 'migration-private',
  ConnectionStrings__PlatformAdmin: 'platform-private',
  ConnectionStrings__OutboxDispatcher: 'dispatcher-private',
  ConnectionStrings__IdentityAuthorization: 'unselected-authorization-private',
  ConnectionStrings__AuthorizationEvidence: 'unselected-evidence-private',
  Identity__AuthorityId: 'unselected-identity', Authentication__Tenant__Issuer: 'unselected-issuer',
  Deployment__Mode: 'SaaS', KEYCLOAK_ADMIN_PASSWORD: 'provider-private',
};

test('restore/build environment carries process prerequisites and no runtime authority or credentials', () => {
  assert.deepEqual(publicSeederEnvironment(undefined, hostileAmbient), {
    PATH: '/owned/bin', HTTPS_PROXY: 'http://owned-proxy', SSL_CERT_FILE: '/owned/trust.pem',
  });
});

test('owned Development runtime carries selected app connection and authority without importing ambient credentials', () => {
  assert.deepEqual(developmentSeederRuntimeEnvironment(selected, hostileAmbient), {
    PATH: '/owned/bin', HTTPS_PROXY: 'http://owned-proxy', SSL_CERT_FILE: '/owned/trust.pem',
    ...selected, Deployment__Mode: 'Development',
  });
});

test('runtime preserves both selected auxiliary strings exactly and never exposes them in diagnostics', () => {
  const pair = {
    ConnectionStrings__IdentityAuthorization: connection('learnstack_identity_authorization', 'authorization; "quoted"'),
    ConnectionStrings__AuthorizationEvidence: connection('learnstack_authorization_evidence', 'evidence; "quoted"'),
  };
  const environment = developmentSeederRuntimeEnvironment({ ...selected, ...pair }, hostileAmbient);
  for (const [key, value] of Object.entries(pair)) assert.equal(environment[key], value);
  for (const key of Object.keys(pair)) {
    assert.throws(() => developmentSeederRuntimeEnvironment({ ...selected, [key]: pair[key] }, hostileAmbient),
      error => !error.message.includes(credentialCanary) && !error.message.includes(pair[key]));
  }
});

for (const key of Object.keys(selected)) {
  test(`runtime refuses absent selected ${key} without an ambient fallback`, () => {
    const missing = { ...selected };
    delete missing[key];
    assert.throws(() => developmentSeederRuntimeEnvironment(missing, hostileAmbient));
  });
}
