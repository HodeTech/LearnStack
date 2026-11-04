// Closed copied BFF transport proof. The installed sources are read, never imported.
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { lstatSync, readFileSync, realpathSync } from 'node:fs';
import { join } from 'node:path';

import ts from 'typescript';

const sha = (bytes) => createHash('sha256').update(bytes).digest('hex');
const subjects = Object.freeze({
  compression: 'dist/compiled/compression/index.js',
  router: 'dist/server/lib/router-server.js',
});
function readRegular(path) {
  assert.equal(realpathSync(path), path, 'Qualified source path must have no aliases');
  const stat = lstatSync(path);
  assert.ok(stat.isFile() && !stat.isSymbolicLink(), 'Qualified subject must be regular');
  return readFileSync(path);
}
function nodes(root, predicate) {
  const found = [];
  function visit(node) {
    if (predicate(node)) found.push(node);
    ts.forEachChild(node, visit);
  }
  visit(root);
  return found;
}
function unique(values, label) {
  assert.equal(values.length, 1, `Expected one qualified ${label}`);
  return values[0];
}
function parse(path, bytes) {
  const file = ts.createSourceFile(
    path,
    bytes.toString('utf8'),
    ts.ScriptTarget.Latest,
    true,
    ts.ScriptKind.JS,
  );
  assert.equal(file.parseDiagnostics.length, 0, 'Qualified subject must parse');
  return file;
}
function location(file, node) {
  const start = node.getStart(file);
  const end = node.end;
  const a = file.getLineAndCharacterOfPosition(start);
  const b = file.getLineAndCharacterOfPosition(end);
  return Object.freeze({
    line: a.line,
    column: a.character,
    endLine: b.line,
    endColumn: b.character,
  });
}
function functionNamed(file, name) {
  return unique(
    nodes(
      file,
      (node) =>
        (ts.isFunctionDeclaration(node) || ts.isFunctionExpression(node)) &&
        node.name?.text === name,
    ),
    name,
  );
}
function textMatches(file, root, expression, label) {
  return unique(
    nodes(
      root,
      (node) => ts.isExpressionStatement(node) && node.expression.getText(file) === expression,
    ),
    label,
  );
}
/** Read only qualified census and the two exact installed subjects. */
export function deriveTransportSpec(identity) {
  assert.ok(Object.isFrozen(identity) && identity.format === 1);
  const { frontendRoot, nextRoot } = identity;
  assert.equal(realpathSync(frontendRoot), frontendRoot);
  assert.equal(realpathSync(nextRoot), nextRoot);
  const censusBytes = readRegular(
    join(frontendRoot, 'framework/next-response-cleanup/census.json'),
  );
  const census = JSON.parse(censusBytes.toString('utf8'));
  assert.equal(census.schemaVersion, 1);
  assert.equal(sha(censusBytes), identity.censusSha256, 'Verified installation census');
  assert.deepEqual(census.tuple, identity.tuple, 'Verified installation tuple');
  assert.ok(Array.isArray(census.files));
  const records = {};
  for (const [key, path] of Object.entries(subjects)) {
    const expected = unique(
      census.files.filter((item) => item.path === path && item.kind === key),
      path,
    );
    const bytes = readRegular(join(nextRoot, path));
    assert.equal(bytes.length, expected.bytes);
    assert.equal(sha(bytes), expected.sha256);
    records[key] = { path, sha256: expected.sha256, bytes: bytes.length, file: parse(path, bytes) };
  }
  const file = records.compression.file;
  const owner = functionNamed(file, 'createResponseCompressionOwner');
  const create = functionNamed(file, 'createCompressor');
  const closing = functionNamed(file, 'onStreamClose');
  const none = functionNamed(file, 'markNoCompression');
  assert.ok(create.pos >= owner.pos && create.end <= owner.end);
  assert.ok(closing.pos >= owner.pos && closing.end <= owner.end);
  assert.ok(none.pos >= owner.pos && none.end <= owner.end);
  const allocated = textMatches(
    file,
    create,
    'returned = true',
    'post-factory allocation statement',
  );
  const factory = textMatches(file, create, 'stream = factory()', 'actual factory assignment');
  assert.ok(factory.pos < allocated.pos);
  const closeTransition = textMatches(
    file,
    closing,
    'streamClosed = true',
    'actual close transition',
  );
  const references = nodes(owner, (node) => ts.isIdentifier(node) && node.text === 'onStreamClose');
  assert.equal(references.length, 2, 'Close callback is only declared and registered');
  assert.equal(references[1].parent.getText(file), "stream.once('close', onStreamClose)");
  assert.ok(none.body?.statements.length);
  const noneEntry = none.body.statements[0];
  const router = records.router.file;
  const request = unique(
    nodes(
      router,
      (node) =>
        ts.isVariableDeclaration(node) &&
        ts.isIdentifier(node.name) &&
        node.name.text === 'requestHandlerImpl',
    ),
    'actual router handler',
  );
  assert.ok(
    request.initializer &&
      ts.isArrowFunction(request.initializer) &&
      ts.isBlock(request.initializer.body),
  );
  const entry = request.initializer.body.statements[0];
  assert.ok(entry && entry.getText(router).includes('addRequestMeta'));
  assert.equal(
    nodes(
      request.initializer.body,
      (node) =>
        ts.isCallExpression(node) &&
        ts.isIdentifier(node.expression) &&
        node.expression.text === 'compress',
    ).length,
    1,
  );
  const wrapper = unique(
    nodes(
      router,
      (node) =>
        ts.isVariableDeclaration(node) &&
        ts.isIdentifier(node.name) &&
        node.name.text === 'requestHandler',
    ),
    'outer router wrapper',
  );
  assert.ok(wrapper.initializer?.getText(router).includes('joinResponseCompression'));
  return Object.freeze({
    version: 1,
    censusSha256: sha(censusBytes),
    tuple: census.tuple,
    compression: {
      path: records.compression.path,
      sha256: records.compression.sha256,
      bytes: records.compression.bytes,
      points: {
        allocation: location(file, allocated),
        close: location(file, closeTransition),
        none: location(file, noneEntry),
      },
    },
    router: {
      path: records.router.path,
      sha256: records.router.sha256,
      bytes: records.router.bytes,
      points: { router: location(router, entry) },
    },
  });
}
function replaceOnce(source, before, after) {
  assert.equal(source.split(before).length, 2, 'Copied source anchor must be unique');
  return source.replace(before, after);
}
/** Forward only the encoding negotiation header in the fixed copied transport fixture. */
export function instrumentCopiedTransportMiddleware(source) {
  assert.equal(typeof source, 'string');
  assert.ok(
    !source.includes("downstream.set('accept-encoding'"),
    'Transport header not already installed',
  );
  const anchor =
    "    if (accept !== null) downstream.set('accept', accept);\n    downstream.set(INGRESS_HEADER, envelope);";
  return replaceOnce(
    source,
    anchor,
    "    if (accept !== null) downstream.set('accept', accept);\n    const transportEncoding = request.headers.get('accept-encoding');\n    if (transportEncoding !== null) downstream.set('accept-encoding', transportEncoding);\n    downstream.set(INGRESS_HEADER, envelope);",
  );
}
/** Produce a fixed copied BFF-runtime variant. Neither input nor shared installation is modified. */
export function instrumentCopiedTransport({ source, identity }) {
  const spec = deriveTransportSpec(identity);
  const observerTemplate = readFileSync(
    new URL('./framework-proof-transport-observer.mjs', import.meta.url),
    'utf8',
  );
  const token = "'__COPIED_TRANSPORT_SPEC__'";
  const observer = replaceOnce(observerTemplate, token, JSON.stringify(spec));
  let runtime = replaceOnce(
    source,
    "import { readFileSync } from 'node:fs';",
    "import { readFileSync } from 'node:fs';\nimport { startCopiedTransportObserver } from './framework-proof-transport-observer.mjs';",
  );
  runtime = replaceOnce(
    runtime,
    '  let shutdownPromise;',
    '  let shutdownPromise;\n  let transportProof;\n  let transportAdvanceClock;',
  );
  runtime = replaceOnce(
    runtime,
    "    const dev = framework.mode === 'development';",
    `    const dev = framework.mode === 'development';
    const transportSetup = startCopiedTransportObserver(framework).then(value => { transportProof = value; return value; });
    await ownSetup(transportSetup);
    assertStarting();`,
  );
  runtime = replaceOnce(
    runtime,
    '    await ownSetup(app.prepare());\n    assertStarting();\n    const handle = app.getRequestHandler();',
    `    await ownSetup(app.prepare());
    assertStarting();
    await ownSetup(transportProof.confirmLoaded());
    assertStarting();
    const handle = transportProof.wrapHandler(app.getRequestHandler(), () => {
      if (typeof transportAdvanceClock !== 'function') throw new Error('Transport clock unavailable');
      transportAdvanceClock();
    });`,
  );
  runtime = replaceOnce(
    runtime,
    '      const results = await Promise.allSettled(shutdownTasks);',
    `      const results = await Promise.allSettled(shutdownTasks);
      if (transportProof) await transportProof.close();`,
  );
  // Input is the exact BFF-instrumented runtime; fixed closure mutates only its fake clock.
  runtime = replaceOnce(
    runtime,
    '    let now = 0; let refreshes = 0; let accounts = 0; const administrations = {};',
    `    let now = 0; let refreshes = 0; let accounts = 0; const administrations = {};
    transportAdvanceClock = () => { now += 3600001; };`,
  );
  return Object.freeze({ runtime, observer });
}
