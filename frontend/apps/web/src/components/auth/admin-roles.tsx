/* eslint-disable @next/next/no-html-link-for-pages -- Protected views use freshly admitted full document navigation. */
import type { AdminCopy } from '@/i18n/administration-catalogues';
import type { NativePageSnapshot, PageGrant, PageView } from '@/server/auth/page-contract';

import { MutationFields } from './admin-forms';
import { permissionLabel, roleLabel } from './admin-labels';

const sameGrant = (a: PageGrant, b: PageGrant) => a.scope === b.scope && a.key === b.key;

export function RoleDirectory({
  view,
  admin,
}: {
  readonly view: Extract<PageView, { kind: 'roles' }>;
  readonly admin: AdminCopy;
}) {
  return view.roles.length === 0 ? (
    <p>{admin('roles.empty')}</p>
  ) : (
    <ul className="auth-resource-list">
      {view.roles.map((role) => (
        <li key={role.roleId}>
          <h2>
            <a href={'/studio/roles/' + role.roleId}>{roleLabel(role, admin)}</a>
          </h2>
          <p>
            {role.capabilityScope === 'Tenant'
              ? admin('scope.tenant')
              : admin('scope.organization')}
          </p>
        </li>
      ))}
    </ul>
  );
}

export function RoleDetails({
  snapshot,
  view,
  admin,
}: {
  readonly snapshot: NativePageSnapshot;
  readonly view: Extract<PageView, { kind: 'role' }>;
  readonly admin: AdminCopy;
}) {
  const { role, permissions, release } = view;
  const target = ['roleId', role.roleId] as const;
  const canChange =
    snapshot.scopeKind === 'tenant' &&
    view.canChangeGrants &&
    snapshot.capabilities?.canChangeRoleGrants;
  const canAdopt =
    snapshot.scopeKind === 'tenant' &&
    view.canAdoptRelease &&
    snapshot.capabilities?.canAdoptRoleRelease &&
    role.builtInKey === 'TenantAdmin' &&
    role.capabilityScope === 'Tenant' &&
    role.adoptedReleaseVersion < release.version;
  const choices = permissions.filter((permission) => permission.scope === role.capabilityScope);
  return (
    <>
      <p>
        <a href="/studio/roles">{admin('roles.back')}</a>
      </p>
      <p className="auth-resource-name">{roleLabel(role, admin)}</p>
      <p>
        {role.capabilityScope === 'Tenant' ? admin('scope.tenant') : admin('scope.organization')}
      </p>
      {!canChange && <p>{admin('roles.read_only')}</p>}
      <section aria-labelledby="role-grants-title">
        <h2 id="role-grants-title">{admin('roles.grants')}</h2>
        {role.grants.length === 0 ? (
          <p>{admin('roles.no_grants')}</p>
        ) : (
          <ul>
            {role.grants.map((grant) => (
              <li key={grant.scope + ':' + grant.key}>{permissionLabel(grant, admin)}</li>
            ))}
          </ul>
        )}
        {canChange && (
          <ul className="auth-actions">
            {choices.map((permission, index) => {
              const exists = role.grants.some((grant) => sameGrant(grant, permission));
              const kernel =
                exists &&
                role.builtInKey === 'TenantAdmin' &&
                role.capabilityScope === 'Tenant' &&
                permission.scope === 'Tenant' &&
                ['identity.membership.admin', 'identity.role.admin'].includes(permission.key);
              if (kernel)
                return (
                  <li key={permission.key}>
                    <p>
                      {permissionLabel(permission, admin)} — {admin('roles.kernel')}
                    </p>
                  </li>
                );
              if (!exists && (!permission.canGrant || role.grants.length >= 8)) return null;
              const desired = exists
                ? role.grants.filter((grant) => !sameGrant(grant, permission))
                : [...role.grants, { scope: permission.scope, key: permission.key }];
              return (
                <li key={permission.key}>
                  <form action="/api/studio/role-grants" method="post">
                    <MutationFields
                      snapshot={snapshot}
                      index={index}
                      version={role.version}
                      target={target}
                    />
                    <button type="submit" name="grants" value={JSON.stringify(desired)}>
                      {exists ? admin('roles.remove_grant') : admin('roles.add_grant')}:{' '}
                      {permissionLabel(permission, admin)}
                    </button>
                  </form>
                </li>
              );
            })}
          </ul>
        )}
      </section>
      <section aria-labelledby="role-release-title">
        <h2 id="role-release-title">{admin('roles.release')}</h2>
        <p>
          {admin('roles.adopted')}: {role.adoptedReleaseVersion}
        </p>
        {canAdopt ? (
          <>
            <p>
              {admin('roles.available')}: {release.version}
            </p>
            <p id="role-release-hint">{admin('roles.adopt_hint')}</p>
            <ul>
              {release.tenantAdminDelta.map((grant) => (
                <li key={grant.scope + ':' + grant.key}>{permissionLabel(grant, admin)}</li>
              ))}
            </ul>
            <form action="/api/studio/role-manifest-adoption" method="post">
              <MutationFields
                snapshot={snapshot}
                index={8}
                version={role.version}
                target={target}
              />
              <input type="hidden" name="manifestFingerprint" value={release.fingerprint} />
              <button
                type="submit"
                name="manifestVersion"
                value={String(release.version)}
                aria-describedby="role-release-hint"
              >
                {admin('roles.adopt')}
              </button>
            </form>
          </>
        ) : (
          <p>{admin('roles.release_current')}</p>
        )}
      </section>
    </>
  );
}
