import assert from 'node:assert/strict';

const membership = /^[0-9a-f]{8}-[0-9a-f]{4}-7[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/;

/** Tiny observations of actual native HTML; no owner proof, ID, notice or HTML leaves this projection. */
export function consentProofView(html, policy, selectedMembership = null) {
  assert.ok(typeof html === 'string' && Buffer.byteLength(html) <= 256 * 1024);
  assert.ok(
    selectedMembership === null ||
      (typeof selectedMembership === 'string' && membership.test(selectedMembership)),
  );
  assert.ok(policy === undefined || (typeof policy === 'string' && policy.length <= 4096));
  let scopeForms = 0;
  let withdrawForms = 0;
  for (const form of html.matchAll(/<form\b([^>]*)>([\s\S]*?)<\/form>/g)) {
    const action = /\baction="([^"]+)"/.exec(form[1])?.[1];
    if (!['/api/auth/entry/consents', '/api/auth/entry/consents/withdraw'].includes(action))
      continue;
    assert.match(form[1], /\bmethod="post"/);
    const csrf = [...form[2].matchAll(/\bname="csrfToken" value="([A-Za-z0-9_-]{43})"/g)];
    const ids = [...form[2].matchAll(/\bname="membershipId" value="([^"]+)"/g)];
    assert.equal(csrf.length, 1);
    assert.equal(ids.length, 1);
    assert.match(ids[0][1], membership);
    if (action === '/api/auth/entry/consents') assert.ok(++scopeForms <= 20);
    else {
      const evidence = [...form[2].matchAll(/\bname="evidenceId" value="([^"]+)"/g)];
      const previous = [...form[2].matchAll(/\bname="expectedEvidenceId" value="([^"]+)"/g)];
      const sequence = [
        ...form[2].matchAll(/\bname="expectedSequence" value="([1-9][0-9]{0,15})"/g),
      ];
      assert.equal(evidence.length, 1);
      assert.match(evidence[0][1], membership);
      assert.equal(previous.length, 1);
      assert.equal(previous[0][1], evidence[0][1]);
      assert.equal(sequence.length, 1);
      assert.ok(Number.isSafeInteger(Number(sequence[0][1])));
      assert.ok(selectedMembership !== null && ids[0][1] === selectedMembership);
      assert.ok(++withdrawForms <= 20);
    }
  }
  const scripts = [...html.matchAll(/<script\b([^>]*)>([\s\S]*?)<\/script>/g)];
  const nonce =
    scripts.length === 1 ? /\bnonce="([A-Za-z0-9+/]{22}==)"/.exec(scripts[0][1])?.[1] : undefined;
  const script = scripts[0]?.[2] ?? '';
  const scriptPolicy = /(?:^|;)\s*script-src\s+([^;]+)/.exec(policy ?? '')?.[1]?.trim();
  const historyProtected =
    nonce !== undefined &&
    scriptPolicy === "'nonce-" + nonce + "'" &&
    html.indexOf('<script') < html.indexOf('<body>') &&
    script.includes("history.replaceState(null,'','/account/consents')") &&
    script.includes("addEventListener('pagehide'") &&
    script.includes("addEventListener('pageshow'") &&
    script.includes('e.persisted||leaving') &&
    script.includes("document.documentElement.style.visibility='hidden'") &&
    script.includes("location.replace('/account/consents')") &&
    !script.includes('location.reload');
  const historyLoaded =
    selectedMembership !== null &&
    new RegExp('<h2\\b[^>]*>[^<>]*' + selectedMembership + '<\\/h2>').test(html);
  return Object.freeze({
    kind: 'consent_entry',
    scopeForms,
    withdrawForms,
    historyLoaded,
    historyProtected,
  });
}
