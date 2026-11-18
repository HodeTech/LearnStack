import { isDeepStrictEqual as equal } from 'node:util';
import { refuse } from './development-realm-errors.mjs';

// ADR-0099 § Realm invariants: the account and account-console clients are disabled, and
// manage-account and manage-account-links leave the default role and the development
// personas. Password change, profile editing and linked-account self-service then leave the
// console; credential recovery uses the reset flow. Warm realms are converged by disabling
// the two clients and detaching exactly these grants; nothing else is repaired.
const encode = encodeURIComponent;
const retired = Object.freeze(['manage-account', 'manage-account-links']);
const detachments = new WeakMap();

function list(value, stage) {
  if (!Array.isArray(value) || value.length > 200
    || value.some(item => !item || typeof item.id !== 'string' || !item.id)
    || new Set(value.map(item => item.id)).size !== value.length) refuse(stage, 'unsupported');
  return value;
}

function sameRole(actual, expected) {
  return actual?.id === expected.id && actual.name === expected.name
    && actual.containerId === expected.containerId && actual.clientRole === true;
}

/** Only a binding minted by this planner can name a default-role composite or a persona grant. */
export function accountRetirementDetachment(binding) {
  const value = detachments.get(binding);
  if (!value) refuse('apply', 'configuration');
  detachments.delete(binding);
  return value;
}

async function client(admin, name, stage) {
  const found = list(await admin.get(`clients?clientId=${encode(name)}&max=2`), stage);
  if (found.length !== 1 || found[0].clientId !== name) refuse(stage, 'unsupported');
  const value = await admin.get(`clients/${encode(found[0].id)}`);
  if (value?.id !== found[0].id || value.clientId !== name || value.protocol !== 'openid-connect'
    || value.publicClient !== true || value.bearerOnly === true || value.serviceAccountsEnabled === true
    || typeof value.enabled !== 'boolean') refuse(stage, 'unsupported');
  return value;
}

async function snapshot(admin, realm, stage) {
  const account = await client(admin, 'account', stage);
  const console = await client(admin, 'account-console', stage);
  if (account.id === console.id) refuse(stage, 'unsupported');
  const roles = new Map();
  for (const name of retired) {
    const role = await admin.get(`clients/${encode(account.id)}/roles/${encode(name)}`);
    if (!role?.id || role.name !== name || role.containerId !== account.id || role.clientRole !== true) refuse(stage, 'unsupported');
    roles.set(name, role);
  }
  // The realm representation is the planner's own authenticated read, never a second one.
  const defaultRole = realm?.defaultRole;
  if (!defaultRole?.id || defaultRole.name !== 'default-roles-learnstack') refuse(stage, 'unsupported');
  const composites = list(await admin.get(`roles-by-id/${encode(defaultRole.id)}/composites`), stage);
  return { account, console, roles, defaultRole, composites };
}

function retiredIn(values, roles) {
  return values.filter(value => [...roles.values()].some(role => sameRole(value, role) || value.id === role.id));
}

/**
 * Plans the three retirement edges: client disable, default-role detachment and each persona's
 * direct grant detachment. An effective grant that survives through any other path (a group,
 * another composite) refuses before any write, rather than being silently left in force.
 */
export async function planAccountRetirement({ admin, realm, manifest, candidateUsers, add, preflightResponse }) {
  const before = await snapshot(admin, realm, 'plan');
  for (const current of [before.account, before.console]) {
    if (current.enabled === false) continue;
    // A partial client PUT: webOrigins and the resource-server flag are carried so the
    // pinned ClientResource neither derives origins nor disables authorization services.
    const body = { enabled: false, webOrigins: structuredClone(current.webOrigins ?? []),
      authorizationServicesEnabled: current.authorizationServicesEnabled === true };
    add('disable_account_client', () => admin.put(`clients/${encode(current.id)}`, body), body);
  }
  const fromDefault = retiredIn(before.composites, before.roles);
  if (fromDefault.length) {
    const binding = Object.freeze({});
    const body = fromDefault.map(role => structuredClone(role));
    detachments.set(binding, Object.freeze({ path: `roles-by-id/${encode(before.defaultRole.id)}/composites`, body }));
    add('detach_default_account_grant', () => admin.detachAccountGrant(binding), body);
  }
  const expected = new Map();
  for (const declaration of manifest.users) {
    const original = candidateUsers.get(declaration.username);
    if (!original) continue;
    const base = `users/${encode(original.user.id)}/role-mappings/clients/${encode(before.account.id)}`;
    const direct = list(await admin.get(base), 'plan');
    const effective = list(await admin.get(`${base}/composite?briefRepresentation=true`), 'plan');
    const grants = retiredIn(direct, before.roles);
    // After the default-role and direct detachments no other path (a group, another
    // composite) may still carry a retired role; such a source refuses before any write.
    const viaDefault = (original.roles?.realmMappings ?? []).some(role => role.id === before.defaultRole.id);
    const removed = [...grants, ...(viaDefault ? fromDefault : [])];
    const covered = role => removed.some(value => value.id === role.id)
      || role.name === 'manage-account-links' && removed.some(value => value.name === 'manage-account');
    if (retiredIn(effective, before.roles).some(role => !covered(role))) refuse('plan', 'unsupported');
    expected.set(declaration.username, direct.filter(role => !grants.some(grant => grant.id === role.id)));
    if (grants.length) {
      const binding = Object.freeze({});
      const body = grants.map(role => structuredClone(role));
      detachments.set(binding, Object.freeze({ path: base, body }));
      preflightResponse(body);
      add('detach_persona_account_grant', () => admin.detachAccountGrant(binding), body);
    }
  }
  return { account: before.account, verify: async (afterUsers, afterRealm) => {
    const check = condition => { if (!condition) refuse('readback', 'readback_conflict'); };
    const after = await snapshot(admin, afterRealm, 'readback');
    check(after.defaultRole.id === before.defaultRole.id);
    check(after.account.enabled === false && after.console.enabled === false);
    check(after.account.id === before.account.id && after.console.id === before.console.id);
    check(retiredIn(after.composites, after.roles).length === 0);
    check(after.composites.length === before.composites.length - fromDefault.length);
    for (const declaration of manifest.users) {
      const actual = afterUsers.get(declaration.username);
      check(Boolean(actual));
      const base = `users/${encode(actual.user.id)}/role-mappings/clients/${encode(after.account.id)}`;
      const direct = list(await admin.get(base), 'readback');
      const effective = list(await admin.get(`${base}/composite?briefRepresentation=true`), 'readback');
      check(retiredIn(effective, after.roles).length === 0);
      if (expected.has(declaration.username))
        check(equal(direct.map(role => role.id).sort(), expected.get(declaration.username).map(role => role.id).sort()));
      else check(direct.length === 0);
    }
  } };
}

/** A user created by the planner holds its one realm role and no account-client grant. */
export function exactCreatedAccountAccess(mappings) {
  return mappings.clientMappings === undefined || Object.keys(mappings.clientMappings).length === 0;
}
