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
        || (config['aggregate.attrs'] !== undefined && config['aggregate.attrs'] !== 'false')
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
      // Never let a desired config overlay hide an administrator's unsafe
      // aggregation setting before effective mapper validation runs.
      if (identityClaims.includes(claimRoot(value.config?.['claim.name']))
        && existing.config?.['aggregate.attrs'] !== undefined && existing.config['aggregate.attrs'] !== 'false')
        refuse(stage, 'mapper_conflict');
      return { ...existing, ...value, id: existing.id, config: { ...existing.config, ...value.config } };
    }),
  ];
}

// Pinned RepresentationToModel removes empty config strings on import/create
// and effective-model reads. This one absent optional setting means no prefix
// in AbstractUserRoleMappingMapper; every other setting remains significant.
export function accountMapperRepresentation(mapper) {
  const prefix = 'usermodel.clientRoleMapping.rolePrefix';
  if (mapper.protocolMapper !== 'oidc-usermodel-client-role-mapper'
    || mapper.config?.['usermodel.clientRoleMapping.clientId'] !== 'account'
    || mapper.config?.['claim.name'] !== 'resource_access.account.roles'
    || mapper.config?.[prefix] !== '') return mapper;
  return { ...mapper, config: Object.fromEntries(Object.entries(mapper.config).filter(([key]) => key !== prefix)) };
}

// The shared console may emit account roles, but no realm/all-client role
// carrier or implicit role/audience transformer is qualified by a claim name.
export function accountMapperCompatibility(mappers, canonical, stage = 'plan') {
  const fail = () => refuse(stage, 'mapper_conflict');
  const configBoundary = {
    'usermodel.clientRoleMapping.clientId': 'account', 'usermodel.clientRoleMapping.rolePrefix': '',
    'claim.name': 'resource_access.account.roles', 'jsonType.label': 'String', multivalued: 'true',
    'access.token.claim': 'true', 'introspection.token.claim': 'true', 'id.token.claim': 'false',
    'userinfo.token.claim': 'false', 'lightweight.claim': 'false',
  };
  if (Object.keys(canonical.config ?? {}).length !== Object.keys(configBoundary).length
    || Object.entries(configBoundary).some(([key, value]) => canonical.config[key] !== value)) fail();
  let carriers = 0;
  let resolvers = 0;
  for (const mapper of mappers) {
    if (!mapper || mapper.protocol !== 'openid-connect' || !knownTypes.has(mapper.protocolMapper)) return undefined;
    const config = accountMapperRepresentation(mapper).config ?? {};
    if (mapper.protocolMapper === 'oidc-usermodel-client-role-mapper') {
      if (Object.keys(config).some(key => !Object.hasOwn(canonical.config, key))
        || Object.entries(canonical.config).some(([key, value]) =>
          key === 'usermodel.clientRoleMapping.rolePrefix' ? config[key] !== undefined : config[key] !== value)) return undefined;
      carriers++;
    } else if (mapper.protocolMapper === 'oidc-audience-resolve-mapper') {
      if (Object.keys(config).some(key => !['access.token.claim', 'introspection.token.claim', 'id.token.claim', 'lightweight.claim'].includes(key))
        || ['access.token.claim', 'introspection.token.claim'].some(key => config[key] !== undefined && config[key] !== 'true')
        || ['id.token.claim', 'lightweight.claim'].some(key => config[key] !== undefined && config[key] !== 'false')) return undefined;
      resolvers++;
    } else {
      if (['oidc-audience-mapper', 'oidc-usermodel-realm-role-mapper'].includes(mapper.protocolMapper)
        || ['resource_access', 'realm_access', ...reservedClaims].includes(claimRoot(config['claim.name']))) return undefined;
    }
  }
  if (resolvers !== 1 || carriers > 1) return undefined;
  return carriers === 1;
}

// A malformed declaration always refuses; only a well-read incompatible graph
// can select the independently qualified stock-normalization path.
export function validateAccountMappers(mappers, canonical, stage = 'plan') {
  const compatible = accountMapperCompatibility(mappers, canonical, stage);
  if (compatible === undefined) refuse(stage, 'mapper_conflict');
  return compatible;
}
