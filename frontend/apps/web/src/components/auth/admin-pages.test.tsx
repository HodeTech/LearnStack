import { fireEvent, render, screen, within } from '@testing-library/react';
import { renderToStaticMarkup } from 'react-dom/server';
import { describe, expect, it, vi } from 'vitest';

import { PreparedAccountPage } from './shell';
import type { NativePageSnapshot, PageView } from '../../server/auth/page-contract';
import {
  administrationSnapshot,
  uiMember,
  uiRole,
  uiOtherRole,
  uiRoleId,
  uiOtherRoleId,
} from '../../test/administration-ui-fixture';

vi.mock('@/server/auth/page', () => ({ optionalPreparedPage: vi.fn() }));
vi.mock('@/client/bff-session', () => ({ extendBffSession: vi.fn() }));

function documentFor(view: PageView, overrides: Partial<NativePageSnapshot> = {}) {
  const snapshot = administrationSnapshot(view, overrides);
  const html = renderToStaticMarkup(<PreparedAccountPage snapshot={snapshot} kind={view.kind} />);
  return { snapshot, document: new DOMParser().parseFromString(html, 'text/html') };
}
function mutationForms(document: Document) {
  return [...document.querySelectorAll<HTMLFormElement>('form')].filter(
    (form) => form.action.includes('/api/studio/') || form.action.includes('/api/account/'),
  );
}
const roleView = (): Extract<PageView, { kind: 'role' }> => ({
  kind: 'role',
  role: {
    ...uiRole,
    grants: [
      { scope: 'Tenant', key: 'identity.membership.admin' },
      { scope: 'Tenant', key: 'identity.role.admin' },
      { scope: 'Tenant', key: 'identity.membership_profile.write' },
    ],
    adoptedReleaseVersion: 1,
    adoptedReleaseFingerprint: 'a'.repeat(64),
  },
  permissions: [
    {
      scope: 'Tenant',
      key: 'identity.membership.admin',
      labelKey: 'lockey_permission_tenant_identity_membership_admin',
      canGrant: true,
    },
    {
      scope: 'Tenant',
      key: 'identity.role.admin',
      labelKey: 'lockey_permission_tenant_identity_role_admin',
      canGrant: true,
    },
    {
      scope: 'Tenant',
      key: 'identity.membership_profile.write',
      labelKey: 'lockey_permission_tenant_identity_membership_profile_write',
      canGrant: false,
    },
    {
      scope: 'Tenant',
      key: 'tenancy.locale.write',
      labelKey: 'lockey_permission_tenant_tenancy_locale_write',
      canGrant: true,
    },
    {
      scope: 'Tenant',
      key: 'tenancy.setting.write',
      labelKey: 'lockey_permission_tenant_tenancy_setting_write',
      canGrant: false,
    },
  ],
  release: {
    version: 2,
    fingerprint: 'b'.repeat(64),
    tenantAdminDelta: [{ scope: 'Tenant', key: 'identity.membership_profile.write' }],
  },
  canChangeGrants: true,
  canAdoptRelease: true,
});

describe('prepared administration views', () => {
  it.each(['en', 'tr'])(
    'renders profile and both shells from the current exact preference in %s',
    (locale) => {
      for (const kind of ['studio', 'portal', 'profile'] as const) {
        const view = kind === 'profile' ? { kind, canWrite: true } : { kind };
        const { document } = documentFor(view, {
          ownProfile: { displayName: 'Mine', preferredLocale: locale, version: '9007199254740995' },
        });
        expect(document.querySelector('section')?.lang).toBe(locale);
        expect(document.querySelector('form[action="/api/auth/signout"]')).not.toBeNull();
        expect(document.querySelector('a[href="/api/auth/entry"]')).not.toBeNull();
        expect(document.querySelector('noscript')?.textContent).toContain('JavaScript');
        expect(document.querySelector('input[name="version"]')?.getAttribute('value')).toBe(
          kind === 'profile' ? '9007199254740995' : undefined,
        );
      }
    },
  );
  it('uses site fallback for missing/disabled preference and never uses the viewed member preference', () => {
    const view = {
      kind: 'member',
      member: { ...uiMember, preferredLocale: 'tr' },
      roles: [uiRole],
    } as const;
    for (const ownProfile of [
      null,
      { displayName: null, preferredLocale: 'disabled', version: '1' },
    ]) {
      const { document } = documentFor(view, {
        ownProfile,
        profileState: ownProfile ? 'available' : 'missing',
      });
      expect(document.querySelector('section')?.lang).toBe('en');
      expect(document.querySelector('h1')?.textContent).toBe('Member details');
    }
  });
  it('keeps an exact-profile absence usable and profile read-only without the write flag', () => {
    const absent = documentFor(
      { kind: 'profile', canWrite: true },
      { ownProfile: null, profileState: 'missing' },
    ).document;
    expect(absent.body.textContent).toContain('No exact membership profile');
    expect(mutationForms(absent)).toHaveLength(0);
    const base = administrationSnapshot({ kind: 'profile', canWrite: true });
    const readonly = documentFor(
      { kind: 'profile', canWrite: true },
      { capabilities: { ...base.capabilities!, canWriteOwnProfile: false } },
    ).document;
    expect(readonly.body.textContent).toContain('do not currently have permission');
    expect(mutationForms(readonly)).toHaveLength(0);
    expect(readonly.querySelector('a[href="/studio/members"]')).not.toBeNull();
  });
  it('posts both profile keys and an exact version using a native form with an explicit default option', () => {
    const { document, snapshot } = documentFor({ kind: 'profile', canWrite: true });
    const form = document.querySelector<HTMLFormElement>('form[action="/api/account/profile"]')!;
    expect(form.method).toBe('post');
    const fields = new FormData(form);
    expect([...fields.keys()]).toEqual([
      'csrfToken',
      'version',
      'idempotencyKey',
      'displayName',
      'preferredLocale',
    ]);
    expect(fields.get('version')).toBe('9007199254740995');
    expect(fields.get('idempotencyKey')).toBe(snapshot.formKeys[0]);
    expect(form.querySelector('select option[value=""]')?.textContent).toContain(
      'default language',
    );
    expect(form.querySelector('input[name="displayName"]')?.getAttribute('maxlength')).toBe('128');
  });
  it.each(['en', 'tr'] as const)(
    'requires an explicit replacement for an unavailable preference in both profile forms (%s)',
    (locale) => {
      for (const kind of ['profile', 'member'] as const) {
        const view =
          kind === 'profile'
            ? { kind, canWrite: true }
            : { kind, member: { ...uiMember, preferredLocale: 'fr' }, roles: [uiRole] };
        const ownProfile = {
          displayName: 'Mine',
          preferredLocale: kind === 'profile' ? 'fr' : locale,
          version: '1',
        };
        const base = administrationSnapshot(view);
        const overrides = { ownProfile, site: { ...base.site, defaultLocale: locale } };
        const { document } = documentFor(view, overrides);
        const action =
          kind === 'profile' ? '/api/account/profile' : '/api/studio/membership-profile';
        const form = document.querySelector<HTMLFormElement>(`form[action="${action}"]`)!;
        const name = form.querySelector<HTMLInputElement>('input[name="displayName"]')!;
        name.value = 'Changed name';
        const radios = [
          ...form.querySelectorAll<HTMLInputElement>('input[name="preferredLocale"]'),
        ];
        expect(radios.every((radio) => !radio.checked)).toBe(true);
        expect(form.checkValidity()).toBe(false);
        expect(new FormData(form).has('preferredLocale')).toBe(false);
        expect(form.querySelector('fieldset legend')?.textContent).toBe(
          locale === 'en' ? 'Preferred language' : 'Tercih edilen dil',
        );
        expect(form.textContent).toContain(
          locale === 'en' ? 'Choosing the default removes' : 'Varsayılanı seçmek',
        );
        for (const choice of ['', 'tr']) {
          radios.find((radio) => radio.value === choice)!.checked = true;
          expect(form.checkValidity()).toBe(true);
          expect(new FormData(form).get('preferredLocale')).toBe(choice);
        }
        const restored = documentFor(view, {
          ...overrides,
          site: { ...overrides.site, enabledLocales: ['en', 'tr', 'fr'] },
        }).document.querySelector<HTMLFormElement>(`form[action="${action}"]`)!;
        expect(new FormData(restored).get('preferredLocale')).toBe('fr');
        expect(restored.checkValidity()).toBe(true);
      }
    },
  );
  it('keeps tenant-wide member disclosure read-only outside exact write scope', () => {
    const { document } = documentFor({
      kind: 'member',
      member: { ...uiMember, inCurrentScope: false },
      roles: [uiRole, uiOtherRole],
    });
    expect(document.body.textContent).toContain('outside your current editing context');
    expect(mutationForms(document)).toHaveLength(0);
    expect(document.body.textContent).not.toContain(uiMember.membershipId);
    expect(document.querySelector('a[href="/api/auth/entry"]')).not.toBeNull();
  });
  it('does not show role choices merely because member management is allowed', () => {
    const view = { kind: 'member', member: uiMember, roles: [uiRole, uiOtherRole] } as const;
    const base = administrationSnapshot(view);
    const { document } = documentFor(view, {
      capabilities: { ...base.capabilities!, canReadRoles: false },
    });
    expect(document.body.textContent).toContain('Role choices are not available');
    expect(document.querySelector('form[action="/api/studio/membership-roles"]')).toBeNull();
    expect(document.querySelector('form[action="/api/studio/membership-status"]')).not.toBeNull();
    expect(document.body.textContent).not.toContain(uiRoleId);
  });
  it('uses independent keys and complete desired arrays in native role Add/Remove buttons', () => {
    const { document } = documentFor({
      kind: 'member',
      member: uiMember,
      roles: [uiRole, uiOtherRole],
    });
    const forms = mutationForms(document);
    const keys = forms.map((form) => new FormData(form).get('idempotencyKey'));
    expect(new Set(keys).size).toBe(keys.length);
    for (const form of forms) expect(new FormData(form).get('version')).toBe('9223372036854775807');
    const add = document.querySelector<HTMLButtonElement>(
      'button[name="roleIds"][value*="' + uiOtherRoleId + '"]',
    )!;
    expect(add.textContent).toContain('Add role: Institution instructor');
    expect(JSON.parse(add.value)).toEqual([uiRoleId, uiOtherRoleId]);
    const remove = [...document.querySelectorAll<HTMLButtonElement>('button[name="roleIds"]')].find(
      (button) => button.textContent?.includes('Remove role'),
    )!;
    expect(JSON.parse(remove.value)).toEqual([]);
    expect(document.querySelector('script')).toBeNull();
    expect(document.body.textContent).not.toContain(uiRoleId);
  });
  it('preserves unknown existing assignments rather than silently dropping them on addition', () => {
    const unseen = '019a154b-0000-7000-8000-000000000099';
    const { document } = documentFor({
      kind: 'member',
      member: { ...uiMember, roleIds: [unseen] },
      roles: [uiOtherRole],
    });
    expect(document.body.textContent).toContain('Some existing role labels are unavailable');
    expect(
      JSON.parse(document.querySelector<HTMLButtonElement>('button[name="roleIds"]')!.value),
    ).toEqual([unseen, uiOtherRoleId]);
    expect(document.body.textContent).not.toContain(unseen);
  });
  it('allows suspended assignment preparation but no profile editing and no revoked mutations', () => {
    const suspended = documentFor({
      kind: 'member',
      member: { ...uiMember, status: 'Suspended' },
      roles: [uiRole],
    }).document;
    expect(suspended.querySelector('form[action="/api/studio/membership-profile"]')).toBeNull();
    expect(suspended.querySelector('button[value="Active"]')?.textContent).toBe(
      'Reactivate membership',
    );
    expect(suspended.querySelector('button[name="roleIds"]')).not.toBeNull();
    const revoked = documentFor({
      kind: 'member',
      member: { ...uiMember, status: 'Revoked' },
      roles: [uiRole],
    }).document;
    expect(mutationForms(revoked)).toHaveLength(0);
  });
  it('paginates with the issued cursor and admitted limit without adding search or IDs to copy', () => {
    const { document } = documentFor({
      kind: 'members',
      items: [uiMember],
      endCursor: 'YQ',
      hasNextPage: true,
      limit: 20,
    });
    expect(document.querySelector('a[href="/studio/members?cursor=YQ&limit=20"]')).not.toBeNull();
    expect(document.body.textContent).toContain('<Member>');
    expect(document.body.textContent).not.toContain(uiMember.membershipId);
    expect(document.querySelector('input[type="search"]')).toBeNull();
  });
  it.each(['denied', 'not_found', 'invalid_cursor', 'unavailable', 'busy'] as const)(
    'renders fixed %s recovery without mutation forms',
    (reason) => {
      const { document } = documentFor(
        { kind: 'members', items: [], endCursor: null, hasNextPage: false, limit: 20 },
        {
          outcome: {
            kind: 'failure',
            reason,
            status: reason === 'busy' ? 429 : 503,
            retryAfterSeconds: reason === 'busy' ? 37 : null,
          },
        },
      );
      expect(document.querySelector('[role="alert"]')).not.toBeNull();
      expect(mutationForms(document)).toHaveLength(0);
      expect(document.querySelector('a[href="/studio/members"]')).not.toBeNull();
      if (reason === 'busy') expect(document.body.textContent).toContain('37');
    },
  );
  it('keeps native profile fields labeled and keyboard editable without client submission logic', () => {
    render(
      <PreparedAccountPage
        snapshot={administrationSnapshot({ kind: 'profile', canWrite: true })}
        kind="profile"
      />,
    );
    const input = screen.getByRole('textbox', { name: 'Display name' });
    input.focus();
    fireEvent.change(input, { target: { value: 'Edited with keyboard' } });
    expect(input).toHaveFocus();
    expect(input).toHaveValue('Edited with keyboard');
    const form = screen.getByRole('button', { name: 'Save profile' }).closest('form')!;
    expect(form).toHaveAttribute('method', 'post');
    expect(within(form).getByRole('combobox', { name: 'Preferred language' })).toBeEnabled();
  });
  it('locks both TenantAdmin kernel grants, permits removal without canGrant and preserves the entire desired graph', () => {
    const view = roleView();
    const { document } = documentFor(view);
    const buttons = [...document.querySelectorAll<HTMLButtonElement>('button[name="grants"]')];
    expect(buttons).toHaveLength(2);
    const remove = buttons.find((button) => button.textContent?.includes('Remove permission'))!;
    const add = buttons.find((button) => button.textContent?.includes('Add permission'))!;
    expect(remove.textContent).toContain('Edit institution membership profiles');
    expect(JSON.parse(remove.value)).toEqual(view.role.grants.slice(0, 2));
    expect(JSON.parse(add.value)).toEqual([
      ...view.role.grants,
      { scope: 'Tenant', key: 'tenancy.locale.write' },
    ]);
    expect(document.body.textContent?.match(/Required administrator permission/g)).toHaveLength(2);
    expect(
      buttons.some((button) => button.textContent?.includes('Manage institution appearance')),
    ).toBe(false);
    for (const form of mutationForms(document))
      expect(new FormData(form).get('version')).toBe('9007199254740993');
    const keys = mutationForms(document).map((form) => new FormData(form).get('idempotencyKey'));
    expect(new Set(keys).size).toBe(keys.length);
    expect(document.body.textContent).not.toContain(uiRoleId);
    expect(document.body.textContent).not.toContain('identity.membership.admin');
    expect(document.body.textContent).not.toContain('lockey_');
  });
  it('keeps branch-context Role views readable with no grant or release mutation', () => {
    const { document } = documentFor(roleView(), { scopeKind: 'organization' });
    expect(document.body.textContent).toContain('Institution administrator');
    expect(document.body.textContent).toContain('eligible institution-wide access');
    expect(mutationForms(document)).toHaveLength(0);
  });
  it('shows release adoption as a separate native mutation with exact version and hidden release fingerprint', () => {
    const view = roleView();
    const { document, snapshot } = documentFor(view);
    const form = document.querySelector<HTMLFormElement>(
      'form[action="/api/studio/role-manifest-adoption"]',
    )!;
    expect([...new FormData(form).entries()]).toEqual([
      ['csrfToken', snapshot.session.csrfToken],
      ['version', view.role.version],
      ['idempotencyKey', snapshot.formKeys[8]],
      ['roleId', uiRoleId],
      ['manifestFingerprint', view.release.fingerprint],
    ]);
    expect(form.querySelector('button')?.getAttribute('name')).toBe('manifestVersion');
    expect(form.querySelector('button')?.getAttribute('value')).toBe('2');
    expect(document.body.textContent).not.toContain(view.release.fingerprint);
    for (const changed of [
      { ...view, canAdoptRelease: false },
      { ...view, role: { ...view.role, adoptedReleaseVersion: 2 } },
      { ...view, role: { ...view.role, builtInKey: 'Instructor' as const } },
    ])
      expect(
        documentFor(changed).document.querySelector(
          'form[action="/api/studio/role-manifest-adoption"]',
        ),
      ).toBeNull();
  });
  it.each(['en', 'tr'])(
    'lists all eight fixed Role templates using human labels in %s',
    (locale) => {
      const roles = [
        ['TenantAdmin', 'Tenant'],
        ['OrgAdmin', 'Organization'],
        ['Instructor', 'Tenant'],
        ['OrgInstructor', 'Organization'],
        ['Learner', 'Tenant'],
        ['Learner', 'Organization'],
        ['Guardian', 'Tenant'],
        ['Editor', 'Tenant'],
      ].map(([builtInKey, capabilityScope], index) => ({
        ...uiRole,
        roleId: uiRoleId.slice(0, -2) + String(index).padStart(2, '0'),
        builtInKey,
        capabilityScope,
      })) as Extract<PageView, { kind: 'roles' }>['roles'];
      const { document } = documentFor(
        { kind: 'roles', roles },
        { ownProfile: { displayName: null, preferredLocale: locale, version: '1' } },
      );
      const labels = [...document.querySelectorAll('.auth-resource-list h2')].map(
        (heading) => heading.textContent,
      );
      expect(labels).toHaveLength(8);
      expect(new Set(labels).size).toBe(8);
      for (const role of roles) expect(document.body.textContent).not.toContain(role.roleId);
      expect(mutationForms(document)).toHaveLength(0);
    },
  );
});
