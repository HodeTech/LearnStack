import assert from 'node:assert/strict';
import { test } from 'node:test';

import { consentProofView } from './consent-proof-view.mjs';
const id = '019a154b-0000-7000-8000-000000000011';
const nonce = 'A'.repeat(22) + '==';
const policy = "default-src 'none'; script-src 'nonce-" + nonce + "'; form-action 'self'";
// Independent proof subject. This contains only the security-bearing pieces the observation checks.
const guard =
  "history.replaceState(null,'','/account/consents');addEventListener('pagehide',()=>{document.documentElement.style.visibility='hidden'});addEventListener('pageshow',e=>{if(e.persisted||leaving)location.replace('/account/consents')})";
const form =
  '<form method="post" action="/api/auth/entry/consents"><input type="hidden" name="csrfToken" value="' +
  'P'.repeat(43) +
  '"><input type="hidden" name="membershipId" value="' +
  id +
  '"><button>View</button></form>';
const html =
  '<html><head><script nonce="' +
  nonce +
  '">' +
  guard +
  '</script></head><body>' +
  form +
  '<h2>Membership ' +
  id +
  '</h2><p>private-notice-canary</p></body></html>';
test('actual form/heading/nonce observations remain closed and payload-free', () => {
  const value = consentProofView(html, policy, id);
  assert.deepEqual(value, {
    kind: 'consent_entry',
    scopeForms: 1,
    historyLoaded: true,
    historyProtected: true,
  });
  for (const privateValue of [id, 'P'.repeat(43), 'private-notice-canary', html, nonce])
    assert.ok(!JSON.stringify(value).includes(privateValue));
  assert.equal(Object.isFrozen(value), true);
});
test('anonymous 200 HTML cannot masquerade as credentialed scope/history proof', () => {
  assert.deepEqual(
    consentProofView('<html><body><a href="/api/auth/signin">Sign in</a></body></html>'),
    { kind: 'consent_entry', scopeForms: 0, historyLoaded: false, historyProtected: false },
  );
});
test('wrong nonce, unrestricted script policy, removed veil and POST reload are visible counter-controls', () => {
  assert.equal(
    consentProofView(html, policy.replace(nonce, 'B'.repeat(22) + '=='), id).historyProtected,
    false,
  );
  assert.equal(
    consentProofView(html, policy.replace("'nonce-" + nonce + "'", "'unsafe-inline'"), id)
      .historyProtected,
    false,
  );
  assert.equal(
    consentProofView(
      html.replace("document.documentElement.style.visibility='hidden'", ''),
      policy,
      id,
    ).historyProtected,
    false,
  );
  assert.equal(
    consentProofView(
      html.replace("location.replace('/account/consents')", 'location.reload()'),
      policy,
      id,
    ).historyProtected,
    false,
  );
  assert.equal(consentProofView(html, policy).historyLoaded, false);
});
test('the observation refuses duplicate proof fields, extra scope rows and oversized HTML', () => {
  assert.throws(() =>
    consentProofView(
      html.replace('</form>', '<input name="csrfToken" value="' + 'P'.repeat(43) + '"></form>'),
      policy,
      id,
    ),
  );
  assert.throws(() => consentProofView('<body>' + form.repeat(21) + '</body>', policy, id));
  assert.throws(() => consentProofView('x'.repeat(256 * 1024 + 1), policy, id));
});
