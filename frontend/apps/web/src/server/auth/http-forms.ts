import type { GrantInput } from '@learnstack/sdk/administration-server';

import { BffInputError } from './input-error.js';
import type { NativeScope } from './native-contract.js';
import type { NativeOperation } from './native-operation-contract.js';

export { bffCursor, bffPageQuery } from './page-query.js';

const uuid = /^[0-9a-f]{8}-[0-9a-f]{4}-[1-8][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/;
const v7 = /^[0-9a-f]{8}-[0-9a-f]{4}-7[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/;
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
function required(fields: URLSearchParams, key: string): string {
  const value = fields.get(key);
  if (value === null) throw new BffInputError();
  return value;
}
function id(value: string, pattern = v7): string {
  if (!pattern.test(value)) throw new BffInputError();
  return value;
}
export function bffContextHint(value: string | null): string | undefined {
  if (value === null) return undefined;
  return value === 'tenant' ? value : id(value, uuid);
}
export function bffScope(fields: URLSearchParams): NativeScope {
  const organization = fields.get('organizationId');
  return Object.freeze({
    tenantId: id(required(fields, 'tenantId'), uuid),
    organizationId: organization === null ? null : id(organization, uuid),
  });
}

/** A finite JSON grammar preserves duplicate keys, including escaped aliases, before projection. */
function arrayField<T>(raw: string, item: (reader: JsonArrayReader) => T): readonly T[] {
  const reader = new JsonArrayReader(raw);
  reader.take('[');
  const values: T[] = [];
  if (!reader.maybe(']')) {
    do {
      if (values.length === 8) throw new BffInputError();
      values.push(item(reader));
    } while (reader.maybe(','));
    reader.take(']');
  }
  reader.end();
  return Object.freeze(values);
}
class JsonArrayReader {
  #offset = 0;
  constructor(private readonly source: string) {}
  #space() {
    while (/[\t\n\r ]/.test(this.source[this.#offset] ?? '\0')) ++this.#offset;
  }
  maybe(token: string): boolean {
    this.#space();
    if (this.source[this.#offset] !== token) return false;
    ++this.#offset;
    return true;
  }
  take(token: string) {
    if (!this.maybe(token)) throw new BffInputError();
  }
  string(): string {
    this.#space();
    const token = /"(?:[\x20-\x21\x23-\x5b\x5d-\uffff]|\\(?:["\\/bfnrt]|u[0-9a-fA-F]{4}))*"/y;
    token.lastIndex = this.#offset;
    const match = token.exec(this.source);
    if (!match) throw new BffInputError();
    this.#offset = token.lastIndex;
    return JSON.parse(match[0]) as string;
  }
  end() {
    this.#space();
    if (this.#offset !== this.source.length) throw new BffInputError();
  }
}
function roleIds(raw: string): readonly string[] {
  const values = arrayField(raw, (reader) => id(reader.string()));
  if (new Set(values).size !== values.length) throw new BffInputError();
  return values;
}
function grants(raw: string): readonly GrantInput[] {
  const values = arrayField(raw, (reader): GrantInput => {
    reader.take('{');
    const fields = new Map<string, string>();
    do {
      const key = reader.string();
      if (!['scope', 'key'].includes(key) || fields.has(key)) throw new BffInputError();
      reader.take(':');
      fields.set(key, reader.string());
    } while (reader.maybe(','));
    reader.take('}');
    const scope = fields.get('scope');
    const key = fields.get('key');
    if (
      (scope !== 'Tenant' && scope !== 'Organization') ||
      key === undefined ||
      !pairs.has(scope + ':' + key)
    )
      throw new BffInputError();
    return Object.freeze({ scope, key });
  });
  if (new Set(values.map((value) => value.scope + ':' + value.key)).size !== values.length)
    throw new BffInputError();
  return values;
}
const common = ['csrfToken', 'version', 'idempotencyKey'];
export const BFF_ADMIN_FORMS: Readonly<Record<string, readonly string[]>> = Object.freeze({
  '/api/studio/membership-profile': [...common, 'membershipId', 'displayName', 'preferredLocale'],
  '/api/studio/membership-roles': [...common, 'membershipId', 'roleIds'],
  '/api/studio/membership-status': [...common, 'membershipId', 'status'],
  '/api/studio/role-grants': [...common, 'roleId', 'grants'],
  '/api/studio/role-manifest-adoption': [
    ...common,
    'roleId',
    'manifestVersion',
    'manifestFingerprint',
  ],
  '/api/account/profile': [...common, 'displayName', 'preferredLocale'],
});
function profile(fields: URLSearchParams) {
  const nullable = (key: string, limit: number) => {
    const value = required(fields, key);
    if (
      value.length > limit ||
      (value !== '' && !value.trim()) ||
      [...value].some((character) => {
        const point = character.codePointAt(0)!;
        return point < 32 || (point >= 127 && point <= 159) || (point >= 0xd800 && point <= 0xdfff);
      })
    )
      throw new BffInputError();
    return value === '' ? null : value;
  };
  return Object.freeze({
    displayName: nullable('displayName', 128),
    preferredLocale: nullable('preferredLocale', 35),
  });
}
export function bffAdministrationForm(
  path: string,
  fields: URLSearchParams,
): Readonly<{ operation: NativeOperation; destination: string }> {
  const version = required(fields, 'version');
  const idempotencyKey = required(fields, 'idempotencyKey');
  if (
    !/^(0|[1-9][0-9]{0,18})$/.test(version) ||
    (version.length === 19 && version > '9223372036854775807') ||
    !/^[\x21-\x7e]{8,128}$/.test(idempotencyKey)
  )
    throw new BffInputError();
  const write = { version, idempotencyKey };
  if (path === '/api/account/profile')
    return {
      operation: { kind: 'setOwnProfile', ...write, profile: profile(fields) },
      destination: '/portal/profile',
    };
  if (path.startsWith('/api/studio/membership-')) {
    const member = id(required(fields, 'membershipId'));
    const destination = '/studio/members/' + member;
    if (path.endsWith('-profile'))
      return {
        operation: { kind: 'setMembershipProfile', ...write, id: member, profile: profile(fields) },
        destination,
      };
    if (path.endsWith('-roles'))
      return {
        operation: {
          kind: 'replaceMembershipRoles',
          ...write,
          id: member,
          roleIds: roleIds(required(fields, 'roleIds')),
        },
        destination,
      };
    if (path.endsWith('-status')) {
      const status = required(fields, 'status');
      if (!['Active', 'Suspended', 'Revoked'].includes(status)) throw new BffInputError();
      return {
        operation: {
          kind: 'changeMembershipStatus',
          ...write,
          id: member,
          status: status as 'Active' | 'Suspended' | 'Revoked',
        },
        destination: status === 'Revoked' ? '/studio/members' : destination,
      };
    }
  }
  const role = id(required(fields, 'roleId'));
  const destination = '/studio/roles/' + role;
  if (path === '/api/studio/role-grants')
    return {
      operation: {
        kind: 'changeRoleGrants',
        ...write,
        id: role,
        grants: grants(required(fields, 'grants')),
      },
      destination,
    };
  if (path === '/api/studio/role-manifest-adoption') {
    const manifestVersion = required(fields, 'manifestVersion');
    const manifestFingerprint = required(fields, 'manifestFingerprint');
    if (
      !/^[1-9][0-9]{0,9}$/.test(manifestVersion) ||
      Number(manifestVersion) > 2147483647 ||
      !/^[0-9a-f]{64}$/.test(manifestFingerprint)
    )
      throw new BffInputError();
    return {
      operation: {
        kind: 'adoptRoleManifest',
        ...write,
        id: role,
        manifestVersion: Number(manifestVersion),
        manifestFingerprint,
      },
      destination,
    };
  }
  throw new BffInputError();
}
