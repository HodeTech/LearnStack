import assert from 'node:assert/strict';
import { test } from 'node:test';

import { dataRequestProofView } from './data-request-proof-view.mjs';
const id = '019a154b-0000-7000-8000-000000000041';
const nonce = 'A'.repeat(22) + '==';
const proof = 'P'.repeat(43);
const key = 'K'.repeat(43);
const policy = "default-src 'none'; script-src 'nonce-" + nonce + "'; form-action 'self'";
// Independent proof subject. This contains only the security-bearing pieces the observation checks.
const guard =
  "history.replaceState(null,'','/account/data');addEventListener('pagehide',()=>{document.documentElement.style.visibility='hidden'});addEventListener('pageshow',e=>{if(e.persisted||leaving)location.replace('/account/data')})";
const hidden = (name, value) => '<input type="hidden" name="' + name + '" value="' + value + '">';
const filing =
  '<form method="post" action="/api/account/data-requests/export">' +
  hidden('csrfToken', proof) +
  hidden('idempotencyKey', key) +
  '<button>Ask</button></form>';
const cancel =
  '<form method="post" action="/api/account/data-requests/cancel">' +
  hidden('csrfToken', proof) +
  hidden('idempotencyKey', key) +
  hidden('requestId', id) +
  hidden('version', '3') +
  '<button>Cancel</button></form>';
const download =
  '<form method="post" action="/api/account/data-requests/download">' +
  hidden('csrfToken', proof) +
  hidden('requestId', id) +
  '<button>Download</button></form>';
const erasure =
  '<form method="post" action="/api/account/data-requests/erasure">' +
  hidden('csrfToken', proof) +
  hidden('idempotencyKey', key) +
  '<fieldset><legend>Confirm</legend><label class="check"><input type="checkbox" name="confirm" value="erase" required aria-required="true"><span>I understand</span></label></fieldset><button>Erase</button></form>';
const page = (body, view = 'session') =>
  '<html><head><script nonce="' +
  nonce +
  '">' +
  guard +
  '</script></head><body><main id="main" data-data-request-view="' +
  view +
  '">' +
  body +
  '<p>private-request-canary</p></main></body></html>';
test('actual form, confirmation and nonce observations remain closed and payload-free', () => {
  const value = dataRequestProofView(page(filing + cancel + download), policy);
  assert.deepEqual(value, {
    kind: 'data_request_document',
    view: 'session',
    filingForms: 1,
    actionForms: 1,
    downloadForms: 1,
    confirmationProtected: true,
    historyProtected: true,
  });
  for (const privateValue of [id, proof, key, 'private-request-canary', nonce])
    assert.ok(!JSON.stringify(value).includes(privateValue));
  assert.equal(Object.isFrozen(value), true);
  const confirmation = dataRequestProofView(page(erasure, 'session_erasure'), policy);
  assert.equal(confirmation.view, 'session_erasure');
  assert.equal(confirmation.confirmationProtected, true);
});
test('anonymous 200 HTML cannot masquerade as a credentialed request page', () => {
  assert.deepEqual(
    dataRequestProofView('<html><body><a href="/api/auth/signin">Sign in</a></body></html>'),
    {
      kind: 'data_request_document',
      view: null,
      filingForms: 0,
      actionForms: 0,
      downloadForms: 0,
      confirmationProtected: true,
      historyProtected: false,
    },
  );
});
test('wrong nonce, unrestricted script policy, removed veil, POST reload and an unchecked confirmation are visible', () => {
  const html = page(filing);
  assert.equal(
    dataRequestProofView(html, policy.replace(nonce, 'B'.repeat(22) + '==')).historyProtected,
    false,
  );
  assert.equal(
    dataRequestProofView(html, policy.replace("'nonce-" + nonce + "'", "'unsafe-inline'"))
      .historyProtected,
    false,
  );
  assert.equal(
    dataRequestProofView(
      html.replace("document.documentElement.style.visibility='hidden'", ''),
      policy,
    ).historyProtected,
    false,
  );
  assert.equal(
    dataRequestProofView(
      html.replace("location.replace('/account/data')", 'location.reload()'),
      policy,
    ).historyProtected,
    false,
  );
  assert.equal(
    dataRequestProofView(page(erasure.replace(' required ', ' ')), policy).confirmationProtected,
    false,
  );
  assert.equal(
    dataRequestProofView(
      page(erasure.replace('<fieldset><legend>Confirm</legend>', '<div>')),
      policy,
    ).confirmationProtected,
    false,
  );
});
test('the observation refuses duplicate proofs, missing keys, foreign targets, extra rows and oversized HTML', () => {
  assert.throws(() =>
    dataRequestProofView(
      page(filing.replace('</form>', hidden('csrfToken', proof) + '</form>')),
      policy,
    ),
  );
  assert.throws(() =>
    dataRequestProofView(page(cancel.replace(hidden('idempotencyKey', key), '')), policy),
  );
  assert.throws(() => dataRequestProofView(page(cancel.replace(id, 'not-a-request')), policy));
  assert.throws(() =>
    dataRequestProofView(page(filing.replace('/api/account/', '/api/auth/entry/')), policy),
  );
  assert.throws(() => dataRequestProofView(page(filing.repeat(4)), policy));
  assert.throws(() => dataRequestProofView(page(filing, 'studio'), policy));
  assert.throws(() => dataRequestProofView('x'.repeat(256 * 1024 + 1), policy));
});
