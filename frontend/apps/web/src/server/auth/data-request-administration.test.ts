import { readdirSync, readFileSync } from 'node:fs';
import { join } from 'node:path';

import type {
  MembershipDetail,
  SubjectDataRequest,
  SubjectDataRequestPage,
} from '@learnstack/sdk/administration-server';
import { describe, expect, it, vi } from 'vitest';

import { renderBffDocument } from './html';
import {
  BFF_ADMIN_FORMS,
  BffDataRequestConfirmationError,
  DATA_REQUEST_ADMIN_FORMS,
  DATA_REQUEST_ADMIN_REFUSALS,
  bffAdministrationForm,
  dataRequestAdministrationDestination,
  dataRequestAdministrationRefusal,
} from './http-forms';
import { BFF_POST_PATHS, DATA_REQUEST_DOCUMENT_PATHS, bffActionDocumentTarget } from './http-paths';
import type { BffPage } from './http-protocol';
import { NativeApiError } from './native-contract';
import { DATA_REQUEST_REFUSALS } from './native-data-request-http';
import type { NativeOperation, NativeOperationResult } from './native-operation-contract';
import type {
  DataRequestAdministrationRefusal,
  NativePageSnapshot,
  PageCapabilities,
  PageDataRequest,
} from './page-contract';
import { nativeDocumentPlan, preloadPage } from './page-preload';
import { BffSessionError } from './session-contract';
import {
  dataRequestErasureTarget,
  isBffRecoveryTarget,
  isBffRedirectTarget,
  isNativeReturnPath,
} from './session-path';
import snapshot from '../../../../../../backend/openapi/v1.json';
import { dataRequestRefusalSentence } from '../../components/auth/admin-data-request-labels';
import { createAdministrationTranslator } from '../../i18n/administration-catalogues';

const repository = join(process.cwd(), '../../..');
const requestId = '019a154b-0000-7000-8000-000000000041';
const memberId = '019a154b-0000-7000-8000-000000000012';
const otherMember = '019a154b-0000-7000-8000-000000000013';
/** A Revoked Membership: the API answers its detail 404, as `MembershipAdministrationAssignmentTests` pins. */
const revokedMember = '019a154b-0000-7000-8000-000000000019';
const erasurePage = '/studio/data-requests/erasure?membership=' + memberId;
const userId = '019a154b-0000-7000-8000-0000000000ff';
const organizationId = '019a154b-0000-7000-8000-000000000014';
const key = 'k'.repeat(43);
const csrfToken = 'C'.repeat(43);
const pageInfo = { nextCursor: null, previousCursor: null, hasNext: false, hasPrevious: false };
const capabilities: PageCapabilities = {
  canReadTenantMembers: true,
  canReadOrganizationMembers: false,
  canWriteMemberProfiles: true,
  canManageMemberships: true,
  canReadRoles: false,
  canChangeRoleGrants: false,
  canAdoptRoleRelease: false,
  canWriteOwnProfile: true,
  canManageOrganizations: false,
  canManageCustomFields: false,
  canManageFederation: false,
};
const membership = (id: string, value: Partial<MembershipDetail> = {}): MembershipDetail => ({
  membershipId: id,
  userId,
  organizationId: null,
  // The detail read answers only an Active or Suspended Membership.
  status: 'Active',
  displayName: 'Subject ' + id.slice(-2),
  preferredLocale: 'tr',
  roleIds: ['019a154b-0000-7000-8000-0000000000aa'],
  version: '9',
  ...value,
});
const receipt = (value: Partial<SubjectDataRequest> = {}): SubjectDataRequest => ({
  id: requestId,
  kind: 'erasure',
  origin: 'administrator',
  status: 'scheduled',
  createdAt: '2026-11-23T10:00:00.0000000+00:00',
  updatedAt: null,
  completedAt: null,
  lastRefusal: null,
  failureReason: null,
  retained: [],
  artifact: null,
  version: '0',
  executeAt: '2026-11-24T10:00:00.0000000+00:00',
  requestedByYou: true,
  membershipIds: [memberId],
  ...value,
});
const success = (data: unknown): NativeOperationResult =>
  ({
    kind: 'success',
    status: 200,
    data,
    etag: null,
    location: null,
    replayed: false,
  }) as NativeOperationResult;
const refused = (status: number, errors?: Record<string, { key: string }[]>) =>
  ({
    kind: 'api-error',
    status,
    error: { code: 'unknown' },
    problem: {
      status,
      type: 'about:blank',
      title: 'private-upstream-title',
      instance: '/owned',
      code: status === 409 ? 'business_rule_violation' : 'not_found',
      messageKey: 'lockey_private',
      correlationId: 'private-correlation',
      ...(errors ? { errors } : {}),
    },
  }) as NativeOperationResult;
const page = (items: SubjectDataRequest[], nextCursor: string | null = null) =>
  success({
    items,
    pageInfo: { ...pageInfo, nextCursor, hasNext: nextCursor !== null },
  } satisfies SubjectDataRequestPage);
const base: Omit<NativePageSnapshot, 'capabilities' | 'ownProfile' | 'profileState' | 'outcome'> = {
  target: '/studio/data-requests',
  site: {
    displayName: 'School',
    defaultLocale: 'en',
    enabledLocales: ['en', 'tr'],
    theme: null,
    showPlatformAttribution: true,
  },
  session: {
    state: 'authenticated',
    displayName: 'School',
    expiresAt: 1800000300000,
    limitedByAbsolute: false,
    csrfToken,
  },
  scopeKind: 'tenant',
  formKeys: [key],
};
function fixture(
  results: Partial<Record<NativeOperation['kind'], NativeOperationResult>>,
  memberships: Readonly<Record<string, NativeOperationResult>> = {},
) {
  const read = vi.fn(async (operation: NativeOperation): Promise<NativeOperationResult> => {
    if (operation.kind === 'getMembership')
      return (
        memberships[operation.id] ??
        (operation.id === revokedMember ? refused(404) : success(membership(operation.id)))
      );
    return (
      {
        getOwnCapabilities: success(capabilities),
        getOwnProfile: success({ displayName: 'Actor', preferredLocale: 'en', version: '1' }),
        getMembershipCustomFields: success({ membershipId: memberId, version: '9', fields: [] }),
        ...results,
      }[operation.kind] ?? { kind: 'invalid-request' }
    );
  });
  const wrap = vi.fn((cursor: string) => 'sealed-' + cursor.length);
  return {
    read,
    wrap,
    operations: () => read.mock.calls.map(([operation]) => operation),
    load: (target: string, organization: string | null = null) => {
      const plan = nativeDocumentPlan(target, false);
      return preloadPage(plan, { ...base, target: plan.target }, organization, read, wrap);
    },
  };
}
const ready = <K extends string>(value: NativePageSnapshot, kind: K) => {
  if (value.outcome.kind !== 'ready' || value.outcome.view.kind !== kind)
    throw new Error('Expected ' + kind);
  return value.outcome.view as Extract<NativePageSnapshot['outcome'], { kind: 'ready' }>['view'] & {
    kind: K;
  };
};

describe('administrator data-request pages: native preparation (ADR-0106 §§ 3, 6, 13)', () => {
  it('lists twenty requests, names each subject as the member list does and seals the cursor', async () => {
    const exportId = '019a154b-0000-7000-8000-000000000042';
    const readmitted = receipt({
      id: '019a154b-0000-7000-8000-000000000043',
      kind: 'export',
      executeAt: null,
      // A re-admitted person: the Revoked tombstone first, the current Membership second.
      membershipIds: [revokedMember, otherMember],
    });
    const items = [
      receipt({ membershipIds: [revokedMember] }),
      receipt({ id: exportId, kind: 'export', executeAt: null }),
      readmitted,
      receipt({
        id: '019a154b-0000-7000-8000-000000000044',
        origin: 'self',
        requestedByYou: false,
        membershipIds: [otherMember],
      }),
    ];
    const test = fixture({ listDataRequests: page(items, 'raw-api-cursor') });
    const view = ready(await test.load('/studio/data-requests'), 'data_requests');
    expect(test.operations()).toEqual([
      { kind: 'getOwnCapabilities' },
      { kind: 'getOwnProfile' },
      { kind: 'listDataRequests', limit: 20 },
      // One read per distinct first Membership, never per request, and none for the administrator's erasure.
      { kind: 'getMembership', id: memberId },
      { kind: 'getMembership', id: revokedMember },
      { kind: 'getMembership', id: otherMember },
    ]);
    expect(view.endCursor).toBe('sealed-14');
    expect(test.wrap).toHaveBeenCalledExactlyOnceWith('raw-api-cursor');
    const subject = (id: string) => ({
      membershipId: id,
      displayName: 'Subject ' + id.slice(-2),
      status: 'Active',
      organizationScoped: false,
    });
    expect(view.items.map((item) => item.subject)).toEqual([
      'revoked',
      subject(memberId),
      // Its tombstone's read was refused; the other request's read of its current Membership names it.
      subject(otherMember),
      subject(otherMember),
    ]);
    const serialized = JSON.stringify(view);
    for (const secret of [userId, 'raw-api-cursor', '0000000000aa', '"tr"', 'roleIds'])
      expect(serialized).not.toContain(secret);
    expect(Date.parse(view.preparedAt)).toBeGreaterThan(0);
  });

  it.each([
    ['an administrator erasure', { origin: 'administrator' }],
    ['an erasure awaiting the operator', { origin: 'self', status: 'awaiting_privileged_step' }],
    ['a completed erasure', { origin: 'self', status: 'completed' }],
    ['a fan-out erasure that completed', { origin: 'closure_fanout', status: 'completed' }],
  ] as const)(
    'never reads the subject of %s, whose Memberships are all Revoked',
    async (_, value) => {
      const test = fixture({
        listDataRequests: page([receipt({ ...value, membershipIds: [memberId, otherMember] })]),
        getDataRequest: success(receipt({ ...value, membershipIds: [memberId, otherMember] })),
      });
      for (const target of ['/studio/data-requests', '/studio/data-requests/' + requestId]) {
        const result = await test.load(target);
        if (result.outcome.kind !== 'ready') throw new Error('Expected a ready page');
        const view = result.outcome.view;
        const item =
          view.kind === 'data_requests'
            ? view.items[0]!
            : (view as { request: PageDataRequest }).request;
        expect(item.subject).toBe('revoked');
      }
      expect(test.operations().map((operation) => operation.kind)).not.toContain('getMembership');
    },
  );

  it('keeps reading the subject of a scheduled or running self-filed erasure and of every export', async () => {
    const items = [
      receipt({ origin: 'self', membershipIds: [memberId] }),
      receipt({
        id: '019a154b-0000-7000-8000-000000000045',
        origin: 'self',
        status: 'fulfilling',
        membershipIds: [otherMember],
      }),
      receipt({
        id: '019a154b-0000-7000-8000-000000000046',
        kind: 'export',
        status: 'completed',
        executeAt: null,
        membershipIds: [revokedMember],
      }),
    ];
    const test = fixture({ listDataRequests: page(items) });
    const view = ready(await test.load('/studio/data-requests'), 'data_requests');
    expect(
      test
        .operations()
        .filter((operation) => operation.kind === 'getMembership')
        .map((operation) => (operation as { id: string }).id),
    ).toEqual([memberId, otherMember, revokedMember]);
    // An export of a revoked person is not known to be Revoked: its refused read leaves it unnamed.
    expect(
      view.items.map((item) =>
        typeof item.subject === 'object' ? (item.subject?.membershipId ?? null) : item.subject,
      ),
    ).toEqual([memberId, otherMember, null]);
  });

  it('keeps a page usable when a subject read is refused, and never fails it for a name', async () => {
    const exported = receipt({ kind: 'export', executeAt: null });
    const test = fixture({ listDataRequests: page([exported]) }, { [memberId]: refused(404) });
    const view = ready(await test.load('/studio/data-requests'), 'data_requests');
    expect(view.items[0]!.subject).toBeNull();
    const mismatch = fixture(
      { listDataRequests: page([exported]) },
      { [memberId]: success(membership(otherMember)) },
    );
    expect(
      ready(await mismatch.load('/studio/data-requests'), 'data_requests').items[0]!.subject,
    ).toBeNull();
  });

  const exportsOf = (ids: readonly string[]) =>
    page(
      ids.map((id, index) =>
        receipt({
          id: '019a154b-0000-7000-8000-0000000002' + String(index).padStart(2, '0'),
          kind: 'export',
          executeAt: null,
          membershipIds: [id],
        }),
      ),
    );
  const twelve = Array.from(
    { length: 12 },
    (_, index) => '019a154b-0000-7000-8000-0000000001' + String(index).padStart(2, '0'),
  );

  it('reads subjects four at a time and lets no read outlive a failed page', async () => {
    const ids = twelve;
    let open = 0;
    let peak = 0;
    let settled = 0;
    const gate: (() => void)[] = [];
    const test = fixture({ listDataRequests: exportsOf(ids) });
    const common = test.read.getMockImplementation()!;
    test.read.mockImplementation(async (operation) => {
      if (operation.kind !== 'getMembership') return common(operation);
      open += 1;
      peak = Math.max(peak, open);
      await new Promise<void>((resolve) => gate.push(resolve));
      open -= 1;
      settled += 1;
      if (operation.id === ids[1]) throw new BffSessionError('revoked');
      return success(membership(operation.id));
    });
    const loading = test.load('/studio/data-requests');
    let rejected = false;
    void loading.catch(() => {
      rejected = true;
    });
    for (let round = 0; round < 40 && !rejected; round += 1) {
      await new Promise((resolve) => setTimeout(resolve, 0));
      gate.splice(0).forEach((release) => release());
    }
    await expect(loading).rejects.toThrow(BffSessionError);
    expect(peak).toBe(4);
    // The failure stops further reads, and the page settles only after every started read did.
    const started = test.operations().filter((operation) => operation.kind === 'getMembership');
    expect(started.length).toBeLessThan(ids.length);
    expect(settled).toBe(started.length);
  });

  const stopsAfter = async (failure: (id: string) => NativeOperationResult | never) => {
    const ids = twelve;
    const test = fixture({ listDataRequests: exportsOf(ids) });
    const common = test.read.getMockImplementation()!;
    test.read.mockImplementation(async (operation) =>
      operation.kind === 'getMembership' && operation.id === ids[1]
        ? failure(operation.id)
        : common(operation),
    );
    const view = ready(await test.load('/studio/data-requests'), 'data_requests');
    const started = test
      .operations()
      .filter((operation) => operation.kind === 'getMembership')
      .map((operation) => (operation as { id: string }).id);
    expect(started).toContain(ids[1]);
    // No identifier is taken once the failure is known; every unread or failed subject stays unnamed.
    expect(started.length).toBeLessThan(ids.length);
    for (const item of view.items)
      expect(item.subject === null).toBe(
        !started.includes(item.membershipIds[0]!) || item.membershipIds[0] === ids[1],
      );
  };

  it.each([429, 500, 503])(
    'stops further subject reads after a %s and still renders the page',
    (status) => stopsAfter(() => refused(status)),
  );

  it.each(['response', 'deadline'] as const)(
    'degrades a thrown %s transport failure of a subject read instead of failing the page',
    (reason) =>
      stopsAfter(() => {
        throw new NativeApiError(reason);
      }),
  );

  it('still fails the page for a cancelled subject read', async () => {
    const test = fixture(
      { listDataRequests: exportsOf([memberId]) },
      { [memberId]: { kind: 'cancelled' } },
    );
    await expect(test.load('/studio/data-requests')).rejects.toThrow(BffSessionError);
  });

  it('withholds the pages before any data-request read without the membership capability', async () => {
    for (const target of [
      '/studio/data-requests',
      '/studio/data-requests/' + requestId,
      erasurePage,
      '/studio/data-requests/erasure',
    ]) {
      const test = fixture({
        getOwnCapabilities: success({ ...capabilities, canManageMemberships: false }),
      });
      const result = await test.load(target);
      expect(result.outcome).toMatchObject({ kind: 'failure', reason: 'denied', status: 403 });
      expect(test.operations().map((operation) => operation.kind)).toEqual([
        'getOwnCapabilities',
        'getOwnProfile',
      ]);
    }
  });

  it('reads one request with its closed result and keeps at most twenty Membership identifiers', async () => {
    const many = Array.from(
      { length: 30 },
      (_, index) => '019a154b-0000-7000-8000-0000000003' + String(index).padStart(2, '0'),
    );
    const test = fixture({ getDataRequest: success(receipt({ membershipIds: many })) });
    const view = ready(
      await test.load('/studio/data-requests/' + requestId + '?result=erasure'),
      'data_request',
    );
    // The administrator's erasure concerns a Revoked person: no member read.
    expect(test.operations().slice(2)).toEqual([{ kind: 'getDataRequest', id: requestId }]);
    expect(view.result).toBe('erasure');
    expect(view.request.subject).toBe('revoked');
    expect(view.request.membershipIds).toEqual(many.slice(0, 20));
    expect(view.request.membershipCount).toBe(30);
    const exported = fixture({
      getDataRequest: success(receipt({ kind: 'export', executeAt: null, membershipIds: many })),
    });
    await exported.load('/studio/data-requests/' + requestId);
    expect(exported.operations().slice(2)).toEqual([
      { kind: 'getDataRequest', id: requestId },
      { kind: 'getMembership', id: many[0] },
    ]);
    expect(
      ready(
        await fixture({ getDataRequest: success(receipt()) }).load(
          '/studio/data-requests/' + requestId,
        ),
        'data_request',
      ).result,
    ).toBeNull();
  });

  it('admits only the closed query of each data-request document', () => {
    expect(nativeDocumentPlan('/studio/data-requests?cursor=YWJj', false)).toMatchObject({
      kind: 'data_requests',
      cursor: 'YWJj',
      limit: 20,
    });
    for (const target of [
      '/studio/data-requests?limit=100',
      '/studio/data-requests?result=export',
      '/studio/data-requests/' + requestId + '?result=closure',
      '/studio/data-requests/' + requestId + '?cursor=YWJj',
      '/studio/data-requests/' + requestId + '?result=export&result=erasure',
      '/studio/data-requests/' + requestId.toUpperCase(),
      '/studio/data-requests/' + requestId + '/artifact',
    ])
      expect(() => nativeDocumentPlan(target, false), target).toThrow();
    expect(isNativeReturnPath('/studio/data-requests')).toBe(true);
    expect(isNativeReturnPath('/studio/data-requests/' + requestId)).toBe(true);
    expect(isNativeReturnPath('/studio/data-requests/' + requestId + '?result=export')).toBe(false);
  });

  it('admits the erasure page with only one canonical Membership and reads nothing about it', async () => {
    expect(nativeDocumentPlan(erasurePage, false)).toEqual({
      target: '/studio/data-requests/erasure',
      kind: 'data_request_erasure',
      limit: 20,
      selectedFieldId: null,
      consentCursor: undefined,
      membershipId: memberId,
    });
    expect(nativeDocumentPlan('/studio/data-requests/erasure', false)).toMatchObject({
      kind: 'data_request_erasure',
      membershipId: null,
    });
    for (const target of [
      erasurePage.toUpperCase(),
      '/studio/data-requests/erasure?membership=' + memberId.toUpperCase(),
      '/studio/data-requests/erasure?membership=019a154b-0000-4000-8000-000000000012',
      '/studio/data-requests/erasure?membership=x',
      '/studio/data-requests/erasure?membership=',
      erasurePage + '&membership=' + otherMember,
      erasurePage + '&result=erasure',
      erasurePage + '&cursor=YWJj',
      '/studio/data-requests/erasure?result=erasure',
      '/studio/data-requests/erasure/' + memberId,
      '/studio/members/' + memberId + '?membership=' + memberId,
      '/studio/data-requests/' + requestId + '?membership=' + memberId,
    ])
      expect(() => nativeDocumentPlan(target, false), target).toThrow();
    const test = fixture({});
    const view = ready(await test.load(erasurePage), 'data_request_erasure');
    expect(view).toEqual({ kind: 'data_request_erasure', membershipId: memberId });
    expect(test.operations()).toEqual([{ kind: 'getOwnCapabilities' }, { kind: 'getOwnProfile' }]);
    // The erasure page is the one recovery and redirect target that carries a query; queries never return authority.
    expect(dataRequestErasureTarget(memberId)).toBe(erasurePage);
    expect(() => dataRequestErasureTarget(memberId.toUpperCase())).toThrow();
    expect(isNativeReturnPath('/studio/data-requests/erasure')).toBe(true);
    expect(isNativeReturnPath(erasurePage)).toBe(false);
    expect(isBffRecoveryTarget(erasurePage)).toBe(true);
    expect(isBffRedirectTarget(erasurePage)).toBe(true);
    const result = '/studio/data-requests/' + requestId + '?result=erasure';
    expect(isBffRecoveryTarget(result)).toBe(false);
    expect(isBffRedirectTarget(result)).toBe(true);
    for (const hostile of [
      erasurePage + '&x=1',
      erasurePage.replace('membership', 'member'),
      '/studio/data-requests/' + requestId + '?result=closure',
      '/studio/data-requests/' + requestId + '?result=export&result=erasure',
      '//other.example' + erasurePage,
      'https://other.example' + result,
    ]) {
      expect(isBffRecoveryTarget(hostile), hostile).toBe(false);
      expect(isBffRedirectTarget(hostile), hostile).toBe(false);
    }
  });

  it("shows a member's own requests only in the member's exact group, and an unavailable list as such", async () => {
    const mine = receipt({ membershipIds: [otherMember, memberId] });
    const theirs = receipt({
      id: '019a154b-0000-7000-8000-000000000044',
      membershipIds: [otherMember],
    });
    const test = fixture({
      getMembership: success(membership(memberId)),
      listDataRequests: page([mine, theirs], 'more'),
    });
    const view = ready(await test.load('/studio/members/' + memberId), 'member');
    expect(test.operations()).toContainEqual({ kind: 'listDataRequests', limit: 100 });
    expect(view.dataRequests).toMatchObject({ complete: false });
    expect(view.dataRequests!.items!.map((item) => item.id)).toEqual([mine.id]);
    expect(view.dataRequests!.items![0]!.subject).toEqual({
      membershipId: memberId,
      displayName: 'Subject 12',
      status: 'Active',
      organizationScoped: false,
    });
    // Another group's member: the list would be the wrong group's, so it is not read.
    const other = fixture({});
    other.read.mockImplementation(async (operation) =>
      operation.kind === 'getMembership'
        ? success(membership(memberId, { organizationId }))
        : operation.kind === 'getOwnCapabilities'
          ? success(capabilities)
          : operation.kind === 'getMembershipCustomFields'
            ? success({ membershipId: memberId, version: '9', fields: [] })
            : success({ displayName: 'Actor', preferredLocale: 'en', version: '1' }),
    );
    const outside = ready(await other.load('/studio/members/' + memberId), 'member');
    expect(outside.dataRequests).toBeUndefined();
    expect(other.operations().map((operation) => operation.kind)).not.toContain('listDataRequests');
    const unavailable = fixture({ listDataRequests: refused(503) });
    const degraded = ready(await unavailable.load('/studio/members/' + memberId), 'member');
    expect(degraded.dataRequests).toMatchObject({ items: null, complete: false });
    // A transport failure (deadline, oversized or non-JSON body) also hides only the section.
    for (const reason of ['response', 'deadline'] as const) {
      const thrown = fixture({});
      const common = thrown.read.getMockImplementation()!;
      thrown.read.mockImplementation(async (operation) => {
        if (operation.kind === 'listDataRequests') throw new NativeApiError(reason);
        return common(operation);
      });
      const result = await thrown.load('/studio/members/' + memberId);
      expect(result.outcome.kind).toBe('ready');
      expect(ready(result, 'member').dataRequests).toMatchObject({ items: null, complete: false });
    }
    // A session failure or cancellation still fails the page.
    for (const failure of [new BffSessionError('revoked'), new BffSessionError('cancelled')]) {
      const failing = fixture({});
      const common = failing.read.getMockImplementation()!;
      failing.read.mockImplementation(async (operation) => {
        if (operation.kind === 'listDataRequests') throw failure;
        return common(operation);
      });
      await expect(failing.load('/studio/members/' + memberId)).rejects.toBe(failure);
    }
    const cancelled = fixture({ listDataRequests: { kind: 'cancelled' } });
    await expect(cancelled.load('/studio/members/' + memberId)).rejects.toThrow(BffSessionError);
    const denied = fixture({
      getOwnCapabilities: success({ ...capabilities, canManageMemberships: false }),
    });
    expect(
      ready(await denied.load('/studio/members/' + memberId), 'member').dataRequests,
    ).toBeUndefined();
  });
});

describe('administrator data-request forms (ADR-0106 §§ 3, 10, 11; ADR-0107 § 4)', () => {
  const form = (path: string, fields: Record<string, string>) =>
    bffAdministrationForm(path, new URLSearchParams(fields));
  const create = { csrfToken, idempotencyKey: key, membershipId: memberId };
  const write = { csrfToken, idempotencyKey: key, version: '9223372036854775807', requestId };

  it('owns exactly four closed POST paths, all action documents and none a native data page', () => {
    const paths = Object.keys(DATA_REQUEST_ADMIN_FORMS);
    expect(paths).toEqual([
      '/api/studio/data-request-export',
      '/api/studio/data-request-erasure',
      '/api/studio/data-request-cancel',
      '/api/studio/data-request-revoke',
    ]);
    for (const path of paths) {
      expect(BFF_POST_PATHS).toContain(path);
      expect(BFF_ADMIN_FORMS[path]).toBe(DATA_REQUEST_ADMIN_FORMS[path]);
      expect(bffActionDocumentTarget(path)).toBe(true);
    }
    // The person's data-request routes answer their own native document, never an action document.
    expect(DATA_REQUEST_DOCUMENT_PATHS.length).toBeGreaterThan(10);
    for (const path of DATA_REQUEST_DOCUMENT_PATHS) {
      expect(bffActionDocumentTarget(path), path).toBe(false);
      expect(paths).not.toContain(path);
    }
  });

  it('maps each form to its one operation and fixed destinations', () => {
    expect(form('/api/studio/data-request-export', create)).toEqual({
      operation: {
        kind: 'createDataExportForSubject',
        membershipId: memberId,
        idempotencyKey: key,
      },
      destination: '/studio/members/' + memberId,
    });
    // A Revoked Membership has no member page: the erasure recovers to the page it was filed from.
    expect(form('/api/studio/data-request-erasure', { ...create, confirm: 'erase' })).toEqual({
      operation: {
        kind: 'createDataErasureForSubject',
        membershipId: memberId,
        idempotencyKey: key,
      },
      destination: erasurePage,
    });
    expect(form('/api/studio/data-request-cancel', write)).toEqual({
      operation: {
        kind: 'cancelDataRequest',
        id: requestId,
        version: '9223372036854775807',
        idempotencyKey: key,
      },
      destination: '/studio/data-requests/' + requestId,
    });
    expect(form('/api/studio/data-request-revoke', write).operation).toMatchObject({
      kind: 'revokeDataExport',
      id: requestId,
    });
  });

  it('re-checks the erasure confirmation as its own recoverable refusal', () => {
    for (const fields of [create, { ...create, confirm: 'yes' }, { ...create, confirm: '' }])
      expect(() => form('/api/studio/data-request-erasure', fields)).toThrow(
        BffDataRequestConfirmationError,
      );
    // A malformed target is never recoverable: the Membership grammar comes first.
    let thrown: unknown;
    try {
      form('/api/studio/data-request-erasure', { ...create, membershipId: 'x' });
    } catch (error) {
      thrown = error;
    }
    expect(thrown).toBeInstanceOf(Error);
    expect(thrown).not.toBeInstanceOf(BffDataRequestConfirmationError);
  });

  it.each([
    ['/api/studio/data-request-export', { ...create, membershipId: memberId.toUpperCase() }],
    [
      '/api/studio/data-request-export',
      { ...create, membershipId: '019a154b-0000-4000-8000-000000000012' },
    ],
    ['/api/studio/data-request-export', { ...create, confirm: 'erase' }],
    ['/api/studio/data-request-export', { ...create, tenantId: organizationId }],
    ['/api/studio/data-request-export', { csrfToken, membershipId: memberId }],
    ['/api/studio/data-request-export', { ...create, idempotencyKey: 'short' }],
    ['/api/studio/data-request-cancel', { ...write, version: '01' }],
    ['/api/studio/data-request-cancel', { ...write, version: '9223372036854775808' }],
    ['/api/studio/data-request-cancel', { ...write, requestId: 'x' }],
    ['/api/studio/data-request-revoke', { ...write, membershipId: memberId }],
    ['/api/studio/data-request-revoke', { csrfToken, idempotencyKey: key, requestId }],
  ])('refuses %s with a hostile or incomplete form', (path, fields) => {
    expect(() => form(path, fields as Record<string, string>)).toThrow();
  });

  it('refuses a duplicated field', () => {
    const fields = new URLSearchParams(create);
    fields.append('membershipId', otherMember);
    expect(() => bffAdministrationForm('/api/studio/data-request-export', fields)).toThrow();
  });

  it('redirects a settled action to its request page with a closed result, and creation only by its exact Location', () => {
    const location = '/api/v1/identity/data-requests/' + requestId;
    expect(
      dataRequestAdministrationDestination(
        '/api/studio/data-request-export',
        '/studio/members/' + memberId,
        location,
      ),
    ).toBe('/studio/data-requests/' + requestId + '?result=export');
    expect(
      dataRequestAdministrationDestination(
        '/api/studio/data-request-erasure',
        '/studio/members/' + memberId,
        location,
      ),
    ).toBe('/studio/data-requests/' + requestId + '?result=erasure');
    expect(
      dataRequestAdministrationDestination(
        '/api/studio/data-request-cancel',
        '/studio/data-requests/' + requestId,
        null,
      ),
    ).toBe('/studio/data-requests/' + requestId + '?result=cancelled');
    expect(
      dataRequestAdministrationDestination(
        '/api/studio/data-request-revoke',
        '/studio/data-requests/' + requestId,
        null,
      ),
    ).toBe('/studio/data-requests/' + requestId + '?result=revoked');
    for (const bad of [
      null,
      '/api/v1/identity/me/data-requests/' + requestId,
      location + '/cancellations',
      'https://other.example' + location,
      location.toUpperCase(),
    ])
      expect(
        dataRequestAdministrationDestination(
          '/api/studio/data-request-export',
          '/studio/members/' + memberId,
          bad,
        ),
      ).toBeNull();
    expect(
      dataRequestAdministrationDestination('toString', '/studio/members/' + memberId, location),
    ).toBeNull();
    expect(
      dataRequestAdministrationDestination(
        '/api/studio/invitation-create',
        '/studio/invitations',
        location,
      ),
    ).toBeNull();
  });
});

/** Every `lockey_*` reason the administrator routes document, and every one the Identity data-request code spells. */
function administratorReasons(): Set<string> {
  const reasons = new Set<string>();
  for (const [path, item] of Object.entries(
    snapshot.paths as Record<
      string,
      Record<string, { responses?: Record<string, { description?: string }> }>
    >,
  )) {
    if (!path.startsWith('/api/v1/identity/data-requests')) continue;
    for (const operation of Object.values(item))
      for (const response of Object.values(operation.responses ?? {}))
        for (const match of (response.description ?? '').matchAll(
          /\b(?:lockey_([a-z_]+)|(data_export_[a-z_]+))\b/g,
        ))
          reasons.add('lockey_' + (match[1] ?? match[2]));
  }
  const directory = join(
    repository,
    'backend/src/Modules/Identity/LearnStack.Modules.Identity.Application/DataRequests',
  );
  for (const file of readdirSync(directory).filter((name) => name.endsWith('.cs')))
    for (const match of readFileSync(join(directory, file), 'utf8').matchAll(/"(lockey_[a-z_]+)"/g))
      reasons.add(match[1]!);
  return reasons;
}
/** Answered by HTTP status and code with the template's generic sentences, never by a reason sentence. */
const ENVELOPE = new Set([
  'lockey_business_rule_violation',
  'lockey_not_found',
  'lockey_forbidden',
  'lockey_dependency_unavailable',
  'lockey_validation_failed',
  'lockey_invalid_value',
  'lockey_concurrency_conflict',
  'lockey_identifier_required',
]);
/** Reasons only the person's download routes answer; no administrator route can, and the test proves it. */
const PERSON_ONLY = new Set(['lockey_data_export_ticket_limit']);
const REFUSALS: readonly DataRequestAdministrationRefusal[] = [
  'request_already_active',
  'blocked_hold',
  'blocked_last_administrator',
  'subject_export_available',
  'subject_membership_current',
  'subject_is_requester',
  'subject_not_admitted',
  'snapshot_too_large',
  'data_export_unavailable',
  'data_request_window_ended',
  'data_request_not_cancellable',
  'unknown',
];

describe('administrator data-request refusals (ADR-0106 § 7)', () => {
  it('maps every reason to the refusal of its own name', () => {
    for (const [reason, refusal] of Object.entries(DATA_REQUEST_ADMIN_REFUSALS))
      expect(refusal, reason).toBe(reason.slice('lockey_'.length));
  });

  it('names every reason the administrator routes or the Identity code can answer', () => {
    const reasons = administratorReasons();
    expect(reasons.size).toBeGreaterThan(15);
    const unmapped = [...reasons].filter(
      (reason) =>
        !Object.hasOwn(DATA_REQUEST_ADMIN_REFUSALS, reason) &&
        !ENVELOPE.has(reason) &&
        !PERSON_ONLY.has(reason),
    );
    expect(unmapped).toEqual([]);
    // A person-only reason never appears in an administrator route's contract.
    const documented = JSON.stringify(
      Object.entries(snapshot.paths).filter(([path]) =>
        path.startsWith('/api/v1/identity/data-requests'),
      ),
    );
    for (const reason of PERSON_ONLY) {
      expect(documented).not.toContain(reason);
      expect(documented).not.toContain(reason.slice('lockey_'.length));
    }
    // The administrator's map is the person's map without the download-only reason.
    expect(Object.keys(DATA_REQUEST_ADMIN_REFUSALS).sort()).toEqual(
      Object.keys(DATA_REQUEST_REFUSALS)
        .filter((reason) => !PERSON_ONLY.has(reason))
        .sort(),
    );
  });

  it('picks the first known reason of errors.$ or errors.artifact, else the generic refusal', () => {
    expect(
      dataRequestAdministrationRefusal({ $: [{ key: 'lockey_subject_membership_current' }] }),
    ).toBe('subject_membership_current');
    expect(
      dataRequestAdministrationRefusal({ artifact: [{ key: 'lockey_data_export_unavailable' }] }),
    ).toBe('data_export_unavailable');
    expect(
      dataRequestAdministrationRefusal({
        $: [{ key: 'lockey_future_reason' }, { key: 'lockey_request_already_active' }],
      }),
    ).toBe('request_already_active');
    expect(
      dataRequestAdministrationRefusal({
        membershipId: [{ key: 'lockey_request_already_active' }],
      }),
    ).toBe('unknown');
    expect(dataRequestAdministrationRefusal({ $: [{ key: 'toString' }] })).toBe('unknown');
    expect(dataRequestAdministrationRefusal(undefined)).toBe('unknown');
  });

  it.each(['en', 'tr'] as const)('gives every refusal its own %s sentence', (locale) => {
    const { admin, dataRequest } = createAdministrationTranslator(locale);
    const sentences = REFUSALS.map((refusal) =>
      dataRequestRefusalSentence(refusal, admin, dataRequest),
    );
    expect(new Set(sentences).size).toBe(REFUSALS.length);
    const fallback = createAdministrationTranslator(locale).admin('failure.unavailable');
    for (const sentence of sentences) {
      expect(sentence).not.toBe(fallback);
      expect(sentence).not.toMatch(/lockey_|\{|\}/);
    }
  });
});

describe('administrator data-request recovery document', () => {
  const site = base.site;
  const render = (value: Partial<Extract<BffPage, { kind: 'mutation_error' }>>, locale = 'en') =>
    new DOMParser().parseFromString(
      renderBffDocument(site, {
        kind: 'mutation_error',
        action: '/api/studio/data-request-erasure',
        destination: erasurePage,
        fields: [
          ['csrfToken', csrfToken],
          ['idempotencyKey', key],
          ['membershipId', memberId],
          ['confirm', 'erase'],
        ],
        reason: 'denied',
        retryable: false,
        dataRequest: { refusal: null },
        locale,
        ...value,
      } as BffPage),
      'text/html',
    );

  it.each(REFUSALS)('explains %s in one alert, with no retry and no API text', (refusal) => {
    for (const locale of ['en', 'tr']) {
      const document = render({ dataRequest: { refusal }, retryable: true }, locale);
      const { admin, dataRequest } = createAdministrationTranslator(locale);
      expect(document.querySelector('[role="alert"]')?.textContent).toBe(
        dataRequestRefusalSentence(refusal, admin, dataRequest),
      );
      expect(document.querySelector('form')).toBeNull();
      expect(document.querySelector('a[href="' + erasurePage + '"]')).not.toBeNull();
      expect(document.body.textContent).not.toMatch(/private-|lockey_/);
      expect(document.documentElement.getAttribute('data-bff-history-recovery')).toBe(erasurePage);
      expect(document.querySelector('a[href="/account/data"]') !== null).toBe(
        refusal === 'subject_is_requester',
      );
    }
  });

  it('keeps the template sentence and a deliberate same-request retry for an unconfirmed outcome', () => {
    const document = render({ reason: 'unavailable', retryable: true });
    expect(document.querySelector('[role="alert"]')?.textContent).toBe(
      'The result could not be confirmed. Check the item before deciding whether to retry this same request.',
    );
    const form = document.querySelector('form')!;
    expect(form.getAttribute('action')).toBe('/api/studio/data-request-erasure');
    expect([...form.querySelectorAll('input')].map((input) => [input.name, input.value])).toEqual([
      ['csrfToken', csrfToken],
      ['idempotencyKey', key],
      ['membershipId', memberId],
      ['confirm', 'erase'],
    ]);
  });

  it('asks for the confirmation again, in Turkish too, without a resubmission form', () => {
    for (const [locale, text] of [
      ['en', 'Tick every confirmation box to go ahead.'],
      ['tr', 'Devam etmek için tüm onay kutularını işaretleyin.'],
    ] as const) {
      const document = render({ reason: 'validation' }, locale);
      expect(document.querySelector('[role="alert"]')?.textContent).toBe(text);
      expect(document.querySelector('form')).toBeNull();
      expect(document.documentElement.lang).toBe(locale);
    }
  });
});
