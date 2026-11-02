// Copied-fixture source binding: derive the real pipe's owner.join await and end-work parameter
// from the already qualified installed/generated three-parameter join subjects.
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import {
  closeSync,
  constants,
  fstatSync,
  lstatSync,
  openSync,
  readSync,
  realpathSync,
} from 'node:fs';
import { join } from 'node:path';
import { pathToFileURL } from 'node:url';

import { loadRouterProofParser } from './framework-router-generated-pipes.mjs';

const sha = (bytes) => createHash('sha256').update(bytes).digest('hex');
const identifier = /^[A-Za-z_$][A-Za-z0-9_$]*$/;
function exactBytes(path, expectedBytes, expectedSha) {
  assert.equal(realpathSync(path), path);
  const before = lstatSync(path, { bigint: true });
  const stable = (left, right) =>
    ['dev', 'ino', 'size', 'mtimeNs', 'ctimeNs', 'mode', 'nlink', 'uid', 'gid'].every(
      (key) => left[key] === right[key],
    );
  assert.ok(before.isFile() && !before.isSymbolicLink() && before.size === BigInt(expectedBytes));
  const fd = openSync(path, constants.O_RDONLY | constants.O_NOFOLLOW);
  try {
    const opened = fstatSync(fd, { bigint: true });
    assert.ok(opened.isFile() && stable(opened, before));
    const bytes = Buffer.alloc(expectedBytes + 1);
    let size = 0,
      count;
    while (size < bytes.length && (count = readSync(fd, bytes, size, bytes.length - size, size)))
      size += count;
    const after = fstatSync(fd, { bigint: true });
    const named = lstatSync(path, { bigint: true });
    assert.ok(
      size === expectedBytes &&
        stable(after, opened) &&
        named.isFile() &&
        !named.isSymbolicLink() &&
        stable(named, opened),
    );
    assert.equal(realpathSync(path), path);
    const result = bytes.subarray(0, size);
    assert.equal(sha(result), expectedSha);
    return result;
  } finally {
    closeSync(fd);
  }
}
function all(ts, root, accept) {
  const found = [];
  const visit = (node) => {
    if (accept(node)) found.push(node);
    ts.forEachChild(node, visit);
  };
  visit(root);
  return found;
}
function location(file, node) {
  const start = file.getLineAndCharacterOfPosition(node.getStart(file));
  const end = file.getLineAndCharacterOfPosition(node.end);
  return Object.freeze({
    line: start.line,
    column: start.character,
    endLine: end.line,
    endColumn: end.character,
  });
}

function faultParser(identity, expected) {
  return loadRouterProofParser(identity, expected);
}

/** `files` must be the frozen `deriveRouterPipeUnion(...).files` inventory. */
export function deriveFaultJoinPoints(identity, files, parserSpec) {
  assert.ok(Object.isFrozen(identity) && Array.isArray(files) && Object.isFrozen(files));
  assert.ok(files.length >= 27 && files.length <= 155);
  const ts = faultParser(identity, parserSpec);
  const result = [];
  for (const file of files) {
    assert.ok(Object.isFrozen(file) && Array.isArray(file.entries));
    assert.ok(['installed', 'generated'].includes(file.kind));
    assert.ok(
      typeof file.path === 'string' &&
        (file.kind === 'installed'
          ? /^dist\/(?:compiled\/next-server\/|esm\/server\/|server\/)[a-z0-9./-]+\.js$/.test(
              file.path,
            )
          : /^next\/server\/[a-zA-Z0-9_()./[\]-]+\.js$/.test(file.path)) &&
        file.path.split('/').every((part) => part && part !== '.' && part !== '..'),
    );
    assert.ok(Number.isSafeInteger(file.bytes) && file.bytes > 0 && file.bytes <= 4 * 1024 * 1024);
    assert.match(file.sha256, /^[0-9a-f]{64}$/);
    const named =
      file.kind === 'installed'
        ? join(identity.nextRoot, file.path)
        : join(identity.appRoot, '.next', file.path.slice('next/'.length));
    assert.equal(pathToFileURL(named).href, file.url);
    const bytes = exactBytes(named, file.bytes, file.sha256);
    const parsed = ts.createSourceFile(
      file.path,
      bytes.toString('utf8'),
      ts.ScriptTarget.Latest,
      true,
      ts.ScriptKind.JS,
    );
    assert.equal(parsed.parseDiagnostics.length, 0);
    for (const [entryIndex, entry] of file.entries.entries()) {
      assert.ok(Object.isFrozen(entry) && identifier.test(entry.responseName));
      const begin = parsed.getPositionOfLineAndCharacter(entry.point.line, entry.point.column);
      const candidates = all(
        ts,
        parsed,
        (node) =>
          (ts.isFunctionExpression(node) ||
            ts.isFunctionDeclaration(node) ||
            ts.isArrowFunction(node)) &&
          node.getStart(parsed) <= begin &&
          begin < node.end &&
          node.parameters?.length === 3 &&
          node.modifiers?.some((modifier) => modifier.kind === ts.SyntaxKind.AsyncKeyword),
      );
      assert.ok(candidates.length >= 1);
      const fn = candidates.sort(
        (a, b) => a.end - a.getStart(parsed) - (b.end - b.getStart(parsed)),
      )[0];
      assert.equal(fn.parameters[0].name.getText(parsed), entry.responseName);
      const endWorkName = fn.parameters[2].name.getText(parsed);
      assert.match(endWorkName, identifier);
      const joins = all(
        ts,
        fn.body,
        (node) =>
          ts.isAwaitExpression(node) &&
          ts.isCallExpression(node.expression) &&
          ts.isPropertyAccessExpression(node.expression.expression) &&
          node.expression.expression.name.text === 'join' &&
          node.expression.arguments.length === 0,
      );
      assert.equal(joins.length, 1, 'One real owner.join await in qualified join');
      const ownerName = joins[0].expression.expression.expression.getText(parsed);
      assert.match(ownerName, identifier);
      result.push(
        Object.freeze({
          fileIndex: file.index,
          entryIndex,
          responseName: entry.responseName,
          ownerName,
          endWorkName,
          joinPoint: location(parsed, joins[0]),
          fileSha256: file.sha256,
          result: resultPoint(ts, parsed, fn),
        }),
      );
    }
  }
  assert.ok(result.length >= 27 && result.length <= 155);
  return Object.freeze(result);
}

function resultPoint(ts, parsed, fn) {
  const statements = fn.body.statements;
  const tail = [...statements].slice(-4);
  assert.equal(tail.length, 4);
  const names = [];
  for (const statement of tail.slice(0, 3)) {
    assert.ok(ts.isIfStatement(statement) && ts.isIdentifier(statement.expression));
    const body = statement.thenStatement;
    assert.ok(ts.isThrowStatement(body) && ts.isIdentifier(body.expression));
    names.push(statement.expression.text, body.expression.text);
  }
  assert.ok(ts.isReturnStatement(tail[3]));
  return Object.freeze({ point: location(parsed, tail[0]), names: Object.freeze(names) });
}

/** Additional breakpoints bind only the already qualified installed owner/router bytes. */
export function deriveFaultBoundaryPoints(identity, subjects) {
  const ts = faultParser(identity, subjects.parser);
  function parse(kind, expected) {
    const subject = subjects[kind];
    assert.equal(subject.path, expected);
    const path = join(identity.nextRoot, expected);
    const bytes = exactBytes(path, subject.bytes, subject.sha256);
    const file = ts.createSourceFile(
      expected,
      bytes.toString('utf8'),
      ts.ScriptTarget.Latest,
      true,
      ts.ScriptKind.JS,
    );
    assert.equal(file.parseDiagnostics.length, 0);
    return file;
  }
  function one(nodes) {
    assert.equal(nodes.length, 1, 'One qualified fault boundary');
    return nodes[0];
  }
  const compression = parse('compression', 'dist/compiled/compression/index.js');
  const owner = one(
    all(
      ts,
      compression,
      (n) => ts.isFunctionExpression(n) && n.name?.text === 'createResponseCompressionOwner',
    ),
  );
  assert.deepEqual(
    owner.parameters.map((n) => n.name.getText(compression)),
    ['response'],
  );
  function named(name) {
    return one(all(ts, owner.body, (n) => ts.isFunctionDeclaration(n) && n.name?.text === name));
  }
  const install = one(
    all(
      ts,
      owner.body,
      (n) =>
        ts.isCallExpression(n) &&
        n.expression.getText(compression) === 'Object.defineProperty' &&
        n.arguments[0]?.getText(compression) === 'response',
    ),
  );
  const cleanup = one(
    all(
      ts,
      named('removeOwnedListeners').body,
      (n) =>
        ts.isCallExpression(n) &&
        n.expression.getText(compression) === 'response.removeListener' &&
        ts.isStringLiteral(n.arguments[0]) &&
        n.arguments[0].text === 'finish',
    ),
  );
  const transition = one(
    all(
      ts,
      named('transitionFailure').body,
      (n) => ts.isIfStatement(n) && n.expression.getText(compression) === 'stream || abortResponse',
    ),
  );
  const drain = one(
    all(
      ts,
      named('setResponseDrainListener').body,
      (n) =>
        ts.isExpressionStatement(n) &&
        n.expression.getText(compression) === 'drainListener = listener',
    ),
  );
  const router = parse('router', 'dist/server/lib/router-server.js');
  const fn = one(
    all(
      ts,
      router,
      (n) => ts.isFunctionExpression(n) && n.name?.text === 'joinResponseCompression',
    ),
  );
  assert.deepEqual(
    fn.parameters.map((n) => n.name.getText(router)),
    ['response', 'actualOperation', 'explicitEndWork'],
  );
  const registration = one(
    all(
      ts,
      router,
      (node) =>
        ts.isCallExpression(node) &&
        ts.isIdentifier(node.expression) &&
        node.expression.text === 'compress',
    ),
  );
  assert.equal(registration.arguments.length, 3);
  assert.equal(registration.arguments[0].getText(router), 'req');
  assert.equal(registration.arguments[1].getText(router), 'res');
  assert.ok(ts.isArrowFunction(registration.arguments[2]));
  assert.equal(registration.arguments[2].parameters.length, 0);
  assert.ok(ts.isBlock(registration.arguments[2].body));
  assert.equal(registration.arguments[2].body.statements.length, 0);
  const joined = one(
    all(
      ts,
      fn.body,
      (n) =>
        ts.isAwaitExpression(n) &&
        ts.isCallExpression(n.expression) &&
        n.expression.expression.getText(router) === 'owner.join',
    ),
  );
  return Object.freeze({
    ownerInstall: location(compression, install),
    cleanup: location(compression, cleanup),
    transition: location(compression, transition),
    drain: location(compression, drain),
    routerRegistration: location(router, registration),
    routerJoin: location(router, joined),
    routerResult: resultPoint(ts, router, fn),
  });
}
