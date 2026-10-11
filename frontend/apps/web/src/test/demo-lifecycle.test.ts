// @vitest-environment node
import { execFileSync, spawnSync } from 'node:child_process';
import { mkdirSync, mkdtempSync, readFileSync, realpathSync, rmSync, writeFileSync } from 'node:fs';
import { createServer as createHttpsServer } from 'node:https';
import { createServer } from 'node:net';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

import { afterEach, describe, expect, it } from 'vitest';

import { composeSelection } from '../../../../../scripts/compose.mjs';
import { boundedBody, demoRequest, vacant } from '../../../../../scripts/demo-http.mjs';
import { DemoProcesses } from '../../../../../scripts/demo-process.mjs';
import { isApiListenerEvent, validateCertificate } from '../../../../../scripts/demo.mjs';
import {
  publicApiEnvironment,
  publicDemoApiEnvironment,
  publicWebEnvironment,
} from '../../../../../scripts/public-runtime.mjs';
import { defaultServices, healthyServices } from '../../../../../scripts/wait-compose.mjs';

const roots: string[] = [];
const owners: DemoProcesses[] = [];
afterEach(async () => {
  await Promise.all(owners.splice(0).map((owner) => owner.dispose()));
  for (const root of roots.splice(0)) rmSync(root, { recursive: true, force: true });
});
function temporary() {
  const root = mkdtempSync(join(tmpdir(), 'learnstack-demo-control-'));
  roots.push(root);
  return root;
}
function owner(signal = new AbortController().signal) {
  const processes = new DemoProcesses(signal);
  owners.push(processes);
  return processes;
}

describe('shared explicit demo stack selection', () => {
  it.each([
    'COMPOSE_PROFILES=gated',
    ' export COMPOSE_PROFILES = gated',
    'COMPOSE_FILE: foreign.yml',
    'COMPOSE_PROJECT_NAME=foreign',
    'COMPOSE_ENV_FILES=foreign.env',
  ])('refuses a selected dotenv Compose control before Docker: %s', (assignment) => {
    const root = temporary();
    writeFileSync(join(root, '.env'), `POSTGRES_PASSWORD=private\n${assignment}\n`);
    expect(() => composeSelection('dev', {}, root)).toThrow('Reserved COMPOSE_*');
    writeFileSync(join(root, '.env'), '# COMPOSE_PROFILES=gated\nPOSTGRES_PASSWORD=private\n');
    expect(() => composeSelection('dev', {}, root)).not.toThrow();
  });
  it('refuses stale ambient seed credentials before stack or database actions', () => {
    const root = temporary();
    mkdirSync(join(root, 'scripts'));
    const script = join(root, 'scripts/seed.sh');
    writeFileSync(script, readFileSync(new URL('../../../../../scripts/seed.sh', import.meta.url)));
    const migration = 'Host=localhost;Username=learnstack_migration';
    const app = 'Host=localhost;Username=learnstack_app';
    writeFileSync(
      join(root, 'selected.env'),
      `ConnectionStrings__Migration=${migration}\nConnectionStrings__Default=${app}\n`,
    );
    const env = {
      PATH: process.env.PATH,
      LEARNSTACK_COMPOSE_ENV_FILE: 'selected.env',
      ConnectionStrings__Migration: migration,
      ConnectionStrings__Default: app,
    };
    const run = (environment: Record<string, string | undefined>) =>
      spawnSync('bash', [script, '--validate-config'], {
        cwd: root,
        env: { ...environment, NODE_ENV: 'test' },
        encoding: 'utf8',
        timeout: 5000,
      });
    expect(run(env).status).toBe(0);
    for (const key of ['ConnectionStrings__Migration', 'ConnectionStrings__Default']) {
      const result = run({ ...env, [key]: 'Host=foreign;Username=stale' });
      expect(result.error).toBeUndefined();
      expect(result.status).not.toBe(0);
      expect(result.stderr).toContain('disagrees');
      expect(result.stderr).not.toContain('foreign');
    }
  });
  it('reads the selected migration env file and refuses its runtime role before any database act', () => {
    const root = temporary();
    writeFileSync(
      join(root, 'Makefile'),
      readFileSync(new URL('../../../../../Makefile', import.meta.url)),
    );
    mkdirSync(join(root, 'scripts'));
    writeFileSync(
      join(root, 'scripts/connection-string.awk'),
      readFileSync(new URL('../../../../../scripts/connection-string.awk', import.meta.url)),
    );
    writeFileSync(
      join(root, '.env'),
      'ConnectionStrings__Migration=Username=learnstack_platform\n',
    );
    writeFileSync(
      join(root, 'alternate.env'),
      'ConnectionStrings__Migration=Username=learnstack_app\n',
    );
    const result = spawnSync('make', ['migrate'], {
      cwd: root,
      env: {
        PATH: process.env.PATH,
        NODE_ENV: 'test',
        LEARNSTACK_COMPOSE_ENV_FILE: 'alternate.env',
      },
      encoding: 'utf8',
      timeout: 5000,
    });
    expect(result.error).toBeUndefined();
    expect(result.status).not.toBe(0);
    expect(result.stdout).toContain("Username='learnstack_app'");
    expect(result.stdout).not.toContain("Username='learnstack_platform'");
  });
  it('accepts only the owned API address in a structured hosting lifetime event', () => {
    const origin = 'http://127.0.0.1:5080';
    const event = { SourceContext: 'Microsoft.Hosting.Lifetime', address: origin };
    expect(isApiListenerEvent(JSON.stringify(event), origin)).toBe(true);
    expect(isApiListenerEvent(JSON.stringify({ ...event, address: origin + '1' }), origin)).toBe(
      false,
    );
    expect(isApiListenerEvent(JSON.stringify({ ...event, SourceContext: 'foreign' }), origin)).toBe(
      false,
    );
    expect(isApiListenerEvent(`Now listening on: ${origin}`, origin)).toBe(false);
    expect(isApiListenerEvent('null', origin)).toBe(false);
  });
  it('keeps project/files/env stable and removes inherited Compose and dotenv shadows', () => {
    const root = temporary();
    writeFileSync(join(root, '.env'), 'POSTGRES_PASSWORD=private-file-value\n');
    const selected = composeSelection(
      'e2e',
      {
        PATH: '/bin',
        COMPOSE_PROFILES: 'gated',
        COMPOSE_FILE: 'foreign.yml',
        COMPOSE_PROJECT_NAME: 'foreign',
        POSTGRES_PASSWORD: 'ambient-shadow',
        LEARNSTACK_COMPOSE_PROJECT: 'owned-demo',
      },
      root,
    );
    expect(selected.project).toBe('owned-demo');
    expect(selected.args).toEqual([
      'compose',
      '--project-name',
      'owned-demo',
      '--env-file',
      join(root, '.env'),
      '-f',
      join(root, 'infra/compose/dev.yml'),
      '-f',
      join(root, 'infra/compose/e2e.yml'),
    ]);
    expect(selected.env).not.toHaveProperty('COMPOSE_PROFILES');
    expect(selected.env).not.toHaveProperty('COMPOSE_FILE');
    expect(selected.env).not.toHaveProperty('COMPOSE_PROJECT_NAME');
    expect(selected.env).not.toHaveProperty('POSTGRES_PASSWORD');
    expect(selected.env.PATH).toBe('/bin');
    expect(Object.isFrozen(selected.args)).toBe(true);
  });
  it('refuses missing environment, unsupported mode and unsafe project names', () => {
    const root = temporary();
    expect(() => composeSelection('dev', {}, root)).toThrow();
    writeFileSync(join(root, '.env'), 'A=B\n');
    expect(() => composeSelection('unknown', {}, root)).toThrow();
    expect(() =>
      composeSelection('dev', { LEARNSTACK_COMPOSE_PROJECT: '../foreign' }, root),
    ).toThrow();
  });
  it('waits for postgres first, then refuses missing/extra/unhealthy full-stack rows', () => {
    const names = ['postgres', 'keycloak', 'one', 'two', 'three', 'four', 'five'];
    const model = {
      services: Object.fromEntries(names.map((name) => [name, { healthcheck: {} }])),
    };
    const rows = names.map((name) => ({ Service: name, State: 'running', Health: 'healthy' }));
    const withGated = { services: { ...model.services, gated: { profiles: ['gated'] } } };
    expect(defaultServices(withGated)).toEqual(names);
    expect(healthyServices(withGated, rows)).toBe(true);
    expect(healthyServices(model, rows.slice(0, 1), true)).toBe(true);
    expect(healthyServices(model, rows)).toBe(true);
    expect(healthyServices(model, rows.slice(0, -1))).toBe(false);
    expect(
      healthyServices(model, [...rows, { Service: 'orphan', State: 'running', Health: 'healthy' }]),
    ).toBe(false);
    expect(
      healthyServices(
        model,
        rows.map((row) => ({ ...row, Health: '' })),
      ),
    ).toBe(false);
    expect(() => healthyServices({ services: {} }, [])).toThrow();
  });
});

describe('demo runtime boundaries', () => {
  it('isolates the Development API user-secret profile within the invocation', () => {
    const root = temporary();
    const env = publicDemoApiEnvironment(
      { ConnectionStrings__Default: 'application' },
      { apiOrigin: 'http://127.0.0.1:5080', secret: 'private' },
      root,
      {
        PATH: '/bin',
        HOME: '/foreign-home',
        APPDATA: '/foreign-secrets',
        DOTNET_CLI_HOME: '/foreign',
      },
    );
    expect(env.HOME).toBe(realpathSync(root));
    expect(env.APPDATA).toBe(realpathSync(root));
    expect(env.TMPDIR).toBe(realpathSync(root));
    expect(env).not.toHaveProperty('DOTNET_CLI_HOME');
    expect(env.ConnectionStrings__Default).toBe('application');
    expect(env.ASPNETCORE_ENVIRONMENT).toBe('Development');
  });
  it('gives native web addons an owned cache without ambient credentials or diagnostics', () => {
    const root = temporary();
    const values = {
      LEARNSTACK_PUBLIC_API_ORIGIN: 'http://127.0.0.1:5080',
      LEARNSTACK_PUBLIC_HOP_SECRET: 'private',
      LEARNSTACK_PUBLIC_TLS_CERT: '/owned/cert',
      LEARNSTACK_PUBLIC_TLS_KEY: '/owned/key',
      ConnectionStrings__Migration: 'owner',
    };
    expect(
      publicWebEnvironment(values, root, { PATH: '/bin', DEBUG: '*', HOME: '/foreign' }),
    ).toEqual({
      PATH: '/bin',
      TMPDIR: root,
      SWC_NATIVE_BINDING_CACHE: join(realpathSync(root), 'swc-native-cache'),
      NODE_ENV: 'production',
      NEXT_TELEMETRY_DISABLED: '1',
      NODE_TLS_REJECT_UNAUTHORIZED: '1',
      ...Object.fromEntries(
        Object.entries(values).filter(([key]) => key.startsWith('LEARNSTACK_PUBLIC_')),
      ),
    });
  });
  it('passes only request runtime credentials and explicit hop configuration', () => {
    const env = publicApiEnvironment(
      { ConnectionStrings__Default: 'application' },
      { apiOrigin: 'http://127.0.0.1:5080', secret: 'private' },
      {
        PATH: '/bin',
        ConnectionStrings__Migration: 'owner',
        ConnectionStrings__OutboxDispatcher: 'worker',
        LEARNSTACK_MIGRATION_PW: 'owner',
        ASPNETCORE_FORWARDEDHEADERS_ENABLED: 'true',
        Tenancy__TrustedHop__Secrets__1: 'stale',
      },
    );
    expect(env).toEqual({
      PATH: '/bin',
      ConnectionStrings__Default: 'application',
      ASPNETCORE_ENVIRONMENT: 'Development',
      ASPNETCORE_URLS: 'http://127.0.0.1:5080',
      Tenancy__TrustedHop__Networks__0: '127.0.0.1/32',
      Tenancy__TrustedHop__Secrets__0: 'private',
    });
  });
  it('refuses a foreign listener without closing it', async () => {
    const server = createServer();
    await new Promise<void>((resolve) => server.listen(0, '127.0.0.1', resolve));
    const address = server.address();
    if (address === null || typeof address === 'string') throw new Error('Missing listener');
    try {
      await expect(vacant(address.port)).rejects.toThrow('occupied');
      expect(server.listening).toBe(true);
    } finally {
      await new Promise<void>((resolve) => server.close(() => resolve()));
    }
    await expect(vacant(address.port)).resolves.toBeUndefined();
  });
  it('bounds response bodies and cancels oversized input', async () => {
    await expect(boundedBody(new Response('ready'), 5)).resolves.toBe('ready');
    await expect(boundedBody(new Response('too large'), 3)).rejects.toThrow('bound');
  });
  it('requires matching, valid TLS and keeps hostname checks with a loopback resolver', async () => {
    const root = temporary();
    const cert = join(root, 'cert.pem');
    const key = join(root, 'key.pem');
    execFileSync(
      'openssl',
      [
        'req',
        '-x509',
        '-newkey',
        'rsa:2048',
        '-nodes',
        '-keyout',
        key,
        '-out',
        cert,
        '-days',
        '1',
        '-subj',
        '/CN=demo.test',
        '-addext',
        'subjectAltName=DNS:demo.test',
      ],
      { stdio: 'ignore' },
    );
    expect(
      validateCertificate(readFileSync(cert), readFileSync(key), ['demo.test']).checkHost(
        'demo.test',
      ),
    ).toBe('demo.test');
    expect(() =>
      validateCertificate(readFileSync(cert), readFileSync(key), ['other.test']),
    ).toThrow();
    const otherKey = join(root, 'other-key.pem');
    execFileSync(
      'openssl',
      ['genpkey', '-algorithm', 'RSA', '-pkeyopt', 'rsa_keygen_bits:2048', '-out', otherKey],
      { stdio: 'ignore' },
    );
    expect(() =>
      validateCertificate(readFileSync(cert), readFileSync(otherKey), ['demo.test']),
    ).toThrow();
    expect(() =>
      validateCertificate(
        readFileSync(cert),
        readFileSync(key),
        ['demo.test'],
        Date.now() + 2 * 86400000,
      ),
    ).toThrow();
    const server = createHttpsServer(
      { cert: readFileSync(cert), key: readFileSync(key) },
      (_request, response) => response.end('ready'),
    );
    await new Promise<void>((resolve) => server.listen(0, '127.0.0.1', resolve));
    const address = server.address();
    if (address === null || typeof address === 'string') throw new Error('Missing TLS listener');
    const options = {
      ca: readFileSync(cert),
      signal: new AbortController().signal,
      loopback: true,
    };
    try {
      const url = `https://demo.test:${address.port}/`;
      expect((await demoRequest(url, options)).body).toBe('ready');
      await expect(demoRequest(url, { ...options, ca: [] })).rejects.toThrow();
      await expect(demoRequest(`https://other.test:${address.port}/`, options)).rejects.toThrow();
    } finally {
      server.closeAllConnections();
      await new Promise<void>((resolve) => server.close(() => resolve()));
    }
  });
});

describe('owned real process lifecycle', () => {
  it('captures split multibyte stdout intact and bounds accumulated bytes', async () => {
    const processes = owner();
    let observed: (chunk: Buffer) => void = () => {};
    const first = new Promise<Buffer>((resolve) => {
      observed = resolve;
    });
    const running = processes.start(
      process.execPath,
      [
        '-e',
        `
      const text=Buffer.from('Ağaç duruşu');
      const keepAlive=setInterval(()=>{},1000);
      process.once('SIGUSR2',()=>{clearInterval(keepAlive);process.stdout.write(text.subarray(2));});
      process.stdout.write(text.subarray(0,2));
    `,
      ],
      { capture: true, label: 'Split UTF-8 control', observe: observed },
    );
    expect((await first).length).toBe(2);
    running.child.kill('SIGUSR2');
    expect((await running.exit).code).toBe(0);
    expect(running.result().output).toBe('Ağaç duruşu');
    await processes.dispose();
    await expect(
      owner().run(
        process.execPath,
        ['-e', "process.stdout.write(Buffer.from('ğ'.repeat(1024*1024+1)));"],
        { capture: true, label: 'Byte bound control' },
      ),
    ).rejects.toThrow();
  });
  it('routes real terminal hangup through the supervisor cancellation path', async () => {
    const processes = owner();
    let ready: () => void = () => {};
    const started = new Promise<void>((resolve) => {
      ready = resolve;
    });
    const moduleUrl = new URL('../../../../../scripts/demo-process.mjs', import.meta.url).href;
    const running = processes.start(
      process.execPath,
      [
        '--input-type=module',
        '-e',
        `
      import {installDemoCancellation} from ${JSON.stringify(moduleUrl)};
      const remove=installDemoCancellation(()=>{console.log('cancelled');remove();});
      console.log('ready');
    `,
      ],
      {
        capture: true,
        observe: (chunk) => {
          if (chunk.toString().includes('ready')) ready();
        },
      },
    );
    await started;
    running.child.kill('SIGHUP');
    expect((await running.exit).code).toBe(0);
    expect(running.result().output).toContain('cancelled');
  });
  it('cancels when the actual parent exits and leaves no owned group behind', async () => {
    const moduleUrl = new URL('../../../../../scripts/demo-process.mjs', import.meta.url).href;
    const watcher = `
      import {installDemoCancellation} from ${JSON.stringify(moduleUrl)};
      const remove=installDemoCancellation(()=>{console.log('parent-loss handled');remove();});
      process.send('ready');
    `;
    const output = await owner().run(
      process.execPath,
      [
        '--input-type=module',
        '-e',
        `
      import {spawn} from 'node:child_process';
      const child=spawn(process.execPath,['--input-type=module','-e',${JSON.stringify(watcher)}],
        {stdio:['ignore','inherit','inherit','ipc']});
      child.once('message',()=>process.exit(0));
    `,
      ],
      { capture: true, label: 'Parent-loss control', timeout: 3000 },
    );
    expect(output).toContain('parent-loss handled');
  });
  it('joins a daemon and its descendant, and closes ownership against later spawns', async () => {
    const processes = owner();
    let release: (pid: number) => void = () => {};
    const ready = new Promise<number>((resolve) => {
      release = resolve;
    });
    let output = '';
    const running = processes.daemon(
      process.execPath,
      [
        '-e',
        `
      const {spawn}=require('node:child_process');
      const child=spawn(process.execPath,['-e','setInterval(()=>{},1000)'],{stdio:'ignore'});
      let stopping=false;
      const finish=()=>{if(stopping) process.exit(0);};
      child.once('exit',finish);
      process.on('SIGTERM',()=>{
        stopping=true;
        if(child.exitCode!==null || child.signalCode!==null) finish();
        else child.kill('SIGTERM');
      });
      console.log(child.pid);
      setInterval(()=>{},1000);
    `,
      ],
      {
        label: 'tree',
        observe: (chunk) => {
          output += chunk.toString();
          if (output.includes('\n')) release(Number(output.trim()));
        },
      },
    );
    const descendant = await ready;
    expect(Number.isInteger(descendant)).toBe(true);
    expect(() => process.kill(descendant, 0)).not.toThrow();
    await processes.dispose();
    await running.exit;
    expect(running.child.exitCode).toBe(0);
    expect(() => process.kill(descendant, 0)).toThrow();
    expect(() => processes.start(process.execPath, ['-e', 'process.exit(0)'])).toThrow('closed');
  });
  it('captures a successful command and refuses its nonzero control', async () => {
    const processes = owner();
    await expect(
      processes.run(process.execPath, ['-e', 'process.stdout.write("ready")'], {
        capture: true,
        label: 'control',
      }),
    ).resolves.toBe('ready');
    await expect(
      processes.run(process.execPath, ['-e', 'process.exit(2)'], { label: 'control' }),
    ).rejects.toThrow('control failed');
  });
  it('aborts and joins a real setup child', async () => {
    const controller = new AbortController();
    const processes = owner(controller.signal);
    const running = processes.run(process.execPath, ['-e', 'setInterval(()=>{},1000)'], {
      label: 'setup',
    });
    controller.abort();
    await expect(running).rejects.toThrow();
    await expect(processes.dispose()).resolves.toBeUndefined();
  });
  it('refuses a hung command at its deadline and a daemon that exits early', async () => {
    const processes = owner();
    await expect(
      processes.run(process.execPath, ['-e', 'setInterval(()=>{},1000)'], {
        timeout: 20,
        label: 'hung',
      }),
    ).rejects.toThrow('hung failed');
    processes.daemon(process.execPath, ['-e', 'process.exit(1)'], { label: 'daemon' });
    await expect(processes.failure).resolves.toEqual(
      expect.objectContaining({ message: 'demo: daemon exited failed' }),
    );
    expect(() => processes.assertActive()).toThrow('daemon exited');
  });
});
