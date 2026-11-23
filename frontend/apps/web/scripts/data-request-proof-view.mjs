import assert from 'node:assert/strict';

const uuid = /^[0-9a-f]{8}-[0-9a-f]{4}-7[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/;
const views = new Set([
  'signin',
  'error',
  'session',
  'session_erasure',
  'session_closure',
  'entry_scopes',
  'entry',
  'entry_erasure',
  'entry_closure',
]);
const keyed = new Set(['export', 'erasure', 'closure', 'cancel', 'revoke']);
const targeted = new Set(['cancel', 'revoke', 'download']);

/** Tiny observations of actual native HTML; no proof, key, identifier or page text leaves this projection. */
export function dataRequestProofView(html, policy) {
  assert.ok(typeof html === 'string' && Buffer.byteLength(html) <= 256 * 1024);
  assert.ok(policy === undefined || (typeof policy === 'string' && policy.length <= 4096));
  const view = /<main\b[^>]*\bdata-data-request-view="([a-z_]+)"/.exec(html)?.[1] ?? null;
  assert.ok(view === null || views.has(view));
  let filingForms = 0;
  let actionForms = 0;
  let downloadForms = 0;
  let confirmationProtected = true;
  for (const form of html.matchAll(/<form\b([^>]*)>([\s\S]*?)<\/form>/g)) {
    const action = /\baction="([^"]+)"/.exec(form[1])?.[1];
    const match = /^\/api\/(?:account|auth\/entry)\/data-requests(?:\/([a-z]+))?$/.exec(
      action ?? '',
    );
    if (!match) continue;
    assert.match(form[1], /\bmethod="post"/);
    const field = (name) => [
      ...form[2].matchAll(new RegExp('\\bname="' + name + '" value="([^"]*)"', 'g')),
    ];
    const name = match[1] ?? 'view';
    const entry = action.startsWith('/api/auth/entry/');
    assert.equal(field('csrfToken').length, 1);
    assert.match(field('csrfToken')[0][1], /^[A-Za-z0-9_-]{43}$/);
    assert.equal(field('idempotencyKey').length, keyed.has(name) ? 1 : 0);
    if (keyed.has(name)) assert.match(field('idempotencyKey')[0][1], /^[A-Za-z0-9_-]{43}$/);
    assert.equal(field('requestId').length, targeted.has(name) ? 1 : 0);
    if (targeted.has(name)) assert.match(field('requestId')[0][1], uuid);
    assert.equal(field('group').length, entry ? 1 : 0);
    if (entry) assert.ok(field('group')[0][1] === 'tenant' || uuid.test(field('group')[0][1]));
    if (name === 'cancel' || name === 'revoke')
      assert.match(field('version')[0][1], /^(0|[1-9][0-9]{0,18})$/);
    if (name === 'erasure' || name === 'closure') {
      const boxes = [...form[2].matchAll(/<input type="checkbox"([^>]*)>/g)];
      const expected =
        name === 'erasure' ? ['confirm:erase'] : ['confirm:close', 'acknowledge:no_status'];
      confirmationProtected &&=
        /<fieldset><legend>[^<]+<\/legend>/.test(form[2]) &&
        boxes.length === expected.length &&
        boxes.every(
          (box, index) =>
            / required\b/.test(box[1]) &&
            box[1].includes(
              'name="' +
                expected[index].split(':')[0] +
                '" value="' +
                expected[index].split(':')[1] +
                '"',
            ),
        );
    }
    if (name === 'download') assert.ok(++downloadForms <= 20);
    else if (keyed.has(name) && name !== 'cancel' && name !== 'revoke')
      assert.ok(++filingForms <= 3);
    else assert.ok(++actionForms <= 40);
  }
  const scripts = [...html.matchAll(/<script\b([^>]*)>([\s\S]*?)<\/script>/g)];
  const nonce =
    scripts.length === 1 ? /\bnonce="([A-Za-z0-9+/]{22}==)"/.exec(scripts[0][1])?.[1] : undefined;
  const script = scripts[0]?.[2] ?? '';
  const scriptPolicy = /(?:^|;)\s*script-src\s+([^;]+)/.exec(policy ?? '')?.[1]?.trim();
  // The one fixed destination: the bare page, or a retained group's page on exactly a retained group's view. No
  // result, step or cursor is restored, and every restoration goes to the address the page recorded.
  const destination = /history\.replaceState\(null,'','([^']*)'\)/.exec(script)?.[1];
  const group = /^\/account\/data(?:\?group=(.+))?$/.exec(destination ?? '');
  const grouped = ['entry', 'entry_erasure', 'entry_closure'].includes(view ?? '');
  const historyProtected =
    nonce !== undefined &&
    scriptPolicy === "'nonce-" + nonce + "'" &&
    html.indexOf('<script') < html.indexOf('<body>') &&
    group !== null &&
    (group[1] === undefined
      ? !grouped
      : grouped && (group[1] === 'tenant' || uuid.test(group[1]))) &&
    script.includes("addEventListener('pagehide'") &&
    script.includes("addEventListener('pageshow'") &&
    script.includes('e.persisted||leaving') &&
    script.includes("document.documentElement.style.visibility='hidden'") &&
    script.includes("location.replace('" + destination + "')") &&
    script.split('location.replace(').length ===
      script.split("location.replace('" + destination + "')").length &&
    !script.includes('location.reload');
  return Object.freeze({
    kind: 'data_request_document',
    view,
    filingForms,
    actionForms,
    downloadForms,
    confirmationProtected,
    historyProtected,
  });
}
