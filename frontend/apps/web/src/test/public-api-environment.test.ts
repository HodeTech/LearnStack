// @vitest-environment node
import { spawnSync } from 'node:child_process';
import { randomBytes } from 'node:crypto';
import {
  chmodSync,
  existsSync,
  linkSync,
  mkdirSync,
  mkdtempSync,
  readFileSync,
  realpathSync,
  rmSync,
  symlinkSync,
  writeFileSync,
} from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

import { afterEach, describe, expect, it } from 'vitest';

import {
  developmentSeederRuntimeEnvironment,
  publicSeederEnvironment,
} from '../../../../../scripts/demo-database.mjs';
import {
  publicApiEnvironment,
  publicDemoApiEnvironment,
  publicWebEnvironment,
} from '../../../../../scripts/public-runtime.mjs';
import { loadLocalEnvironment } from '../server/ingress';

const roots: string[] = [];
afterEach(() => {
  for (const root of roots.splice(0)) rmSync(root, { recursive: true, force: true });
});
function temporary() {
  const root = realpathSync(mkdtempSync(join(tmpdir(), 'learnstack-api-environment-')));
  roots.push(root);
  return root;
}
function connection(database: string, role = 'learnstack_app') {
  return Object.entries({
    Host: 'selected.invalid',
    Database: database,
    Username: role,
    Password: randomBytes(24).toString('base64url'),
    'Application Name': `runtime; "${randomBytes(8).toString('hex')}"`,
  })
    .map(([key, value]) => `${key}="${value.replaceAll('"', '""')}"`)
    .join(';');
}
function pair() {
  return {
    ConnectionStrings__IdentityAuthorization: connection('learnstack_identity_authorization'),
    ConnectionStrings__AuthorizationEvidence: connection('learnstack_authorization_evidence'),
  };
}
function selected() {
  return {
    ConnectionStrings__Default: connection('business'),
    ConnectionStrings__OutboxDispatcher: connection('business', 'learnstack_outbox_admin'),
  };
}
const configuration = {
  apiOrigin: 'http://127.0.0.1:5080',
  secret: randomBytes(32).toString('base64url'),
};
const keys = [
  'ConnectionStrings__IdentityAuthorization',
  'ConnectionStrings__AuthorizationEvidence',
] as const;
const apiIdentity = {
  Authentication__Tenant__Issuer: 'http://localhost:8080/realms/learnstack',
  Identity__AuthorityId: '01930000-0000-7000-8000-000000009001',
};
const nativeKeys = [
  'Identity__NativeSession__CurrentSecret',
  'Identity__NativeSession__PreviousSecret',
  'Identity__NativeSession__PreviousRetiresAt',
  'Identity__NativeSession__HttpsPort',
  'Identity__SelfBootstrap__Enabled',
] as const;
function nativeSettings() {
  return {
    Identity__NativeSession__CurrentSecret: randomBytes(32).toString('base64url'),
    Identity__NativeSession__PreviousSecret: randomBytes(32).toString('base64url'),
    Identity__NativeSession__PreviousRetiresAt: new Date(Date.now() + 300_000).toISOString(),
    Identity__NativeSession__HttpsPort: '4443',
    Identity__SelfBootstrap__Enabled: 'true',
  };
}
function invitationSettings() {
  return {
    InvitationEmail__KeyRing: JSON.stringify({
      active: 'owned',
      keys: { owned: randomBytes(32).toString('base64') },
    }),
    InvitationEmail__Smtp__Host: 'smtp.example.invalid',
    InvitationEmail__Smtp__Port: '465',
    InvitationEmail__Smtp__Sender: 'fixture@example.invalid',
    InvitationEmail__Smtp__Tls: 'ImplicitTls',
    InvitationEmail__Smtp__Username: 'synthetic-fixture',
    InvitationEmail__Smtp__Password: randomBytes(24).toString('base64url'),
  };
}
function writeEnvironment(root: string, relative: string, values: Record<string, string>) {
  writeFileSync(
    join(root, relative),
    Object.entries(values)
      .map(([key, value]) => `${key}=${value}`)
      .join('\n'),
    { mode: 0o600 },
  );
}

describe('selected native API environment', () => {
  it.each(['root', 'projection', 'process'] as const)(
    'selects exact native settings from %s only for API startup',
    (source) => {
      const root = temporary();
      mkdirSync(join(root, 'frontend/apps/web'), { recursive: true });
      const native = nativeSettings();
      writeEnvironment(root, '.env', { ...apiIdentity, ...(source === 'root' ? native : {}) });
      if (source === 'projection') writeEnvironment(root, 'frontend/apps/web/.env.local', native);
      const environment = source === 'process' ? native : {};
      const selectedValues = loadLocalEnvironment(root, environment, { apiAuthentication: true });
      expect(selectedValues).toMatchObject({ ...apiIdentity, ...native });
      const publicValues = loadLocalEnvironment(root, environment);
      for (const key of nativeKeys) expect(publicValues).not.toHaveProperty(key);
    },
  );

  it('requires native values to agree across root, optional web projection and process', () => {
    const root = temporary();
    mkdirSync(join(root, 'frontend/apps/web'), { recursive: true });
    const native = nativeSettings();
    writeEnvironment(root, '.env', { ...apiIdentity, ...native });
    writeEnvironment(root, 'frontend/apps/web/.env.local', native);
    expect(loadLocalEnvironment(root, native, { apiAuthentication: true })).toMatchObject(native);
    for (const key of nativeKeys) {
      expect(() =>
        loadLocalEnvironment(
          root,
          { [key]: 'conflicting-private-canary' },
          { apiAuthentication: true },
        ),
      ).toThrow('Conflicting API native configuration');
      writeEnvironment(root, 'frontend/apps/web/.env.local', {
        ...native,
        [key]: 'conflicting-private-canary',
      });
      expect(() => loadLocalEnvironment(root, {}, { apiAuthentication: true })).toThrow(
        'Conflicting API native configuration',
      );
      // Public-only selection does not consume or validate these private settings.
      for (const name of nativeKeys)
        expect(loadLocalEnvironment(root, native)).not.toHaveProperty(name);
      writeEnvironment(root, 'frontend/apps/web/.env.local', native);
    }
  });

  it('keeps the issuer and authority atomic in a web projection that declares the authority', () => {
    const root = temporary();
    mkdirSync(join(root, 'frontend/apps/web'), { recursive: true });
    const native = nativeSettings();
    writeEnvironment(root, '.env', apiIdentity);
    writeEnvironment(root, 'frontend/apps/web/.env.local', {
      ...native,
      Identity__AuthorityId: apiIdentity.Identity__AuthorityId,
    });
    expect(() => loadLocalEnvironment(root, {}, { apiAuthentication: true })).toThrow(
      'Incomplete API authentication configuration',
    );
    writeEnvironment(root, 'frontend/apps/web/.env.local', { ...native, ...apiIdentity });
    expect(loadLocalEnvironment(root, {}, { apiAuthentication: true })).toMatchObject({
      ...native,
      ...apiIdentity,
    });
  });

  it.each(['public-api', 'demo'] as const)(
    'forwards only selected native keys and the identity pair to %s',
    (launch) => {
      const native = nativeSettings();
      const values = { ...selected(), ...apiIdentity, ...native };
      const ambient = {
        ...nativeSettings(),
        Identity__NativeSession__PreviousRetiresAtUtc: 'unselected-alias',
      };
      const environment =
        launch === 'demo'
          ? publicDemoApiEnvironment(values, configuration, temporary(), ambient)
          : publicApiEnvironment(values, configuration, ambient);
      expect(environment).toMatchObject({ ...apiIdentity, ...native });
      expect(environment).not.toHaveProperty('Identity__NativeSession__PreviousRetiresAtUtc');
    },
  );

  it('does not inherit ambient native secrets or enable the baseline and keeps web/Seeder credentials separate', () => {
    const native = nativeSettings();
    const values = { ...selected(), ...apiIdentity, ...native };
    for (const environment of [
      publicApiEnvironment(selected(), configuration, native),
      publicDemoApiEnvironment(selected(), configuration, temporary(), native),
    ]) {
      expect(environment.Identity__NativeSession__HttpsPort).toBe('3000');
      for (const key of nativeKeys.filter((key) => key !== 'Identity__NativeSession__HttpsPort'))
        expect(environment).not.toHaveProperty(key);
    }
    for (const environment of [
      publicWebEnvironment(values, temporary(), native),
      publicSeederEnvironment(undefined, native),
      developmentSeederRuntimeEnvironment(values, native),
    ])
      for (const key of nativeKeys) expect(environment).not.toHaveProperty(key);
  });

  it('checks bounded nonempty selected values without taking over API option-format validation', () => {
    const root = temporary();
    for (const key of nativeKeys) {
      for (const invalid of [
        '',
        ' ',
        '\t',
        '\0private-canary',
        'private-canary\n',
        'x'.repeat(257),
      ]) {
        expect(() =>
          loadLocalEnvironment(root, { [key]: invalid }, { apiAuthentication: true }),
        ).toThrow('Invalid API native configuration');
        expect(() =>
          publicApiEnvironment({ ...selected(), [key]: invalid }, configuration, {}),
        ).toThrow('Invalid API native configuration');
      }
    }
    const deferred = { Identity__NativeSession__CurrentSecret: 'API-validates-the-secret-format' };
    expect(loadLocalEnvironment(root, deferred, { apiAuthentication: true })).toMatchObject(
      deferred,
    );
    expect(publicApiEnvironment({ ...selected(), ...deferred }, configuration, {})).toMatchObject(
      deferred,
    );
    expect(
      publicApiEnvironment(
        { ...selected(), Identity__NativeSession__HttpsPort: '443' },
        configuration,
        {},
      ).Identity__NativeSession__HttpsPort,
    ).toBe('443');
  });
});

describe('selected API auxiliary source environment', () => {
  it.each([
    ['absent', {}, true],
    ['complete', pair(), true],
    ['both empty', Object.fromEntries(keys.map((key) => [key, ''])), false],
    ['authorization empty', { [keys[0]]: '' }, false],
    ['evidence empty', { [keys[1]]: '' }, false],
    ['authorization only', { [keys[0]]: pair()[keys[0]] }, false],
    ['evidence only', { [keys[1]]: pair()[keys[1]] }, false],
    ['both whitespace', Object.fromEntries(keys.map((key) => [key, ' \t'])), false],
  ] as const)('uses the same %s pair rule for seed, API and demo', (_name, auxiliary, valid) => {
    const values: Record<string, string> = {
      ...selected(),
      Identity__AuthorityId: '01930000-0000-7000-8000-000000009001',
      Authentication__Tenant__Issuer: 'http://localhost:8080/realms/learnstack',
      ...auxiliary,
    };
    const ambient = pair();
    for (const make of [
      () => developmentSeederRuntimeEnvironment(values, ambient),
      () => publicApiEnvironment(values, configuration, ambient),
      () => publicDemoApiEnvironment(values, configuration, temporary(), ambient),
    ]) {
      if (!valid) {
        expect(make).toThrow(/configuration is invalid$/);
        continue;
      }
      const environment = make();
      expect(environment.ConnectionStrings__Default).toBe(values.ConnectionStrings__Default);
      for (const key of keys) {
        if (key in auxiliary) expect(environment[key]).toBe(values[key]);
        else expect(environment).not.toHaveProperty(key);
      }
    }
  });

  it.each(['public-api', 'demo'] as const)(
    'preserves the selected auxiliary pair byte-exactly for %s',
    (launch) => {
      const values = { ...selected(), ...pair() };
      const ambient = {
        ...pair(),
        ConnectionStrings__Migration: connection('owner', 'learnstack_migration'),
      };
      const environment =
        launch === 'demo'
          ? publicDemoApiEnvironment(values, configuration, temporary(), ambient)
          : publicApiEnvironment(values, configuration, ambient);
      for (const key of keys) expect(environment[key]).toBe(values[key]);
      expect(environment).not.toHaveProperty('ConnectionStrings__Migration');
    },
  );

  it('keeps helper ambient values, native web and build prerequisites separate from runtime credentials', () => {
    const values = { ...selected(), ...pair() };
    const ambient = {
      ...values,
      KEYCLOAK_ADMIN_PASSWORD: randomBytes(24).toString('hex'),
      PATH: '/owned/bin',
    };
    const api = publicApiEnvironment(selected(), configuration, ambient);
    const demo = publicDemoApiEnvironment(selected(), configuration, temporary(), ambient);
    const web = publicWebEnvironment(values, temporary(), ambient);
    const build = publicSeederEnvironment(undefined, ambient);
    for (const environment of [api, demo, web, build]) {
      for (const key of [...keys, 'KEYCLOAK_ADMIN_PASSWORD'])
        expect(environment).not.toHaveProperty(key);
    }
  });

  for (const key of keys) {
    it(`rejects a partial selected ${key} without borrowing its ambient partner`, () => {
      const values = { ...selected(), [key]: pair()[key] };
      const ambient = pair();
      for (const make of [
        () => publicApiEnvironment(values, configuration, ambient),
        () => publicDemoApiEnvironment(values, configuration, temporary(), ambient),
      ])
        expect(make).toThrow('Authorization source configuration is invalid');
    });
  }

  it.each(['', ' ', '\t', '\n', '\r', '\0'])(
    'rejects an explicit invalid selected pair %j without printing its values',
    (invalid) => {
      const values = {
        ...selected(),
        ...pair(),
        ConnectionStrings__AuthorizationEvidence: invalid,
      };
      for (const make of [
        () => publicApiEnvironment(values, configuration, {}),
        () => publicDemoApiEnvironment(values, configuration, temporary(), {}),
      ])
        expect(make).toThrow('Authorization source configuration is invalid');
    },
  );

  it('uses a complete process pair atomically and otherwise preserves the selected file pair', () => {
    const root = temporary();
    const file = pair();
    const explicit = pair();
    writeFileSync(
      join(root, '.env'),
      Object.entries(file)
        .map(([key, value]) => `${key}=${value}`)
        .join('\n'),
      { mode: 0o600 },
    );
    const options = { apiAuthentication: true };
    expect(loadLocalEnvironment(root, {}, options)).toMatchObject(file);
    expect(loadLocalEnvironment(root, explicit, options)).toMatchObject(explicit);
    for (const key of keys) {
      expect(() => loadLocalEnvironment(root, { [key]: explicit[key] }, options)).toThrow(
        'Incomplete API authorization source configuration',
      );
    }
  });
});

function launcher() {
  const root = temporary();
  for (const path of ['scripts', 'frontend/apps/web/.server/auth', 'backend', 'bin'])
    mkdirSync(join(root, path), { recursive: true });
  writeFileSync(join(root, 'package.json'), '{"type":"module"}\n');
  for (const name of ['public-local.mjs', 'public-runtime.mjs', 'demo-database.mjs'])
    writeFileSync(
      join(root, 'scripts', name),
      readFileSync(new URL(`../../../../../scripts/${name}`, import.meta.url)),
    );
  for (const name of [
    'ingress.js',
    'auth/http-paths.js',
    'auth/page-query.js',
    'auth/session-path.js',
    'auth/input-error.js',
  ])
    writeFileSync(
      join(root, 'frontend/apps/web/.server', name),
      readFileSync(new URL(`../../.server/${name}`, import.meta.url)),
    );
  const marker = join(root, 'api-child');
  writeFileSync(
    join(root, 'bin/dotnet'),
    `#!${process.execPath}\nimport { writeFileSync } from 'node:fs';\nwriteFileSync(${JSON.stringify(marker)}, JSON.stringify({ args: process.argv.slice(2), env: process.env }), { mode: 0o600 });\n`,
    { mode: 0o700 },
  );
  const values = {
    ...selected(),
    ...pair(),
    LEARNSTACK_PUBLIC_API_ORIGIN: configuration.apiOrigin,
    LEARNSTACK_PUBLIC_HOP_SECRET: configuration.secret,
    LEARNSTACK_PUBLIC_TLS_CERT: '/owned/cert',
    LEARNSTACK_PUBLIC_TLS_KEY: '/owned/key',
  };
  return { root, marker, values };
}
function launch(
  fixture: ReturnType<typeof launcher>,
  values: Record<string, string>,
  environment: Record<string, string> = {},
) {
  writeFileSync(
    join(fixture.root, '.env'),
    Object.entries(values)
      .map(([key, value]) => `${key}=${value}`)
      .join('\n'),
    { mode: 0o600 },
  );
  return spawnSync(process.execPath, [join(fixture.root, 'scripts/public-local.mjs'), 'api'], {
    cwd: fixture.root,
    env: { NODE_ENV: 'test', PATH: join(fixture.root, 'bin'), ...environment },
    encoding: 'utf8',
    timeout: 4000,
    killSignal: 'SIGKILL',
  });
}

describe('actual public-api child admission', () => {
  it.each(['file', 'projection', 'process'] as const)(
    'passes selected native settings from %s to the actual API child',
    (source) => {
      const fixture = launcher();
      const native = nativeSettings();
      const values = { ...fixture.values, ...apiIdentity, ...(source === 'file' ? native : {}) };
      if (source === 'projection')
        writeEnvironment(fixture.root, 'frontend/apps/web/.env.local', {
          ...apiIdentity,
          ...native,
        });
      const result = launch(fixture, values, source === 'process' ? native : {});
      expect(result.error).toBeUndefined();
      expect(result.status, result.stderr).toBe(0);
      const child = JSON.parse(readFileSync(fixture.marker, 'utf8')) as {
        env: Record<string, string>;
      };
      expect(child.env).toMatchObject({ ...apiIdentity, ...native });
      expect(result.stdout).toBe('');
      expect(result.stderr).toBe('');
    },
  );

  it('refuses a root/process native conflict before creating the API child', () => {
    const fixture = launcher();
    const native = nativeSettings();
    const result = launch(
      fixture,
      { ...fixture.values, ...apiIdentity, ...native },
      {
        Identity__NativeSession__CurrentSecret: randomBytes(32).toString('base64url'),
      },
    );
    expect(result.error).toBeUndefined();
    expect(result.status).toBe(1);
    expect(existsSync(fixture.marker)).toBe(false);
    expect(result.stdout).toBe('');
    expect(result.stderr).toBe('Local public configuration or launch failed\n');
  });

  it.each(['file', 'process'] as const)(
    'passes the selected auxiliary pair from %s into the actual child environment',
    (source) => {
      const fixture = launcher();
      const explicit = pair();
      const result = launch(fixture, fixture.values, source === 'process' ? explicit : {});
      expect(result.error).toBeUndefined();
      expect(result.status, result.stderr).toBe(0);
      const child = JSON.parse(readFileSync(fixture.marker, 'utf8')) as {
        args: string[];
        env: Record<string, string>;
      };
      expect(child.args).toEqual(['run', '--no-launch-profile', '--project', 'src/LearnStack.Api']);
      for (const key of keys)
        expect(child.env[key]).toBe(source === 'process' ? explicit[key] : fixture.values[key]);
      expect(child.env.Identity__NativeSession__HttpsPort).toBe('3000');
      for (const key of nativeKeys.filter((key) => key !== 'Identity__NativeSession__HttpsPort'))
        expect(child.env).not.toHaveProperty(key);
      expect(result.stdout).toBe('');
      expect(result.stderr).toBe('');
    },
  );

  it.each([
    'file-partial',
    'process-partial',
    'file-empty',
    'process-empty',
    'file-control',
  ] as const)(
    'refuses %s before the API child starts and keeps the diagnostic fixed',
    (invalid) => {
      const fixture = launcher();
      const values: Record<string, string> = { ...fixture.values };
      let environment: Record<string, string> = {};
      if (invalid === 'file-partial') delete values.ConnectionStrings__AuthorizationEvidence;
      if (invalid === 'process-partial')
        environment = {
          ConnectionStrings__IdentityAuthorization: pair().ConnectionStrings__IdentityAuthorization,
        };
      if (invalid === 'file-empty') values.ConnectionStrings__AuthorizationEvidence = '';
      if (invalid === 'process-empty')
        environment = { ...pair(), ConnectionStrings__AuthorizationEvidence: ' ' };
      if (invalid === 'file-control') values.ConnectionStrings__AuthorizationEvidence += '\0';
      const result = launch(fixture, values, environment);
      expect(result.error).toBeUndefined();
      expect(result.status).toBe(1);
      expect(existsSync(fixture.marker)).toBe(false);
      expect(result.stdout).toBe('');
      expect(result.stderr).toBe('Local public configuration or launch failed\n');
    },
  );
});

describe('selected invitation email API environment', () => {
  it.each(['permissions', 'symlink', 'hardlink', 'oversize', 'utf8'] as const)(
    'refuses an unsafe %s API source before selecting its secrets',
    (kind) => {
      const root = temporary();
      const invitation = invitationSettings();
      writeEnvironment(root, 'selected-private', { ...apiIdentity, ...invitation });
      const path = join(root, '.env');
      if (kind === 'symlink') symlinkSync('selected-private', path);
      else if (kind === 'hardlink') linkSync(join(root, 'selected-private'), path);
      else {
        writeEnvironment(root, '.env', { ...apiIdentity, ...invitation });
        if (kind === 'permissions') chmodSync(path, 0o644);
        if (kind === 'oversize') writeFileSync(path, 'IGNORED=' + 'x'.repeat(256 * 1024));
        if (kind === 'utf8')
          writeFileSync(path, Buffer.concat([Buffer.from('IGNORED='), Buffer.from([0xc3, 0x28])]));
      }
      expect(() => loadLocalEnvironment(root, {}, { apiAuthentication: true })).toThrow(
        /^Cannot read local environment$/,
      );
    },
  );

  it.each(['file', 'projection', 'process'] as const)(
    'passes selected invitation settings from %s to the actual API child',
    (source) => {
      const fixture = launcher();
      const invitation = invitationSettings();
      const values = {
        ...fixture.values,
        ...apiIdentity,
        ...(source === 'file' ? invitation : {}),
      };
      if (source === 'projection')
        writeEnvironment(fixture.root, 'frontend/apps/web/.env.local', invitation);
      const result = launch(fixture, values, source === 'process' ? invitation : {});
      expect(result.error).toBeUndefined();
      expect(result.status, result.stderr).toBe(0);
      const child = JSON.parse(readFileSync(fixture.marker, 'utf8')) as {
        env: Record<string, string>;
      };
      expect(child.env).toMatchObject(invitation);
      expect(result.stdout).toBe('');
      expect(result.stderr).toBe('');
    },
  );

  it.each(['root', 'projection', 'process'] as const)(
    'selects invitation settings from %s only for the API',
    (source) => {
      const root = temporary();
      mkdirSync(join(root, 'frontend/apps/web'), { recursive: true });
      const invitation = invitationSettings();
      writeEnvironment(root, '.env', {
        ...apiIdentity,
        ...(source === 'root' ? invitation : {}),
      });
      if (source === 'projection')
        writeEnvironment(root, 'frontend/apps/web/.env.local', invitation);
      const processValues = source === 'process' ? invitation : {};
      const values = loadLocalEnvironment(root, processValues, { apiAuthentication: true });
      expect(values).toMatchObject(invitation);
      for (const key of Object.keys(invitation))
        expect(loadLocalEnvironment(root, processValues)).not.toHaveProperty(key);
    },
  );

  it('forwards only the seven selected settings and never ambient or prefix lookalikes', () => {
    const invitation = invitationSettings();
    const ambient = invitationSettings();
    const values = {
      ...selected(),
      ...invitation,
      InvitationEmail__Smtp__ProtocolLogging: 'true',
      InvitationEmail__OtherSecret: 'unselected',
    };
    for (const make of [
      () => publicApiEnvironment(values, configuration, ambient),
      () => publicDemoApiEnvironment(values, configuration, temporary(), ambient),
    ]) {
      const environment = make();
      expect(environment).toMatchObject(invitation);
      expect(environment).not.toHaveProperty('InvitationEmail__Smtp__ProtocolLogging');
      expect(environment).not.toHaveProperty('InvitationEmail__OtherSecret');
    }
    for (const environment of [
      publicApiEnvironment(selected(), configuration, ambient),
      publicDemoApiEnvironment(selected(), configuration, temporary(), ambient),
      publicWebEnvironment(values, temporary(), ambient),
      publicSeederEnvironment(undefined, ambient),
      developmentSeederRuntimeEnvironment({ ...values, ...apiIdentity }, ambient),
    ])
      for (const key of Object.keys(invitation)) expect(environment).not.toHaveProperty(key);
  });

  it('refuses conflicting selected secrets without choosing a source or printing values', () => {
    const root = temporary();
    const invitation = invitationSettings();
    writeEnvironment(root, '.env', { ...apiIdentity, ...invitation });
    expect(() =>
      loadLocalEnvironment(
        root,
        { InvitationEmail__KeyRing: invitationSettings().InvitationEmail__KeyRing },
        { apiAuthentication: true },
      ),
    ).toThrow(/^Conflicting API invitation email configuration$/);
    expect(() =>
      loadLocalEnvironment(root, { ...invitation }, { apiAuthentication: true }),
    ).not.toThrow();
  });

  it('bounds selected UTF-8 configuration and preserves empty development credentials', () => {
    const values = {
      ...selected(),
      ...invitationSettings(),
      InvitationEmail__Smtp__Username: '',
      InvitationEmail__Smtp__Password: '',
    };
    expect(publicApiEnvironment(values, configuration, {})).toMatchObject(values);
    const maximum = { ...values, InvitationEmail__Smtp__Password: 'é'.repeat(4096) };
    expect(publicApiEnvironment(maximum, configuration, {}).InvitationEmail__Smtp__Password).toBe(
      maximum.InvitationEmail__Smtp__Password,
    );
    const root = temporary();
    expect(loadLocalEnvironment(root, maximum, { apiAuthentication: true })).toMatchObject(maximum);
    for (const invalid of [
      { InvitationEmail__KeyRing: ' ' },
      { InvitationEmail__Smtp__Host: 'smtp.example.invalid\nother' },
      { InvitationEmail__Smtp__Password: 'é'.repeat(4097) },
      { InvitationEmail__Smtp__Password: 'secret\0suffix' },
    ]) {
      const selectedValues = { ...values, ...invalid };
      expect(() => publicApiEnvironment(selectedValues, configuration, {})).toThrow(
        /^Invalid API invitation email configuration$/,
      );
      expect(() => loadLocalEnvironment(root, selectedValues, { apiAuthentication: true })).toThrow(
        /^Invalid API invitation email configuration$/,
      );
    }
  });
});

describe('selected custom-field replay key ring', () => {
  const current = 'Idempotency__CustomFields__CurrentKeyId';
  const oldKey = 'Idempotency__CustomFields__Keys__old-key';
  const newKey = 'Idempotency__CustomFields__Keys__new-key';
  const selectedRing = () => ({
    [current]: 'new-key',
    [oldKey]: randomBytes(32).toString('base64'),
    [newKey]: randomBytes(32).toString('base64'),
  });

  it.each(['root', 'projection', 'process'] as const)(
    'selects %s keys for the API only, including hyphenated retained IDs',
    (source) => {
      const root = temporary();
      const ring = selectedRing();
      writeEnvironment(root, '.env', { ...apiIdentity, ...(source === 'root' ? ring : {}) });
      if (source === 'projection') {
        mkdirSync(join(root, 'frontend/apps/web'), { recursive: true });
        writeEnvironment(root, 'frontend/apps/web/.env.local', ring);
      }
      const processValues = source === 'process' ? ring : {};
      const values = loadLocalEnvironment(root, processValues, { apiAuthentication: true });
      const api = publicApiEnvironment({ ...selected(), ...values }, configuration, {});
      expect(api).toMatchObject(ring);
      const demoApi = publicDemoApiEnvironment(
        { ...selected(), ...values },
        configuration,
        root,
        {},
      );
      expect(demoApi).toMatchObject(ring);
      const webSelection = loadLocalEnvironment(root, processValues);
      const web = publicWebEnvironment(values, root, ring);
      for (const key of Object.keys(ring)) {
        expect(webSelection).not.toHaveProperty(key);
        expect(web).not.toHaveProperty(key);
      }
    },
  );

  it('never borrows the ambient ring, forwards no lookalikes, and invents no key', () => {
    const root = temporary();
    const ambient = selectedRing();
    const values = { ...selected(), Idempotency__CustomFields__OtherSecret: 'unselected' };
    for (const api of [
      publicApiEnvironment(values, configuration, ambient),
      publicDemoApiEnvironment(values, configuration, root, ambient),
    ]) {
      for (const key of Object.keys(ambient)) expect(api).not.toHaveProperty(key);
      expect(api).not.toHaveProperty('Idempotency__CustomFields__OtherSecret');
    }
  });

  it('preserves malformed or conflicting ring settings as invalid API-only input without blocking startup', () => {
    const root = temporary();
    const ring = selectedRing();
    writeEnvironment(root, '.env', { ...apiIdentity, ...ring });
    const values = loadLocalEnvironment(
      root,
      { [newKey]: 'different-material' },
      { apiAuthentication: true },
    );
    expect(values[newKey]).toBe('');
    const api = publicApiEnvironment({ ...selected(), ...values }, configuration, {});
    expect(api[newKey]).toBe('');
    expect(api[oldKey]).toBe(ring[oldKey]);
    const prefix = 'Idempotency__CustomFields__Keys__';
    for (const count of [4, 5, 6]) {
      const keys = Object.fromEntries(
        Array.from({ length: count }, (_, i) => [
          `${prefix}key${i}`,
          randomBytes(32).toString('base64'),
        ]),
      );
      const ringValues = { [current]: 'key0', ...keys };
      for (const selection of [
        ringValues,
        loadLocalEnvironment(temporary(), ringValues, { apiAuthentication: true }),
      ]) {
        for (const api of [
          publicApiEnvironment({ ...selected(), ...selection }, configuration, {}),
          publicDemoApiEnvironment({ ...selected(), ...selection }, configuration, root, {}),
        ]) {
          if (count === 4) {
            expect(api).toMatchObject(ringValues);
            expect(api).not.toHaveProperty(`${prefix}invalid_id`);
          } else {
            // The underscore sentinel is outside the API's closed key-ID
            // grammar; an oversized selection cannot become a usable subset.
            expect(api[`${prefix}invalid_id`]).toBe('');
          }
          expect(Object.keys(api).filter((key) => key.startsWith(prefix))).toHaveLength(
            count === 4 ? 4 : 6,
          );
        }
      }
    }
    const malformed = publicApiEnvironment(
      { ...selected(), [current]: 'bad', Idempotency__CustomFields__Keys__bad: 'invalid-base64' },
      configuration,
      {},
    );
    expect(malformed.Idempotency__CustomFields__Keys__bad).toBe('invalid-base64');
    for (const name of [
      'Idempotency__CustomFields__Keys__Upper',
      'Idempotency__CustomFields__Keys__nested__value',
    ]) {
      const invalid = publicApiEnvironment(
        { ...selected(), ...ring, [current]: 'new-key', [name]: 'not-forwarded' },
        configuration,
        {},
      );
      expect(invalid.Idempotency__CustomFields__Keys__invalid_id).toBe('');
      expect(invalid).not.toHaveProperty(name);
    }
  });
});
