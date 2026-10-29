import 'server-only';

import { publicThemeCss } from '@/components/public/theme';
import type { AuthCopy } from '@/i18n/auth-catalogues';
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

const hidden = (name: string, value: string) =>
  `<input type="hidden" name="${escape(name)}" value="${escape(value)}">`;
function entryContent(page: Extract<BffPage, { kind: 'entry' }>, copy: AuthCopy): string {
  const entry = page.entry;
  if (!entry)
    return `<p>${escape(copy('entry.no_session'))}</p><a href="/api/auth/signin">${escape(copy('shell.signin'))}</a>`;
  const proof = hidden('csrfToken', entry.csrfToken);
  const signout = `<p>${escape(copy('shell.signout_scope'))}</p><form action="/api/auth/signout" method="post">${proof}<button type="submit">${escape(copy('entry.signout'))}</button></form>`;
  if (entry.displayLabel === null)
    return `<p>${escape(copy('entry.reauthenticate'))}</p>${signout}`;
  const label = `<p>${escape(copy('entry.account'))}: <strong>${escape(entry.displayLabel)}</strong></p><p>${escape(copy('entry.confirm'))}</p>`;
  const retry = `<p><a href="/api/auth/entry">${escape(copy('entry.retry'))}</a></p>`;
  if (entry.identityState === 'missing')
    return `${label}<p>${escape(copy('entry.missing'))}</p><form action="/api/auth/entry/create" method="post">${proof}<button type="submit">${escape(copy('entry.create'))}</button></form>${retry}${signout}`;
  if (!entry.contexts)
    return `${label}<p>${escape(copy('entry.unavailable'))}</p>${retry}${signout}`;
  const choices = entry.contexts.items
    .map(
      (item) =>
        `<li><form action="/api/auth/entry/select" method="post">${proof}${hidden('tenantId', item.tenantId)}${item.organizationId === null ? '' : hidden('organizationId', item.organizationId)}${page.cursor === undefined ? '' : hidden('cursor', page.cursor)}<p>${escape(item.tenantName)} — ${escape(item.organizationName ?? copy('entry.tenant'))}</p><button type="submit">${escape(item.canUseCurrentHost ? copy('entry.select') : copy('entry.destination'))}</button></form></li>`,
    )
    .join('');
  const next =
    entry.contexts.hasNextPage && entry.contexts.endCursor !== null
      ? `<p><a href="/api/auth/entry?cursor=${encodeURIComponent(entry.contexts.endCursor)}">${escape(copy('entry.next'))}</a></p>`
      : '';
  return `${label}${choices ? `<ul>${choices}</ul>` : `<p>${escape(copy('entry.empty'))}</p>`}${next}${retry}${signout}`;
}
function entryFailureContent(
  page: Extract<BffPage, { kind: 'entry_error' }>,
  copy: AuthCopy,
): string {
  const hint =
    page.retryAfterSeconds === undefined
      ? ''
      : `<p>${escape(copy('entry.retry_after'))}: ${page.retryAfterSeconds}</p>`;
  const recovery =
    page.reason === 'reauthenticate'
      ? `<p><a href="/api/auth/signin">${escape(copy('session.reauthenticate'))}</a></p>`
      : `<p><a href="/api/auth/entry">${escape(copy('entry.retry'))}</a></p>`;
  const messages: Record<Extract<BffPage, { kind: 'entry_error' }>['reason'], string> = {
    reauthenticate: copy('entry.failure_reauthenticate'),
    creation_disabled: copy('entry.failure_creation_disabled'),
    denied: copy('entry.failure_denied'),
    busy: copy('entry.failure_busy'),
    unavailable: copy('entry.failure_unavailable'),
  };
  return `<p>${escape(messages[page.reason])}</p>${hint}${recovery}`;
}
function mutationContent(
  page: Extract<BffPage, { kind: 'mutation_error' }>,
  copy: AuthCopy,
): string {
  const retry = page.retryable
    ? `<form action="${escape(page.action)}" method="post">${page.fields.map(([name, value]) => hidden(name, value)).join('')}<button type="submit">${escape(copy('mutation.retry'))}</button></form>`
    : '';
  const messages: Record<Extract<BffPage, { kind: 'mutation_error' }>['reason'], string> = {
    denied: copy('mutation.denied'),
    stale: copy('mutation.stale'),
    in_progress: copy('mutation.in_progress'),
    key_reuse: copy('mutation.key_reuse'),
    unavailable: copy('mutation.unavailable'),
  };
  return `<p>${escape(messages[page.reason])}</p><p><a href="${escape(page.destination)}">${escape(copy('mutation.lookup'))}</a></p>${retry}`;
}

/** Closed middleware document, with every text/attribute escaped. No user HTML or script. */
export function renderBffDocument(
  site: PublicSite,
  page:
    | BffPage
    | Readonly<{ kind: 'unavailable' | 'busy' }>
    | Readonly<{ kind: 'account_unavailable'; returnPath: '/studio' | '/portal' }>,
): string {
  const ui = createAuthTranslator(site.defaultLocale);
  const { t } = createPublicTranslator(site.defaultLocale);
  const copy = ui.copy;
  const title =
    page.kind === 'account_change'
      ? copy('entry.account_change_title')
      : page.kind === 'entry' || page.kind === 'entry_error'
        ? copy('entry.title')
        : page.kind === 'mutation_error'
          ? copy('mutation.title')
          : page.kind === 'account_unavailable'
            ? copy('session.unavailable_title')
            : page.kind === 'signin'
              ? copy('signin.title')
              : page.kind === 'unavailable'
                ? copy('signin.unavailable_title')
                : copy('error.title');
  const content =
    page.kind === 'account_change'
      ? `<p>${escape(copy('entry.account_change'))}</p><p>${escape(copy('shell.signout_scope'))}</p><form action="/api/auth/signout" method="post">${hidden('csrfToken', page.csrfToken)}<button type="submit">${escape(copy('shell.signout'))}</button></form>`
      : page.kind === 'entry_error'
        ? entryFailureContent(page, copy)
        : page.kind === 'entry'
          ? entryContent(page, copy)
          : page.kind === 'mutation_error'
            ? mutationContent(page, copy)
            : page.kind === 'signin'
              ? `<p>${escape(site.displayName)}</p><p>${escape(copy('signin.description'))}</p>
      <form action="/api/auth/signin/keycloak" method="post">
      <input type="hidden" name="csrfToken" value="${escape(page.csrfToken)}">
      <input type="hidden" name="returnTo" value="${escape(page.returnPath)}">
      ${page.contextHint === undefined ? '' : hidden('contextHint', page.contextHint)}
      <button type="submit">${escape(copy('signin.continue'))}</button></form>`
              : `<p>${escape(
                  page.kind === 'busy'
                    ? copy('error.busy')
                    : page.kind === 'account_unavailable'
                      ? copy('session.unavailable')
                      : page.kind === 'unavailable'
                        ? copy('signin.unavailable')
                        : copy('error.description'),
                )}</p>
      ${
        page.kind === 'account_unavailable'
          ? `<p><a href="${escape(page.returnPath)}">${escape(copy('session.retry'))}</a></p><p><a href="/api/auth/signin?returnTo=${encodeURIComponent(page.returnPath)}">${escape(copy('session.reauthenticate'))}</a></p>`
          : page.kind === 'unavailable'
            ? ''
            : `<p><a href="/api/auth/signin">${escape(copy('error.retry'))}</a></p>`
      }
      <p><a href="/${escape(site.defaultLocale)}/courses">${escape(copy('shell.catalog'))}</a></p>`;
  return `<!doctype html><html lang="${escape(ui.locale)}" dir="${escape(ui.direction)}">
    <head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
    <meta name="robots" content="noindex,nofollow"><title>${escape(title)}</title>
    <style>${css}${publicThemeCss(site.theme) ?? ''}</style></head>
    <body><a class="skip" href="#main-content">${escape(t('chrome.skip_to_content'))}</a><main id="main-content" tabindex="-1"><h1>${escape(title)}</h1>${content}</main></body></html>`;
}
