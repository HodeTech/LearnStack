// @vitest-environment node
import { createServer } from 'node:http';
import { connect } from 'node:net';

import type { OwnDataRequest } from '@learnstack/sdk/administration-server';
import { afterEach, describe, expect, it } from 'vitest';

import { DATA_REQUEST_REFUSALS } from './native-data-request-http';
import type { NativeOperationResult } from './native-operation-contract';
import en from '../../i18n/messages/en/data.json';
import {
  DownloadWire,
  artifact,
  chunkBytes,
  closeDataRequestFixtures,
  fixture,
  formKeys,
  id,
  ok,
  organization,
  pageInfo,
  refused,
  request,
  second,
  shown,
  ticket,
} from '../../test/data-request-person-fixture';

afterEach(closeDataRequestFixtures);

/** A form key as the page issues them: 43 URL-safe characters, distinct for each label. */
const keyFor = (label: string) => label.replaceAll(/[^A-Za-z0-9]/g, '').padEnd(43, 'k');
const links = (html: string) =>
  [...html.matchAll(/class="action" href="([^"]*)"/g)].map((match) => match[1]);
const view = (html: string) => /data-data-request-view="([a-z_]+)"/.exec(html)?.[1];
/** The hidden `group` values of every retained form on a page. */
const groups = (html: string) =>
  [...html.matchAll(/name="group" value="([^"]*)"/g)].map((match) => match[1]);
/** The operations a caller mock was asked to perform, in order. */
const operations = (f: Awaited<ReturnType<typeof fixture>>) =>
  f.own.mock.calls.map(([operation]) => operation);
const retainedOperations = (f: Awaited<ReturnType<typeof fixture>>) =>
  f.retained.mock.calls.map(([, , operation]) => operation);

type Write = Readonly<{
  action: 'export' | 'erasure' | 'closure' | 'cancel' | 'revoke';
  fields: Record<string, string>;
  result: string;
  session: (key: string) => object;
  entry: (key: string, group: string) => object;
}>;
/** Each filing and action route with the exact operation it files in each mode. */
const WRITES: readonly Write[] = [
  {
    action: 'export',
    fields: {},
    result: 'export',
    session: (idempotencyKey) => ({ kind: 'createOwnDataExport', idempotencyKey }),
    entry: (idempotencyKey, group) => ({ kind: 'createRetainedDataExport', group, idempotencyKey }),
  },
  {
    action: 'erasure',
    fields: { confirm: 'erase' },
    result: 'erasure',
    session: (idempotencyKey) => ({ kind: 'createOwnDataErasure', idempotencyKey }),
    entry: (idempotencyKey, group) => ({
      kind: 'createRetainedDataErasure',
      group,
      idempotencyKey,
    }),
  },
  {
    action: 'closure',
    fields: { confirm: 'close', acknowledge: 'no_status' },
    result: 'closure',
    session: (idempotencyKey) => ({ kind: 'createOwnAccountClosure', idempotencyKey }),
    entry: (idempotencyKey, group) => ({
      kind: 'createRetainedAccountClosure',
      group,
      idempotencyKey,
    }),
  },
  {
    action: 'cancel',
    fields: { requestId: id, version: '3' },
    result: 'cancelled',
    session: (idempotencyKey) => ({
      kind: 'cancelOwnDataRequest',
      id,
      version: '3',
      idempotencyKey,
    }),
    entry: (idempotencyKey, group) => ({
      kind: 'cancelRetainedDataRequest',
      group,
      id,
      version: '3',
      idempotencyKey,
    }),
  },
  {
    action: 'revoke',
    fields: { requestId: second, version: '9007199254740993' },
    result: 'revoked',
    session: (idempotencyKey) => ({
      kind: 'revokeOwnDataExport',
      id: second,
      version: '9007199254740993',
      idempotencyKey,
    }),
    entry: (idempotencyKey, group) => ({
      kind: 'revokeRetainedDataExport',
      group,
      id: second,
      version: '9007199254740993',
      idempotencyKey,
    }),
  },
];
const GROUPS = ['tenant', organization] as const;

describe('every filing and action route files exactly its own operation (ADR-0106 §§ 9-11)', () => {
  it.each(WRITES.map((write) => [write.action, write] as const))(
    'session %s files the own operation and answers the fixed page',
    async (_action, write) => {
      const f = await fixture('session');
      const key = keyFor('session-' + write.action);
      const wire = await f.run('POST', '/api/account/data-requests/' + write.action, {
        csrfToken: f.csrf,
        idempotencyKey: key,
        ...write.fields,
      });
      expect(wire.statusCode).toBe(303);
      expect(wire.headers.get('location')).toBe('/account/data?result=' + write.result);
      expect(wire.html).toBe('');
      expect(operations(f)).toEqual([write.session(key)]);
      // A live session never reaches the retained caller.
      expect(f.retained).not.toHaveBeenCalled();
    },
  );

  it.each(GROUPS.flatMap((group) => WRITES.map((write) => [write.action, group, write] as const)))(
    'retained %s in group %s files the retained operation and answers that group',
    async (_action, group, write) => {
      const f = await fixture('entry');
      const key = keyFor('entry-' + write.action);
      const wire = await f.run('POST', '/api/auth/entry/data-requests/' + write.action, {
        csrfToken: f.csrf,
        idempotencyKey: key,
        group,
        ...write.fields,
      });
      expect(wire.statusCode).toBe(303);
      expect(wire.headers.get('location')).toBe(
        '/account/data?group=' + group + '&result=' + write.result,
      );
      expect(wire.html).toBe('');
      expect(retainedOperations(f)).toEqual([write.entry(key, group)]);
      // The retained entry never performs a live-session operation.
      expect(f.own).not.toHaveBeenCalled();
      expect(f.administration).not.toHaveBeenCalled();
    },
  );

  it.each([
    ['session', undefined],
    ['entry', 'tenant'],
    ['entry', organization],
  ] as const)(
    'the %s download mints one ticket, reads each chunk in order and answers the attachment (group %s)',
    async (mode, group) => {
      const f = await fixture(mode);
      const wire = new DownloadWire();
      await f.run(
        'POST',
        mode === 'session'
          ? '/api/account/data-requests/download'
          : '/api/auth/entry/data-requests/download',
        { csrfToken: f.csrf, requestId: id, ...(group ? { group } : {}) },
        { wire },
      );
      expect(wire.statusCode).toBe(200);
      expect(wire.headers.get('content-disposition')).toBe(
        'attachment; filename="data-export.json"',
      );
      expect(Buffer.concat(wire.bytes).equals(Buffer.concat([chunkBytes(0), chunkBytes(1)]))).toBe(
        true,
      );
      if (mode === 'session') {
        expect(operations(f)).toEqual([
          { kind: 'mintOwnDataExportTicket', id },
          { kind: 'readOwnDataExportChunk', id, ticket, index: 0 },
          { kind: 'readOwnDataExportChunk', id, ticket, index: 1 },
        ]);
        expect(f.retained).not.toHaveBeenCalled();
      } else {
        expect(retainedOperations(f)).toEqual([
          { kind: 'mintRetainedDataExportTicket', group, id },
          { kind: 'readRetainedDataExportChunk', group, id, ticket, index: 0 },
          { kind: 'readRetainedDataExportChunk', group, id, ticket, index: 1 },
        ]);
        expect(f.own).not.toHaveBeenCalled();
      }
    },
  );
});

describe('one refusal sentence per route and mode, never an API text', () => {
  const ROUTES = [
    [
      'export',
      'request_already_active',
      '$',
      { csrfToken: '', idempotencyKey: keyFor('r-export') },
    ],
    [
      'erasure',
      'blocked_hold',
      '$',
      { csrfToken: '', idempotencyKey: keyFor('r-erasure'), confirm: 'erase' },
    ],
    [
      'closure',
      'blocked_last_administrator',
      '$',
      {
        csrfToken: '',
        idempotencyKey: keyFor('r-closure'),
        confirm: 'close',
        acknowledge: 'no_status',
      },
    ],
    [
      'cancel',
      'data_request_window_ended',
      '$',
      { csrfToken: '', idempotencyKey: keyFor('r-cancel'), requestId: id, version: '3' },
    ],
    [
      'revoke',
      'data_export_unavailable',
      'artifact',
      { csrfToken: '', idempotencyKey: keyFor('r-revoke'), requestId: id, version: '4' },
    ],
    ['download', 'data_export_ticket_limit', 'artifact', { csrfToken: '', requestId: id }],
  ] as const;
  it.each(
    (['session', 'entry'] as const).flatMap((mode) =>
      ROUTES.map(([action, reason, where, form]) => [mode, action, reason, where, form] as const),
    ),
  )('%s %s answers the %s sentence in a 409', async (mode, action, reason, where, form) => {
    const f = await fixture(mode);
    const api = refused(409, 'business_rule_violation', {
      [where]: [{ key: 'lockey_' + reason }],
    });
    if (mode === 'session') f.own.mockReturnValueOnce(api);
    else f.retained.mockImplementationOnce(async () => api);
    const wire = await f.run(
      'POST',
      (mode === 'session' ? '/api/account/data-requests/' : '/api/auth/entry/data-requests/') +
        action,
      { ...form, csrfToken: f.csrf, ...(mode === 'entry' ? { group: 'tenant' } : {}) },
    );
    expect(wire.statusCode).toBe(409);
    expect(wire.headers.get('content-type')).toBe('text/html; charset=utf-8');
    expect(wire.headers.has('content-disposition')).toBe(false);
    expect(wire.html).toContain('role="alert"');
    expect(wire.html).toContain(shown(en.refusal[reason]));
    for (const other of Object.values(en.refusal).filter(
      (sentence) => sentence !== en.refusal[reason],
    ))
      expect(wire.html).not.toContain(shown(other));
    expect(wire.html).not.toContain('lockey_');
    // The refusal leads back by the owner's mode, never by a submitted group.
    expect(links(wire.html)).toEqual(
      mode === 'session' ? ['/account/data', '/portal'] : ['/account/data', '/api/auth/entry'],
    );
    expect(wire.html).not.toContain('group=');
  });

  it('pairs every reason the person can meet with the sentence of its own name', async () => {
    for (const [reason, key] of Object.entries(DATA_REQUEST_REFUSALS)) {
      expect(key, reason).toBe('refusal.' + reason.slice('lockey_'.length));
      expect(Object.hasOwn(en.refusal, key.slice('refusal.'.length)), key).toBe(true);
    }
    const f = await fixture('session');
    for (const [reason, key] of Object.entries(DATA_REQUEST_REFUSALS)) {
      f.own.mockReturnValueOnce(refused(409, 'business_rule_violation', { $: [{ key: reason }] }));
      const wire = await f.run('POST', '/api/account/data-requests/export', {
        csrfToken: f.csrf,
        idempotencyKey: keyFor('pair'),
      });
      expect(wire.html, reason).toContain(
        shown(en.refusal[key.slice('refusal.'.length) as keyof typeof en.refusal]),
      );
    }
  });
});

describe('the retained pages reached after a redirect (ADR-0106 § 9)', () => {
  it('follows the 303 of a retained erasure to that group’s page with its notice', async () => {
    const listed = [request()];
    const f = await fixture('entry', listed);
    const filed = await f.run('POST', '/api/auth/entry/data-requests/erasure', {
      csrfToken: f.csrf,
      idempotencyKey: keyFor('follow'),
      group: organization,
      confirm: 'erase',
    });
    const location = filed.headers.get('location') as string;
    expect(location).toBe('/account/data?group=' + organization + '&result=erasure');
    f.retained.mockClear();
    // The group now lists the erasure the person asked for, which is what the notice describes.
    listed.splice(
      0,
      listed.length,
      request({
        kind: 'erasure',
        status: 'scheduled',
        executeAt: '2999-01-01T00:00:00.0000000+00:00',
      }),
    );
    const page = await f.run('GET', location);
    expect(page.statusCode).toBe(200);
    expect(view(page.html)).toBe('entry');
    expect(page.html).toContain(
      '<div class="notice" role="status"><h2>' +
        en.result.title +
        '</h2><p>' +
        shown(en.result.erasure) +
        '</p></div>',
    );
    expect(page.html).toContain('<title>' + en.result.title + ' – ' + en.common.title);
    // Every retained form of the page names the group the page was opened for, and no other.
    expect(new Set(groups(page.html))).toEqual(new Set([organization]));
    expect(groups(page.html).length).toBeGreaterThanOrEqual(1);
    // A reload or a restored page reopens the group, never the result.
    expect(page.html).toContain(
      "history.replaceState(null,'','/account/data?group=" + organization + "')",
    );
    expect(retainedOperations(f)).toEqual([
      { kind: 'listRetainedDataRequests', group: organization, limit: 20 },
    ]);
    expect(f.administration).not.toHaveBeenCalled();
    expect(f.own).not.toHaveBeenCalled();
    expect(f.native.self).not.toHaveBeenCalled();
    // A result is shown for each closed value, while the list still shows what it says, and nothing else.
    const matching = {
      export: request(),
      closure: request({ kind: 'closure', status: 'scheduled' }),
      cancelled: request({ status: 'cancelled' }),
      revoked: request({
        status: 'completed',
        artifact: artifact({ readable: false, status: 'expired' }),
      }),
    } as const;
    for (const result of ['export', 'closure', 'cancelled', 'revoked'] as const) {
      listed.splice(0, listed.length);
      const empty = (await f.run('GET', '/account/data?group=tenant&result=' + result)).html;
      expect(empty, result + ' over an empty list').not.toContain(shown(en.result[result]));
      listed.push(matching[result]);
      expect((await f.run('GET', '/account/data?group=tenant&result=' + result)).html).toContain(
        shown(en.result[result]),
      );
    }
  });

  it.each([
    ['erasure', 'entry_erasure', 1],
    ['closure', 'entry_closure', 2],
  ] as const)(
    'opens the %s confirmation of a retained group with its group and required boxes',
    async (step, name, boxes) => {
      const f = await fixture('entry');
      for (const group of GROUPS) {
        const page = await f.run('GET', '/account/data?group=' + group + '&step=' + step);
        expect(page.statusCode).toBe(200);
        expect(view(page.html)).toBe(name);
        const form = page.html.slice(
          page.html.indexOf('action="/api/auth/entry/data-requests/' + step + '"'),
        );
        const body = form.slice(0, form.indexOf('</form>'));
        expect(body).toContain('name="group" value="' + group + '"');
        expect([...body.matchAll(/type="checkbox"[^>]*required/g)]).toHaveLength(boxes);
        expect(body).toContain('<fieldset><legend>');
        expect(body).toMatch(/name="idempotencyKey" value="[A-Za-z0-9_-]{43}"/);
        expect(page.html).toContain(
          "history.replaceState(null,'','/account/data?group=" + group + "')",
        );
      }
      expect(f.own).not.toHaveBeenCalled();
      expect(f.administration).not.toHaveBeenCalled();
    },
  );

  it('refuses every retained query that names no group, an unknown one or a stray parameter', async () => {
    const f = await fixture('entry');
    for (const target of [
      '/account/data?result=export',
      '/account/data?cursor=AAAA',
      '/account/data?step=erasure',
      '/account/data?group=Tenant',
      '/account/data?group=019a154b-0000-4000-8000-0000000000a1',
      '/account/data?group=tenant&step=everything',
      '/account/data?group=tenant&result=everything',
      '/account/data?group=tenant&result=export&result=export',
      '/account/data?group=tenant&scopeCursor=AAAA',
      '/account/data?group=tenant&cursor=not%20sealed',
      '/account/data?group=tenant&group=tenant',
      '/account/data?scopeCursor=' + 'A'.repeat(1025),
    ])
      expect((await f.run('GET', target)).statusCode, target).toBe(400);
    expect(f.retained).not.toHaveBeenCalled();
  });

  it('pages a retained group and the chooser through cursors sealed to the owner', async () => {
    const f = await fixture('entry', [request()]);
    f.retained.mockImplementationOnce(async () =>
      ok({ items: [], pageInfo: { ...pageInfo, nextCursor: 'raw-group-cursor', hasNext: true } }),
    );
    const list = (await f.run('GET', '/account/data?group=' + organization)).html;
    expect(list).not.toContain('raw-group-cursor');
    const next = new RegExp(
      'href="/account/data\\?group=' + organization + '&amp;cursor=([A-Za-z0-9_-]+)"',
    ).exec(list)![1]!;
    f.retained.mockClear();
    const second = await f.run('GET', '/account/data?group=' + organization + '&cursor=' + next);
    expect(second.statusCode).toBe(200);
    expect(retainedOperations(f)).toEqual([
      {
        kind: 'listRetainedDataRequests',
        group: organization,
        limit: 20,
        cursor: 'raw-group-cursor',
      },
    ]);
    f.retained.mockImplementationOnce(async () =>
      ok({ items: [], pageInfo: { ...pageInfo, nextCursor: 'raw-scope-cursor', hasNext: true } }),
    );
    f.retained.mockClear();
    const chooser = (await f.run('GET', '/account/data')).html;
    expect(chooser).not.toContain('raw-scope-cursor');
    expect(view(chooser)).toBe('entry_scopes');
    const scopeNext = /href="\/account\/data\?scopeCursor=([A-Za-z0-9_-]+)"/.exec(chooser)![1]!;
    f.retained.mockClear();
    expect((await f.run('GET', '/account/data?scopeCursor=' + scopeNext)).statusCode).toBe(200);
    expect(retainedOperations(f)).toEqual([
      { kind: 'listRetainedDataRequestScopes', limit: 20, cursor: 'raw-scope-cursor' },
    ]);
  });
});

describe('one browser holding both an entry and a session (ADR-0106 § 9)', () => {
  const dead = (browser: string) =>
    '__Host-ls-entry=' +
    'x'.repeat(43) +
    '; __Host-ls-session=' +
    'y'.repeat(43) +
    '; __Host-ls-browser=' +
    browser;

  it('shows the retained entry while it lives, and calls no live-session operation', async () => {
    const f = await fixture('both', [request()]);
    const wire = await f.run('GET', '/account/data');
    expect(wire.statusCode).toBe(200);
    expect(view(wire.html)).toBe('entry_scopes');
    expect(retainedOperations(f)).toEqual([{ kind: 'listRetainedDataRequestScopes', limit: 20 }]);
    expect(f.own).not.toHaveBeenCalled();
    expect(f.administration).not.toHaveBeenCalled();
    const group = await f.run('GET', '/account/data?group=tenant');
    expect(view(group.html)).toBe('entry');
    expect(f.own).not.toHaveBeenCalled();
    expect(f.administration).not.toHaveBeenCalled();
  });

  it('falls back to the live session when the entry has expired', async () => {
    const f = await fixture('both', [request()]);
    // The entry lives five minutes; the session outlasts it.
    f.advance(301_000);
    const wire = await f.run('GET', '/account/data');
    expect(wire.statusCode).toBe(200);
    expect(view(wire.html)).toBe('session');
    expect(operations(f)).toEqual([{ kind: 'listOwnDataRequests', limit: 20 }]);
    expect(f.retained).not.toHaveBeenCalled();
    // Only the session's own addresses remain; a retained group is no longer the person's to open.
    expect((await f.run('GET', '/account/data?group=tenant')).statusCode).toBe(400);
  });

  it('falls back to the live session when the entry cookie names a revoked entry', async () => {
    // The session this person selected came from an entry: the browser still carries that entry's cookie.
    const f = await fixture('session', [request()]);
    const spent = '__Host-ls-entry=' + f.owner.finished.handle + '; ' + f.owner.cookie;
    const wire = await f.run('GET', '/account/data', undefined, { headers: { cookie: spent } });
    expect(wire.statusCode).toBe(200);
    expect(view(wire.html)).toBe('session');
    expect(operations(f)).toEqual([{ kind: 'listOwnDataRequests', limit: 20 }]);
    expect(f.retained).not.toHaveBeenCalled();
    // A bare unknown entry handle is the same refusal of the entry, and the session still answers.
    const unknown = await f.run('GET', '/account/data', undefined, {
      headers: { cookie: '__Host-ls-entry=' + 'x'.repeat(43) + '; ' + f.owner.cookie },
    });
    expect(view(unknown.html)).toBe('session');
  });

  it('answers 401 with the sign-in way back when neither owner lives, calling nothing', async () => {
    const f = await fixture('both', [request()]);
    const browser = f.session!.finished.browser;
    const answers = async (cookie: string) => {
      const wire = await f.run('GET', '/account/data', undefined, { headers: { cookie } });
      expect(wire.statusCode, cookie).toBe(401);
      expect(wire.html, cookie).toContain(shown(en.error.unauthorized));
      expect(links(wire.html), cookie).toEqual(['/api/auth/signin']);
    };
    // Handles that name no owner at all, then the real ones once both owners have ended.
    await answers(dead(browser));
    f.advance(2_000_000);
    await answers(f.cookie);
    await answers('__Host-ls-entry=' + f.entry!.finished.handle + '; __Host-ls-browser=' + browser);
    await answers(f.session!.cookie);
    expect(f.own).not.toHaveBeenCalled();
    expect(f.retained).not.toHaveBeenCalled();
    expect(f.administration).not.toHaveBeenCalled();
  });

  it('names the owner of a posted action by its path and checks that owner’s proof', async () => {
    const f = await fixture('both');
    const key = keyFor('both-export');
    // The session's route files in the session with the session's proof…
    const own = await f.run('POST', '/api/account/data-requests/export', {
      csrfToken: f.sessionCsrf!,
      idempotencyKey: key,
    });
    expect(own.headers.get('location')).toBe('/account/data?result=export');
    expect(operations(f)).toEqual([{ kind: 'createOwnDataExport', idempotencyKey: key }]);
    expect(f.retained).not.toHaveBeenCalled();
    // …the entry's route files in the entry with the entry's proof…
    f.own.mockClear();
    const retained = await f.run('POST', '/api/auth/entry/data-requests/export', {
      csrfToken: f.entryCsrf!,
      idempotencyKey: key,
      group: 'tenant',
    });
    expect(retained.headers.get('location')).toBe('/account/data?group=tenant&result=export');
    expect(retainedOperations(f)).toEqual([
      { kind: 'createRetainedDataExport', group: 'tenant', idempotencyKey: key },
    ]);
    expect(f.own).not.toHaveBeenCalled();
    // …and the other owner's proof is a refused proof, with nothing filed.
    f.retained.mockClear();
    const crossed = [
      ['/api/account/data-requests/export', f.entryCsrf!, {}],
      ['/api/auth/entry/data-requests/export', f.sessionCsrf!, { group: 'tenant' }],
    ] as const;
    for (const [path, csrfToken, extra] of crossed)
      expect(
        (await f.run('POST', path, { csrfToken, idempotencyKey: key, ...extra })).statusCode,
        path,
      ).toBe(403);
    expect(f.own).not.toHaveBeenCalled();
    expect(f.retained).not.toHaveBeenCalled();
  });
});

describe('a continuation is the owner’s and the purpose’s alone (ADR-0106 § 9)', () => {
  /** Flips one bit of the first ciphertext byte of a sealed cursor: it can never equal the genuine value. */
  const tampered = (cursor: string, offset = 13) => {
    const bytes = Buffer.from(cursor, 'base64url');
    bytes[offset] = bytes[offset]! ^ 1;
    const value = bytes.toString('base64url');
    expect(value).not.toBe(cursor);
    return value;
  };
  const withNext = (raw: string): NativeOperationResult =>
    ok({ items: [], pageInfo: { ...pageInfo, nextCursor: raw, hasNext: true } });
  const sealed = /href="\/account\/data\?(?:group=[^&"]+&amp;)?cursor=([A-Za-z0-9_-]+)"/;

  it('refuses a tampered cursor in either mode before any read, whichever byte is changed', async () => {
    const session = await fixture('session');
    session.own.mockReturnValueOnce(withNext('raw-api-cursor'));
    const cursor = sealed.exec((await session.run('GET', '/account/data')).html)![1]!;
    const bytes = Buffer.from(cursor, 'base64url');
    // 1 version + 12 nonce + 14 ciphertext + 16 tag: change one bit of each region in turn.
    expect(bytes.length).toBe(43);
    session.own.mockClear();
    for (const offset of [0, 1, 13, 26, 27, 42])
      expect(
        (await session.run('GET', '/account/data?cursor=' + tampered(cursor, offset))).statusCode,
        'offset ' + offset,
      ).toBe(400);
    expect(session.own).not.toHaveBeenCalled();
    // The untouched value still opens, so the refusals above are the tampering and nothing else.
    expect((await session.run('GET', '/account/data?cursor=' + cursor)).statusCode).toBe(200);
    expect(operations(session)).toEqual([
      { kind: 'listOwnDataRequests', limit: 20, cursor: 'raw-api-cursor' },
    ]);

    const entry = await fixture('entry');
    entry.retained.mockImplementationOnce(async () => withNext('raw-group-cursor'));
    const group = sealed.exec((await entry.run('GET', '/account/data?group=tenant')).html)![1]!;
    entry.retained.mockClear();
    for (const offset of [0, 13, 42])
      expect(
        (await entry.run('GET', '/account/data?group=tenant&cursor=' + tampered(group, offset)))
          .statusCode,
        'offset ' + offset,
      ).toBe(400);
    expect(entry.retained).not.toHaveBeenCalled();
  });

  it('refuses another owner’s cursor in the live session and in the retained entry', async () => {
    const f = await fixture('session');
    const other = await f.login();
    const asOther = { headers: { cookie: other.cookie } };
    f.own.mockReturnValueOnce(withNext('raw-first'));
    const first = sealed.exec((await f.run('GET', '/account/data')).html)![1]!;
    f.own.mockReturnValueOnce(withNext('raw-other'));
    const foreign = sealed.exec(
      (await f.run('GET', '/account/data', undefined, asOther)).html,
    )![1]!;
    f.own.mockClear();
    // Each owner's own cursor opens; the other's is refused before any read.
    expect((await f.run('GET', '/account/data?cursor=' + first)).statusCode).toBe(200);
    expect(
      (await f.run('GET', '/account/data?cursor=' + foreign, undefined, asOther)).statusCode,
    ).toBe(200);
    expect(operations(f).map((operation) => (operation as { cursor?: string }).cursor)).toEqual([
      'raw-first',
      'raw-other',
    ]);
    f.own.mockClear();
    expect((await f.run('GET', '/account/data?cursor=' + foreign)).statusCode).toBe(400);
    expect(
      (await f.run('GET', '/account/data?cursor=' + first, undefined, asOther)).statusCode,
    ).toBe(400);
    expect(f.own).not.toHaveBeenCalled();

    // The same holds between two retained entries.
    const entry = await fixture('entry');
    const stranger = await entry.enter();
    const asStranger = { headers: { cookie: stranger.cookie } };
    entry.retained.mockImplementationOnce(async () => withNext('raw-entry-a'));
    entry.retained.mockImplementationOnce(async () => withNext('raw-entry-b'));
    const a = sealed.exec((await entry.run('GET', '/account/data?group=tenant')).html)![1]!;
    const b = sealed.exec(
      (await entry.run('GET', '/account/data?group=tenant', undefined, asStranger)).html,
    )![1]!;
    entry.retained.mockClear();
    expect((await entry.run('GET', '/account/data?group=tenant&cursor=' + b)).statusCode).toBe(400);
    expect(
      (await entry.run('GET', '/account/data?group=tenant&cursor=' + a, undefined, asStranger))
        .statusCode,
    ).toBe(400);
    expect(entry.retained).not.toHaveBeenCalled();
  });

  it('refuses a cursor of another purpose: a chooser cursor in a group and the reverse', async () => {
    const f = await fixture('entry');
    f.retained.mockImplementationOnce(async () => withNext('raw-group-cursor'));
    const group = sealed.exec((await f.run('GET', '/account/data?group=tenant')).html)![1]!;
    f.retained.mockImplementationOnce(async () => withNext('raw-scope-cursor'));
    const scopes = /scopeCursor=([A-Za-z0-9_-]+)"/.exec(
      (await f.run('GET', '/account/data')).html,
    )![1]!;
    f.retained.mockClear();
    expect((await f.run('GET', '/account/data?group=tenant&cursor=' + scopes)).statusCode).toBe(
      400,
    );
    expect((await f.run('GET', '/account/data?scopeCursor=' + group)).statusCode).toBe(400);
    expect(f.retained).not.toHaveBeenCalled();
    // Each opens for its own purpose.
    expect((await f.run('GET', '/account/data?group=tenant&cursor=' + group)).statusCode).toBe(200);
    expect((await f.run('GET', '/account/data?scopeCursor=' + scopes)).statusCode).toBe(200);
  });

  it('refuses an entry’s cursor in a session and a session’s cursor in an entry', async () => {
    const f = await fixture('both');
    const sessionOnly = f.session!.cookie;
    const entryOnly =
      '__Host-ls-entry=' +
      f.entry!.finished.handle +
      '; __Host-ls-browser=' +
      f.session!.finished.browser;
    f.own.mockReturnValueOnce(withNext('raw-session-cursor'));
    f.retained.mockImplementationOnce(async () => withNext('raw-entry-cursor'));
    const fromSession = sealed.exec(
      (await f.run('GET', '/account/data', undefined, { headers: { cookie: sessionOnly } })).html,
    )![1]!;
    const fromEntry = sealed.exec(
      (
        await f.run('GET', '/account/data?group=tenant', undefined, {
          headers: { cookie: entryOnly },
        })
      ).html,
    )![1]!;
    f.own.mockClear();
    f.retained.mockClear();
    expect(
      (
        await f.run('GET', '/account/data?cursor=' + fromEntry, undefined, {
          headers: { cookie: sessionOnly },
        })
      ).statusCode,
    ).toBe(400);
    expect(
      (
        await f.run('GET', '/account/data?group=tenant&cursor=' + fromSession, undefined, {
          headers: { cookie: entryOnly },
        })
      ).statusCode,
    ).toBe(400);
    expect(f.own).not.toHaveBeenCalled();
    expect(f.retained).not.toHaveBeenCalled();
  });
});

describe('form keys are fresh, distinct and reach the API as sent (ADR-0106 § 10)', () => {
  const states = (): OwnDataRequest[] => [
    request({ id: '019a154b-0000-7000-8000-000000000001' }),
    request({
      id: '019a154b-0000-7000-8000-000000000002',
      status: 'completed',
      completedAt: '2026-11-22T10:00:00Z',
      artifact: artifact(),
    }),
    request({
      id: '019a154b-0000-7000-8000-000000000003',
      status: 'completed',
      completedAt: '2026-11-22T10:00:00Z',
      artifact: artifact({ id: '019a154b-0000-7000-8000-0000000000f3' }),
    }),
    request({
      id: '019a154b-0000-7000-8000-000000000004',
      kind: 'erasure',
      status: 'scheduled',
      executeAt: '2026-11-24T10:00:00Z',
    }),
    request({
      id: '019a154b-0000-7000-8000-000000000005',
      kind: 'closure',
      status: 'scheduled',
      executeAt: '2026-11-24T10:00:00Z',
    }),
  ];

  it.each(['session', 'entry'] as const)(
    'gives every keyed form of a %s page its own key and a new page new keys',
    async (mode) => {
      const f = await fixture(mode, states());
      const target = mode === 'session' ? '/account/data' : '/account/data?group=tenant';
      const first = formKeys((await f.run('GET', target)).html);
      // Two cancellations, two revocations, one more cancellation and the export filing.
      expect(first.length).toBeGreaterThanOrEqual(5);
      expect(new Set(first).size).toBe(first.length);
      const again = formKeys((await f.run('GET', target)).html);
      expect(again).toHaveLength(first.length);
      expect(new Set(again).size).toBe(again.length);
      expect(again.filter((key) => first.includes(key))).toEqual([]);
      // The confirmation steps (open only where nothing of that kind is active) hold keys of their own.
      const idle = await fixture(mode);
      const steps: string[] = [];
      for (const step of ['erasure', 'closure'])
        steps.push(
          ...formKeys(
            (await idle.run('GET', target + (target.includes('?') ? '&' : '?') + 'step=' + step))
              .html,
          ),
        );
      expect(steps).toHaveLength(2);
      expect(new Set([...first, ...again, ...steps]).size).toBe(first.length * 2 + 2);
    },
  );

  it('sends the same key to the operation again on a double submit and answers the same redirect', async () => {
    const f = await fixture('session');
    const key = keyFor('twice');
    const form = { csrfToken: f.csrf, idempotencyKey: key };
    // The API replays the first outcome for the same key; the page adds nothing of its own to the replay.
    f.own.mockReturnValueOnce(ok({ id, kind: 'export', status: 'requested', version: '0' }));
    f.own.mockReturnValueOnce({
      ...ok({ id, kind: 'export', status: 'requested', version: '0' }),
      replayed: true,
    } as NativeOperationResult);
    const first = await f.run('POST', '/api/account/data-requests/export', form);
    const again = await f.run('POST', '/api/account/data-requests/export', form);
    for (const wire of [first, again]) {
      expect(wire.statusCode).toBe(303);
      expect(wire.headers.get('location')).toBe('/account/data?result=export');
      expect(wire.html).toBe('');
    }
    expect(operations(f)).toEqual([
      { kind: 'createOwnDataExport', idempotencyKey: key },
      { kind: 'createOwnDataExport', idempotencyKey: key },
    ]);
    // A different key is a different filing.
    const next = await f.run('POST', '/api/account/data-requests/export', {
      csrfToken: f.csrf,
      idempotencyKey: keyFor('thrice'),
    });
    expect(next.statusCode).toBe(303);
    expect(
      operations(f).map((operation) => (operation as { idempotencyKey: string }).idempotencyKey),
    ).toEqual([key, key, keyFor('thrice')]);
  });

  it('refuses the same key reused for another request with the API’s own sentence', async () => {
    const f = await fixture('session');
    f.own.mockReturnValueOnce(refused(409, 'idempotency_key_reuse'));
    const wire = await f.run('POST', '/api/account/data-requests/export', {
      csrfToken: f.csrf,
      idempotencyKey: keyFor('reused'),
    });
    expect(wire.statusCode).toBe(409);
    expect(wire.html).toContain(shown(en.error.key_reuse));
  });
});

describe('the download POST refuses every hostile form before any mint', () => {
  const sessionForm = (extra: Record<string, string>, csrfToken: string) => ({
    csrfToken,
    requestId: id,
    ...extra,
  });
  it('in a live session', async () => {
    const f = await fixture('session');
    const cases: [string, Record<string, string> | URLSearchParams, number][] = [
      ['a malformed request', sessionForm({ requestId: 'not-a-uuid' }, f.csrf), 400],
      [
        'a version-4 request',
        sessionForm({ requestId: '019a154b-0000-4000-8000-000000000041' }, f.csrf),
        400,
      ],
      ['an upper-case request', sessionForm({ requestId: id.toUpperCase() }, f.csrf), 400],
      ['a missing request', { csrfToken: f.csrf }, 400],
      ['an empty request', sessionForm({ requestId: '' }, f.csrf), 400],
      ['a stray key', sessionForm({ idempotencyKey: keyFor('stray') }, f.csrf), 400],
      ['a stray version', sessionForm({ version: '3' }, f.csrf), 400],
      ['a stray group', sessionForm({ group: 'tenant' }, f.csrf), 400],
      ['a stray tenant', sessionForm({ tenantId: id }, f.csrf), 400],
      [
        'a repeated request',
        new URLSearchParams([
          ['csrfToken', f.csrf],
          ['requestId', id],
          ['requestId', second],
        ]),
        400,
      ],
      ['a wrong proof', sessionForm({}, 'x'.repeat(43)), 403],
      ['no proof', { requestId: id }, 403],
    ];
    for (const [name, form, status] of cases) {
      const wire = await f.run('POST', '/api/account/data-requests/download', form);
      expect(wire.statusCode, name).toBe(status);
      expect(wire.headers.has('content-disposition'), name).toBe(false);
    }
    expect(f.own).not.toHaveBeenCalled();
    expect(f.retained).not.toHaveBeenCalled();
  });

  it('in the retained entry', async () => {
    const f = await fixture('entry');
    const valid = { csrfToken: f.csrf, requestId: id, group: 'tenant' };
    const cases: [string, Record<string, string> | URLSearchParams, number][] = [
      ['a missing group', { csrfToken: f.csrf, requestId: id }, 400],
      ['an empty group', { ...valid, group: '' }, 400],
      ['an upper-case group', { ...valid, group: 'Tenant' }, 400],
      ['a version-4 group', { ...valid, group: '019a154b-0000-4000-8000-0000000000a1' }, 400],
      ['a malformed request', { ...valid, requestId: 'not-a-uuid' }, 400],
      ['a missing request', { csrfToken: f.csrf, group: 'tenant' }, 400],
      ['a stray key', { ...valid, idempotencyKey: keyFor('stray') }, 400],
      ['a stray version', { ...valid, version: '3' }, 400],
      ['a stray tenant', { ...valid, tenantId: id }, 400],
      [
        'a repeated group',
        new URLSearchParams([...Object.entries(valid), ['group', organization]]),
        400,
      ],
      ['a wrong proof', { ...valid, csrfToken: 'x'.repeat(43) }, 403],
    ];
    for (const [name, form, status] of cases) {
      const wire = await f.run('POST', '/api/auth/entry/data-requests/download', form);
      expect(wire.statusCode, name).toBe(status);
      expect(wire.headers.has('content-disposition'), name).toBe(false);
    }
    expect(f.own).not.toHaveBeenCalled();
    expect(f.retained).not.toHaveBeenCalled();
  });
});

describe('the Origin of a posted action is compared whole', () => {
  it('refuses a duplicated Origin header, however its value was joined', async () => {
    const f = await fixture('session');
    const form = { csrfToken: f.csrf, idempotencyKey: keyFor('origin') };
    const host = 'https://institution.example.test:3000';
    for (const origin of [
      host + ', https://evil.test',
      'https://evil.test, ' + host,
      host + ', ' + host,
      host + ',' + host,
    ]) {
      const wire = await f.run('POST', '/api/account/data-requests/export', form, { origin });
      expect(wire.statusCode, origin).toBe(400);
    }
    // The list form a header parser could also produce.
    const list = await f.run('POST', '/api/account/data-requests/export', form, {
      origin: [host, host],
    });
    expect(list.statusCode).toBe(400);
    expect(f.own).not.toHaveBeenCalled();
    // The exact Origin files.
    expect(
      (await f.run('POST', '/api/account/data-requests/export', form, { origin: host })).statusCode,
    ).toBe(303);
  });

  it('is reached with the joined value only, because Node joins repeated Origin lines with a comma', async () => {
    const received = await new Promise<string | undefined>((resolve, reject) => {
      const server = createServer((incoming, response) => {
        response.end();
        server.close();
        resolve(incoming.headers.origin);
      });
      server.listen(0, '127.0.0.1', () => {
        const { port } = server.address() as { port: number };
        const socket = connect(port, '127.0.0.1', () =>
          socket.end(
            'POST / HTTP/1.1\r\nHost: x\r\nOrigin: https://a.test\r\nOrigin: https://b.test\r\nContent-Length: 0\r\nConnection: close\r\n\r\n',
          ),
        );
        socket.on('error', reject);
        socket.resume();
      });
    });
    expect(received).toBe('https://a.test, https://b.test');
  });
});
