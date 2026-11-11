import { isDeepStrictEqual as equal } from 'node:util';
import { refuse } from './development-realm-errors.mjs';

const name = 'learnstack-event-reader';
const encode = encodeURIComponent;
const mapper = Object.freeze({ name: 'learnstack-event-reader-roles', protocol: 'openid-connect', consentRequired: false,
  protocolMapper: 'oidc-usermodel-client-role-mapper', config: {
    'usermodel.clientRoleMapping.clientId': 'realm-management', 'claim.name': 'resource_access.realm-management.roles',
    'jsonType.label': 'String', multivalued: 'true', 'access.token.claim': 'true',
    'id.token.claim': 'false', 'userinfo.token.claim': 'false', 'introspection.token.claim': 'false',
  } });
const fields = Object.freeze({ clientId: name, enabled: true, protocol: 'openid-connect',
  clientAuthenticatorType: 'client-secret', publicClient: false, bearerOnly: false,
  standardFlowEnabled: false, implicitFlowEnabled: false, directAccessGrantsEnabled: false,
  serviceAccountsEnabled: true, fullScopeAllowed: false, redirectUris: [], webOrigins: [],
  defaultClientScopes: [], optionalClientScopes: [] });
const list = value => { if (!Array.isArray(value) || value.length > 256) refuse('plan', 'unsupported'); return value; };
const plain = value => Object.fromEntries(Object.entries(value).filter(([key]) => key !== 'id'));
const sameRole = (a, b) => a.id === b.id && a.name === b.name && a.containerId === b.containerId && a.clientRole === true;
const detachments = new WeakMap();
export function eventReaderDefaultRoleDetachment(binding) {
  const value = detachments.get(binding);
  if (!value) refuse('apply', 'configuration');
  detachments.delete(binding);
  return value;
}

/** Separate machine identity. Existing secrets, excess roles, scopes or mappers are never silently adopted. */
export async function planEventReader(admin, secret) {
  if (typeof secret !== 'string' || secret.length < 16 || /[\r\n]/.test(secret)) refuse('plan', 'configuration');
  const management = list(await admin.get('clients?clientId=realm-management&max=2'));
  if (management.length !== 1 || !management[0].id) refuse('plan', 'unsupported');
  const managementId = management[0].id;
  const role = await admin.get(`clients/${encode(managementId)}/roles/view-events`);
  if (!role?.id || role.name !== 'view-events' || role.containerId !== managementId || role.clientRole !== true || role.composite !== false)
    refuse('plan', 'unsupported');
  const found = list(await admin.get(`clients?clientId=${name}&max=2`));
  if (found.length > 1) refuse('plan', 'unsupported');
  const existing = found[0];
  if (existing) await verify(existing.id);

  async function verify(id) {
    if (!id) refuse('readback', 'readback_conflict');
    const client = await admin.get(`clients/${encode(id)}`);
    if (!Object.entries(fields).every(([key, value]) => equal(client[key], value))) refuse('plan', 'readback_conflict');
    const credential = await admin.get(`clients/${encode(id)}/client-secret`);
    if (credential?.type !== 'secret' || credential.value !== secret) refuse('plan', 'secret_conflict');
    const mappers = list(await admin.get(`clients/${encode(id)}/protocol-mappers/models`));
    if (mappers.length !== 1 || !equal(plain(mappers[0]), mapper)) refuse('plan', 'mapper_conflict');
    for (const kind of ['default', 'optional'])
      if (list(await admin.get(`clients/${encode(id)}/${kind}-client-scopes`)).length) refuse('plan', 'unsupported');
    const scope = await admin.get(`clients/${encode(id)}/scope-mappings`);
    requireMappings(scope);
    const user = await admin.get(`clients/${encode(id)}/service-account-user`);
    // Pinned service-account-user DTO omits serviceAccountClientId. Its binding
    // comes from this authenticated exact-client endpoint, never a username lookup.
    if (!user?.id || user.enabled !== true || user.serviceAccountClientId !== undefined && user.serviceAccountClientId !== name)
      refuse('plan', 'identity_conflict');
    if (list(await admin.get(`users/${encode(user.id)}/groups?first=0&max=1`)).length) refuse('plan', 'unsupported');
    requireMappings(await admin.get(`users/${encode(user.id)}/role-mappings`));
    const effective = list(await admin.get(`users/${encode(user.id)}/role-mappings/clients/${encode(managementId)}/composite`));
    if (effective.length !== 1 || !sameRole(effective[0], role)) refuse('plan', 'unsupported');
  }
  function requireMappings(value) {
    if (!value || (value.realmMappings?.length ?? 0) !== 0 || !value.clientMappings
        || Object.keys(value.clientMappings).length !== 1) refuse('plan', 'unsupported');
    const mapping = value.clientMappings['realm-management'];
    if (mapping?.id !== managementId || mapping.client !== 'realm-management'
        || mapping.mappings?.length !== 1 || !sameRole(mapping.mappings[0], role)) refuse('plan', 'unsupported');
  }
  const accepted = result => { if (![200, 201, 204].includes(result?.status)) refuse('apply', 'status'); };
  return Object.freeze({ changed: !existing,
    async apply() {
      if (existing) return;
      accepted(await admin.post('clients', { ...fields, secret, protocolMappers: [mapper] }));
      const created = list(await admin.get(`clients?clientId=${name}&max=2`));
      if (created.length !== 1 || !created[0].id) refuse('apply', 'readback_conflict');
      const id = created[0].id;
      // Pinned Keycloak adds service_account when enabling service accounts even
      // with an explicit empty default list. This new client uses its one local
      // role mapper, so retire only that freshly-created automatic assignment.
      const automatic = list(await admin.get(`clients/${encode(id)}/default-client-scopes`));
      if (automatic.some(scope => scope.name !== 'service_account' || !scope.id) || automatic.length > 1)
        refuse('apply', 'unsupported');
      for (const scope of automatic) {
        const binding = Object.freeze({});
        detachments.set(binding, Object.freeze({ path: `clients/${encode(id)}/default-client-scopes/${encode(scope.id)}` }));
        await admin.detachNewEventReaderDefault(binding);
      }
      const user = await admin.get(`clients/${encode(id)}/service-account-user`);
      if (!user?.id) refuse('apply', 'readback_conflict');
      const mappings = await admin.get(`users/${encode(user.id)}/role-mappings`);
      if (Object.keys(mappings.clientMappings ?? {}).length) refuse('apply', 'unsupported');
      const defaults = mappings.realmMappings ?? [];
      if (defaults.length > 1 || defaults.some(value => value.name !== 'default-roles-learnstack')) refuse('apply', 'unsupported');
      // Newly-created provider defaults are removed; a pre-existing machine account is never repaired by deleting grants.
      if (defaults.length) {
        const binding = Object.freeze({});
        detachments.set(binding, Object.freeze({ path: `users/${encode(user.id)}/role-mappings/realm`, body: structuredClone(defaults) }));
        await admin.detachNewEventReaderDefault(binding);
      }
      accepted(await admin.post(`users/${encode(user.id)}/role-mappings/clients/${encode(managementId)}`, [role]));
      accepted(await admin.post(`clients/${encode(id)}/scope-mappings/clients/${encode(managementId)}`, [role]));
      await verify(id);
    },
    async verify() {
      const current = list(await admin.get(`clients?clientId=${name}&max=2`));
      if (current.length !== 1) refuse('readback', 'readback_conflict');
      await verify(current[0].id);
    },
  });
}
