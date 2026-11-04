/* eslint-disable @next/next/no-html-link-for-pages -- Protected views use freshly admitted full document navigation. */
import type { AdminCopy, ProfileCopy } from '@/i18n/administration-catalogues';
import type { NativePageSnapshot, PageView } from '@/server/auth/page-contract';

import { MutationFields, ProfileFields, ProfileValues } from './admin-forms';
import { roleLabel, statusLabel } from './admin-labels';

export function MemberDirectory({
  view,
  admin,
}: {
  readonly view: Extract<PageView, { kind: 'members' }>;
  readonly admin: AdminCopy;
}) {
  const next =
    view.hasNextPage && view.endCursor !== null
      ? '/studio/members?' +
        new URLSearchParams({ cursor: view.endCursor, limit: String(view.limit) })
      : null;
  return (
    <>
      {view.items.length === 0 ? (
        <p>{admin('members.empty')}</p>
      ) : (
        <ul className="auth-resource-list">
          {view.items.map((member) => (
            <li key={member.membershipId}>
              <h2>
                <a href={'/studio/members/' + member.membershipId}>
                  {member.displayName ?? admin('members.unnamed')}
                </a>
              </h2>
              <p>
                {statusLabel(member.status, admin)} ·{' '}
                {member.organizationScoped
                  ? admin('scope.branch_member')
                  : admin('scope.institution_member')}
              </p>
              <p>{member.inCurrentScope ? admin('scope.current') : admin('scope.other')}</p>
            </li>
          ))}
        </ul>
      )}
      <nav aria-label={admin('members.title')}>
        <a href="/studio/members">{admin('members.first')}</a>
        {next && <a href={next}>{admin('members.next')}</a>}
      </nav>
    </>
  );
}

export function MemberDetails({
  snapshot,
  view,
  locale,
  admin,
  profile,
}: {
  readonly snapshot: NativePageSnapshot;
  readonly view: Extract<PageView, { kind: 'member' }>;
  readonly locale: string;
  readonly admin: AdminCopy;
  readonly profile: ProfileCopy;
}) {
  const member = view.member;
  const target = ['membershipId', member.membershipId] as const;
  const active = member.status === 'Active';
  const editableStatus = active || member.status === 'Suspended';
  const canWrite =
    member.inCurrentScope &&
    active &&
    member.canWriteProfile &&
    snapshot.capabilities?.canWriteMemberProfiles;
  const canManage =
    member.inCurrentScope &&
    editableStatus &&
    member.canChangeStatus &&
    snapshot.capabilities?.canManageMemberships;
  const roles = snapshot.capabilities?.canReadRoles ? view.roles : null;
  const assigned = roles?.filter((role) => member.roleIds.includes(role.roleId));
  const choices = roles?.filter(
    (role) => role.capabilityScope === (member.organizationScoped ? 'Organization' : 'Tenant'),
  );
  const canReplace =
    member.inCurrentScope &&
    editableStatus &&
    member.canReplaceRoles &&
    snapshot.capabilities?.canManageMemberships;
  return (
    <>
      <p>
        <a href="/studio/members">{admin('members.back')}</a>
      </p>
      <p className="auth-resource-name">{member.displayName ?? admin('members.unnamed')}</p>
      <p>
        {statusLabel(member.status, admin)} ·{' '}
        {member.organizationScoped
          ? admin('scope.branch_member')
          : admin('scope.institution_member')}
      </p>
      {!member.inCurrentScope && (
        <p className="auth-notice">
          {admin('scope.read_only')} <a href="/api/auth/entry">{admin('navigation.contexts')}</a>
        </p>
      )}
      <section aria-labelledby="member-profile-title">
        <h2 id="member-profile-title">{admin('members.profile')}</h2>
        {canWrite ? (
          <form action="/api/studio/membership-profile" method="post">
            <MutationFields
              snapshot={snapshot}
              index={0}
              version={member.version}
              target={target}
            />
            <ProfileFields
              value={member}
              snapshot={snapshot}
              locale={locale}
              profile={profile}
              prefix="member-profile"
            />
          </form>
        ) : (
          <ProfileValues value={member} locale={locale} profile={profile} />
        )}
      </section>
      <section aria-labelledby="member-roles-title">
        <h2 id="member-roles-title">{admin('members.assignments')}</h2>
        {roles === null ? (
          <p>{admin('members.roles_unavailable')}</p>
        ) : (
          <>
            {member.roleIds.length === 0 && <p>{admin('members.no_roles')}</p>}
            {assigned && assigned.length > 0 && (
              <ul>
                {assigned.map((role) => (
                  <li key={role.roleId}>{roleLabel(role, admin)}</li>
                ))}
              </ul>
            )}
            {assigned?.length !== member.roleIds.length && <p>{admin('members.roles_unknown')}</p>}
            {member.status === 'Suspended' && <p>{admin('members.prepared')}</p>}
            {canReplace && (
              <ul className="auth-actions">
                {choices?.map((role, index) => {
                  const exists = member.roleIds.includes(role.roleId);
                  if (!exists && member.roleIds.length >= 8) return null;
                  const desired = exists
                    ? member.roleIds.filter((id) => id !== role.roleId)
                    : [...member.roleIds, role.roleId];
                  return (
                    <li key={role.roleId}>
                      <form action="/api/studio/membership-roles" method="post">
                        <MutationFields
                          snapshot={snapshot}
                          index={3 + index}
                          version={member.version}
                          target={target}
                        />
                        <button type="submit" name="roleIds" value={JSON.stringify(desired)}>
                          {exists ? admin('members.remove_role') : admin('members.add_role')}:{' '}
                          {roleLabel(role, admin)}
                        </button>
                      </form>
                    </li>
                  );
                })}
              </ul>
            )}
          </>
        )}
      </section>
      {canManage && (
        <section aria-labelledby="member-access-title">
          <h2 id="member-access-title">{admin('members.lifecycle')}</h2>
          <p>{admin('members.suspend_hint')}</p>
          <form action="/api/studio/membership-status" method="post">
            <MutationFields
              snapshot={snapshot}
              index={1}
              version={member.version}
              target={target}
            />
            <button type="submit" name="status" value={active ? 'Suspended' : 'Active'}>
              {active ? admin('members.suspend') : admin('members.reactivate')}
            </button>
          </form>
          <details className="auth-danger">
            <summary>{admin('members.revoke')}</summary>
            <p id="member-revoke-hint">{admin('members.revoke_hint')}</p>
            <form action="/api/studio/membership-status" method="post">
              <MutationFields
                snapshot={snapshot}
                index={2}
                version={member.version}
                target={target}
              />
              <button
                type="submit"
                name="status"
                value="Revoked"
                aria-describedby="member-revoke-hint"
              >
                {admin('members.revoke')}
              </button>
            </form>
          </details>
        </section>
      )}
    </>
  );
}
