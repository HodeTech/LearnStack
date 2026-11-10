// Focused UI/native transport observations inside verify-bff-browser's one owned browser lifetime.
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';

import { customFieldBrowserNoticeHash } from './bff-custom-fields-fixture.mjs';
import { withProofWaiters } from './bff-proof-support.mjs';

const copies = Object.fromEntries(
  ['en', 'tr'].map((locale) => [
    locale,
    JSON.parse(
      readFileSync(
        new URL('../src/i18n/messages/' + locale + '/admin.json', import.meta.url),
        'utf8',
      ),
    ).custom_fields,
  ]),
);
const sensitiveId = '019a154b-0000-7000-8000-000000000102';
const complexId = '019a154b-0000-7000-8000-000000000101';

async function submit(
  page,
  form,
  path,
  origin,
  activate = () => form.locator('button[type="submit"]').click(),
) {
  const [request, response] = await withProofWaiters(
    [
      page.waitForRequest(
        (request) => request.method() === 'POST' && new URL(request.url()).pathname === path,
      ),
      page.waitForResponse(
        (response) =>
          response.request().method() === 'POST' && new URL(response.url()).pathname === path,
      ),
    ],
    activate,
  );
  assert.equal(await request.headerValue('origin'), origin);
  assert.equal(response.status(), 303);
  assert.equal(await response.headerValue('cache-control'), 'no-store');
  assert.ok(Buffer.byteLength(request.postData() ?? '') <= 16384);
  return new URLSearchParams(request.postData());
}

export async function verifyCustomFieldBrowser({ page, site, mark }) {
  const origin = 'https://' + site.host;
  const copy = copies[site.locale.startsWith('tr') ? 'tr' : 'en'];
  for (const [type, category] of [
    ['boolean', 'None'],
    ['number', 'Identity'],
    ['enum', 'Behaviour'],
  ]) {
    mark('browser-custom-field-author-' + type);
    await page.goto(origin + '/studio/custom-fields');
    const form = page.locator('form.custom-field-js-builder');
    await form.waitFor({ state: 'visible' });
    assert.equal(await form.locator('#definition-category').inputValue(), '');
    await form.locator('#definition-key').fill('browser-' + type);
    const label =
      type === 'boolean' ? '<img src=x onerror=alert(1)> browser flag' : 'Browser ' + type;
    await form.locator('#definition-control-label-' + site.locale).fill(label);
    await form.locator('#definition-control-type').selectOption(type);
    await form.locator('#definition-category').selectOption(category);
    if (type === 'number') {
      await form.locator('#definition-control-min').fill('-3.5');
      await form.locator('#definition-control-max').fill('3.5');
    }
    if (type === 'enum') {
      await form.locator('#definition-control-choice-0').fill('first');
      await form.locator('#definition-control-choice-0-' + site.locale).fill('First choice');
      await form.locator('#definition-control-add-choice').click();
      await page.waitForFunction(
        () => document.activeElement?.id === 'definition-control-choice-1',
      );
      await form.locator('#definition-control-choice-1').fill('second');
      await form.locator('#definition-control-choice-1-' + site.locale).fill('Second choice');
    }
    assert.equal(await page.locator('img').count(), 0, 'authored markup remains literal text');
    const fields = await submit(page, form, '/api/studio/custom-field-create', origin);
    const payload = JSON.parse(fields.get('payload'));
    assert.equal(payload.piiCategory, category);
    assert.equal(typeof payload.schema, 'object');
    assert.equal(payload.schema.properties.value.type, type === 'enum' ? 'string' : type);
    if (type === 'enum')
      assert.deepEqual(payload.schema.properties.value.enum, ['first', 'second']);
    if (type === 'number')
      assert.deepEqual(
        [payload.schema.properties.value.minimum, payload.schema.properties.value.maximum],
        [-3.5, 3.5],
      );
    await page.waitForURL(origin + '/studio/custom-fields');
    const item = page.getByRole('heading', { level: 2, name: label, exact: true });
    await item.locator('a').click();
    await page.waitForURL((url) => url.pathname.startsWith('/studio/custom-fields/'));
    await page.getByRole('heading', { level: 2, name: label, exact: true }).waitFor();
    assert.equal(await page.locator('img').count(), 0);
  }

  mark('browser-custom-field-own-consent');
  await page.goto(origin + '/portal/profile');
  assert.equal(
    await page.locator('form[action="/api/account/profile"]').count(),
    0,
    'fixture denies ProfileWrite while own consent remains admitted',
  );
  assert.equal((await page.content()).includes('BROWSER_WITHHELD_CANARY'), false);
  const sensitive = page
    .locator('section[aria-labelledby="member-custom-fields-title"] .auth-resource-list > li')
    .filter({
      has: page.getByRole('heading', { level: 3, name: 'Fixture sensitive', exact: true }),
    });
  const grant = sensitive.locator('form[action="/api/account/consent-grant"]');
  await grant.waitFor();
  assert.equal(await grant.locator('[name="definitionId"]').inputValue(), sensitiveId);
  const confirm = grant.locator('input[name="confirmed"]');
  await confirm.focus();
  await page.keyboard.press('Space');
  assert.equal(await confirm.isChecked(), true);
  await page.keyboard.press('Tab');
  assert.equal(
    await grant.getByRole('button').evaluate((button) => button === document.activeElement),
    true,
  );
  const focusStyle = await grant.getByRole('button').evaluate((button) => ({
    style: getComputedStyle(button).outlineStyle,
    width: getComputedStyle(button).outlineWidth,
  }));
  assert.notEqual(focusStyle.style, 'none');
  assert.notEqual(focusStyle.width, '0px');
  const consentFields = await submit(page, grant, '/api/account/consent-grant', origin, () =>
    page.keyboard.press('Enter'),
  );
  assert.equal(consentFields.get('confirmed'), 'true');
  assert.equal(consentFields.get('noticeLocale'), 'en');
  assert.equal(consentFields.get('expectedNoticeHash'), customFieldBrowserNoticeHash);
  await page.waitForURL(origin + '/portal/profile');
  await sensitive.getByText('BROWSER_WITHHELD_CANARY', { exact: true }).waitFor();
  assert.equal(
    await page.locator('form[action="/api/account/profile"]').count(),
    0,
    'personal consent does not grant ProfileWrite',
  );

  mark('browser-custom-field-retirement');
  await page.goto(origin + '/studio/custom-fields/' + sensitiveId);
  const retirement = page.locator('form[action="/api/studio/custom-field-retire"]');
  await retirement.locator('..').locator('summary').click();
  await retirement.locator('input[type="checkbox"]').check();
  await submit(page, retirement, '/api/studio/custom-field-retire', origin);
  await page.waitForURL(origin + '/studio/custom-fields/' + sensitiveId);
  await page.goto(origin + '/portal/profile?field=' + sensitiveId);
  const retiredSensitive = page
    .locator('section[aria-labelledby="member-custom-fields-title"] .auth-resource-list > li')
    .filter({
      has: page.getByRole('heading', { level: 3, name: 'Fixture sensitive', exact: true }),
    });
  await retiredSensitive.getByText(copy.retired, { exact: true }).waitFor();
  assert.equal(await retiredSensitive.locator('a[href*="?field="]').count(), 0);
  assert.equal(await retiredSensitive.locator('[name^="answer_"]').count(), 0);
  assert.equal(
    await retiredSensitive.locator('form[action="/api/account/consent-withdraw"]').count(),
    1,
  );
  assert.equal((await page.locator('a[href="/account/consents"]').count()) > 0, true);
}

export async function verifyCustomFieldNoScript({ page, site, mark, replacement }) {
  const origin = 'https://' + site.host;
  mark('browser-custom-field-no-script');
  await page.goto(origin + '/portal/profile?field=' + complexId);
  const form = page.locator('section[aria-labelledby="custom-field-native-fallback-title"] form');
  await form.waitFor({ state: 'visible' });
  const count = form.locator('select[name="count_0_root"]');
  assert.equal(await count.getAttribute('id'), 'ns-0-root-count');
  assert.equal(await form.locator('label[for="ns-0-root-count"]').count(), 1);
  await count.selectOption(String(replacement.length));
  assert.equal(await form.locator('[name^="answer_"]').count(), 3);
  for (let index = 0; index < replacement.length; ++index) {
    const item = form.locator('details').nth(index);
    if (!(await item.evaluate((details) => details.open))) await item.locator('summary').click();
    await item
      .locator('select[name="answer_0_root_' + index + '"]')
      .selectOption(String(replacement[index]));
  }
  assert.equal(
    await form.locator('textarea').count(),
    0,
    'the no-script journey uses typed controls',
  );
  const fields = await submit(page, form, '/api/account/custom-fields', origin);
  assert.equal(fields.get('count_0_root'), String(replacement.length));
  assert.equal(fields.get('answer_0_root_0'), String(replacement[0]));
  assert.equal(fields.get('answer_0_root_1'), String(replacement[1]));
  assert.equal(
    fields.get('answer_0_root_2'),
    '',
    'unused declared controls are submitted without becoming answers',
  );
  assert.equal(fields.has('controlSchema'), false);
  const patch = JSON.parse(fields.get('payload'));
  assert.equal(patch.set[0].definitionId, complexId);
  assert.equal(
    patch.set[0].envelope.value,
    null,
    'only the server reconstructs the selected typed answers',
  );
  await page.waitForURL(origin + '/portal/profile');
  const saved = page
    .locator('section[aria-labelledby="member-custom-fields-title"] .auth-resource-list > li')
    .filter({ has: page.getByRole('heading', { level: 3, name: 'Fixture list', exact: true }) });
  assert.equal(await saved.locator('ol > li').count(), replacement.length);
  const copy = copies[site.locale.startsWith('tr') ? 'tr' : 'en'];
  assert.deepEqual(
    await saved.locator('ol > li').allTextContents(),
    replacement.map((value) => (value ? copy.yes : copy.no)),
  );
}
