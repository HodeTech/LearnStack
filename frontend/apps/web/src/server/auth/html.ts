import 'server-only';

import {
  languageLabel,
  permissionLabel,
  roleLabel,
  statusLabel,
} from '@/components/auth/admin-labels';
import { BFF_HISTORY_SOURCE } from '@/components/auth/history';
import { publicThemeCss } from '@/components/public/theme';
import { createAdministrationTranslator } from '@/i18n/administration-catalogues';
import type { AuthCopy } from '@/i18n/auth-catalogues';
import { createAuthTranslator } from '@/i18n/auth-catalogues';
import { createPublicTranslator } from '@/i18n/catalogues';

import type { BffPage } from './http-protocol';
import type { PageSite } from './page-contract';
import { isNativeReturnPath } from './session-path';

function scopeContent(page: Extract<BffPage, { kind: 'scope_choices' }>, copy: AuthCopy): string {
  const selection = page.selection;
  const proof = hidden('csrfToken', selection.session.csrfToken);
  const choices = selection.contexts.items
    .map((item) => {
      const name = `<p>${escape(item.tenantName)} — ${escape(item.organizationName ?? copy('entry.tenant'))}</p>`;
      if (item.current)
        return `<li>${name}<p aria-current="true">${escape(copy('entry.scope_current'))}</p></li>`;
      return `<li>${name}<form action="/api/auth/switch" method="post">${proof}${hidden('tenantId', item.tenantId)}${item.organizationId === null ? '' : hidden('organizationId', item.organizationId)}${page.cursor === undefined ? '' : hidden('cursor', page.cursor)}<button type="submit">${escape(item.canUseCurrentHost ? copy('entry.scope_choose') : copy('entry.destination'))}</button></form></li>`;
    })
    .join('');
  const next =
    selection.contexts.hasNextPage && selection.contexts.endCursor !== null
      ? `<p><a href="/api/auth/entry?cursor=${encodeURIComponent(selection.contexts.endCursor)}">${escape(copy('entry.next'))}</a></p>`
      : '';
  return `${choices ? `<ul>${choices}</ul>` : `<p>${escape(copy('entry.scope_empty'))}</p>`}${next}<p><a href="/portal">${escape(copy('entry.scope_back'))}</a></p><p>${escape(copy('shell.signout_scope'))}</p><form action="/api/auth/signout" method="post">${proof}<button type="submit">${escape(copy('shell.signout'))}</button></form>`;
}

const css = `:root{--ls-primary:#075985;--ls-bg:#ffffff;--ls-fg:#172033;--ls-muted:#475569}*{box-sizing:border-box}body{margin:0;background:var(--ls-bg);color:var(--ls-fg);font:1rem/1.6 system-ui,sans-serif}main{width:min(100% - 2rem,36rem);margin:8vh auto;padding:2rem;border:1px solid currentColor;border-radius:.75rem;overflow-wrap:anywhere}h1{line-height:1.25}a{color:inherit;text-decoration:underline}button{max-width:100%;font:inherit;white-space:normal;background:var(--ls-bg);color:var(--ls-fg);border:2px solid currentColor;border-radius:.25rem;padding:.75rem 1rem;cursor:pointer}textarea{width:100%;max-width:100%;min-height:4rem;font:inherit;color:inherit;background:var(--ls-bg);border:1px solid currentColor}a:focus-visible,button:focus-visible,textarea:focus-visible,main:focus{outline:3px solid var(--ls-fg);outline-offset:4px}.skip{position:fixed;inset-inline-start:1rem;top:1rem;padding:.75rem;background:var(--ls-bg);transform:translateY(-200%)}.skip:focus{transform:none}`;
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
function mutationEdits(
  page: Extract<BffPage, { kind: 'mutation_error' }>,
  copy: AuthCopy,
  locale: string,
) {
  const { admin, profile } = createAdministrationTranslator(locale);
  const labels = {
    displayName: copy('mutation.edit_display_name'),
    preferredLocale: copy('mutation.edit_preferred_locale'),
    status: copy('mutation.edit_status'),
    roleIds: copy('mutation.edit_role_ids'),
    grants: copy('mutation.edit_grants'),
    manifestVersion: copy('mutation.edit_manifest_version'),
  };
  const edits = page.fields
    .filter(([name]) => Object.hasOwn(labels, name))
    .map(([name, value]) => {
      let visible = value;
      if (name === 'roleIds') {
        const ids: unknown = JSON.parse(value);
        const named = Array.isArray(ids)
          ? ids.map((id) => page.roleLabels?.find((role) => role.roleId === id))
          : [];
        visible =
          named.length === 0
            ? admin('members.no_roles')
            : named.every((role) => role !== undefined)
              ? named.map((role) => roleLabel(role!, admin)).join('\n')
              : copy('mutation.role_labels_unavailable');
      } else if (name === 'grants') {
        const grants = JSON.parse(value) as { scope: 'Tenant' | 'Organization'; key: string }[];
        visible = grants.length
          ? grants.map((grant) => permissionLabel(grant, admin)).join('\n')
          : admin('roles.no_grants');
      } else if (name === 'status' && ['Active', 'Suspended', 'Revoked'].includes(value)) {
        visible = statusLabel(value as 'Active' | 'Suspended' | 'Revoked', admin);
      } else if (name === 'preferredLocale')
        visible = value ? languageLabel(value, locale) : profile('site_default');
      return `<p><label for="mutation-edit-${escape(name)}">${escape(labels[name as keyof typeof labels])}</label><br><textarea id="mutation-edit-${escape(name)}" readonly>${escape(visible)}</textarea>${hidden(name, value)}</p>`;
    })
    .join('');
  return edits
    ? `<section aria-labelledby="submitted-edits"><h2 id="submitted-edits">${escape(copy('mutation.edits_title'))}</h2><p>${escape(copy('mutation.edits_copy'))}</p>${edits}</section>`
    : '';
}
function mutationContent(
  page: Extract<BffPage, { kind: 'mutation_error' }>,
  copy: AuthCopy,
  locale: string,
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
  const edits =
    page.reason === 'stale' || page.reason === 'key_reuse' ? mutationEdits(page, copy, locale) : '';
  return `<p>${escape(messages[page.reason])}</p>${edits}<p><a href="${escape(page.destination)}">${escape(copy('mutation.lookup'))}</a></p>${retry}`;
}

/** Closed native document; escaped values and the one fixed history guard. */
export function renderBffDocument(
  site: PageSite,
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
      : page.kind === 'scope_choices'
        ? copy('entry.scope_title')
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
      : page.kind === 'scope_choices'
        ? scopeContent(page, copy)
        : page.kind === 'entry_error'
          ? entryFailureContent(page, copy)
          : page.kind === 'entry'
            ? entryContent(page, copy)
            : page.kind === 'mutation_error'
              ? mutationContent(page, copy, ui.locale)
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
  const recovery =
    page.kind === 'mutation_error' && isNativeReturnPath(page.destination)
      ? page.destination
      : page.kind === 'entry' ||
          page.kind === 'scope_choices' ||
          page.kind === 'entry_error' ||
          page.kind === 'account_change'
        ? '/api/auth/entry'
        : page.kind === 'account_unavailable' && isNativeReturnPath(page.returnPath)
          ? page.returnPath
          : '/api/auth/signin';
  return `<!doctype html><html lang="${escape(ui.locale)}" dir="${escape(ui.direction)}" data-bff-history-recovery="${escape(recovery)}">
    <head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
    <meta name="robots" content="noindex,nofollow"><title>${escape(title)}</title>
    <style>${css}${publicThemeCss(site.theme) ?? ''}</style><script id="bff-history">${BFF_HISTORY_SOURCE}</script></head>
    <body><a class="skip" href="#main-content">${escape(t('chrome.skip_to_content'))}</a><main id="main-content" tabindex="-1"><h1>${escape(title)}</h1>${content}<noscript><p>${escape(copy('session.javascript_required'))}</p></noscript></main></body></html>`;
}
