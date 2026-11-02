// Copied-fixture generated pipe subjects, independently bound to genuine producer outputs.
// Read-only admission and parser/source checks precede observation; no source is evaluated here.
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import {
  constants,
  closeSync,
  fstatSync,
  lstatSync,
  openSync,
  readSync,
  realpathSync,
} from 'node:fs';
import { createRequire } from 'node:module';
import { dirname, join, relative } from 'node:path';
import { pathToFileURL } from 'node:url';

import { verifyFrameworkBuild } from '../../../scripts/framework-installation.mjs';

const KEY = 'learnstack.next.responseCompressionOwner';
const MAX_FILES = 128,
  MAX_POINTS = 128,
  MAX_BYTES = 4 * 1024 * 1024;
const MAX_TOTAL = 32 * 1024 * 1024;
const sha = (bytes) => createHash('sha256').update(bytes).digest('hex');
const refused = () => {
  throw new Error('Copied generated pipe subject refused');
};
const check = (condition) => {
  if (!condition) refused();
};

function bytesAt(path, max) {
  check(realpathSync(path) === path);
  const before = lstatSync(path, { bigint: true });
  const stable = (left, right) =>
    ['dev', 'ino', 'size', 'mtimeNs', 'ctimeNs', 'mode', 'nlink', 'uid', 'gid'].every(
      (key) => left[key] === right[key],
    );
  check(before.isFile() && !before.isSymbolicLink() && before.size <= BigInt(max));
  const fd = openSync(path, constants.O_RDONLY | constants.O_NOFOLLOW);
  try {
    const opened = fstatSync(fd, { bigint: true });
    check(opened.isFile() && stable(opened, before));
    const buffer = Buffer.alloc(max + 1);
    let size = 0,
      count;
    while (size < buffer.length && (count = readSync(fd, buffer, size, buffer.length - size, size)))
      size += count;
    const after = fstatSync(fd, { bigint: true });
    const named = lstatSync(path, { bigint: true });
    check(
      size <= max &&
        BigInt(size) === opened.size &&
        stable(after, opened) &&
        named.isFile() &&
        !named.isSymbolicLink() &&
        stable(named, opened) &&
        realpathSync(path) === path,
    );
    return buffer.subarray(0, size);
  } finally {
    closeSync(fd);
  }
}

// Called by the copied-source builder BEFORE genuine build. Only resolution and
// byte reads occur. This finite spec is embedded in its normal graph-bound code.
export function describeRouterParser(identity) {
  const require = createRequire(join(identity.appRoot, 'package.json'));
  const entry = realpathSync(require.resolve('typescript'));
  const packagePath = realpathSync(require.resolve('typescript/package.json'));
  const root = dirname(packagePath),
    store = realpathSync(join(identity.frontendRoot, 'node_modules'));
  const packageBytes = bytesAt(packagePath, 64 * 1024),
    metadata = JSON.parse(packageBytes);
  check(metadata.name === 'typescript' && metadata.version === '5.6.3');
  check(entry === join(root, 'lib/typescript.js'));
  const location = relative(store, root);
  check(location !== '' && !location.startsWith('..') && !location.startsWith('/'));
  const entryBytes = bytesAt(entry, 16 * 1024 * 1024);
  return Object.freeze({
    version: metadata.version,
    location,
    packageBytes: packageBytes.length,
    packageSha256: sha(packageBytes),
    entryBytes: entryBytes.length,
    entrySha256: sha(entryBytes),
  });
}

function loadParser(identity, expected) {
  check(
    Object.keys(expected).sort().join(',') ===
      'entryBytes,entrySha256,location,packageBytes,packageSha256,version',
  );
  check(JSON.stringify(describeRouterParser(identity)) === JSON.stringify(expected));
  // Literal, normally resolved test-only parser. No arbitrary path/module import.
  // The source model binds this literal load and both package-resolution calls.
  const require = createRequire(join(identity.appRoot, 'package.json'));
  const ts = require('typescript');
  check(ts.version === expected.version);
  return ts;
}

function parsed(ts, source) {
  const name = '/router-pipe-subject.js';
  const file = ts.createSourceFile(name, source, ts.ScriptTarget.Latest, true, ts.ScriptKind.JS);
  check(file.parseDiagnostics.length === 0);
  // In-memory binder only: no source resolution, declaration libraries or IO.
  const host = {
    getSourceFile: (path) => (path === name ? file : undefined),
    getDefaultLibFileName: () => '',
    writeFile: refused,
    getCurrentDirectory: () => '/',
    getDirectories: () => [],
    fileExists: (path) => path === name,
    readFile: (path) => (path === name ? source : undefined),
    getCanonicalFileName: (path) => path,
    useCaseSensitiveFileNames: () => true,
    getNewLine: () => '\n',
  };
  const program = ts.createProgram([name], { allowJs: true, noLib: true, noResolve: true }, host);
  return { file, checker: program.getTypeChecker() };
}

function all(ts, node, predicate) {
  const result = [];
  function visit(value) {
    if (predicate(value)) result.push(value);
    ts.forEachChild(value, visit);
  }
  visit(node);
  return result;
}

// A deliberately closed normal form for the COMPLETE installed join body.
// It accepts the observed SWC renaming/hoisting/boolean/sequence transforms,
// not an arbitrary function with similarly named method calls.
function joinShape(ts, checker, fn) {
  check(
    ts.isBlock(fn.body) &&
      fn.parameters.length === 3 &&
      fn.modifiers?.some((item) => item.kind === ts.SyntaxKind.AsyncKeyword),
  );
  const bindings = new Map();
  function id(node) {
    check(ts.isIdentifier(node));
    const symbol = checker.getSymbolAtLocation(node);
    if (!symbol || !symbol.declarations?.length) {
      check(['undefined', 'Symbol', 'Promise'].includes(node.text));
      return node.text === 'undefined' ? ['undefined'] : ['global', node.text];
    }
    if (!bindings.has(symbol)) bindings.set(symbol, bindings.size);
    return ['id', bindings.get(symbol)];
  }
  for (const parameter of fn.parameters) {
    check(ts.isIdentifier(parameter.name) && !parameter.initializer && !parameter.dotDotDotToken);
    id(parameter.name);
  }
  const strip = (node) => (ts.isParenthesizedExpression(node) ? strip(node.expression) : node);
  const not = (value) => (value[0] === 'not' ? value[1] : ['not', value]);
  const simple = (value) => ['id', 'global', 'undefined', 'string', 'typeof'].includes(value[0]);
  function expression(input) {
    const node = strip(input);
    if (ts.isIdentifier(node)) return id(node);
    if (ts.isStringLiteral(node)) return ['string', node.text];
    if (ts.isNumericLiteral(node)) return ['number', Number(node.text)];
    if (node.kind === ts.SyntaxKind.TrueKeyword) return ['boolean', true];
    if (node.kind === ts.SyntaxKind.FalseKeyword) return ['boolean', false];
    if (ts.isVoidExpression(node)) {
      check(ts.isNumericLiteral(node.expression) && node.expression.text === '0');
      return ['undefined'];
    }
    if (ts.isTypeOfExpression(node)) return ['typeof', expression(node.expression)];
    if (ts.isAwaitExpression(node)) return ['await', expression(node.expression)];
    if (ts.isPrefixUnaryExpression(node)) {
      check(node.operator === ts.SyntaxKind.ExclamationToken);
      const value = expression(node.operand);
      if (value[0] === 'number' && [0, 1].includes(value[1])) return ['boolean', !value[1]];
      return not(value);
    }
    if (ts.isPropertyAccessExpression(node))
      return ['property', !!node.questionDotToken, expression(node.expression), node.name.text];
    if (ts.isElementAccessExpression(node))
      return [
        'element',
        !!node.questionDotToken,
        expression(node.expression),
        expression(node.argumentExpression),
      ];
    if (ts.isCallExpression(node))
      return [
        'call',
        !!node.questionDotToken,
        expression(node.expression),
        node.arguments.map(expression),
      ];
    if (ts.isConditionalExpression(node))
      return [
        'conditional',
        expression(node.condition),
        expression(node.whenTrue),
        expression(node.whenFalse),
      ];
    if (ts.isArrowFunction(node)) {
      check(!node.modifiers?.length && node.parameters.length === 1 && ts.isBlock(node.body));
      const parameter = node.parameters[0];
      check(ts.isIdentifier(parameter.name) && !parameter.initializer && !parameter.dotDotDotToken);
      return ['arrow', id(parameter.name), statements(node.body.statements)];
    }
    if (ts.isBinaryExpression(node)) {
      let left = expression(node.left),
        right = expression(node.right),
        kind = node.operatorToken.kind;
      if ([ts.SyntaxKind.EqualsEqualsToken, ts.SyntaxKind.EqualsEqualsEqualsToken].includes(kind)) {
        // SWC reverses safe primitive comparisons and weakens typeof === string.
        const typeComparison =
          (left[0] === 'typeof' && right[0] === 'string') ||
          (right[0] === 'typeof' && left[0] === 'string');
        check(kind === ts.SyntaxKind.EqualsEqualsEqualsToken || typeComparison);
        if (simple(left) && simple(right) && JSON.stringify(left) > JSON.stringify(right))
          [left, right] = [right, left];
        return ['equal', left, right];
      }
      check(kind === ts.SyntaxKind.EqualsToken);
      return ['assign', left, right];
    }
    refused();
  }
  function effect(input) {
    const node = strip(input);
    if (ts.isBinaryExpression(node)) {
      if (node.operatorToken.kind === ts.SyntaxKind.CommaToken)
        return [...effect(node.left), ...effect(node.right)];
      if (node.operatorToken.kind === ts.SyntaxKind.AmpersandAmpersandToken)
        return [['if', expression(node.left), effect(node.right), []]];
      if (node.operatorToken.kind === ts.SyntaxKind.BarBarToken)
        return [['if', not(expression(node.left)), effect(node.right), []]];
    }
    return [['effect', expression(node)]];
  }
  function statement(node) {
    if (ts.isBlock(node)) return statements(node.statements);
    if (ts.isVariableStatement(node))
      return node.declarationList.declarations.flatMap((item) => {
        check(ts.isIdentifier(item.name));
        // Uninitialized SWC hoists have no runtime action. Binding identity remains
        // checker-derived when first used, rather than keyed by the minified name.
        return item.initializer
          ? [['effect', ['assign', id(item.name), expression(item.initializer)]]]
          : [];
      });
    if (ts.isExpressionStatement(node)) return effect(node.expression);
    if (ts.isIfStatement(node))
      return [
        [
          'if',
          expression(node.expression),
          statement(node.thenStatement),
          node.elseStatement ? statement(node.elseStatement) : [],
        ],
      ];
    if (ts.isTryStatement(node)) {
      check(node.catchClause?.variableDeclaration && !node.finallyBlock);
      const caught = id(node.catchClause.variableDeclaration.name);
      return [['try', statement(node.tryBlock), caught, statement(node.catchClause.block)]];
    }
    if (ts.isReturnStatement(node) || ts.isThrowStatement(node)) {
      check(node.expression);
      return [[ts.isReturnStatement(node) ? 'return' : 'throw', expression(node.expression)]];
    }
    refused();
  }
  function statements(nodes) {
    return nodes.flatMap(statement);
  }
  return JSON.stringify(statements(fn.body.statements));
}

function functionAt(ts, node) {
  for (let parent = node.parent; parent; parent = parent.parent)
    if (
      ts.isFunctionExpression(parent) ||
      ts.isFunctionDeclaration(parent) ||
      ts.isArrowFunction(parent)
    )
      return parent;
  return undefined;
}

function symbolCalls(ts, file) {
  return all(
    ts,
    file,
    (node) =>
      ts.isCallExpression(node) &&
      ts.isPropertyAccessExpression(node.expression) &&
      ts.isIdentifier(node.expression.expression) &&
      node.expression.expression.text === 'Symbol' &&
      node.expression.name.text === 'for' &&
      node.arguments.length === 1 &&
      ts.isStringLiteral(node.arguments[0]) &&
      node.arguments[0].text === KEY,
  );
}

function candidatePoints(ts, subject, reference) {
  const { file, checker } = parsed(ts, subject.toString('utf8'));
  const result = [];
  for (const call of symbolCalls(ts, file)) {
    // The separate abort marker is not a variable initialized directly by this
    // call inside the three-parameter join function. Do not confuse it with entry.
    if (!ts.isVariableDeclaration(call.parent) || call.parent.initializer !== call) continue;
    const fn = functionAt(ts, call);
    if (!fn || fn.parameters.length !== 3 || !ts.isBlock(fn.body)) continue;
    check(joinShape(ts, checker, fn) === reference);
    const first = fn.body.statements[0];
    check(ts.isVariableStatement(first));
    const initialized = first.declarationList.declarations.find((item) => item.initializer);
    check(initialized === call.parent);
    const begin = file.getLineAndCharacterOfPosition(first.getStart(file));
    const end = file.getLineAndCharacterOfPosition(call.end);
    const responseName = fn.parameters[0].name.text;
    check(/^[A-Za-z_$][A-Za-z0-9_$]*$/.test(responseName));
    result.push(
      Object.freeze({
        responseName,
        point: Object.freeze({
          line: begin.line,
          column: begin.character,
          endLine: end.line,
          endColumn: end.character,
        }),
      }),
    );
  }
  check(result.length <= 8);
  return result;
}

function recordBytes(identity, row) {
  check(row && Object.keys(row).sort().join(',') === 'bytes,path,sha256');
  check(
    typeof row.path === 'string' &&
      row.path.startsWith('next/server/') &&
      !row.path.includes('\\') &&
      !row.path.includes('\0') &&
      row.path.split('/').every((part) => part && part !== '.' && part !== '..'),
  );
  check(
    Number.isSafeInteger(row.bytes) &&
      row.bytes > 0 &&
      row.bytes <= MAX_BYTES &&
      /^[0-9a-f]{64}$/.test(row.sha256),
  );
  const path = join(identity.appRoot, '.next', row.path.slice('next/'.length));
  const bytes = bytesAt(path, MAX_BYTES);
  check(bytes.length === row.bytes && sha(bytes) === row.sha256);
  return { path, bytes };
}

// Called only inside the copied observer's tracked setup, after genuine child
// admission and before any dynamic Next import. It never acquires/prepares state.
export function deriveGeneratedPipeSubjects(framework, parserSpec) {
  check(Object.isFrozen(framework) && framework.mode === 'production');
  const identity = framework.identity;
  check(identity && Object.isFrozen(identity));
  // Repeat the EXISTING read-only validator to get an exact independently checked
  // manifest, rather than trusting a newly reread unbound list after admission.
  const verified = verifyFrameworkBuild({
    appRoot: identity.appRoot,
    frontendRoot: identity.frontendRoot,
  });
  check(
    JSON.stringify(verified.identity.tuple) === JSON.stringify(identity.tuple) &&
      verified.identity.nextRoot === identity.nextRoot &&
      verified.identity.censusSha256 === identity.censusSha256 &&
      verified.identity.patchSha256 === identity.patchSha256 &&
      verified.identity.generatorSha256 === identity.generatorSha256,
  );
  const manifestPath = join(identity.appRoot, '.next/framework-build.json');
  const manifestBytes = bytesAt(manifestPath, 16 * 1024 * 1024);
  assert.deepEqual(JSON.parse(manifestBytes), verified.manifest);
  const ts = loadParser(identity, parserSpec);
  const censusBytes = bytesAt(
    join(identity.frontendRoot, 'framework/next-response-cleanup/census.json'),
    16 * 1024 * 1024,
  );
  check(sha(censusBytes) === identity.censusSha256);
  const census = JSON.parse(censusBytes);
  const references = census.files.filter(
    (row) => row.kind === 'pipe' && row.path === 'dist/server/pipe-readable.js',
  );
  check(references.length === 1);
  const referenceRow = references[0];
  const referenceBytes = bytesAt(join(identity.nextRoot, referenceRow.path), MAX_BYTES);
  check(
    referenceBytes.length === referenceRow.bytes && sha(referenceBytes) === referenceRow.sha256,
  );
  const reference = parsed(ts, referenceBytes.toString('utf8'));
  const referenceFns = all(
    ts,
    reference.file,
    (node) =>
      (ts.isFunctionExpression(node) || ts.isFunctionDeclaration(node)) &&
      node.name?.text === 'joinResponseCompression',
  );
  check(referenceFns.length === 1);
  const shape = joinShape(ts, reference.checker, referenceFns[0]);
  const files = verified.manifest.outputs.filter(
    (row) => row.path.startsWith('next/server/') && row.path.endsWith('.js'),
  );
  check(
    files.length > 0 &&
      files.length <= MAX_FILES &&
      new Set(files.map((row) => row.path)).size === files.length,
  );
  let total = 0,
    points = 0;
  const inventory = files.map((row, index) => {
    const { path, bytes } = recordBytes(identity, row);
    total += bytes.length;
    check(total <= MAX_TOTAL);
    const entries = candidatePoints(ts, bytes, shape);
    points += entries.length;
    check(points <= MAX_POINTS);
    const mapRow = verified.manifest.outputs.find((item) => item.path === row.path + '.map');
    const map = mapRow ? recordBytes(identity, mapRow) : undefined;
    if (map) {
      total += map.bytes.length;
      check(total <= MAX_TOTAL);
    }
    // Maps are evidence only. Never fetch sources/sourceRoot/sourceMappingURL,
    // or accept map mappings in place of actual generated-script byte identity.
    return Object.freeze({
      index,
      path: row.path,
      url: pathToFileURL(path).href,
      bytes: row.bytes,
      sha256: row.sha256,
      entries: Object.freeze(entries),
      map: map
        ? Object.freeze({ path: mapRow.path, bytes: mapRow.bytes, sha256: mapRow.sha256 })
        : null,
    });
  });
  check(points > 0 && sha(bytesAt(manifestPath, 16 * 1024 * 1024)) === sha(manifestBytes));
  return Object.freeze({
    manifestSha256: sha(manifestBytes),
    parser: parserSpec,
    referenceSha256: referenceRow.sha256,
    inventory: Object.freeze(inventory),
  });
}

function frozen(value) {
  if (value && typeof value === 'object') {
    for (const item of Object.values(value)) frozen(item);
    Object.freeze(value);
  }
  return value;
}

/** Same independent producer/census calculation in the copied child and its host. */
export function deriveRouterPipeUnion(framework, installed, parserSpec) {
  check(Array.isArray(installed) && installed.length === 27);
  const identity = framework.identity;
  const censusBytes = bytesAt(
    join(identity.frontendRoot, 'framework/next-response-cleanup/census.json'),
    16 * 1024 * 1024,
  );
  check(sha(censusBytes) === identity.censusSha256);
  const census = JSON.parse(censusBytes);
  const installedFiles = installed.map((row, index) => {
    const expected = census.files.filter((item) => item.kind === 'pipe' && item.path === row.path);
    check(
      expected.length === 1 && expected[0].sha256 === row.sha256 && expected[0].bytes === row.bytes,
    );
    const path = join(identity.nextRoot, row.path),
      bytes = bytesAt(path, MAX_BYTES);
    check(bytes.length === row.bytes && sha(bytes) === row.sha256);
    return {
      index,
      kind: 'installed',
      path: row.path,
      url: pathToFileURL(path).href,
      bytes: row.bytes,
      sha256: row.sha256,
      map: null,
      entries: [{ responseName: 'response', point: row.point }],
    };
  });
  check(new Set(installedFiles.map((row) => row.path)).size === 27);
  const generated = deriveGeneratedPipeSubjects(framework, parserSpec);
  const files = [
    ...installedFiles,
    ...generated.inventory.map((row) => ({ ...row, index: row.index + 27, kind: 'generated' })),
  ];
  const sites = files.flatMap((file) =>
    file.entries.map((entry, entryIndex) => ({
      fileIndex: file.index,
      entryIndex,
      kind: file.kind,
      path: file.path,
      bytes: file.bytes,
      sha256: file.sha256,
      url: file.url,
      manifestSha256: file.kind === 'generated' ? generated.manifestSha256 : null,
      responseName: entry.responseName,
      point: entry.point,
    })),
  );
  check(sites.length > 27 && sites.length <= 155);
  return frozen({
    format: 1,
    manifestSha256: generated.manifestSha256,
    censusSha256: identity.censusSha256,
    parser: generated.parser,
    referenceSha256: generated.referenceSha256,
    files,
    sites: sites.map((site, index) => ({ index, ...site })),
  });
}

/** Logical record paths only. This receipt is observation, never source authority. */
export function routerPipeInventory(union) {
  const projected = {
    format: 1,
    manifestSha256: union.manifestSha256,
    censusSha256: union.censusSha256,
    parser: union.parser,
    referenceSha256: union.referenceSha256,
    files: union.files.map(({ url, ...file }) => file),
    sites: union.sites.map(({ url, ...site }) => site),
  };
  return frozen({ ...projected, sha256: sha(Buffer.from(JSON.stringify(projected))) });
}

// The copied fault observer reuses this exact graph-bound parser qualification.
export { loadParser as loadRouterProofParser };
