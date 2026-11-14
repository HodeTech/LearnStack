import type { AdminCopy } from '@/i18n/administration-catalogues';
import type { NativePageSnapshot, PageView } from '@/server/auth/page-contract';

type FederationView = Extract<PageView, { kind: 'federation' }>;
type Connection = NonNullable<FederationView['federation']['connection']>;

function Proof({
  snapshot,
  version,
}: {
  readonly snapshot: NativePageSnapshot;
  readonly version?: string;
}) {
  return (
    <>
      <input type="hidden" name="csrfToken" value={snapshot.session.csrfToken} />
      {version !== undefined && <input type="hidden" name="version" value={version} />}
    </>
  );
}

function Field({
  id,
  name,
  label,
  hint,
  type = 'text',
  required = false,
  defaultValue,
  admin,
  autoComplete = 'off',
}: {
  readonly id: string;
  readonly name: string;
  readonly label: string;
  readonly hint?: string;
  readonly type?: 'text' | 'url' | 'password';
  readonly required?: boolean;
  readonly defaultValue?: string | null;
  readonly admin: AdminCopy;
  readonly autoComplete?: string;
}) {
  return (
    <div className="auth-field">
      <label htmlFor={id}>
        {label}
        {required && <> ({admin('federation.required')})</>}
      </label>
      <input
        id={id}
        name={name}
        type={type}
        required={required}
        aria-required={required ? 'true' : undefined}
        autoComplete={autoComplete}
        spellCheck={false}
        defaultValue={defaultValue ?? undefined}
        maxLength={type === 'password' ? 1024 : 2048}
        aria-describedby={hint ? id + '-hint' : undefined}
      />
      {hint && <p id={id + '-hint'}>{hint}</p>}
    </div>
  );
}

function Certificate({
  id,
  name,
  label,
  admin,
  required = false,
}: {
  readonly id: string;
  readonly name: string;
  readonly label: string;
  readonly admin: AdminCopy;
  readonly required?: boolean;
}) {
  return (
    <div className="auth-field">
      <label htmlFor={id}>
        {label}
        {required && <> ({admin('federation.required')})</>}
      </label>
      <textarea id={id} name={name} rows={6} spellCheck={false} maxLength={8192} />
    </div>
  );
}

/** One configure form per protocol; a stored secret is never shown, and the secret control is write-only. */
function ConfigureForm({
  snapshot,
  admin,
  protocol,
  connection,
}: {
  readonly snapshot: NativePageSnapshot;
  readonly admin: AdminCopy;
  readonly protocol: 'oidc' | 'saml';
  readonly connection: Connection | null;
}) {
  const prefix = 'federation-' + protocol;
  const first = connection === null;
  return (
    <form
      action="/api/studio/federation-configure"
      method="post"
      aria-labelledby={prefix + '-title'}
    >
      <h3 id={prefix + '-title'}>
        {protocol === 'oidc'
          ? admin('federation.configure_oidc')
          : admin('federation.configure_saml')}
      </h3>
      <Proof snapshot={snapshot} version={connection?.version} />
      <input type="hidden" name="protocol" value={protocol} />
      <Field
        id={prefix + '-name'}
        name="displayName"
        label={admin('federation.display_name')}
        hint={admin('federation.display_name_hint')}
        required={first}
        defaultValue={connection?.displayName}
        admin={admin}
      />
      <fieldset>
        <legend>{admin('federation.source')}</legend>
        <div>
          <input
            type="radio"
            id={prefix + '-source-document'}
            name="source"
            value={protocol === 'oidc' ? 'discovery_url' : 'metadata_url'}
            defaultChecked={connection?.source !== 'manual'}
          />
          <label htmlFor={prefix + '-source-document'}>
            {protocol === 'oidc'
              ? admin('federation.source_discovery')
              : admin('federation.source_metadata')}
          </label>
        </div>
        <div>
          <input
            type="radio"
            id={prefix + '-source-manual'}
            name="source"
            value="manual"
            defaultChecked={connection?.source === 'manual'}
          />
          <label htmlFor={prefix + '-source-manual'}>{admin('federation.source_manual')}</label>
        </div>
      </fieldset>
      <Field
        id={prefix + '-document'}
        name="documentUrl"
        type="url"
        label={admin('federation.document_url')}
        hint={admin('federation.url_hint')}
        admin={admin}
      />
      <Field
        id={prefix + '-issuer'}
        name="issuerOrEntityId"
        label={
          protocol === 'oidc'
            ? admin('federation.issuer_input')
            : admin('federation.entity_id_input')
        }
        defaultValue={connection?.issuerOrEntityId}
        admin={admin}
      />
      {protocol === 'oidc' ? (
        <>
          <Field
            id={prefix + '-authorization'}
            name="authorizationEndpoint"
            type="url"
            label={admin('federation.authorization_endpoint')}
            admin={admin}
          />
          <Field
            id={prefix + '-token'}
            name="tokenEndpoint"
            type="url"
            label={admin('federation.token_endpoint')}
            admin={admin}
          />
          <Field
            id={prefix + '-jwks'}
            name="jwksEndpoint"
            type="url"
            label={admin('federation.jwks_endpoint')}
            admin={admin}
          />
          <Field
            id={prefix + '-userinfo'}
            name="userInfoEndpoint"
            type="url"
            label={admin('federation.userinfo_endpoint')}
            admin={admin}
          />
          <Field
            id={prefix + '-client'}
            name="clientId"
            label={admin('federation.client_id')}
            required
            defaultValue={connection?.clientId}
            admin={admin}
          />
          <fieldset>
            <legend>{admin('federation.client_authentication')}</legend>
            {(['client_secret_basic', 'client_secret_post'] as const).map((value, index) => (
              <div key={value}>
                <input
                  type="radio"
                  id={prefix + '-' + value}
                  name="clientAuthentication"
                  value={value}
                  defaultChecked={index === 0}
                />
                <label htmlFor={prefix + '-' + value}>
                  {value === 'client_secret_basic'
                    ? admin('federation.client_secret_basic')
                    : admin('federation.client_secret_post')}
                </label>
              </div>
            ))}
          </fieldset>
          <Field
            id={prefix + '-secret'}
            name="clientSecret"
            type="password"
            label={admin('federation.client_secret')}
            hint={admin('federation.client_secret_hint')}
            required={first}
            autoComplete="new-password"
            admin={admin}
          />
        </>
      ) : (
        <>
          <Field
            id={prefix + '-sso'}
            name="singleSignOnEndpoint"
            type="url"
            label={admin('federation.sso_endpoint')}
            admin={admin}
          />
          <Certificate
            id={prefix + '-certificate-1'}
            name="signingCertificate1"
            label={admin('federation.certificate_1')}
            admin={admin}
          />
          <Certificate
            id={prefix + '-certificate-2'}
            name="signingCertificate2"
            label={admin('federation.certificate_2')}
            admin={admin}
          />
        </>
      )}
      <p>{admin('federation.configure_note')}</p>
      <button type="submit">{admin('federation.save_configuration')}</button>
    </form>
  );
}

function Confirmed({
  snapshot,
  connection,
  action,
  confirm,
  submit,
  admin,
}: {
  readonly snapshot: NativePageSnapshot;
  readonly connection: Connection;
  readonly action: 'disable' | 'delete';
  readonly confirm: string;
  readonly submit: string;
  readonly admin: AdminCopy;
}) {
  const id = 'federation-confirm-' + action;
  return (
    <form action={'/api/studio/federation-' + action} method="post">
      <Proof snapshot={snapshot} version={connection.version} />
      <div className="auth-field">
        <input type="checkbox" id={id} name="confirm" value="yes" required aria-required="true" />
        <label htmlFor={id}>
          {confirm} ({admin('federation.required')})
        </label>
      </div>
      <button type="submit">{submit}</button>
    </form>
  );
}

/** ADR-0099 § User experience: configure, test and activate, with the IdP values and plain explanations. */
export function FederationPage({
  snapshot,
  view,
  admin,
  locale,
}: {
  readonly snapshot: NativePageSnapshot;
  readonly view: FederationView;
  readonly admin: AdminCopy;
  readonly locale: string;
}) {
  const { connection, provider, values } = view.federation;
  const time = (value: string) =>
    new Intl.DateTimeFormat(locale, { dateStyle: 'medium', timeStyle: 'short' }).format(
      new Date(value),
    );
  const states = {
    draft: admin('federation.state_draft'),
    test_open: admin('federation.state_test_open'),
    test_passed: admin('federation.state_test_passed'),
    active: admin('federation.state_active'),
    disabled: admin('federation.state_disabled'),
  } as const;
  const status = connection ? states[connection.status] : admin('federation.state_none');
  return (
    <>
      <p>{admin('federation.intro')}</p>
      <p>{admin('federation.explain_link')}</p>
      <section aria-labelledby="federation-status-title">
        <h2 id="federation-status-title">{admin('federation.status_title')}</h2>
        <dl>
          <dt>{admin('federation.status')}</dt>
          <dd>{status}</dd>
          {connection && (
            <>
              <dt>{admin('federation.protocol')}</dt>
              <dd>
                {connection.protocol === 'oidc'
                  ? admin('federation.protocol_oidc')
                  : admin('federation.protocol_saml')}
              </dd>
              <dt>{admin('federation.display_name')}</dt>
              <dd>{connection.displayName}</dd>
              <dt>{admin('federation.issuer')}</dt>
              <dd>
                <code>{connection.issuerOrEntityId}</code>
              </dd>
            </>
          )}
        </dl>
        {connection?.pending && (
          <div role="status">
            <h3>{admin('federation.pending_title')}</h3>
            <p>{admin('federation.pending')}</p>
            <p>
              {admin('federation.pending_until')}:{' '}
              <time dateTime={connection.pending.quietUntil}>
                {time(connection.pending.quietUntil)}
              </time>
            </p>
            <form action="/api/studio/federation-resume" method="post">
              <Proof snapshot={snapshot} version={connection.version} />
              <button type="submit">{admin('federation.resume')}</button>
            </form>
          </div>
        )}
        {provider === 'drift' && (
          <div role="status">
            <h3>{admin('federation.drift_title')}</h3>
            <p>{admin('federation.drift')}</p>
          </div>
        )}
        {provider === 'unavailable' && (
          <div role="status">
            <h3>{admin('federation.unavailable_title')}</h3>
            <p>{admin('federation.unavailable')}</p>
          </div>
        )}
      </section>
      <section aria-labelledby="federation-configure-title">
        <h2 id="federation-configure-title">{admin('federation.step_configure')}</h2>
        {(['oidc', 'saml'] as const)
          .filter((protocol) => connection === null || connection.protocol === protocol)
          .map((protocol) => (
            <ConfigureForm
              key={protocol}
              snapshot={snapshot}
              admin={admin}
              protocol={protocol}
              connection={connection}
            />
          ))}
        <h3>{admin('federation.values_title')}</h3>
        {values ? (
          <>
            <p>{admin('federation.values_hint')}</p>
            <dl>
              <dt>{admin('federation.redirect_uri')}</dt>
              <dd>
                <code>{values.redirectUri}</code>
              </dd>
              <dt>{admin('federation.sp_entity_id')}</dt>
              <dd>
                <code>{values.entityId}</code>
              </dd>
              {connection?.protocol === 'saml' && (
                <>
                  <dt>{admin('federation.sp_descriptor')}</dt>
                  <dd>
                    <code>{values.descriptorUrl}</code>
                  </dd>
                </>
              )}
            </dl>
          </>
        ) : (
          <p>{admin('federation.values_unavailable')}</p>
        )}
        {connection && (
          <>
            <form
              action="/api/studio/federation-rename"
              method="post"
              aria-labelledby="federation-rename-title"
            >
              <h3 id="federation-rename-title">{admin('federation.rename_title')}</h3>
              <Proof snapshot={snapshot} version={connection.version} />
              <Field
                id="federation-rename-name"
                name="displayName"
                label={admin('federation.display_name')}
                hint={admin('federation.display_name_hint')}
                required
                defaultValue={connection.displayName}
                admin={admin}
              />
              <button type="submit">{admin('federation.rename')}</button>
            </form>
            {connection.protocol === 'oidc' && (
              <form
                action="/api/studio/federation-rotate-secret"
                method="post"
                aria-labelledby="federation-rotate-title"
              >
                <h3 id="federation-rotate-title">{admin('federation.rotate_title')}</h3>
                <Proof snapshot={snapshot} version={connection.version} />
                <p>{admin('federation.rotate_note')}</p>
                <Field
                  id="federation-rotate-secret"
                  name="clientSecret"
                  type="password"
                  label={admin('federation.client_secret')}
                  required
                  autoComplete="new-password"
                  admin={admin}
                />
                <button type="submit">{admin('federation.rotate')}</button>
              </form>
            )}
          </>
        )}
      </section>
      <section aria-labelledby="federation-test-title">
        <h2 id="federation-test-title">{admin('federation.step_test')}</h2>
        <p>{admin('federation.explain_test')}</p>
        {connection && (connection.status === 'draft' || connection.status === 'test_open') ? (
          <>
            <p>{admin('federation.test_hint')}</p>
            {connection.status === 'test_open' && <p>{admin('federation.test_open')}</p>}
            <form action="/api/studio/federation-test" method="post">
              <Proof snapshot={snapshot} />
              <button type="submit">
                {connection.status === 'test_open'
                  ? admin('federation.test_continue')
                  : admin('federation.test_start')}
              </button>
            </form>
          </>
        ) : (
          <p>{admin('federation.test_needs_draft')}</p>
        )}
      </section>
      <section aria-labelledby="federation-activate-title">
        <h2 id="federation-activate-title">{admin('federation.step_activate')}</h2>
        {connection?.status === 'test_passed' && (
          <form action="/api/studio/federation-activate" method="post">
            <Proof snapshot={snapshot} version={connection.version} />
            <p>{admin('federation.activate_note')}</p>
            <button type="submit">{admin('federation.activate')}</button>
          </form>
        )}
        {connection?.status === 'active' && (
          <>
            <p>{admin('federation.active_note')}</p>
            <p>{admin('federation.explain_disable')}</p>
            <Confirmed
              snapshot={snapshot}
              connection={connection}
              action="disable"
              confirm={admin('federation.confirm_disable')}
              submit={admin('federation.disable')}
              admin={admin}
            />
          </>
        )}
        {connection?.status === 'disabled' && (
          <form action="/api/studio/federation-enable" method="post">
            <Proof snapshot={snapshot} version={connection.version} />
            <p>{admin('federation.enable_note')}</p>
            <button type="submit">{admin('federation.enable')}</button>
          </form>
        )}
        {(!connection || ['draft', 'test_open'].includes(connection.status)) && (
          <p>{admin('federation.activate_needs_test')}</p>
        )}
      </section>
      {connection && (
        <section aria-labelledby="federation-delete-title">
          <h2 id="federation-delete-title">{admin('federation.delete_title')}</h2>
          <p>{admin('federation.delete_note')}</p>
          <p>{admin('federation.explain_disable')}</p>
          <Confirmed
            snapshot={snapshot}
            connection={connection}
            action="delete"
            confirm={admin('federation.confirm_delete')}
            submit={admin('federation.delete')}
            admin={admin}
          />
        </section>
      )}
    </>
  );
}
