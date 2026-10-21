const invalidConfiguration = () => new Error('Local demo database configuration is invalid');
const aliases = new Map([
  ['host', 'host'],
  ['server', 'host'],
  ['port', 'port'],
  ['database', 'database'],
  ['username', 'username'],
  ['user name', 'username'],
  ['userid', 'username'],
  ['user id', 'username'],
  ['uid', 'username'],
  ['password', 'password'],
  ['pwd', 'password'],
  ['psw', 'password'],
]);

// The local demo accepts only these five fields, not the general Npgsql grammar.
// Quoted values preserve semicolons/whitespace and use doubled quote escaping.
function localConnectionFields(source) {
  if (typeof source !== 'string' || /[\u0000-\u0008\u000a-\u001f\u007f]/.test(source))
    throw invalidConfiguration();
  const fields = new Map();
  let index = 0;
  while (index < source.length) {
    while (/[; \t]/.test(source[index] ?? '') && index < source.length) index++;
    if (index === source.length) break;
    const start = index;
    while (index < source.length && source[index] !== '=') {
      if (!/[a-z \t]/i.test(source[index])) throw invalidConfiguration();
      index++;
    }
    if (index === source.length) throw invalidConfiguration();
    const key = aliases.get(source.slice(start, index).trim().toLowerCase());
    if (!key || fields.has(key)) throw invalidConfiguration();
    index++;
    while (index < source.length && /[ \t]/.test(source[index])) index++;
    let value = '';
    const quote = source[index];
    if (quote === '"' || quote === "'") {
      index++;
      let closed = false;
      while (index < source.length) {
        const character = source[index++];
        if (character !== quote) value += character;
        else if (source[index] === quote) {
          value += quote;
          index++;
        } else {
          closed = true;
          break;
        }
      }
      if (!closed) throw invalidConfiguration();
      while (index < source.length && /[ \t]/.test(source[index])) index++;
      if (index < source.length && source[index] !== ';') throw invalidConfiguration();
    } else {
      const start = index;
      while (index < source.length && source[index] !== ';') {
        if (source[index] === '"' || source[index] === "'") throw invalidConfiguration();
        index++;
      }
      value = source.slice(start, index).trim();
    }
    if (!value) throw invalidConfiguration();
    fields.set(key, value);
  }
  return fields;
}

/** Reject stale local credentials in the launcher without giving them to a child process. */
export function validateDemoDatabaseConfiguration(values) {
  if (
    values === null ||
    values === undefined ||
    typeof values.POSTGRES_DB !== 'string' ||
    !values.POSTGRES_DB
  )
    throw invalidConfiguration();
  for (const [suffix, role, passwordKey] of [
    ['Migration', 'learnstack_migration', 'LEARNSTACK_MIGRATION_PW'],
    ['Default', 'learnstack_app', 'LEARNSTACK_APP_PW'],
    ['PlatformAdmin', 'learnstack_platform', 'LEARNSTACK_PLATFORM_PW'],
    ['OutboxDispatcher', 'learnstack_outbox_admin', 'LEARNSTACK_OUTBOX_PW'],
  ]) {
    const password = values[passwordKey];
    const fields = localConnectionFields(values[`ConnectionStrings__${suffix}`]);
    const port = fields.get('port') ?? '5432';
    if (
      typeof password !== 'string' ||
      !password ||
      fields.get('username') !== role ||
      fields.get('password') !== password ||
      fields.get('database') !== values.POSTGRES_DB ||
      !['localhost', '127.0.0.1'].includes(fields.get('host')) ||
      !/^\d+$/.test(port) ||
      Number(port) !== 5432
    )
      throw invalidConfiguration();
  }
}

/** Every Seeder mode receives only its own credential and process prerequisites. */
export function publicSeederEnvironment(applicationConnectionString, base = process.env) {
  const environment = {};
  for (const key of ['PATH', 'HOME', 'DOTNET_ROOT', 'TMPDIR', 'APPDATA',
    'HTTP_PROXY', 'HTTPS_PROXY', 'ALL_PROXY', 'NO_PROXY',
    'http_proxy', 'https_proxy', 'all_proxy', 'no_proxy',
    'NODE_EXTRA_CA_CERTS', 'SSL_CERT_FILE', 'SSL_CERT_DIR',
    'NUGET_PACKAGES', 'NUGET_HTTP_CACHE_PATH', 'COREPACK_HOME', 'PNPM_HOME', 'XDG_CACHE_HOME',
    'NPM_CONFIG_CACHE', 'npm_config_cache', 'NPM_CONFIG_STORE_DIR', 'npm_config_store_dir'])
    if (base[key] !== undefined) environment[key] = base[key];
  if (applicationConnectionString !== undefined)
    environment.ConnectionStrings__Default = applicationConnectionString;
  return environment;
}
