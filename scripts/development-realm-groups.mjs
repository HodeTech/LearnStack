import { refuse } from './development-realm-errors.mjs';
import { identityClaims } from './development-realm-mappers.mjs';

const object = value => value !== null && typeof value === 'object' && !Array.isArray(value);
const groupLimit = 200;

function boundedGroups(value, stage) {
  if (!Array.isArray(value) || value.length > groupLimit) refuse(stage, 'unsupported');
  const ids = new Set();
  for (const group of value) {
    if (!object(group) || typeof group.id !== 'string' || !group.id || ids.has(group.id)) refuse(stage, 'unsupported');
    ids.add(group.id);
  }
  return value;
}

export async function readUserGroupIds(admin, userId, stage) {
  // Intentionally omit search/first/max to use the mapper's provider stream.
  // Pinned JPA pagination hides organization groups; the cached adapter itself
  // filters them from both mapper and REST reads. Any nonordinary IDs exposed
  // by the provider remain unqualified. Bytes, time and array size stay bounded.
  const assigned = boundedGroups(await admin.get(`users/${encodeURIComponent(userId)}/groups?briefRepresentation=true`), stage);
  return new Set(assigned.map(group => group.id));
}

/** Qualify every possible inherited identity source without modifying groups. */
export async function readRealmGroups(admin, users, stage) {
  const count = await admin.get('groups/count');
  if (!object(count) || !Number.isSafeInteger(count.count) || count.count < 0 || count.count > groupLimit)
    refuse(stage, 'unsupported');
  // An encoded space survives REST's empty-string-to-null conversion, then
  // GroupsResource trims it to an empty contains-search over all descendants.
  // Disabling hierarchy avoids hidden recursive expansion.
  const values = boundedGroups(await admin.get('groups?search=%20&exact=false&first=0&max=201&briefRepresentation=false&populateHierarchy=false&subGroupsCount=false'), stage);
  if (values.length !== count.count) refuse(stage, 'unsupported');
  const groups = new Map(values.map(group => [group.id, group]));
  for (const group of groups.values()) {
    if (typeof group.name !== 'string' || !group.name
      || (group.parentId != null && (typeof group.parentId !== 'string' || !group.parentId))) refuse(stage, 'unsupported');
    const attributes = group.attributes ?? {};
    if (!object(attributes)) refuse(stage, 'unsupported');
    for (const name of identityClaims)
      if (Object.hasOwn(attributes, name) && (!Array.isArray(attributes[name]) || attributes[name].length !== 0))
        refuse(stage, 'identity_conflict');
    const ancestors = new Set([group.id]);
    let parentId = group.parentId;
    while (parentId != null) {
      if (!groups.has(parentId) || ancestors.has(parentId)) refuse(stage, 'unsupported');
      ancestors.add(parentId);
      parentId = groups.get(parentId).parentId;
    }
  }
  const memberships = new Map();
  for (const user of users) {
    const assigned = await readUserGroupIds(admin, user.id, stage);
    for (const groupId of assigned) if (!groups.has(groupId)) refuse(stage, 'unsupported');
    memberships.set(user.id, assigned);
  }
  return { groups, memberships };
}
