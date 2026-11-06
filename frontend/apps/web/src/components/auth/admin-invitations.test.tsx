import { renderToStaticMarkup } from 'react-dom/server';
import { describe, expect, it, vi } from 'vitest';

import { PreparedAccountPage } from './shell';
import { bffAdministrationForm } from '../../server/auth/http-forms';
import type { NativePageSnapshot, PageInvitation, PageView } from '../../server/auth/page-contract';
import { administrationSnapshot, uiRole } from '../../test/administration-ui-fixture';

vi.mock('@/server/auth/page', () => ({ optionalPreparedPage: vi.fn() }));
vi.mock('@/client/bff-session', () => ({ extendBffSession: vi.fn() }));

const invitation: PageInvitation = {
  invitationId: '019a154b-0000-7000-8000-000000000044',
  email: 'person@example.test',
  organizationScoped: false,
  inCurrentScope: true,
  offeredRole: uiRole,
  roleVersion: uiRole.version,
  version: '9223372036854775807',
  status: 'Pending',
  expiresAt: '2026-11-18T12:00:00.0000000+00:00',
  deliveryStatus: 'Queued',
  acceptanceDisposition: null,
  canReissue: true,
  canRevoke: true,
};
function documentFor(view: PageView, overrides: Partial<NativePageSnapshot> = {}) {
  const snapshot = administrationSnapshot(view, overrides);
  return new DOMParser().parseFromString(
    renderToStaticMarkup(<PreparedAccountPage snapshot={snapshot} kind={view.kind} />),
    'text/html',
  );
}
function adminForms(document: Document) {
  return [...document.querySelectorAll<HTMLFormElement>('form')].filter((form) =>
    form.action.includes('/api/studio/'),
  );
}
function formFields(form: HTMLFormElement) {
  const fields = new URLSearchParams();
  for (const [key, value] of new FormData(form)) fields.append(key, String(value));
  return fields;
}

describe('prepared invitation administration', () => {
  it.each(['en', 'tr'])(
    'submits a lossless pinned choice without JavaScript or Role-directory authority (%s)',
    (locale) => {
      const view = {
        kind: 'invitations',
        items: [],
        roles: [uiRole],
        endCursor: null,
        hasNextPage: false,
        limit: 20,
      } as const;
      const base = administrationSnapshot(view);
      const document = documentFor(view, {
        capabilities: { ...base.capabilities!, canReadRoles: false },
        ownProfile: { displayName: null, preferredLocale: locale, version: '1' },
      });
      const form = document.querySelector<HTMLFormElement>(
        'form[action="/api/studio/invitation-create"]',
      )!;
      expect(form).not.toBeNull();
      expect(form.checkValidity()).toBe(false);
      form.querySelector<HTMLInputElement>('[name="email"]')!.value = 'New.person@example.test';
      form.querySelector<HTMLSelectElement>('[name="offeredRole"]')!.value =
        uiRole.roleId + '.' + uiRole.version;
      expect(form.checkValidity()).toBe(true);
      expect(form.method).toBe('post');
      const fields = formFields(form);
      expect([...fields.keys()]).toEqual(['csrfToken', 'idempotencyKey', 'email', 'offeredRole']);
      expect(bffAdministrationForm('/api/studio/invitation-create', fields)).toMatchObject({
        operation: {
          kind: 'createInvitation',
          invitation: {
            email: 'New.person@example.test',
            roleId: uiRole.roleId,
            roleVersion: uiRole.version,
          },
        },
      });
      expect(document.querySelector('section')?.lang).toBe(locale);
      expect(document.querySelector('a[href="/studio/roles"]')).toBeNull();
      expect(document.querySelector('script')).toBeNull();
      expect(document.body.textContent).not.toContain(uiRole.roleId);
    },
  );

  it.each(['en', 'tr'])(
    'marks required invitation and policy controls visibly and semantically in %s',
    (locale) => {
      const documents = [
        documentFor(
          {
            kind: 'invitations',
            items: [],
            roles: [uiRole],
            endCursor: null,
            hasNextPage: false,
            limit: 20,
          },
          { ownProfile: { displayName: null, preferredLocale: locale, version: '1' } },
        ),
        documentFor(
          {
            kind: 'invitation_policy',
            policy: { days: 14, present: false, version: null, canEdit: true },
          },
          { ownProfile: { displayName: null, preferredLocale: locale, version: '1' } },
        ),
      ];
      const controls = documents.flatMap((document) => [
        ...document.querySelectorAll<HTMLInputElement | HTMLSelectElement>('[required]'),
      ]);
      expect(controls.map((control) => control.name)).toEqual(['email', 'offeredRole', 'days']);
      for (const control of controls) {
        expect(control.getAttribute('aria-required')).toBe('true');
        expect(control.labels?.[0]?.textContent).toContain(
          locale === 'en' ? 'Required' : 'Zorunlu',
        );
      }
    },
  );
  it('renders separate lifecycle and submission facts with safe mailbox isolation and issued pagination', () => {
    const document = documentFor({
      kind: 'invitations',
      items: [
        {
          ...invitation,
          email: '<recipient>@example.test',
          status: 'Expired',
          deliveryStatus: 'SubmissionAccepted',
        },
      ],
      roles: [],
      endCursor: 'YQ',
      hasNextPage: true,
      limit: 20,
    });
    expect(document.querySelector('bdi')?.textContent).toBe('<recipient>@example.test');
    expect(document.querySelector('recipient')).toBeNull();
    expect(document.body.textContent).toContain('Expired');
    expect(document.body.textContent).toContain('Submission accepted by the mail server');
    expect(
      document.querySelector('a[href="/studio/invitations?cursor=YQ&limit=20"]'),
    ).not.toBeNull();
    expect(adminForms(document)).toHaveLength(0);
  });

  it('shows the offered role and truthful already-member result with no membership navigation or mutations', () => {
    const value = {
      ...invitation,
      status: 'Accepted',
      acceptanceDisposition: 'already_member',
      canReissue: false,
      canRevoke: false,
    } as const;
    const document = documentFor({ kind: 'invitation', invitation: value });
    expect(document.body.textContent).toContain('Offered role');
    expect(document.body.textContent).toContain(
      'Their existing roles and profile were preserved; the offered role was not added.',
    );
    expect(adminForms(document)).toHaveLength(0);
    expect(document.querySelector('a[href^="/studio/members/"]')).toBeNull();
  });

  it('uses independent keys for explicit replacement and revocation and explains uncertain old delivery', () => {
    const document = documentFor({
      kind: 'invitation',
      invitation: { ...invitation, deliveryStatus: 'Unresolved' },
    });
    const forms = adminForms(document);
    expect(forms).toHaveLength(2);
    const keys = forms.map((form) => formFields(form).get('idempotencyKey'));
    expect(new Set(keys).size).toBe(2);
    for (const form of forms) {
      expect(formFields(form).get('version')).toBe(invitation.version);
      expect(formFields(form).get('invitationId')).toBe(invitation.invitationId);
      expect(
        bffAdministrationForm(form.getAttribute('action')!, formFields(form)).destination,
      ).toBe('/studio/invitations/' + invitation.invitationId);
    }
    expect(document.body.textContent).toContain(
      'Unfinished earlier email work retains its delivery slot.',
    );
    expect(document.body.textContent).toContain('not a confirmed failure');
    expect(document.body.textContent).toContain('does not confirm inbox delivery');
    expect(document.querySelector('a[href*="#"]')).toBeNull();
  });

  it('preserves an unavailable offered-role label without granting a new pin or reissue', () => {
    const document = documentFor({
      kind: 'invitation',
      invitation: { ...invitation, offeredRole: null, canReissue: false },
    });
    expect(document.body.textContent).toContain('Offered role label is no longer available');
    expect(document.body.textContent).toContain('Revoke this offer and create a new invitation');
    expect(document.querySelector('form[action="/api/studio/invitation-reissue"]')).toBeNull();
    expect(document.querySelector('form[action="/api/studio/invitation-revoke"]')).not.toBeNull();
  });

  it.each([false, true])(
    'keeps absent policy separate from stored version zero (present=%s)',
    (present) => {
      const view = {
        kind: 'invitation_policy',
        policy: { days: 14, present, version: present ? '0' : null, canEdit: true },
      } as const;
      const document = documentFor(view);
      const form = adminForms(document)[0]!;
      const fields = formFields(form);
      expect(fields.has('version')).toBe(present);
      expect(bffAdministrationForm('/api/studio/invitation-policy', fields)).toMatchObject({
        operation: {
          kind: 'setInvitationLifetime',
          days: 14,
          precondition: present ? { kind: 'present', version: '0' } : { kind: 'absent' },
        },
      });
      expect(document.body.textContent).toContain(
        present ? 'saved lifetime policy' : 'default lifetime is 14 days',
      );
      form.querySelector<HTMLInputElement>('[name="days"]')!.value = '31';
      expect(form.checkValidity()).toBe(false);
    },
  );

  it('keeps policy editing separate from member capability and prevents branch editing', () => {
    const view = {
      kind: 'invitation_policy',
      policy: { days: 14, present: false, version: null, canEdit: true },
    } as const;
    const base = administrationSnapshot(view);
    expect(
      adminForms(
        documentFor(view, { capabilities: { ...base.capabilities!, canManageMemberships: false } }),
      ),
    ).toHaveLength(1);
    expect(adminForms(documentFor(view, { scopeKind: 'organization' }))).toHaveLength(0);
    expect(
      adminForms(documentFor({ ...view, policy: { ...view.policy, canEdit: false } })),
    ).toHaveLength(0);
    expect(
      documentFor(view, { scopeKind: 'organization' }).querySelector(
        'a[href="/studio/invitation-policy"]',
      ),
    ).toBeNull();
  });
});
