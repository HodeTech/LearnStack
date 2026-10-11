// @vitest-environment node
import { execFileSync } from 'node:child_process';
import { mkdtempSync, readFileSync, realpathSync, rmSync, writeFileSync } from 'node:fs';
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
  publicWebEnvironment,
} from '../../../../../scripts/public-runtime.mjs';
import { healthyServices } from '../../../../../scripts/wait-compose.mjs';

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
      console.log(child.pid);
      process.on('SIGTERM',()=>{child.once('exit',()=>process.exit(0));child.kill('SIGTERM');});
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
