// Private light controls. No Next import, build, browser, backend or shared writes.
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { spawnSync } from 'node:child_process';
import { EventEmitter } from 'node:events';
import {
  chmodSync,
  mkdtempSync,
  mkdirSync,
  readFileSync,
  realpathSync,
  rmSync,
  unlinkSync,
  writeFileSync,
} from 'node:fs';
import { createServer, request } from 'node:http';
import { createRequire } from 'node:module';
import { tmpdir } from 'node:os';
import { dirname, join } from 'node:path';
import { test } from 'node:test';
import { fileURLToPath, pathToFileURL } from 'node:url';
import zlib from 'node:zlib';

import {
  deriveTransportSpec,
  instrumentCopiedTransport,
  instrumentCopiedTransportMiddleware,
} from '../apps/web/scripts/framework-transport-proof-source.mjs';
import { verifyFrameworkInstallation } from './framework-installation.mjs';
import {
  copiedTransportEventsComplete,
  createCopiedTransportHost,
  transportProofFailureReason,
} from '../apps/web/scripts/framework-transport-proof-host.mjs';

const repo = realpathSync(fileURLToPath(new URL('../../', import.meta.url)));
const template = readFileSync(
  new URL('../apps/web/scripts/framework-proof-transport-observer.mjs', import.meta.url),
  'utf8',
);
const sha = (data) => createHash('sha256').update(data).digest('hex');
const put = (path, data) => {
  mkdirSync(dirname(path), { recursive: true });
  writeFileSync(path, data);
};
const temp = () => realpathSync(mkdtempSync(join(tmpdir(), 'learnstack-transport-prototype-')));
const require = createRequire(import.meta.url);
const refusal = (fn) => {
  try {
    fn();
    assert.fail('Expected refusal');
  } catch (error) {
    return transportProofFailureReason(error);
  }
};

test('actual installed sources parse into exact census-bound locations without loading Next', () => {
  const before = new Set(Object.keys(require.cache));
  const spec = deriveTransportSpec(
    verifyFrameworkInstallation({ appRoot: join(repo, 'frontend/apps/web') }),
  );
  assert.equal(spec.tuple.node, process.versions.node);
  assert.equal(spec.compression.path, 'dist/compiled/compression/index.js');
  assert.equal(spec.router.path, 'dist/server/lib/router-server.js');
  assert.match(spec.censusSha256, /^[0-9a-f]{64}$/);
  for (const item of [spec.compression, spec.router]) {
    assert.match(item.sha256, /^[0-9a-f]{64}$/);
    assert.ok(item.bytes > 0);
    for (const point of Object.values(item.points)) assert.ok(point.line >= 0 && point.column >= 0);
  }
  assert.equal(
    Object.keys(require.cache).some((path) => !before.has(path) && /\/next\/dist\//.test(path)),
    false,
  );
});

test('copied transport middleware forwards exactly one fixed header and refuses anchor drift', () => {
  const path = join(repo, 'frontend/apps/web/src/middleware.ts');
  const original = readFileSync(path, 'utf8');
  const anchor =
    "    if (accept !== null) downstream.set('accept', accept);\n    downstream.set(INGRESS_HEADER, envelope);";
  const added =
    "    const transportEncoding = request.headers.get('accept-encoding');\n    if (transportEncoding !== null) downstream.set('accept-encoding', transportEncoding);\n";
  const result = instrumentCopiedTransportMiddleware(original);
  assert.equal(
    result,
    original.replace(
      anchor,
      "    if (accept !== null) downstream.set('accept', accept);\n" +
        added +
        '    downstream.set(INGRESS_HEADER, envelope);',
    ),
  );
  assert.equal(readFileSync(path, 'utf8'), original, 'original source remains unchanged');
  assert.equal(result.split("downstream.set('accept-encoding'").length, 2);
  assert.throws(() => instrumentCopiedTransportMiddleware(original.replace(anchor, '')));
  assert.throws(() => instrumentCopiedTransportMiddleware(original + '\n' + anchor));
  assert.throws(() => instrumentCopiedTransportMiddleware(result));
});

test('a no-compression observation fails a compressor case without inventing retirement', () => {
  const row = (kind, name = 'healthy-gzip') => ({ case: name, kind, value: null });
  assert.throws(() => copiedTransportEventsComplete('healthy-gzip', [row('router'), row('none')]));
  assert.throws(() =>
    copiedTransportEventsComplete('fence-deflate', [row('none', 'fence-deflate')]),
  );
  assert.throws(() =>
    copiedTransportEventsComplete('healthy-identity', [row('allocation', 'healthy-identity')]),
  );
  assert.equal(
    copiedTransportEventsComplete('healthy-gzip', [row('router'), row('allocation')]),
    false,
  );
  assert.equal(
    copiedTransportEventsComplete('healthy-gzip', [
      row('router'),
      row('allocation'),
      row('encoder-close'),
      row('response-finish'),
      row('response-close'),
      row('handler-ok'),
    ]),
    true,
  );
  assert.equal(
    refusal(() => copiedTransportEventsComplete('healthy-gzip', [row('handler-failed')])),
    'handler-failed',
  );
  assert.equal(
    refusal(() => copiedTransportEventsComplete('healthy-gzip', [row('none')])),
    'unexpected-selection',
  );
  assert.equal(transportProofFailureReason(new Error('private diagnostic')), 'unknown');
  assert.throws(() =>
    copiedTransportEventsComplete('healthy-gzip', [row('none', 'healthy-deflate')]),
  );
});

test('copied BFF instrumenter binds literal spec and refuses changed or duplicate anchors', async () => {
  const { instrumentCopiedBffRuntime } = await import(
    pathToFileURL(join(repo, 'frontend/apps/web/scripts/bff-proof-host.mjs'))
  );
  const source = readFileSync(
    join(repo, 'frontend/apps/web/scripts/public-server-runtime.mjs'),
    'utf8',
  );
  const bff = instrumentCopiedBffRuntime(source, {
    mutation: undefined,
    external: undefined,
    uiMode: true,
    isolatedProtocol: true,
    disabledOwnPreference: false,
    fakeIssuer: 'https://identity.example.test',
    fakeLogout: 'https://identity.example.test/logout',
    sites: [{ host: 'institution.example.test:3000', displayName: 'Institution', locale: 'en' }],
    marker: 'fixture-marker',
    clockBase: 1800000000000,
  });
  const identity = verifyFrameworkInstallation({ appRoot: join(repo, 'frontend/apps/web') });
  const spec = deriveTransportSpec(identity);
  const result = instrumentCopiedTransport({ source: bff, identity });
  assert.ok(
    result.runtime.includes(
      "import { startCopiedTransportObserver } from './framework-proof-transport-observer.mjs';",
    ),
  );
  assert.ok(
    result.runtime.indexOf('control.registerShutdown(shutdown)') <
      result.runtime.indexOf('startCopiedTransportObserver(framework)'),
  );
  assert.ok(
    result.runtime.indexOf('startCopiedTransportObserver(framework)') <
      result.runtime.indexOf("ownSetup(import('next'))"),
  );
  assert.ok(result.runtime.includes('now += 3600001'));
  assert.ok(result.runtime.includes('await transportProof.close()'));
  assert.ok(result.observer.includes(spec.censusSha256));
  assert.equal(result.observer.includes('__COPIED_TRANSPORT_SPEC__'), false);
  assert.equal(
    instrumentCopiedTransport({ source: bff, identity, observerTemplate: 'arbitrary' }).observer,
    result.observer,
  );
  assert.throws(() =>
    deriveTransportSpec(Object.freeze({ ...identity, censusSha256: '0'.repeat(64) })),
  );
  assert.throws(() =>
    deriveTransportSpec(
      Object.freeze({ ...identity, tuple: { ...identity.tuple, next: 'other' } }),
    ),
  );
  assert.throws(() => deriveTransportSpec({ ...identity }));
  assert.throws(() =>
    instrumentCopiedTransport({ source: bff + '\n  let shutdownPromise;', identity }),
  );
  assert.throws(() => instrumentCopiedTransport({ source, identity }));
  const root = temp();
  try {
    for (const [name, text] of [
      ['runtime.mjs', result.runtime],
      ['observer.mjs', result.observer],
    ]) {
      const path = join(root, name);
      put(path, text);
      assert.equal(spawnSync(process.execPath, ['--check', path], { stdio: 'ignore' }).status, 0);
    }
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

function syntheticScope(root) {
  const frontendRoot = join(root, 'frontend');
  const appRoot = join(frontendRoot, 'apps/web');
  const nextRoot = join(frontendRoot, 'node_modules/next');
  mkdirSync(appRoot, { recursive: true });
  const owner = readFileSync(
    join(repo, 'frontend/framework/next-response-cleanup/owner.mjs'),
    'utf8',
  )
    .replace(
      'export function createResponseCompressionOwner',
      'function createResponseCompressionOwner',
    )
    .replace(
      'export async function joinResponseCompression',
      'async function joinResponseCompression',
    );
  const compression =
    owner +
    `\nfunction selectNone(owner) { function nocompress(a) { owner.markNoCompression(); } nocompress('not acceptable'); }\nmodule.exports={createResponseCompressionOwner,selectNone};\n`;
  const router = `
function addRequestMeta() {}
async function initialize() {
  const compress = (_req,_res,next) => next();
  const requestHandlerImpl = async (req,res) => {
    addRequestMeta(req);
    compress(req,res,()=>{});
    return await req.work(res);
  };
  let requestHandler = function(...args) { return (async function joinResponseCompression() { return await requestHandlerImpl(...args); })(); };
  return requestHandler;
}
module.exports={initialize};
`;
  const compressionPath = 'dist/compiled/compression/index.js',
    routerPath = 'dist/server/lib/router-server.js';
  put(join(nextRoot, compressionPath), compression);
  put(join(nextRoot, routerPath), router);
  const records = [
    {
      path: compressionPath,
      kind: 'compression',
      sha256: sha(compression),
      bytes: Buffer.byteLength(compression),
    },
    { path: routerPath, kind: 'router', sha256: sha(router), bytes: Buffer.byteLength(router) },
  ];
  put(
    join(frontendRoot, 'framework/next-response-cleanup/census.json'),
    JSON.stringify({
      schemaVersion: 1,
      tuple: { node: process.versions.node, pnpm: 'fixture', next: 'fixture' },
      files: records,
    }),
  );
  const census = readFileSync(join(frontendRoot, 'framework/next-response-cleanup/census.json'));
  return {
    frontendRoot,
    appRoot,
    nextRoot,
    compressionPath,
    routerPath,
    format: 1,
    censusSha256: sha(census),
    tuple: JSON.parse(census).tuple,
  };
}

test('census drift and missing exact source fail before any observed execution', () => {
  const root = temp();
  try {
    const scope = syntheticScope(root);
    const original = readFileSync(join(scope.nextRoot, scope.compressionPath));
    assert.doesNotThrow(() => deriveTransportSpec(Object.freeze(scope)));
    writeFileSync(
      join(scope.nextRoot, scope.compressionPath),
      Buffer.concat([original, Buffer.from('\n// changed\n')]),
    );
    assert.throws(() => deriveTransportSpec(Object.freeze(scope)));
    writeFileSync(join(scope.nextRoot, scope.compressionPath), original);
    rmSync(join(scope.nextRoot, scope.routerPath));
    assert.throws(() => deriveTransportSpec(Object.freeze(scope)));
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test('the generated observer refuses an alternate subject path before reading it', async () => {
  const root = temp();
  try {
    const scope = syntheticScope(root);
    const spec = structuredClone(deriveTransportSpec(Object.freeze(scope)));
    spec.compression.path = '../../../outside';
    const modulePath = join(scope.appRoot, 'scripts/framework-proof-transport-observer.mjs');
    put(modulePath, template.replace("'__COPIED_TRANSPORT_SPEC__'", JSON.stringify(spec)));
    const { startCopiedTransportObserver } = await import(pathToFileURL(modulePath).href);
    await assert.rejects(
      startCopiedTransportObserver({
        identity: { ...scope, censusSha256: spec.censusSha256, tuple: spec.tuple },
        mode: 'production',
      }),
    );
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test('host accepts only finite sidecar events bound to the unchanged captured family', () => {
  const root = temp();
  try {
    const scope = syntheticScope(root);
    const lease = Object.freeze({ format: 1, pid: 123, nonce: 'a'.repeat(32), kind: 'production' });
    const original = Object.freeze({
      pid: 456,
      group: 456,
      parentPid: 123,
      leaseNonce: lease.nonce,
      incarnation: 'b'.repeat(32),
    });
    let family = original;
    const broker = {
      assertHealthy() {},
      capture: { family: () => family, lease: () => lease },
      records: () => [{ path: join(scope.appRoot, '.framework-state/writer.json') }],
    };
    const host = createCopiedTransportHost({ app: scope.appRoot, broker });
    assert.throws(() => host.arm('arbitrary'));
    host.arm('healthy-gzip');
    assert.throws(() => host.arm('fence-gzip'));
    unlinkSync(join(root, '.transport-proof.arm'));
    const path = join(root, '.transport-proof.events');
    assert.equal(
      refusal(() => host.events()),
      'host-file-io',
    );
    const row = {
      version: 1,
      pid: family.pid,
      sequence: 1,
      case: 'healthy-gzip',
      kind: 'allocation',
      value: 'gzip',
    };
    writeFileSync(path, JSON.stringify(row) + '\n', { mode: 0o600 });
    assert.deepEqual(host.events(), [row]);
    chmodSync(path, 0o644);
    assert.equal(
      refusal(() => host.events()),
      'host-file-identity',
    );
    chmodSync(path, 0o600);
    writeFileSync(path, JSON.stringify({ ...row, pid: 999 }) + '\n');
    assert.equal(
      refusal(() => host.events()),
      'host-schema',
    );
    writeFileSync(path, JSON.stringify({ ...row, extra: 'private' }) + '\n');
    assert.throws(() => host.events());
    writeFileSync(path, JSON.stringify(row) + '\n');
    family = Object.freeze({ ...original });
    assert.equal(
      refusal(() => host.events()),
      'host-binding',
    );
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test('actual append races retry only a stable snapshot of the same private inode', () => {
  const canonical = pathToFileURL(
    join(repo, 'frontend/apps/web/scripts/framework-transport-proof-host.mjs'),
  ).href;
  const script = `
import assert from 'node:assert/strict';
import fs from 'node:fs';
import { syncBuiltinESMExports } from 'node:module';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
const mode = process.argv[1];
const mod = await import(${JSON.stringify(canonical)});
const root = fs.realpathSync(fs.mkdtempSync(join(tmpdir(), 'learnstack-transport-race-')));
const app = join(root, 'frontend/apps/web');
fs.mkdirSync(app, {recursive:true});
const lease = Object.freeze({format:1,pid:123,nonce:'a'.repeat(32),kind:'production'});
const family = Object.freeze({pid:456,group:456,parentPid:123,leaseNonce:lease.nonce,incarnation:'b'.repeat(32)});
const broker = {assertHealthy(){},capture:{family:()=>family,lease:()=>lease},
  records:()=>[{path:join(app,'.framework-state/writer.json')}]};
const path = join(root,'.transport-proof.events');
let sequence = 1, mutations = 0;
const line = (kind) => JSON.stringify({version:1,pid:456,sequence:sequence++,case:'healthy-gzip',kind,value:null}) + '\\n';
fs.writeFileSync(path,line('router'),{mode:0o600});
const id = fs.statSync(path);
const isTarget = (fd) => {
  if (typeof fd !== 'number') return false;
  try {const s=fs.fstatSync(fd);return s.dev===id.dev && s.ino===id.ino;} catch {return false;}
};
function mutate() {
  if (['old-one','append-once','invalid-append'].includes(mode) && mutations) return;
  mutations++;
  if (mode === 'replace') {fs.unlinkSync(path);fs.writeFileSync(path,line('response-finish'),{mode:0o600});}
  else if (mode === 'mode') fs.chmodSync(path,0o644);
  else if (mode === 'shrink') fs.truncateSync(path,0);
  else if (mode === 'invalid-append') fs.appendFileSync(path, JSON.stringify({version:1,pid:456,sequence:sequence++,case:'healthy-gzip',kind:'response-finish',value:null,extra:true}) + '\\n');
  else fs.appendFileSync(path,line('response-finish'));
}
const oldRead = fs.readSync, oldReadFile = fs.readFileSync;
fs.readSync = function(fd,...args) { const target=isTarget(fd); const value=oldRead.call(fs,fd,...args); if(target)mutate();return value; };
fs.readFileSync = function(fd,...args) { const target=isTarget(fd); const value=oldReadFile.call(fs,fd,...args); if(target)mutate();return value; };
syncBuiltinESMExports();
let output;
try {
  const host = mod.createCopiedTransportHost({app,broker});
  try {
    // Faithful copy of the pre-retry reader's single-snapshot consistency test.
    if (mode === 'old-one') {
      const before=fs.lstatSync(path);const fd=fs.openSync(path,fs.constants.O_RDONLY|fs.constants.O_NOFOLLOW);
      try {
        const opened=fs.fstatSync(fd);
        assert.equal(opened.dev,before.dev);assert.equal(opened.ino,before.ino);assert.equal(opened.size,before.size);
        const bytes=fs.readFileSync(fd),after=fs.fstatSync(fd);
        assert.equal(after.size,bytes.length);assert.equal(after.mtimeMs,opened.mtimeMs);assert.equal(after.ctimeMs,opened.ctimeMs);
      } finally {fs.closeSync(fd);}
      output={outcome:'accepted',count:1,mutations};
    } else {const rows=host.events();output={outcome:'accepted',count:rows.length,mutations};}
  }
  catch(error) {output={outcome:'refused',reason:mode==='old-one'?'legacy':mod.transportProofFailureReason(error),mutations};}
} finally { fs.rmSync(root,{recursive:true,force:true}); }
process.stdout.write(JSON.stringify(output));
`;
  const run = (mode) => {
    const child = spawnSync(process.execPath, ['--input-type=module', '--eval', script, mode], {
      encoding: 'utf8',
      timeout: 5000,
    });
    assert.equal(child.status, 0, 'isolated real-file race control');
    return JSON.parse(child.stdout);
  };
  assert.deepEqual(run('old-one'), { outcome: 'refused', reason: 'legacy', mutations: 1 });
  assert.deepEqual(run('append-once'), { outcome: 'accepted', count: 2, mutations: 1 });
  assert.deepEqual(run('append-always'), {
    outcome: 'refused',
    reason: 'host-file-changing',
    mutations: 3,
  });
  assert.deepEqual(run('replace'), {
    outcome: 'refused',
    reason: 'host-file-identity',
    mutations: 1,
  });
  assert.deepEqual(run('mode'), { outcome: 'refused', reason: 'host-file-identity', mutations: 1 });
  assert.deepEqual(run('shrink'), {
    outcome: 'refused',
    reason: 'host-file-changing',
    mutations: 1,
  });
  assert.deepEqual(run('invalid-append'), {
    outcome: 'refused',
    reason: 'host-schema',
    mutations: 1,
  });
});

test('a module changed after breakpoint setup fails script-source confirmation', async () => {
  const root = temp();
  let observer;
  try {
    const scope = syntheticScope(root);
    const spec = deriveTransportSpec(Object.freeze(scope));
    const observerSource = template.replace("'__COPIED_TRANSPORT_SPEC__'", JSON.stringify(spec));
    const modulePath = join(scope.appRoot, 'scripts/framework-proof-transport-observer.mjs');
    put(modulePath, observerSource);
    const { startCopiedTransportObserver } = await import(pathToFileURL(modulePath).href);
    observer = await startCopiedTransportObserver({
      identity: { ...scope, censusSha256: spec.censusSha256, tuple: spec.tuple },
      mode: 'production',
    });
    const compressionPath = join(scope.nextRoot, scope.compressionPath);
    writeFileSync(
      compressionPath,
      readFileSync(compressionPath, 'utf8') + '\n// changed after setup\n',
    );
    require(compressionPath);
    require(join(scope.nextRoot, scope.routerPath));
    await assert.rejects(observer.confirmLoaded());
    await assert.rejects(observer.close());
    observer = undefined;
  } finally {
    if (observer) await observer.close().catch(() => {});
    rmSync(root, { recursive: true, force: true });
  }
});

test('invalid arm and unretired actual handler cannot become successful observer cleanup', async () => {
  const root = temp();
  let observer;
  try {
    const scope = syntheticScope(root);
    const spec = deriveTransportSpec(Object.freeze(scope));
    const modulePath = join(scope.appRoot, 'scripts/framework-proof-transport-observer.mjs');
    put(modulePath, template.replace("'__COPIED_TRANSPORT_SPEC__'", JSON.stringify(spec)));
    const { startCopiedTransportObserver } = await import(pathToFileURL(modulePath).href);
    observer = await startCopiedTransportObserver({
      identity: { ...scope, censusSha256: spec.censusSha256, tuple: spec.tuple },
      mode: 'production',
    });
    require(join(scope.nextRoot, scope.compressionPath));
    require(join(scope.nextRoot, scope.routerPath));
    await observer.confirmLoaded();
    let invoked = false,
      release;
    const response = new EventEmitter();
    const wrapped = observer.wrapHandler(
      () => {
        invoked = true;
        return new Promise((resolve) => {
          release = resolve;
        });
      },
      () => {},
    );
    put(join(root, '.transport-proof.arm'), 'arbitrary-path-or-profile\n');
    assert.throws(() => wrapped({ method: 'GET', url: '/studio' }, new EventEmitter()));
    assert.equal(invoked, false);
    put(join(root, '.transport-proof.arm'), 'healthy-gzip\n');
    wrapped({ method: 'GET', url: '/studio' }, response);
    assert.equal(invoked, true);
    let settled = false;
    const stopping = observer.close().then(
      () => {
        settled = true;
        return false;
      },
      () => {
        settled = true;
        return true;
      },
    );
    await new Promise((resolve) => setImmediate(resolve));
    assert.equal(settled, false, 'issued actual handler work remains joined');
    release();
    // This fabricated close is only a negative-test teardown; it cannot
    // supply positive compression selection/retirement evidence.
    response.emit('close');
    assert.equal(await stopping, true);
    observer = undefined;
  } finally {
    if (observer) await observer.close().catch(() => {});
    rmSync(root, { recursive: true, force: true });
  }
});

test('private CJS router and real zlib/fence flow observes positive and negative without constructor changes', async () => {
  const root = temp();
  let server, observer;
  try {
    const scope = syntheticScope(root);
    const spec = deriveTransportSpec(Object.freeze(scope));
    const result = instrumentCopiedTransport({
      source: `import { readFileSync } from 'node:fs';\n  let shutdownPromise;\n      const results = await Promise.allSettled(shutdownTasks);\n    const dev = framework.mode === 'development';\n    let now = 0; let refreshes = 0; let accounts = 0; const administrations = {};\n    await ownSetup(app.prepare());\n    assertStarting();\n    const handle = app.getRequestHandler();`,
      identity: Object.freeze(scope),
    });
    const modulePath = join(scope.appRoot, 'scripts/framework-proof-transport-observer.mjs');
    put(modulePath, result.observer);
    const { startCopiedTransportObserver } = await import(pathToFileURL(modulePath).href);
    const identity = { ...scope, censusSha256: spec.censusSha256, tuple: spec.tuple };
    observer = await startCopiedTransportObserver({ identity, mode: 'production' });
    const codec = require(join(scope.nextRoot, scope.compressionPath));
    const handler = await require(join(scope.nextRoot, scope.routerPath)).initialize();
    await observer.confirmLoaded();
    const { protectPageResponse } = await import(
      pathToFileURL(join(repo, 'frontend/apps/web/src/server/auth/page-response.ts')).href
    );
    let invalidated = false,
      currentCase;
    const wrapped = observer.wrapHandler(handler, () => {
      invalidated = true;
    });
    server = createServer((req, res) => {
      const rawWrite = res.write.bind(res),
        rawEnd = res.end.bind(res);
      req.work = () => {
        protectPageResponse(
          res,
          () => {
            if (invalidated) throw new Error('actual fence');
          },
          () => {},
          () => () => {},
        );
        const owner = codec.createResponseCompressionOwner(res);
        if (currentCase === 'healthy-identity') {
          codec.selectNone(owner);
          res.end(Buffer.alloc(8192, 65));
          return owner.join();
        }
        const encoding = currentCase.endsWith('gzip') ? 'gzip' : 'deflate';
        const stream = owner.createCompressor(() =>
          encoding === 'gzip' ? zlib.createGzip() : zlib.createDeflate(),
        );
        res.setHeader('content-type', 'text/plain');
        res.setHeader('content-encoding', encoding);
        res.writeHead(200);
        if (!invalidated) {
          stream.on('data', rawWrite);
          stream.once('end', rawEnd);
          stream.end(Buffer.alloc(8192, 65));
        }
        return owner.join();
      };
      void wrapped(req, res);
    });
    await new Promise((resolve) => server.listen(0, '127.0.0.1', resolve));
    for (const name of [
      'healthy-gzip',
      'fence-gzip',
      'healthy-deflate',
      'fence-deflate',
      'healthy-identity',
    ]) {
      invalidated = false;
      currentCase = name;
      put(join(root, '.transport-proof.arm'), name + '\n');
      const wire = await new Promise((resolve, reject) => {
        const req = request(
          { host: '127.0.0.1', port: server.address().port, path: '/studio' },
          (res) => {
            const chunks = [];
            res.on('data', (c) => chunks.push(c));
            res.on('end', () =>
              resolve({
                status: res.statusCode,
                encoding: res.headers['content-encoding'] ?? null,
                cache: res.headers['cache-control'] ?? null,
                body: Buffer.concat(chunks),
              }),
            );
          },
        );
        req.on('error', reject);
        req.end();
      });
      assert.equal(wire.status, name.startsWith('healthy-') ? 200 : 503);
      if (name === 'healthy-identity') assert.equal(wire.body.length, 8192);
      else if (name.startsWith('healthy-'))
        assert.deepEqual(
          (name.endsWith('gzip') ? zlib.gunzipSync : zlib.inflateSync)(wire.body),
          Buffer.alloc(8192, 65),
        );
      else {
        assert.equal(wire.body.length, 0);
        assert.equal(wire.encoding, null);
        assert.equal(wire.cache, 'no-store');
      }
      // All completion gates are actual callbacks, not an elapsed-time proxy.
      for (
        let i = 0;
        i < 100 &&
        !readFileSync(join(root, '.transport-proof.events'), 'utf8')
          .trim()
          .split('\n')
          .map(JSON.parse)
          .some((item) => item.case === name && item.kind === 'handler-ok');
        i++
      )
        await new Promise((resolve) => setImmediate(resolve));
    }
    await new Promise((resolve, reject) =>
      server.close((error) => (error ? reject(error) : resolve())),
    );
    server = undefined;
    await observer.close();
    observer = undefined;
    const events = readFileSync(join(root, '.transport-proof.events'), 'utf8')
      .trim()
      .split('\n')
      .map(JSON.parse);
    for (const name of [
      'healthy-gzip',
      'fence-gzip',
      'healthy-deflate',
      'fence-deflate',
      'healthy-identity',
    ]) {
      const rows = events.filter((item) => item.case === name);
      assert.ok(rows.some((item) => item.kind === 'router'));
      if (name === 'healthy-identity')
        assert.ok(rows.some((item) => item.kind === 'none' && item.value === 'not acceptable'));
      else {
        assert.ok(
          rows.some(
            (item) =>
              item.kind === 'allocation' &&
              item.value === (name.endsWith('gzip') ? 'gzip' : 'deflate'),
          ),
        );
        assert.ok(
          rows.some(
            (item) =>
              item.kind === 'encoder-close' &&
              item.value === (name.startsWith('healthy-') ? 'ended' : 'unended'),
          ),
        );
        assert.ok(
          rows.findIndex((item) => item.kind === 'allocation') <
            rows.findIndex((item) => item.kind === 'encoder-close'),
        );
      }
      assert.ok(rows.some((item) => item.kind === 'response-finish'));
      assert.ok(rows.some((item) => item.kind === 'response-close'));
      assert.ok(rows.some((item) => item.kind === 'handler-ok'));
      assert.ok(
        rows.findIndex((item) => item.kind === 'response-finish') <
          rows.findIndex((item) => item.kind === 'response-close'),
      );
    }
  } finally {
    if (server) await new Promise((resolve) => server.close(resolve));
    if (observer) await observer.close().catch(() => {});
    rmSync(root, { recursive: true, force: true });
  }
});
