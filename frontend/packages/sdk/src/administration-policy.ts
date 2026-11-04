/** Validation of the closed administration wire surface; no permission decision is cached here. */
export type AdministrationShape =
  | 'members'
  | 'member'
  | 'profile'
  | 'roles'
  | 'role'
  | 'permissions'
  | 'contexts'
  | 'capabilities'
  | 'organizations'
  | 'organization'
  | 'version';

export function object(value: unknown): value is Record<string, unknown> {
  return value !== null && typeof value === 'object' && !Array.isArray(value);
}
export function fields(value: unknown, keys: readonly string[]): value is Record<string, unknown> {
  return (
    object(value) &&
    Object.keys(value).length === keys.length &&
    keys.every((key) => Object.hasOwn(value, key))
  );
}
export function identifier(value: unknown): value is string {
  return (
    typeof value === 'string' &&
    /^[0-9a-f]{8}-[0-9a-f]{4}-7[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/.test(value)
  );
}
export function version(value: unknown): value is string {
  return (
    typeof value === 'string' &&
    /^(0|[1-9][0-9]{0,18})$/.test(value) &&
    (value.length < 19 || value <= '9223372036854775807')
  );
}
export function text(
  value: unknown,
  maximum: number,
  empty = false,
  allowControls = false,
): value is string {
  if (typeof value !== 'string' || value.length > maximum || (!empty && value.trim().length === 0))
    return false;
  for (const character of value) {
    const code = character.codePointAt(0)!;
    if (
      code === 0 ||
      (!allowControls && (code < 32 || (code >= 127 && code <= 159))) ||
      (code >= 0xd800 && code <= 0xdfff)
    )
      return false;
  }
  return true;
}
export const displayName = (value: unknown): value is string => text(value, 200, false, true);
export const slug = (value: unknown): value is string =>
  text(value, 63) && /^[a-z0-9]+(?:-[a-z0-9]+)*$/.test(value);
const nullableText = (value: unknown, maximum: number) => value === null || text(value, maximum);
const nullableId = (value: unknown) => value === null || identifier(value);
const scopes = new Set(['Tenant', 'Organization']);
const states = new Set(['Active', 'Suspended', 'Revoked']);
const organizationStates = new Set(['Active', 'Suspended', 'Archived']);
const roleTemplates = new Set([
  'TenantAdmin:Tenant',
  'Instructor:Tenant',
  'Learner:Tenant',
  'Guardian:Tenant',
  'Editor:Tenant',
  'OrgAdmin:Organization',
  'OrgInstructor:Organization',
  'Learner:Organization',
]);
const pairs = new Set([
  'Tenant:identity.membership.admin',
  'Tenant:identity.role.admin',
  'Tenant:identity.membership_profile.write',
  'Organization:identity.membership.admin',
  'Organization:identity.membership_profile.write',
  'Tenant:tenancy.organization.admin',
  'Tenant:tenancy.locale.write',
  'Tenant:tenancy.setting.write',
]);
export function grant(value: unknown): value is { scope: 'Tenant' | 'Organization'; key: string } {
  return (
    fields(value, ['scope', 'key']) &&
    typeof value.scope === 'string' &&
    scopes.has(value.scope) &&
    typeof value.key === 'string' &&
    pairs.has(value.scope + ':' + value.key)
  );
}
export function grants(value: unknown): boolean {
  return (
    Array.isArray(value) &&
    value.length <= 8 &&
    value.every(grant) &&
    new Set(value.map((item) => item.scope + ':' + item.key)).size === value.length
  );
}
export function profileInput(value: unknown): boolean {
  return (
    fields(value, ['displayName', 'preferredLocale']) &&
    nullableText(value.displayName, 128) &&
    nullableText(value.preferredLocale, 35)
  );
}
function pageInfo(value: unknown): boolean {
  return (
    fields(value, ['nextCursor', 'previousCursor', 'hasNext', 'hasPrevious']) &&
    (value.nextCursor === null || text(value.nextCursor, 1024)) &&
    value.previousCursor === null &&
    typeof value.hasNext === 'boolean' &&
    value.hasPrevious === false &&
    value.hasNext === (value.nextCursor !== null)
  );
}
function page(value: unknown, item: (entry: unknown) => boolean): boolean {
  return (
    fields(value, ['items', 'pageInfo']) &&
    Array.isArray(value.items) &&
    value.items.length <= 100 &&
    value.items.every(item) &&
    pageInfo(value.pageInfo)
  );
}
function member(value: unknown, detail: boolean): boolean {
  const keys = [
    'membershipId',
    'userId',
    'organizationId',
    'status',
    'displayName',
    'preferredLocale',
  ];
  if (detail) keys.push('roleIds', 'version');
  return (
    fields(value, keys) &&
    identifier(value.membershipId) &&
    identifier(value.userId) &&
    nullableId(value.organizationId) &&
    typeof value.status === 'string' &&
    states.has(value.status) &&
    (detail || value.status !== 'Revoked') &&
    nullableText(value.displayName, 128) &&
    nullableText(value.preferredLocale, 35) &&
    (!detail ||
      (version(value.version) &&
        Array.isArray(value.roleIds) &&
        value.roleIds.length <= 8 &&
        value.roleIds.every(identifier) &&
        new Set(value.roleIds).size === value.roleIds.length))
  );
}
function role(value: unknown, detail: boolean): boolean {
  const keys = ['roleId', 'builtInKey', 'capabilityScope', 'version'];
  if (detail) keys.push('grants', 'adoptedReleaseVersion', 'adoptedReleaseFingerprint');
  return (
    fields(value, keys) &&
    identifier(value.roleId) &&
    typeof value.builtInKey === 'string' &&
    typeof value.capabilityScope === 'string' &&
    roleTemplates.has(value.builtInKey + ':' + value.capabilityScope) &&
    version(value.version) &&
    (!detail ||
      (grants(value.grants) &&
        Number.isInteger(value.adoptedReleaseVersion) &&
        (value.adoptedReleaseVersion === 1 || value.adoptedReleaseVersion === 2) &&
        typeof value.adoptedReleaseFingerprint === 'string' &&
        /^[0-9a-f]{64}$/.test(value.adoptedReleaseFingerprint)))
  );
}
function organization(value: unknown): boolean {
  return (
    fields(value, ['organizationId', 'slug', 'displayName', 'status', 'isDefault', 'version']) &&
    identifier(value.organizationId) &&
    slug(value.slug) &&
    displayName(value.displayName) &&
    typeof value.status === 'string' &&
    organizationStates.has(value.status) &&
    typeof value.isDefault === 'boolean' &&
    version(value.version)
  );
}
function context(value: unknown): boolean {
  if (
    !fields(value, [
      'tenantId',
      'organizationId',
      'tenantName',
      'organizationName',
      'entryOrigin',
      'canUseCurrentHost',
    ]) ||
    !identifier(value.tenantId) ||
    !nullableId(value.organizationId) ||
    !displayName(value.tenantName) ||
    !(value.organizationId === null
      ? value.organizationName === null
      : displayName(value.organizationName)) ||
    typeof value.entryOrigin !== 'string' ||
    typeof value.canUseCurrentHost !== 'boolean'
  )
    return false;
  try {
    const url = new URL(value.entryOrigin);
    return url.protocol === 'https:' && url.origin === value.entryOrigin;
  } catch {
    return false;
  }
}
function permissions(value: unknown): boolean {
  if (
    !fields(value, ['items', 'release']) ||
    !Array.isArray(value.items) ||
    value.items.length !== 8 ||
    !value.items.every(
      (item) =>
        fields(item, ['scope', 'key', 'labelKey', 'canGrant']) &&
        grant({ scope: item.scope, key: item.key }) &&
        text(item.labelKey, 200) &&
        typeof item.canGrant === 'boolean',
    ) ||
    new Set(value.items.map((item) => item.scope + ':' + item.key)).size !== 8
  )
    return false;
  const release = value.release;
  return (
    fields(release, ['version', 'fingerprint', 'tenantAdminDelta']) &&
    release.version === 2 &&
    typeof release.fingerprint === 'string' &&
    /^[0-9a-f]{64}$/.test(release.fingerprint) &&
    grants(release.tenantAdminDelta) &&
    (release.tenantAdminDelta as unknown[]).length === 3 &&
    (release.tenantAdminDelta as { scope: string; key: string }[]).every(
      (item) => item.scope === 'Tenant' && item.key.startsWith('tenancy.'),
    )
  );
}
const flags = [
  'canReadTenantMembers',
  'canReadOrganizationMembers',
  'canWriteMemberProfiles',
  'canManageMemberships',
  'canReadRoles',
  'canChangeRoleGrants',
  'canAdoptRoleRelease',
  'canWriteOwnProfile',
  'canManageOrganizations',
] as const;

export function isAdministrationSuccess(shape: AdministrationShape, value: unknown): boolean {
  switch (shape) {
    case 'version':
      return fields(value, ['version']) && version(value.version);
    case 'members':
      return page(value, (item) => member(item, false));
    case 'member':
      return member(value, true);
    case 'profile':
      return (
        fields(value, ['displayName', 'preferredLocale', 'version']) &&
        profileInput({
          displayName: value.displayName,
          preferredLocale: value.preferredLocale,
        }) &&
        version(value.version)
      );
    case 'role':
      return role(value, true);
    case 'roles':
      return (
        fields(value, ['items']) &&
        Array.isArray(value.items) &&
        value.items.length === 8 &&
        value.items.every((item) => role(item, false)) &&
        new Set(value.items.map((item) => item.roleId)).size === 8 &&
        new Set(value.items.map((item) => item.builtInKey + ':' + item.capabilityScope)).size === 8
      );
    case 'permissions':
      return permissions(value);
    case 'contexts':
      return page(value, context);
    case 'capabilities':
      return fields(value, flags) && flags.every((flag) => typeof value[flag] === 'boolean');
    case 'organization':
      return organization(value);
    case 'organizations':
      return (
        fields(value, ['items', 'defaultOrganizationId', 'tenantVersion', 'pageInfo']) &&
        Array.isArray(value.items) &&
        value.items.length <= 100 &&
        value.items.every(organization) &&
        nullableId(value.defaultOrganizationId) &&
        version(value.tenantVersion) &&
        pageInfo(value.pageInfo)
      );
  }
}
