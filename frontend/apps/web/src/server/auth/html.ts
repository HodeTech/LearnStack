import 'server-only';

import { publicThemeCss } from '@/components/public/theme';
import { createAuthTranslator } from '@/i18n/auth-catalogues';
import { createPublicTranslator } from '@/i18n/catalogues';

import type { PublicSite } from '../public-entry';
import type { BffPage } from './http-protocol';

const css = `:root{--ls-primary:#075985;--ls-bg:#ffffff;--ls-fg:#172033;--ls-muted:#475569}*{box-sizing:border-box}body{margin:0;background:var(--ls-bg);color:var(--ls-fg);font:1rem/1.6 system-ui,sans-serif}main{width:min(100% - 2rem,36rem);margin:8vh auto;padding:2rem;border:1px solid currentColor;border-radius:.75rem;overflow-wrap:anywhere}h1{line-height:1.25}a{color:inherit;text-decoration:underline}button{max-width:100%;font:inherit;white-space:normal;background:var(--ls-bg);color:var(--ls-fg);border:2px solid currentColor;border-radius:.25rem;padding:.75rem 1rem;cursor:pointer}a:focus-visible,button:focus-visible,main:focus{outline:3px solid var(--ls-fg);outline-offset:4px}.skip{position:fixed;inset-inline-start:1rem;top:1rem;padding:.75rem;background:var(--ls-bg);transform:translateY(-200%)}.skip:focus{transform:none}`;
const escape = (value: string) =>
  value.replace(
    /[&<>"']/g,
    (character) =>
      ({
        '&': '&amp;',
        '<': '&lt;',
        '>': '&gt;',
        '"': '&quot;',
        "'": '&#39;',
      })[character]!,
  );

/** Closed middleware document, with every text/attribute escaped. No user HTML or script. */
export function renderBffDocument(
  site: PublicSite,
  page: BffPage | Readonly<{ kind: 'unavailable' | 'busy' }>,
): string {
  const ui = createAuthTranslator(site.defaultLocale);
  const { t } = createPublicTranslator(site.defaultLocale);
  const copy = ui.copy;
  const title =
    page.kind === 'signin'
      ? copy('signin.title')
      : page.kind === 'unavailable'
        ? copy('signin.unavailable_title')
        : copy('error.title');
  const content =
    page.kind === 'signin'
      ? `<p>${escape(site.displayName)}</p><p>${escape(copy('signin.description'))}</p>
      <form action="/api/auth/signin/keycloak" method="post">
      <input type="hidden" name="csrfToken" value="${escape(page.csrfToken)}">
      <input type="hidden" name="returnTo" value="${escape(page.returnPath)}">
      <button type="submit">${escape(copy('signin.continue'))}</button></form>`
      : `<p>${escape(
          page.kind === 'busy'
            ? copy('error.busy')
            : page.kind === 'unavailable'
              ? copy('signin.unavailable')
              : copy('error.description'),
        )}</p>
      ${page.kind === 'unavailable' ? '' : `<p><a href="/api/auth/signin">${escape(copy('error.retry'))}</a></p>`}
      <p><a href="/${escape(site.defaultLocale)}/courses">${escape(copy('shell.catalog'))}</a></p>`;
  return `<!doctype html><html lang="${escape(ui.locale)}" dir="${escape(ui.direction)}">
    <head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
    <meta name="robots" content="noindex,nofollow"><title>${escape(title)}</title>
    <style>${css}${publicThemeCss(site.theme) ?? ''}</style></head>
    <body><a class="skip" href="#main-content">${escape(t('chrome.skip_to_content'))}</a><main id="main-content" tabindex="-1"><h1>${escape(title)}</h1>${content}</main></body></html>`;
}
