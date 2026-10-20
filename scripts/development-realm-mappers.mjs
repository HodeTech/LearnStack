import { refuse } from './development-realm-errors.mjs';

export const identityClaims = Object.freeze(['user_id', 'tenant_id', 'organization_id']);
const reservedClaims = new Set(['iss', 'sub', 'aud', 'azp', 'typ', 'scope', 'iat', 'exp', 'nbf']);
const knownTypes = new Set([
  'oidc-acr-mapper', 'oidc-address-mapper', 'oidc-allowed-origins-mapper',
  'oidc-audience-mapper', 'oidc-audience-resolve-mapper', 'oidc-full-name-mapper',
  'oidc-organization-membership-mapper', 'oidc-sub-mapper', 'oidc-usermodel-attribute-mapper',
  'oidc-usermodel-client-role-mapper', 'oidc-usermodel-property-mapper',
  'oidc-usermodel-realm-role-mapper', 'oidc-usersessionmodel-note-mapper',
]);

// Match pinned 26.6.2 JsonUtils.splitClaimPath: a backslash escapes any
// character; an unescaped dot ends a nonempty component. A leading dot is literal.
export function claimRoot(claim) {
  if (typeof claim !== 'string') return '';
  const first = /^((\\.|[^\\.])+?)\./.exec(claim);
  return (first ? first[1] : claim).replace(/\\(.)/g, '$1');
}

export function validateEffectiveMappers(mappers, stage = 'plan') {
  const fail = () => refuse(stage, 'mapper_conflict');
  for (const mapper of mappers) {
    if (!mapper || mapper.protocol !== 'openid-connect' || !knownTypes.has(mapper.protocolMapper)) fail();
    const config = mapper.config ?? {};
    const root = claimRoot(config['claim.name']);
    if (reservedClaims.has(root)) fail();
    if (mapper.protocolMapper === 'oidc-usermodel-client-role-mapper' && root.includes('${client_id}')) fail();
    if (identityClaims.includes(root)) {
      if (mapper.protocolMapper !== 'oidc-usermodel-attribute-mapper'
        || config['claim.name'] !== root || config['user.attribute'] !== root
        || config['jsonType.label'] !== 'String' || config.multivalued !== 'false'
        || config['access.token.claim'] !== 'true' || config['id.token.claim'] !== 'true'
        || config['userinfo.token.claim'] !== 'false') fail();
    }
  }
  for (const claim of identityClaims)
    if (mappers.filter(mapper => claimRoot(mapper.config?.['claim.name']) === claim).length !== 1) fail();
  for (const type of ['oidc-sub-mapper', 'oidc-audience-mapper', 'oidc-audience-resolve-mapper'])
    if (mappers.filter(mapper => mapper.protocolMapper === type).length !== 1) fail();
  const subject = mappers.find(mapper => mapper.protocolMapper === 'oidc-sub-mapper');
  if (subject.config?.['access.token.claim'] !== 'true') fail();
  const audience = mappers.find(mapper => mapper.protocolMapper === 'oidc-audience-mapper');
  if (audience.config?.['included.client.audience'] !== 'learnstack-api'
    || audience.config?.['access.token.claim'] !== 'true'
    || audience.config?.['id.token.claim'] !== 'false') fail();
  const resolver = mappers.find(mapper => mapper.protocolMapper === 'oidc-audience-resolve-mapper');
  if (resolver.config?.['access.token.claim'] !== 'true' || resolver.config?.['id.token.claim'] === 'true') fail();
}

// The owned names identify repairable mappings; an unexpected implementation is
// an administrator customization, rather than permission to replace its code.
export function mergeOwnedMappers(current, desired, stage = 'plan') {
  const names = new Set();
  for (const mapper of current) {
    if (!mapper || typeof mapper.name !== 'string' || names.has(mapper.name)
      || typeof mapper.id !== 'string' || !mapper.id) refuse(stage, 'mapper_conflict');
    names.add(mapper.name);
  }
  return [
    ...current.filter(mapper => !desired.some(value => value.name === mapper.name)),
    ...desired.map(value => {
      const existing = current.find(mapper => mapper.name === value.name);
      if (!existing) return structuredClone(value);
      if (existing.protocolMapper !== value.protocolMapper || existing.protocol !== value.protocol)
        refuse(stage, 'mapper_conflict');
      return { ...existing, ...value, id: existing.id, config: { ...existing.config, ...value.config } };
    }),
  ];
}
