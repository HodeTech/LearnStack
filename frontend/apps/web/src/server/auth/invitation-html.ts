import { prepareInvitationCatalogues } from './invitation-copy.js';
import type { InvitationCatalogues, InvitationCopy } from './invitation-copy.js';
import type { InvitationPage } from './native-invitation-contract.js';

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
const proof = (value: unknown): value is string =>
  typeof value === 'string' && /^[A-Za-z0-9_-]{43}$/.test(value);

// This is the first script and precedes all UI, styles and other script execution.
// Only the bounded fragment is copied. Scrubbing happens before parsing or reporting.
export const INVITATION_LANDING_SCRIPT = `(()=>{let code=location.hash.length===81?location.hash.slice(1):'';const had=location.hash!=='';try{history.replaceState(null,'','/invitations')}catch{code='';location.replace('/invitations');return}let leaving=false;addEventListener('pagehide',()=>{leaving=true;code='';const input=document.getElementById('invitation-code');if(input)input.value='';document.documentElement.style.visibility='hidden'});addEventListener('pageshow',e=>{if(e.persisted||leaving){document.documentElement.style.visibility='hidden';location.replace('/invitations')}});addEventListener('DOMContentLoaded',()=>{if(!had)return;const form=document.getElementById('invitation-capture');const input=document.getElementById('invitation-code');const error=document.getElementById('invitation-invalid');if(!/^[0-9a-f]{8}-[0-9a-f]{4}-7[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}\\.[A-Za-z0-9_-]{42}[AEIMQUYcgkosw048]$/.test(code)){code='';if(error)error.hidden=false;if(input){input.setAttribute('aria-invalid','true');input.focus()}return}try{input.value=code;code='';HTMLFormElement.prototype.submit.call(form)}catch{if(error)error.hidden=false}finally{code='';if(input)input.value=''}})})();`;

export const INVITATION_CONFIRM_SCRIPT = `(()=>{try{history.replaceState(null,'','/invitations/confirm')}catch{document.documentElement.style.visibility='hidden';location.replace('/invitations/confirm');return}let leaving=false;addEventListener('pagehide',()=>{leaving=true;document.documentElement.style.visibility='hidden'});addEventListener('pageshow',e=>{if(e.persisted||leaving){document.documentElement.style.visibility='hidden';location.replace('/invitations/confirm')}})})();`;

const css = `*{box-sizing:border-box}body{margin:0;background:#fff;color:#172033;font:1rem/1.6 system-ui,sans-serif}main{width:min(100% - 2rem,38rem);margin:5vh auto;padding:1.25rem;border:1px solid currentColor;border-radius:.5rem;overflow-wrap:anywhere}h1{line-height:1.25}a{color:inherit}button,input{max-width:100%;font:inherit;color:inherit;background:#fff;border:2px solid currentColor;border-radius:.25rem;padding:.65rem}input:not([type=hidden]){width:100%}button{white-space:normal;cursor:pointer}form{margin-block:1rem}dt{font-weight:700}dd{margin-inline-start:0;margin-block-end:.75rem}a:focus-visible,button:focus-visible,input:focus-visible,main:focus{outline:3px solid #075985;outline-offset:4px}.skip{position:fixed;top:1rem;inset-inline-start:1rem;background:#fff;padding:.5rem;transform:translateY(-200%)}.skip:focus{transform:none}[hidden]{display:none!important}`;

function form(action: string, csrf: string | undefined, label: string, fields = '') {
  if (!proof(csrf)) return '';
  return `<form action="${action}" method="post">${hidden('csrfToken', csrf)}${fields}<button type="submit">${escape(label)}</button></form>`;
}

function confirmation(
  page: Extract<InvitationPage, { kind: 'confirm' }>,
  invitation: InvitationCopy,
) {
  const action = (name: 'inspect' | 'verify' | 'accept' | 'result', label: string, fields = '') =>
    form('/api/auth/invitation/' + name, page.csrfToken, label, fields);
  const inspect = () => action('inspect', invitation('confirm.inspect_action'));
  const result = () => action('result', invitation('confirm.result_action'));
  const signout = () =>
    form(
      '/api/auth/signout',
      page.sessionCsrfToken ?? page.entryCsrfToken,
      invitation('common.signout'),
    );
  switch (page.state) {
    case 'signin':
      return `<p>${escape(invitation('confirm.signin'))}</p>${form('/api/auth/signin/keycloak', page.signinCsrfToken, invitation('confirm.signin_action'), hidden('returnTo', '/') + hidden('invitation', 'continue'))}`;
    case 'bootstrap':
      return `<p>${escape(invitation('confirm.bootstrap'))}</p>${form('/api/auth/entry/create', page.entryCsrfToken, invitation('confirm.bootstrap_action'))}${signout()}`;
    case 'inspect':
      return `<p>${escape(invitation('confirm.inspect'))}</p>${inspect()}${signout()}`;
    case 'forbidden':
      return `<p role="status">${escape(invitation('confirm.forbidden'))}</p>${action('verify', invitation('confirm.verify_action'))}<p>${escape(invitation('confirm.verification_hint'))}</p>${inspect()}${signout()}`;
    case 'ready': {
      const offer = page.inspection;
      if (!offer)
        return `<p role="alert">${escape(invitation('failure.unavailable'))}</p>${inspect()}`;
      const roles = {
        TenantAdmin: invitation('roles.tenant_admin'),
        Instructor: invitation('roles.instructor'),
        Learner: invitation('roles.learner'),
        Guardian: invitation('roles.guardian'),
        Editor: invitation('roles.editor'),
        OrgAdmin: invitation('roles.org_admin'),
        OrgInstructor: invitation('roles.org_instructor'),
      };
      const row = (label: string, value: string) =>
        `<dt>${escape(label)}</dt><dd><bdi>${escape(value)}</bdi></dd>`;
      return `<p>${escape(invitation('confirm.ready'))}</p><dl>${row(invitation('confirm.email'), offer.verifiedEmail)}${row(invitation('confirm.institution'), offer.tenantLabel)}${row(invitation('confirm.branch'), offer.organizationLabel ?? invitation('confirm.tenant_scope'))}${row(invitation('confirm.offered_role'), roles[offer.roleKey])}${row(invitation('confirm.expires'), new Intl.DateTimeFormat(page.locale, { dateStyle: 'medium', timeStyle: 'short', timeZone: 'UTC' }).format(new Date(offer.expiresAt)) + ' UTC')}</dl>${action('accept', invitation('confirm.accept'), hidden('version', offer.version))}${inspect()}${signout()}`;
    }
    case 'accepted':
      if (page.disposition !== 'created' && page.disposition !== 'already_member')
        return `<p role="alert">${escape(invitation('failure.unavailable'))}</p>${result()}`;
      return `<p role="status">${escape(page.disposition === 'already_member' ? invitation('confirm.already_member') : invitation('confirm.accepted'))}</p><p>${escape(page.sessionAvailable ? invitation('confirm.session_ready') : invitation('confirm.session_unavailable'))}</p>${page.sessionAvailable ? `<p><a href="/portal">${escape(invitation('confirm.continue'))}</a></p>` : result()}`;
    case 'unknown':
      return `<p role="alert">${escape(invitation('failure.unknown'))}</p>${result()}`;
    case 'unavailable':
      return `<p role="alert">${escape(invitation('failure.unavailable'))}</p>${inspect()}${result()}`;
    case 'expired':
      return `<p role="alert">${escape(invitation('failure.expired'))}</p><p><a href="/invitations">${escape(invitation('failure.restart'))}</a></p>`;
    case 'account_change':
      return `<p role="alert">${escape(invitation('failure.account_change'))}</p>${signout()}`;
  }
}

/** No Next, RSC, reporting, credentials or private owner imports belong to this renderer. */
export function createInvitationRenderer(catalogues: InvitationCatalogues) {
  const select = prepareInvitationCatalogues(catalogues);
  return function renderInvitationPage(page: InvitationPage, nonce?: string): string {
    if (nonce !== undefined && !/^[A-Za-z0-9+/]{22}==$/.test(nonce))
      throw new Error('Invitation response unavailable');
    const { locale, invitation } = select(page.locale);
    const language = new Intl.Locale(page.locale).toString();
    const info = new Intl.Locale(language) as Intl.Locale & {
      getTextInfo?: () => { direction: string };
    };
    const direction = info.getTextInfo?.().direction === 'rtl' ? 'rtl' : 'ltr';
    const landing = page.kind === 'landing';
    const content = landing
      ? `<p>${escape(invitation('landing.description'))}</p><p id="invitation-invalid" role="alert"${page.invalid ? '' : ' hidden'}>${escape(invitation('landing.invalid'))}</p><form id="invitation-capture" action="/api/auth/invitation/capture" method="post">${hidden('csrfToken', page.csrfToken)}<label for="invitation-code">${escape(invitation('landing.code_label'))}</label><input id="invitation-code" name="code" type="text" required maxlength="80" autocomplete="off" autocapitalize="none" spellcheck="false" aria-describedby="invitation-invalid"${page.invalid ? ' aria-invalid="true"' : ''}><button type="submit">${escape(invitation('landing.continue'))}</button></form><noscript><p>${escape(invitation('landing.no_script'))}</p></noscript>`
      : confirmation(page, invitation) +
        form('/api/auth/invitation/cancel', page.csrfToken, invitation('common.cancel'));
    const observation = landing
      ? ' data-invitation-state="landing"'
      : ` data-invitation-state="${page.state}"${page.state === 'accepted' && page.disposition ? ` data-invitation-disposition="${page.disposition}" data-invitation-session="${page.sessionAvailable ? 'ready' : 'unavailable'}"` : ''}`;
    return `<!doctype html><html lang="${escape(language)}" dir="${direction}"><head><meta charset="utf-8"><script${nonce ? ` nonce="${nonce}"` : ''}>${landing ? INVITATION_LANDING_SCRIPT : INVITATION_CONFIRM_SCRIPT}</script><meta name="viewport" content="width=device-width, initial-scale=1"><meta name="robots" content="noindex,nofollow"><meta name="referrer" content="same-origin"><title lang="${locale}">${escape(invitation('common.title'))}</title><style>${css}</style></head><body><a class="skip" href="#main">${escape(invitation('common.skip'))}</a><main id="main" tabindex="-1" lang="${locale}" dir="ltr"${observation}><h1>${escape(landing ? invitation('landing.title') : invitation('common.title'))}</h1>${content}<p><a href="/">${escape(invitation('common.home'))}</a></p></main></body></html>`;
  };
}
