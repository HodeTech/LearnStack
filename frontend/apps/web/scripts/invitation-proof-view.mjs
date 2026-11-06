import assert from 'node:assert/strict';

const states = new Set([
  'landing',
  'signin',
  'bootstrap',
  'inspect',
  'forbidden',
  'ready',
  'accepted',
  'unknown',
  'unavailable',
  'expired',
  'account_change',
]);
const actions = new Map([
  ['/api/auth/invitation/capture', 'csrfToken'],
  ['/api/auth/invitation/cancel', 'csrfToken'],
  ['/api/auth/signin/keycloak', 'signinCsrfToken'],
  ['/api/auth/entry/create', 'entryCsrfToken'],
  ['/api/auth/signout', 'sessionCsrfToken'],
]);

/** Fixed safe native-view projection for the existing private fixture pipe. */
export function invitationProofView(html) {
  assert.ok(typeof html === 'string' && Buffer.byteLength(html) <= 256 * 1024);
  const statesFound = [...html.matchAll(/data-invitation-state="([a-z_]+)"/g)];
  assert.equal(statesFound.length, 1);
  const state = statesFound[0][1];
  assert.ok(states.has(state));
  const value = {
    state,
    hasRecipientAddress:
      /[A-Za-z0-9][A-Za-z0-9.!#$%&'*+/=?^_`{|}~-]*@[A-Za-z0-9.-]+\.[A-Za-z]{2,}/.test(html),
  };
  for (const form of html.matchAll(/<form\b([^>]*)>([\s\S]*?)<\/form>/g)) {
    const action = /\baction="([^"]+)"/.exec(form[1])?.[1];
    if (!actions.has(action) && action !== '/api/auth/invitation/accept') continue;
    assert.match(form[1], /\bmethod="post"/);
    const csrf = [...form[2].matchAll(/name="csrfToken" value="([A-Za-z0-9_-]{43})"/g)];
    assert.equal(csrf.length, 1);
    const key = actions.get(action) ?? 'csrfToken';
    assert.ok(value[key] === undefined || value[key] === csrf[0][1]);
    value[key] = csrf[0][1];
    if (action === '/api/auth/invitation/accept') {
      const version = /name="version" value="(0|[1-9][0-9]{0,18})"/.exec(form[2])?.[1];
      assert.ok(version !== undefined);
      value.version = version;
    }
  }
  if (state === 'accepted') {
    const disposition = /data-invitation-disposition="(created|already_member)"/.exec(html)?.[1];
    const session = /data-invitation-session="(ready|unavailable)"/.exec(html)?.[1];
    assert.ok(disposition && session);
    value.disposition = disposition;
    value.sessionAvailable = session === 'ready';
  }
  return value;
}
