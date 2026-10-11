import { randomBytes, X509Certificate, createPublicKey } from 'node:crypto';
import { lookup } from 'node:dns/promises';
import {
  existsSync,
  mkdirSync,
  mkdtempSync,
  readFileSync,
  rmSync,
  writeFileSync,
  unlinkSync,
} from 'node:fs';
import { tmpdir } from 'node:os';
import { resolve, join } from 'node:path';
import { getCACertificates } from 'node:tls';
import { setTimeout as pause } from 'node:timers/promises';
import { fileURLToPath } from 'node:url';
import { composeSelection, repositoryRoot } from './compose.mjs';
import { DemoCommandError, DemoProcesses, installDemoCancellation } from './demo-process.mjs';
import { demoRequest, vacant } from './demo-http.mjs';
import { defaultServices, waitCompose } from './wait-compose.mjs';
import { publicApiEnvironment, publicWebEnvironment } from './public-runtime.mjs';

const appRoot = join(repositoryRoot, 'frontend/apps/web');
const backendRoot = join(repositoryRoot, 'backend');
const seederDll = join(
  backendRoot,
  'src/LearnStack.Tools.Seeder/bin/Release/net10.0/LearnStack.Tools.Seeder.dll',
);

/** Only the owned host's structured lifetime event establishes listener readiness. */
export function isApiListenerEvent(line, origin) {
  try {
    const event = JSON.parse(line);
    return event?.SourceContext === 'Microsoft.Hosting.Lifetime' && event.address === origin;
  } catch {
    return false;
  }
}

export function validateCertificate(certificate, privateKey, hosts, now = Date.now()) {
  const leaf = new X509Certificate(certificate);
  if (
    Date.parse(leaf.validFrom) > now ||
    Date.parse(leaf.validTo) <= now ||
    !leaf.publicKey
      .export({ type: 'spki', format: 'der' })
      .equals(createPublicKey(privateKey).export({ type: 'spki', format: 'der' })) ||
    hosts.some((host) => !leaf.checkHost(host))
  )
    throw new Error('TLS leaf/key/validity/host mismatch; follow README Quickstart');
  return leaf;
}

/** Start the same seeded, production-built topology for foreground use and CI proof. */
export async function startDemo(mode) {
  if (!['local', 'ci'].includes(mode)) throw new Error('Use make demo DEMO_MODE=local or ci');
  const controller = new AbortController();
  const processes = new DemoProcesses(controller.signal);
  const lock = join(repositoryRoot, '.data/demo.lock');
  const rootEnv = join(repositoryRoot, '.env');
  let privateRoot;
  let ownedEnvironment;
  let selected;
  let startedStack = false;
  let locked = false;
  let disposed;
  let signalRequested = false;
  const cancel = () => {
    signalRequested = true;
    controller.abort();
  };
  const removeCancellation = installDemoCancellation(cancel);
  const run = (command, args, label, options = {}) =>
    processes.run(command, args, {
      cwd: repositoryRoot,
      env: process.env,
      label,
      ...options,
    });
  const compose = (args, options = {}) =>
    run('docker', [...selected.args, ...args], 'Selected Compose command', {
      cwd: selected.cwd,
      env: selected.env,
      ...options,
    });
  async function dispose() {
    disposed ??= (async () => {
      controller.abort();
      try {
        await processes.dispose();
        // Cleanup has a separate signal: cancellation must not cancel teardown.
        if (mode === 'ci' && startedStack) {
          const cleanup = new DemoProcesses(new AbortController().signal);
          try {
            await cleanup.run(
              'docker',
              [...selected.args, '--profile', '*', 'down', '--volumes', '--remove-orphans'],
              {
                cwd: selected.cwd,
                env: selected.env,
                label: 'Owned Compose teardown',
                timeout: 20000,
              },
            );
            const remaining = await cleanup.run(
              'docker',
              [...selected.args, '--profile', '*', 'ps', '-a', '-q'],
              {
                cwd: selected.cwd,
                env: selected.env,
                label: 'Owned Compose teardown verification',
                capture: true,
                timeout: 5000,
              },
            );
            if (remaining.trim()) throw new Error('Owned Compose cleanup incomplete');
          } finally {
            await cleanup.dispose();
          }
        }
        if (ownedEnvironment !== undefined) {
          if (readFileSync(rootEnv, 'utf8') !== ownedEnvironment)
            throw new Error('CI environment changed; private files retained');
          unlinkSync(rootEnv);
        }
        if (privateRoot) rmSync(privateRoot, { recursive: true });
        if (locked) rmSync(lock, { recursive: true });
      } finally {
        // Repeated terminal signals must not interrupt asynchronous teardown.
        removeCancellation();
      }
    })();
    return disposed;
  }
  try {
    if (process.platform === 'win32')
      throw new Error('The demo requires the documented POSIX/WSL environment');
    mkdirSync(join(repositoryRoot, '.data'), { recursive: true });
    try {
      mkdirSync(lock, { mode: 0o700 });
      locked = true;
    } catch {
      throw new Error(
        'Another demo owns .data/demo.lock; verify its shutdown before removing a stale lock',
      );
    }
    await vacant(3000);
    for (const [command, args] of [
      ['node', ['--version']],
      ['pnpm', ['--version']],
      ['dotnet', ['--version']],
      ['docker', ['compose', 'version']],
      ['openssl', ['version']],
      ['make', ['--version']],
    ])
      await run(command, args, `Required ${command}`, {
        cwd: command === 'dotnet' ? backendRoot : repositoryRoot,
        timeout: 15000,
      });
    privateRoot = mkdtempSync(join(tmpdir(), 'learnstack-demo-'));
    // Git/hook configuration is untouched. The frontend lock and pinned SDK/tool
    // manifests are the same dependency inputs used by required checks.
    console.log('demo: restoring pinned dependencies');
    await run('pnpm', ['install', '--frozen-lockfile'], 'Locked frontend restore', {
      cwd: join(repositoryRoot, 'frontend'),
      timeout: 180000,
    });
    await run('dotnet', ['restore', 'LearnStack.slnx'], 'Backend restore', {
      cwd: backendRoot,
      timeout: 180000,
    });
    await run('dotnet', ['tool', 'restore'], 'Pinned tools restore', {
      cwd: backendRoot,
      timeout: 60000,
    });
    await run('pnpm', ['--filter', '@learnstack/web', 'build:ingress'], 'Ingress compilation', {
      cwd: join(repositoryRoot, 'frontend'),
    });
    await run(
      'dotnet',
      [
        'build',
        'src/LearnStack.Tools.Seeder',
        '--configuration',
        'Release',
        '--no-restore',
        '--nologo',
      ],
      'Seeder build',
      { cwd: backendRoot },
    );
    const manifest = JSON.parse(
      await run('dotnet', [seederDll, '--demo-manifest'], 'Seed-derived manifest', {
        capture: true,
        cwd: backendRoot,
      }),
    );
    const hosts = [...new Set(manifest.map((lane) => lane.host))];
    const { loadLocalEnvironment, parseLocalEnvironment, publicServerConfiguration } = await import(
      '../frontend/apps/web/.server/ingress.js'
    );
    if (mode === 'ci') {
      if (existsSync(rootEnv) || existsSync(join(appRoot, '.env.local')))
        throw new Error('CI requires an isolated checkout without private root/web configuration');
      const values = parseLocalEnvironment(
        readFileSync(join(repositoryRoot, '.env.example'), 'utf8'),
      );
      for (const [name, suffix] of [
        ['LEARNSTACK_MIGRATION_PW', 'Migration'],
        ['LEARNSTACK_APP_PW', 'Default'],
        ['LEARNSTACK_PLATFORM_PW', 'PlatformAdmin'],
        ['LEARNSTACK_OUTBOX_PW', 'OutboxDispatcher'],
      ]) {
        const password = randomBytes(24).toString('hex');
        values[name] = password;
        values[`ConnectionStrings__${suffix}`] = values[`ConnectionStrings__${suffix}`].replace(
          /Password=[^;]*/,
          `Password=${password}`,
        );
      }
      values.POSTGRES_PASSWORD = randomBytes(24).toString('hex');
      values.LEARNSTACK_PUBLIC_HOP_SECRET = randomBytes(32).toString('base64url');
      values.LEARNSTACK_PUBLIC_TLS_CERT = join(privateRoot, 'leaf.pem');
      values.LEARNSTACK_PUBLIC_TLS_KEY = join(privateRoot, 'leaf-key.pem');
      ownedEnvironment =
        Object.entries(values)
          .map(([key, value]) => `${key}=${value}`)
          .join('\n') + '\n';
      writeFileSync(rootEnv, ownedEnvironment, { flag: 'wx', mode: 0o600 });
      await run(
        'openssl',
        [
          'req',
          '-x509',
          '-newkey',
          'rsa:2048',
          '-nodes',
          '-keyout',
          values.LEARNSTACK_PUBLIC_TLS_KEY,
          '-out',
          values.LEARNSTACK_PUBLIC_TLS_CERT,
          '-days',
          '2',
          '-subj',
          '/CN=LearnStack temporary demo',
          '-addext',
          `subjectAltName=DNS:localhost,${hosts.map((host) => `DNS:${host}`).join(',')}`,
        ],
        'Disposable CI TLS',
        { timeout: 15000 },
      );
    } else {
      if (!existsSync(rootEnv))
        writeFileSync(rootEnv, readFileSync(join(repositoryRoot, '.env.example')), {
          flag: 'wx',
          mode: 0o600,
        });
      await run('node', ['scripts/public-local.mjs', 'prepare'], 'Private local preparation');
    }
    const rawValues = parseLocalEnvironment(readFileSync(rootEnv, 'utf8'));
    for (const [key, value] of Object.entries(rawValues))
      if (process.env[key] !== undefined && process.env[key] !== value)
        throw new Error('Environment disagrees with private root configuration');
    const values = loadLocalEnvironment(repositoryRoot, process.env);
    const configuration = publicServerConfiguration(values, repositoryRoot);
    await vacant(Number(new URL(configuration.apiOrigin).port || '80'));
    await run(
      'dotnet',
      [seederDll, '--validate-demo-config'],
      'Local database configuration validation',
      { cwd: backendRoot, env: { ...process.env, ...rawValues } },
    );
    const certificate = readFileSync(configuration.certificate);
    const leaf = validateCertificate(certificate, readFileSync(configuration.privateKey), [
      ...hosts,
      'localhost',
    ]);
    const ca =
      mode === 'ci'
        ? certificate
        : [...getCACertificates('default'), ...getCACertificates('system')];
    if (mode === 'local') {
      for (const host of hosts) {
        const addresses = await lookup(host, { all: true });
        if (
          !addresses.length ||
          addresses.some((row) => !['127.0.0.1', '::1'].includes(row.address))
        )
          throw new Error('Seed hosts must resolve to loopback; follow README Quickstart');
      }
      // Trust is checked before infrastructure starts, against the same leaf and
      // CA set used by readiness. No trust-store writes are performed.
      const trustFile = join(privateRoot, 'trusted-roots.pem');
      writeFileSync(trustFile, ca.join('\n'), { mode: 0o600 });
      await run(
        'openssl',
        ['verify', '-CAfile', trustFile, configuration.certificate],
        'TLS trust verification',
        { timeout: 15000 },
      );
    }
    const composeEnvironment = {
      ...process.env,
      LEARNSTACK_COMPOSE_PROJECT:
        mode === 'ci' ? `learnstack-demo-${randomBytes(8).toString('hex')}` : 'learnstack-dev',
      LEARNSTACK_COMPOSE_ENV_FILE: rootEnv,
    };
    selected = composeSelection(mode === 'ci' ? 'e2e' : 'dev', composeEnvironment);
    const composeModel = JSON.parse(
      await compose(['config', '--format', 'json'], {
        capture: true,
        timeout: 15000,
      }),
    );
    const existingRows = (
      await compose(['ps', '--status', 'running', '--format', '{{.Service}}'], {
        capture: true,
        timeout: 15000,
      })
    )
      .split('\n')
      .filter(Boolean);
    if (mode === 'ci' && existingRows.length) throw new Error('CI project must be unoccupied');
    for (const service of defaultServices(composeModel)) {
      const description = composeModel.services[service];
      if (mode === 'local' && existingRows.includes(service)) continue;
      for (const port of description.ports ?? [])
        if (port.protocol === 'tcp' && /^\d+$/.test(String(port.published)))
          await vacant(Number(port.published));
    }
    console.log('demo: starting the selected seven-service stack');
    startedStack = true;
    await compose(['up', '-d'], { timeout: 180000 });
    await waitCompose(selected, processes, controller.signal, true);
    await run('make', ['migrate'], 'All migration chains', {
      env: {
        ...process.env,
        ConnectionStrings__Migration: rawValues.ConnectionStrings__Migration,
      },
      timeout: 300000,
    });
    await waitCompose(selected, processes, controller.signal);
    await run('dotnet', [seederDll], 'Contextual seed convergence', {
      cwd: backendRoot,
      env: {
        PATH: process.env.PATH,
        HOME: process.env.HOME,
        ConnectionStrings__Default: rawValues.ConnectionStrings__Default,
      },
      timeout: 180000,
    });
    if (mode === 'ci') {
      await compose(['stop', 'keycloak'], { timeout: 20000 });
      const rows = await compose(['ps', '--status', 'running', '--format', '{{.Service}}'], {
        capture: true,
      });
      if (rows.split('\n').includes('keycloak')) throw new Error('Owned Keycloak did not stop');
    }
    console.log('demo: building production API and renderer');
    await run(
      'dotnet',
      ['build', 'src/LearnStack.Api', '--configuration', 'Release', '--no-restore', '--nologo'],
      'Production API build',
      { cwd: backendRoot },
    );
    await run('pnpm', ['--filter', '@learnstack/web', 'build'], 'Production renderer build', {
      cwd: join(repositoryRoot, 'frontend'),
      timeout: 180000,
    });
    const observation = {
      apiReady: false,
      webReady: false,
      counts: new Map(),
      malformed: false,
    };
    let apiTail = '';
    const observeApi = (chunk) => {
      apiTail += chunk.toString();
      if (apiTail.length > 1024 * 1024) throw new Error('Unbounded API diagnostic line');
      const lines = apiTail.split('\n');
      apiTail = lines.pop();
      for (const line of lines) {
        if (isApiListenerEvent(line, configuration.apiOrigin)) observation.apiReady = true;
        // Step 3's proof consumes sanitized structured public completion counts.
        // No raw diagnostic line is written to artifacts or the terminal.
      }
    };
    let webTail = '';
    processes.daemon(
      'dotnet',
      [join(backendRoot, 'src/LearnStack.Api/bin/Release/net10.0/LearnStack.Api.dll')],
      {
        cwd: join(backendRoot, 'src/LearnStack.Api'),
        env: publicApiEnvironment(values, configuration),
        label: 'Owned API',
        observe: observeApi,
      },
    );
    processes.daemon('node', ['scripts/public-server.mjs'], {
      cwd: appRoot,
      env: publicWebEnvironment(values, privateRoot),
      label: 'Owned native web',
      observe: (chunk) => {
        webTail = (webTail + chunk.toString()).slice(-1024);
        if (webTail.includes('Public HTTPS listener ready on 127.0.0.1:3000'))
          observation.webReady = true;
      },
    });
    const readinessDeadline = Date.now() + 60000;
    while (!observation.apiReady || !observation.webReady) {
      processes.assertActive();
      if (Date.now() >= readinessDeadline) throw new Error('Owned API/web startup deadline');
      await pause(100, undefined, { signal: controller.signal });
    }
    for (const lane of manifest) {
      processes.assertActive();
      const result = await demoRequest(lane.lessonUrl, {
        ca,
        signal: controller.signal,
        loopback: mode === 'ci',
      });
      if (
        result.status !== 200 ||
        !String(result.headers['cache-control']).includes('no-store') ||
        !result.body.includes(`<h1>${lane.lessonTitle}</h1>`) ||
        !result.body.includes(lane.displayName) ||
        !result.body.includes(`lang="${lane.locale}"`) ||
        lane.fields.some((field) => !result.body.includes(field.value))
      )
        throw new Error('Positive seeded host/locale/content readiness failed');
    }
    console.log(
      'demo: ready; Ctrl+C stops only owned API/web processes. Local Compose data is retained.',
    );
    for (const lane of manifest)
      for (const url of [lane.catalogUrl, lane.courseUrl, lane.lessonUrl]) console.log(url);
    return {
      mode,
      manifest,
      configuration,
      ca,
      leaf,
      privateRoot,
      observation,
      processes,
      signal: controller.signal,
      dispose,
      compose,
      signalRequested: () => signalRequested,
    };
  } catch (error) {
    try {
      await dispose();
    } catch {
      throw new DemoCommandError('Cleanup could not be confirmed; owned private files retained');
    }
    throw error;
  }
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  let runtime;
  try {
    if (process.argv.length > 3) throw new Error('Use make demo DEMO_MODE=local or ci');
    runtime = await startDemo(process.argv[2] ?? process.env.DEMO_MODE ?? 'local');
    if (runtime.mode === 'ci') {
      const { verifyPublicDemo } = await import(
        '../frontend/apps/web/scripts/verify-public-demo.mjs'
      );
      await verifyPublicDemo(runtime);
    } else {
      await Promise.race([
        runtime.processes.failure.then((error) => {
          throw error;
        }),
        new Promise((resolve) => {
          if (runtime.signal.aborted) resolve();
          else runtime.signal.addEventListener('abort', resolve, { once: true });
        }),
      ]);
    }
  } catch (error) {
    if (runtime?.mode !== 'local' || !runtime.signalRequested()) {
      // Labels are fixed locally; never echo provider, configuration or build output.
      console.error(
        error instanceof DemoCommandError ||
          (error instanceof Error &&
            /^(?:Use make demo|Port \d+ is occupied|Another demo owns|TLS leaf|Seed hosts|Environment disagrees|CI requires)/.test(
              error.message,
            ))
          ? error.message
          : 'demo: startup/proof failed; inspect prerequisites, TLS, configuration and selected stack',
      );
      process.exitCode = 1;
    }
  } finally {
    try {
      await runtime?.dispose();
    } catch {
      console.error('demo: cleanup could not be confirmed; owned private files retained');
      process.exitCode = 1;
    }
  }
}
