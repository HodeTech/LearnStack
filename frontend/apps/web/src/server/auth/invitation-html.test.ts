import { readFileSync } from 'node:fs';
import { runInNewContext } from 'node:vm';

import { describe, expect, it, vi } from 'vitest';

import { invitationKeys, prepareInvitationCatalogues } from './invitation-copy';
import {
  createInvitationRenderer,
  INVITATION_CONFIRM_SCRIPT,
  INVITATION_LANDING_SCRIPT,
} from './invitation-html';
import type { InvitationInspection, InvitationPage } from './native-invitation-contract';
import en from '../../i18n/messages/en/invitation.json';
import tr from '../../i18n/messages/tr/invitation.json';
import { checkCatalogues, publicCallsiteKeys } from '../../test/ui-catalogue-checks';

const csrfToken = 'c'.repeat(43);
const render = createInvitationRenderer({ en, tr });
const inspection: InvitationInspection = {
  invitationId: '019a154b-0000-7000-8000-000000000031',
  version: '9007199254740993',
  expiresAt: '2026-11-20T15:30:00.0000000+00:00',
  tenantLabel: 'School <script>no()</script>',
  organizationLabel: 'Branch & center',
  roleId: '019a154b-0000-7000-8000-000000000032',
  roleKey: 'Learner',
  roleScope: 'Organization',
  verifiedEmail: 'recipient+full@example.test',
};
const page = (
  value: Partial<Extract<InvitationPage, { kind: 'confirm' }>> = {},
): InvitationPage => ({
  kind: 'confirm',
  locale: 'en',
  csrfToken,
  state: 'inspect',
  ...value,
});
const parse = (html: string) => new DOMParser().parseFromString(html, 'text/html');

describe('native invitation documents and complete authored copy', () => {
  it('checks the actual typed callsites, equal en/tr keys and ICU grammar', () => {
    const source = readFileSync('src/server/auth/invitation-html.ts', 'utf8');
    const used = publicCallsiteKeys(source, 'invitation-html.ts', 'invitation');
    expect(used.length).toBeGreaterThan(30);
    checkCatalogues([en, tr], used);
    expect(new Set(invitationKeys).size).toBe(invitationKeys.length);
    const select = prepareInvitationCatalogues({ en, tr });
    expect(select('tr-Latn-TR').invitation('common.title')).toBe(tr.common.title);
    expect(select('ar').invitation('common.title')).toBe(en.common.title);
    const incomplete = structuredClone(tr);
    delete (incomplete.confirm as Partial<typeof incomplete.confirm>).already_member;
    expect(() => createInvitationRenderer({ en, tr: incomplete })).toThrow(
      'Invitation catalogue unavailable',
    );
    expect(() => checkCatalogues([en, tr], [...used, 'confirm.misspelled'])).toThrow('Used UI key');
    expect(() =>
      publicCallsiteKeys('invitation(problem.messageKey)', 'dirty.ts', 'invitation'),
    ).toThrow('literal');
  });

  it('renders a clean code-paste form without scripts being required and places scrub before visible UI', () => {
    const html = render({ kind: 'landing', locale: 'tr-TR', csrfToken });
    const document = parse(html);
    expect(document.querySelector('script')?.textContent).toBe(INVITATION_LANDING_SCRIPT);
    expect(html.indexOf('<script>')).toBeLessThan(html.indexOf('<title'));
    expect(html.indexOf('<script>')).toBeLessThan(html.indexOf('<style>'));
    expect(document.querySelector('form')?.getAttribute('action')).toBe(
      '/api/auth/invitation/capture',
    );
    expect(document.querySelector('form')?.method).toBe('post');
    expect(document.querySelector('input[name=code]')?.getAttribute('value')).toBeNull();
    expect(document.querySelector('input[name=code]')?.getAttribute('autocomplete')).toBe('off');
    expect(document.querySelector('noscript')?.textContent).toContain('/invitations');
    expect(document.querySelector('main')?.lang).toBe('tr');
    expect(document.documentElement.lang).toBe('tr-TR');
    expect(document.querySelector('[aria-describedby]')?.getAttribute('aria-describedby')).toBe(
      'invitation-invalid',
    );
    expect(
      document.querySelector('script[src],link[rel=preload],link[rel=prefetch],img,iframe'),
    ).toBeNull();
  });

  it('shows full verified email inside bidi isolation and submits only the observed version', () => {
    const html = render(page({ state: 'ready', inspection }));
    const document = parse(html);
    expect(document.querySelector('bdi')?.textContent).toBe(inspection.verifiedEmail);
    expect(document.body.textContent).toContain(inspection.tenantLabel);
    expect(document.body.querySelector('script')).toBeNull();
    expect(
      document
        .querySelector('form[action="/api/auth/invitation/accept"] input[name=version]')
        ?.getAttribute('value'),
    ).toBe(inspection.version);
    expect(
      document.querySelectorAll(
        'input[name=code],input[name=invitationId],input[name=tenantId],input[name=organizationId]',
      ),
    ).toHaveLength(0);
    expect(document.body.textContent).toContain(en.confirm.offered_role);
    expect(html).not.toContain(inspection.roleId);
  });

  it('keeps login and bootstrap on their existing explicit native routes with separate proofs', () => {
    const signin = parse(render(page({ state: 'signin', signinCsrfToken: 's'.repeat(43) })));
    const form = signin.querySelector('form[action="/api/auth/signin/keycloak"]');
    expect(form?.querySelector('input[name=csrfToken]')?.getAttribute('value')).toBe(
      's'.repeat(43),
    );
    expect(form?.querySelector('input[name=returnTo]')?.getAttribute('value')).toBe('/');
    expect(form?.querySelector('input[name=invitation]')?.getAttribute('value')).toBe('continue');
    const bootstrap = parse(render(page({ state: 'bootstrap', entryCsrfToken: 'e'.repeat(43) })));
    const create = bootstrap.querySelector('form[action="/api/auth/entry/create"]');
    expect(create?.querySelectorAll('input')).toHaveLength(1);
    expect(create?.querySelector('input')?.getAttribute('value')).toBe('e'.repeat(43));
  });

  it('never presents an accept form or expected mailbox on a forbidden or unknown outcome', () => {
    for (const state of ['forbidden', 'unknown', 'account_change'] as const) {
      const document = parse(render(page({ state, inspection, sessionCsrfToken: 's'.repeat(43) })));
      expect(document.querySelector('form[action="/api/auth/invitation/accept"]')).toBeNull();
      expect(document.body.textContent).not.toContain(inspection.verifiedEmail);
      expect(document.body.textContent).not.toContain(inspection.tenantLabel);
      if (state === 'unknown') {
        expect(
          document.querySelector('form[action="/api/auth/invitation/inspect"]'),
        ).not.toBeNull();
        expect(document.querySelector('form[action="/api/auth/invitation/result"]')).not.toBeNull();
      }
      if (state === 'account_change')
        expect(
          document.querySelector('form[action="/api/auth/signout"] input')?.getAttribute('value'),
        ).toBe('s'.repeat(43));
    }
  });

  it('separates accepted membership from session publication and explains already-member truthfully', () => {
    const document = parse(
      render(
        page({
          locale: 'tr',
          state: 'accepted',
          disposition: 'already_member',
          sessionAvailable: false,
        }),
      ),
    );
    expect(document.body.textContent).toContain(tr.confirm.already_member);
    expect(document.body.textContent).toContain(tr.confirm.session_unavailable);
    expect(document.body.textContent).not.toContain(tr.confirm.session_ready);
    expect(document.querySelector('form[action="/api/auth/invitation/result"]')).not.toBeNull();
    expect(document.querySelector('form[action="/api/auth/invitation/accept"]')).toBeNull();
    const unavailable = parse(render(page({ state: 'accepted' })));
    expect(unavailable.body.textContent).not.toContain(en.confirm.accepted);
    expect(unavailable.body.textContent).toContain(en.failure.unavailable);
  });
});

function scriptFixture(hash: string, source = INVITATION_LANDING_SCRIPT) {
  const events = new Map<string, (event?: unknown) => void>();
  const order: string[] = [];
  const input = { value: '', setAttribute: vi.fn(), focus: vi.fn() };
  const invalid = { hidden: true };
  const root = { style: { visibility: '' } };
  const submitted: string[] = [];
  const location = { hash, replace: vi.fn((path: string) => order.push('navigate:' + path)) };
  const form = {};
  runInNewContext(source, {
    location,
    history: {
      replaceState: (state: unknown, title: string, path: string) => {
        expect(state).toBeNull();
        expect(title).toBe('');
        order.push('scrub:' + path);
        location.hash = '';
      },
    },
    document: {
      documentElement: root,
      getElementById: (id: string) => {
        order.push('dom:' + id);
        return id === 'invitation-code' ? input : id === 'invitation-invalid' ? invalid : form;
      },
    },
    addEventListener: (name: string, listener: (event?: unknown) => void) => {
      events.set(name, listener);
    },
    HTMLFormElement: {
      prototype: {
        submit() {
          order.push('submit');
          submitted.push(input.value);
        },
      },
    },
  });
  return { order, input, invalid, location, submitted, events, root };
}

describe('actual inline invitation fragment and history source', () => {
  const code = inspection.invitationId + '.' + Buffer.alloc(32, 7).toString('base64url');
  it('scrubs before all DOM work and hands off only one canonical bounded code, then clears the input', () => {
    const fixture = scriptFixture('#' + code);
    expect(fixture.order).toEqual(['scrub:/invitations']);
    expect(fixture.location.hash).toBe('');
    fixture.events.get('DOMContentLoaded')!();
    expect(fixture.submitted).toEqual([code]);
    expect(fixture.input.value).toBe('');
    expect(fixture.invalid.hidden).toBe(true);
  });

  it.each(['#malformed-private-fragment', '#' + code.slice(0, -1) + 'B', '#' + 'x'.repeat(4096)])(
    'scrubs malformed input before reporting and never submits it',
    (hash) => {
      const fixture = scriptFixture(hash);
      expect(fixture.order[0]).toBe('scrub:/invitations');
      fixture.events.get('DOMContentLoaded')!();
      expect(fixture.submitted).toEqual([]);
      expect(fixture.input.value).toBe('');
      expect(fixture.invalid.hidden).toBe(false);
      expect(fixture.input.setAttribute).toHaveBeenCalledWith('aria-invalid', 'true');
    },
  );

  it('leaves clean landing passive and uses a fixed GET for every restored confirmation document', () => {
    const clean = scriptFixture('');
    clean.events.get('DOMContentLoaded')!();
    expect(clean.submitted).toEqual([]);
    const confirmation = scriptFixture('', INVITATION_CONFIRM_SCRIPT);
    confirmation.events.get('pagehide')!();
    expect(confirmation.root.style.visibility).toBe('hidden');
    confirmation.events.get('pageshow')!({ persisted: true });
    expect(confirmation.location.replace).toHaveBeenCalledWith('/invitations/confirm');
    expect(confirmation.submitted).toEqual([]);
  });
});
