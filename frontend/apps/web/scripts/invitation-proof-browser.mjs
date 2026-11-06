import assert from 'node:assert/strict';

import { withProofWaiters } from './bff-proof-support.mjs';

/** Two finite browser journeys over the real pre-Next HTTPS invitation handler. */
export async function observeInvitationCapture(browser, origin, code, privateFree, mark) {
  let networkPrivateFree = true;
  let browserPrivateFree = true;
  let historyPrivateFree = true;
  let exactOrigin = true;
  let captures = 0;
  for (const javaScriptEnabled of [true, false]) {
    const context = await browser.newContext({ javaScriptEnabled });
    context.setDefaultTimeout(10000);
    context.setDefaultNavigationTimeout(10000);
    try {
      const page = await context.newPage();
      const cdp = await context.newCDPSession(page);
      let documents = 0;
      context.on('request', (request) => {
        if (request.resourceType() !== 'document') return;
        documents++;
        networkPrivateFree &&= documents <= 12 && privateFree(request.url());
      });
      mark(javaScriptEnabled ? 'browser-invitation-capture' : 'browser-invitation-noscript');
      const capture = page.waitForRequest(
        (request) => new URL(request.url()).pathname === '/api/auth/invitation/capture',
      );
      const arrived = page.waitForURL(origin + '/invitations/confirm', {
        waitUntil: 'domcontentloaded',
      });
      const [submitted] = await withProofWaiters([capture, arrived], async () => {
        if (javaScriptEnabled) {
          await page.goto(origin + '/invitations#' + code, { waitUntil: 'commit' });
        } else {
          const landing = await page.goto(origin + '/invitations', {
            waitUntil: 'domcontentloaded',
          });
          assert.equal(landing.status(), 200);
          assert.equal(await page.locator('main[data-invitation-state="landing"]').count(), 1);
          await page.locator('input[name="code"]').fill(code);
          await page
            .locator('form[action="/api/auth/invitation/capture"] button[type="submit"]')
            .click();
        }
      });
      mark('browser-invitation-origin');
      assert.equal(submitted.method(), 'POST');
      const fields = new URLSearchParams(submitted.postData());
      assert.equal([...fields.keys()].sort().join(','), 'code,csrfToken');
      assert.ok(fields.get('code') === code, 'capture retains the bounded submitted value');
      assert.match(fields.get('csrfToken'), /^[A-Za-z0-9_-]{43}$/);
      const headers = await submitted.allHeaders();
      exactOrigin &&= headers.origin === origin;
      assert.equal(new URL(headers.referer).pathname, '/invitations');
      assert.equal(new URL(headers.referer).hash, '');
      captures++;
      mark('browser-invitation-confirm');
      assert.equal(await page.locator('main[data-invitation-state="signin"]').count(), 1);
      browserPrivateFree &&= privateFree(page.url()) && privateFree(await page.content());
      if (javaScriptEnabled) {
        browserPrivateFree &&= privateFree(
          await page.evaluate(() =>
            JSON.stringify({
              history: history.state,
              local: Object.entries(localStorage),
              session: Object.entries(sessionStorage),
            }),
          ),
        );
        mark('browser-invitation-history');
        await page.goto(origin + '/invitations', { waitUntil: 'domcontentloaded' });
        await page.goBack({ waitUntil: 'domcontentloaded' });
        await page.waitForURL(origin + '/invitations/confirm', { waitUntil: 'domcontentloaded' });
        assert.equal(await page.locator('main[data-invitation-state="signin"]').count(), 1);
      }
      const navigation = await cdp.send('Page.getNavigationHistory');
      historyPrivateFree &&=
        navigation.entries.length <= 8 &&
        navigation.entries.every((entry) => privateFree(entry.url) && privateFree(entry.title));
      mark('browser-invitation-cancel');
      // Release each captured custody owner through its actual native form.
      await page
        .locator('form[action="/api/auth/invitation/cancel"] button[type="submit"]')
        .click();
      await page.waitForURL(origin + '/invitations', { waitUntil: 'domcontentloaded' });
    } finally {
      await context.close();
    }
  }
  return {
    captures,
    script: true,
    noScript: true,
    exactOrigin,
    networkPrivateFree,
    browserPrivateFree,
    historyPrivateFree,
  };
}
