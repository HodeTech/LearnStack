export const invitationKeys = [
  'common.title',
  'common.required',
  'common.skip',
  'common.cancel',
  'common.home',
  'common.signout',
  'landing.title',
  'landing.description',
  'landing.code_label',
  'landing.continue',
  'landing.no_script',
  'landing.invalid',
  'confirm.signin',
  'confirm.signin_action',
  'confirm.bootstrap',
  'confirm.bootstrap_action',
  'confirm.inspect',
  'confirm.inspect_action',
  'confirm.forbidden',
  'confirm.verify_action',
  'confirm.verification_hint',
  'confirm.ready',
  'confirm.email',
  'confirm.institution',
  'confirm.branch',
  'confirm.tenant_scope',
  'confirm.offered_role',
  'confirm.expires',
  'confirm.accept',
  'confirm.accepted',
  'confirm.already_member',
  'confirm.session_ready',
  'confirm.continue',
  'confirm.session_unavailable',
  'confirm.result_action',
  'failure.unknown',
  'failure.unavailable',
  'failure.expired',
  'failure.account_change',
  'failure.restart',
  'failure.retry',
  'roles.tenant_admin',
  'roles.instructor',
  'roles.learner',
  'roles.guardian',
  'roles.editor',
  'roles.org_admin',
  'roles.org_instructor',
] as const;
export type InvitationKey = (typeof invitationKeys)[number];
export type InvitationCopy = (key: InvitationKey) => string;
export type InvitationCatalogues = Readonly<{ en: unknown; tr: unknown }>;

/** Native startup supplies the same authored JSON used by catalogue validation. */
export function prepareInvitationCatalogues(catalogues: InvitationCatalogues) {
  const prepared = new Map<string, ReadonlyMap<InvitationKey, string>>();
  for (const locale of ['en', 'tr'] as const) {
    const values = new Map<InvitationKey, string>();
    const visit = (value: unknown, prefix = '') => {
      if (!value || typeof value !== 'object' || Array.isArray(value))
        throw new Error('Invitation catalogue unavailable');
      for (const [key, child] of Object.entries(value)) {
        const path = prefix ? prefix + '.' + key : key;
        if (typeof child === 'string') {
          if (
            !invitationKeys.includes(path as InvitationKey) ||
            !child.trim() ||
            child.length > 1000 ||
            /[{}]/.test(child) ||
            child.includes('\0')
          )
            throw new Error('Invitation catalogue unavailable');
          values.set(path as InvitationKey, child);
        } else visit(child, path);
      }
    };
    visit(catalogues[locale]);
    if (values.size !== invitationKeys.length) throw new Error('Invitation catalogue unavailable');
    prepared.set(locale, values);
  }
  return (contentLocale: string) => {
    let locale = contentLocale;
    while (!prepared.has(locale) && locale.includes('-'))
      locale = locale.slice(0, locale.lastIndexOf('-'));
    if (!prepared.has(locale)) locale = 'en';
    const messages = prepared.get(locale)!;
    const invitation: InvitationCopy = (key) =>
      messages.get(key) ?? messages.get('failure.unavailable')!;
    return Object.freeze({ locale, invitation });
  };
}
