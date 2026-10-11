import { setTimeout as pause } from 'node:timers/promises';
import { resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { composeSelection } from './compose.mjs';
import { DemoProcesses } from './demo-process.mjs';
import { boundedBody } from './demo-http.mjs';

export function defaultServices(model) {
  const expected = Object.keys(model.services).filter(
    (key) => !model.services[key].profiles?.length,
  );
  if (expected.length !== 7 || !expected.includes('postgres') || !expected.includes('keycloak'))
    throw new Error('Default Compose inventory must contain the accepted seven services');
  return expected;
}

export function healthyServices(model, rows, postgresOnly = false) {
  const expected = defaultServices(model);
  const selected = postgresOnly ? ['postgres'] : expected;
  if (
    !postgresOnly &&
    (rows.length !== expected.length || rows.some((row) => !expected.includes(row.Service)))
  )
    return false;
  return selected.every((name) => {
    const matches = rows.filter((row) => row.Service === name);
    return (
      matches.length === 1 &&
      matches[0].State === 'running' &&
      (!model.services[name].healthcheck ||
        model.services[name].healthcheck.disable ||
        matches[0].Health === 'healthy')
    );
  });
}

export async function waitCompose(selected, processes, signal, postgresOnly = false) {
  const run = (args) =>
    processes.run('docker', [...selected.args, ...args], {
      cwd: selected.cwd,
      env: selected.env,
      capture: true,
      label: 'Compose readiness',
      timeout: 15000,
    });
  const model = JSON.parse(await run(['config', '--format', 'json']));
  const deadline = Date.now() + 180000;
  while (true) {
    signal.throwIfAborted();
    const raw = await run(['ps', '-a', '--format', 'json']);
    const rows = raw.trim().startsWith('[')
      ? JSON.parse(raw)
      : raw
          .trim()
          .split('\n')
          .filter(Boolean)
          .map((line) => JSON.parse(line));
    if (healthyServices(model, rows, postgresOnly)) break;
    if (Date.now() >= deadline)
      throw new Error('Compose readiness deadline; inspect the selected project');
    await pause(1000, undefined, { signal });
  }
  if (postgresOnly) return;
  for (const realm of ['learnstack', 'learnstack-hub']) {
    const issuer = `http://localhost:8080/realms/${realm}`;
    while (true) {
      signal.throwIfAborted();
      try {
        const response = await fetch(`${issuer}/.well-known/openid-configuration`, {
          signal: AbortSignal.any([signal, AbortSignal.timeout(3000)]),
          redirect: 'error',
        });
        const body = await boundedBody(response, 65536);
        if (response.ok && JSON.parse(body).issuer === issuer) break;
      } catch {
        /* Bounded first-boot polling; never expose server responses. */
      }
      if (Date.now() >= deadline) throw new Error('Both Keycloak realm discoveries must be ready');
      await pause(1000, undefined, { signal });
    }
  }
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const controller = new AbortController();
  const processes = new DemoProcesses(controller.signal);
  for (const name of ['SIGINT', 'SIGTERM']) process.on(name, () => controller.abort());
  try {
    const args = process.argv.slice(2);
    if (args.length > 1 || (args.length === 1 && args[0] !== '--postgres'))
      throw new Error('Invalid readiness arguments');
    await waitCompose(
      composeSelection(process.env.LEARNSTACK_COMPOSE_MODE ?? 'dev'),
      processes,
      controller.signal,
      args[0] === '--postgres',
    );
  } catch {
    console.error('Stack/realm readiness failed; inspect the selected Compose project');
    process.exitCode = 1;
  } finally {
    await processes.dispose();
  }
}
