// @vitest-environment node
import { describe, expect, it } from 'vitest';

import { bffAdministrationForm, BffInvitationInputError } from './http-forms';

const id = '019a154b-0000-7000-8000-000000000022';
const version = '9223372036854775807';
const key = 'exact-invitation-key';
const common = { csrfToken: 'A'.repeat(43), idempotencyKey: key };
const create = { ...common, email: 'Person@example.test', offeredRole: id + '.' + version };

describe('strict invitation administration form input', () => {
  it('unpacks one no-script Role choice while preserving the pinned Int64 string', () => {
    expect(
      bffAdministrationForm('/api/studio/invitation-create', new URLSearchParams(create)),
    ).toEqual({
      operation: {
        kind: 'createInvitation',
        invitation: { email: create.email, roleId: id, roleVersion: version },
        idempotencyKey: key,
      },
      destination: '/studio/invitations',
    });
  });
  it('rejects selectors, duplicate choice, malformed IDs, versions and aliases before any operation', () => {
    for (const fields of [
      new URLSearchParams({ ...create, organizationId: id }),
      new URLSearchParams({ ...create, version: '1' }),
      new URLSearchParams({ ...create, roleId: id, roleVersion: '1' }),
      new URLSearchParams({ ...create, offeredRole: id + '.9223372036854775808' }),
      new URLSearchParams({ ...create, offeredRole: id + '.00' }),
      new URLSearchParams({ ...create, offeredRole: id.toUpperCase() + '.1' }),
      new URLSearchParams({ ...create, email: 'Person\n@example.test' }),
      new URLSearchParams([...Object.entries(create), ['offeredRole', id + '.1']]),
    ])
      expect(() => bffAdministrationForm('/api/studio/invitation-create', fields)).toThrow();
  });
  it('classifies editable invitation values for safe recovery while rejecting contradictory target grammar', () => {
    for (const [fields, invalidFields] of [
      [{ ...create, email: '', offeredRole: '' }, ['email', 'offeredRole']],
      [{ ...create, email: 'not-an-address' }, ['email']],
    ] as const) {
      try {
        bffAdministrationForm('/api/studio/invitation-create', new URLSearchParams(fields));
        throw new Error('Expected invalid form');
      } catch (error) {
        expect(error).toBeInstanceOf(BffInvitationInputError);
        expect((error as BffInvitationInputError).invalidFields).toEqual(invalidFields);
      }
    }
    expect(() =>
      bffAdministrationForm(
        '/api/studio/invitation-policy',
        new URLSearchParams({ ...common, presence: 'absent', days: '31' }),
      ),
    ).toThrow(BffInvitationInputError);
  });
  it.each(['revoke', 'reissue'])('binds %s to an exact UUID and strong version', (action) => {
    expect(
      bffAdministrationForm(
        '/api/studio/invitation-' + action,
        new URLSearchParams({ ...common, invitationId: id, version }),
      ),
    ).toMatchObject({
      operation: {
        kind: action === 'revoke' ? 'revokeInvitation' : 'reissueInvitation',
        id,
        version,
        idempotencyKey: key,
      },
      destination: '/studio/invitations/' + id,
    });
  });
  it('separates absent policy from present zero and refuses contradictory conditions', () => {
    for (const presence of ['absent', 'present']) {
      const fields = new URLSearchParams({ ...common, presence, days: '14' });
      if (presence === 'present') fields.set('version', '0');
      expect(bffAdministrationForm('/api/studio/invitation-policy', fields)).toMatchObject({
        operation: {
          kind: 'setInvitationLifetime',
          days: 14,
          precondition:
            presence === 'absent' ? { kind: 'absent' } : { kind: 'present', version: '0' },
        },
      });
    }
    for (const extra of [
      { presence: 'absent', version: '0', days: '14' },
      { presence: 'present', days: '14' },
      { presence: 'absent', days: '0' },
      { presence: 'absent', days: '31' },
      { presence: 'absent', days: '1.5' },
      { presence: 'absent', days: '014' },
      { presence: 'missing', days: '14' },
      { presence: 'present', version: '00', days: '14' },
    ] as Record<string, string>[])
      expect(() =>
        bffAdministrationForm(
          '/api/studio/invitation-policy',
          new URLSearchParams({ ...common, ...extra }),
        ),
      ).toThrow();
  });
});
