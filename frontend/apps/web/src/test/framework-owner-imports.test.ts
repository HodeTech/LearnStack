// @vitest-environment node
// Structural guard for trusted repository source, not a JavaScript sandbox.
import { mkdtempSync, readFileSync, realpathSync, rmSync } from 'node:fs';
import { isBuiltin } from 'node:module';
import { tmpdir } from 'node:os';
import { dirname, join, posix, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

import ts from 'typescript';
import { describe, expect, it } from 'vitest';

import { prepareFrameworkProof } from '../../scripts/framework-proof-support.mjs';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '../../../../..');
const owner = 'frontend/apps/web/scripts/public-server.mjs';
const child = 'frontend/apps/web/scripts/public-runtime-child.mjs';
const build = 'frontend/apps/web/scripts/framework-build.mjs';
const guard = 'frontend/scripts/framework-installation.mjs';
const generic = 'frontend/scripts/framework-process-family.mjs';
const family = 'frontend/scripts/framework-runtime-family.mjs';
const controller = 'frontend/scripts/framework-runtime-child-control.mjs';
const relay = 'frontend/apps/web/scripts/framework-proof-relay.mjs';
const ingressBinding = 'frontend/apps/web/scripts/framework-proof-ingress-bind.mjs';
const runtime = './public-server-runtime.mjs';
type Profile = 'none' | 'ingress' | 'admission' | 'bff';
const edges = new Map<string, readonly string[]>([
  [owner, [guard, generic, family, build]],
  [child, [controller, guard]],
  [build, [guard]],
  [guard, [generic]],
  [generic, []],
  [family, [generic]],
  [controller, []],
]);

function readSources(): Map<string, string> {
  return new Map(
    [...edges.keys()].map((path) => [path, readFileSync(resolve(root, path), 'utf8')]),
  );
}

function copiedSources(profile: Profile): Map<string, string> {
  const directory = realpathSync(mkdtempSync(join(tmpdir(), 'learnstack-owner-closure-')));
  try {
    const app = join(directory, 'frontend/apps/web');
    prepareFrameworkProof({ sourceApp: resolve(root, 'frontend/apps/web'), app, profile });
    const sources = readSources();
    for (const path of [owner, child, relay, ...(profile === 'ingress' ? [ingressBinding] : [])])
      sources.set(path, readFileSync(join(app, 'scripts', posix.basename(path)), 'utf8'));
    return sources;
  } finally {
    rmSync(directory, { recursive: true, force: true });
  }
}

function refuse(path: string): never {
  throw new Error(`Unmodeled owner import boundary: ${path}`);
}
function allNodes(file: ts.SourceFile): ts.Node[] {
  const nodes: ts.Node[] = [];
  const visit = (node: ts.Node) => {
    nodes.push(node);
    ts.forEachChild(node, visit);
  };
  visit(file);
  return nodes;
}
function ancestorFunction(node: ts.Node): string | undefined {
  for (let parent = node.parent; parent; parent = parent.parent)
    if (ts.isFunctionDeclaration(parent)) return parent.name?.text;
  return undefined;
}
function variableStatement(
  statement: ts.Statement | undefined,
  name: string,
): ts.VariableDeclaration | undefined {
  if (
    !statement ||
    !ts.isVariableStatement(statement) ||
    !(statement.declarationList.flags & ts.NodeFlags.Const) ||
    statement.declarationList.declarations.length !== 1
  )
    return undefined;
  const declaration = statement.declarationList.declarations[0];
  return declaration && ts.isIdentifier(declaration.name) && declaration.name.text === name
    ? declaration
    : undefined;
}

/** Check the one private runtime edge as a concrete control-flow shape, not a path exemption. */
function runtimeBoundary(
  file: ts.SourceFile,
  nodes: ts.Node[],
  profile?: Profile,
): ts.CallExpression {
  const tries = file.statements.filter(ts.isTryStatement);
  if (tries.length !== 1) return refuse(file.fileName);
  const statements = tries[0]!.tryBlock.statements;
  const framework = statements.findIndex((statement) => variableStatement(statement, 'framework'));
  const admission = statements.findIndex((statement) => variableStatement(statement, 'control'));
  const admissionDeclaration = variableStatement(statements[admission], 'control');
  const frameworkDeclaration = variableStatement(statements[framework], 'framework');
  if (
    framework < 0 ||
    admission <= framework ||
    frameworkDeclaration?.initializer?.getText(file) !==
      'verifyFrameworkRuntimeChild({ appRoot, ...child.binding })' ||
    admissionDeclaration?.initializer?.getText(file) !==
      (profile === undefined
        ? 'await child.admit(framework)'
        : `await child.admit(framework, { proof: createCopiedChildProof('${profile}') })`)
  )
    return refuse(file.fileName);
  if (profile !== undefined) {
    if (
      statements[admission - 1]?.getText(file) !==
        "const { createCopiedChildProof } = await import('./framework-proof-relay.mjs');" ||
      admission - 1 <= framework ||
      nodes.filter((node) => ts.isIdentifier(node) && node.text === 'createCopiedChildProof')
        .length !== 2
    )
      return refuse(file.fileName);
  }
  const admittedIf = statements[admission + 1];
  if (
    !admittedIf ||
    !ts.isIfStatement(admittedIf) ||
    admittedIf.expression.getText(file) !== 'control' ||
    admittedIf.elseStatement ||
    !ts.isBlock(admittedIf.thenStatement) ||
    admittedIf.thenStatement.statements.length !== (profile === 'ingress' ? 4 : 2)
  )
    return refuse(file.fileName);
  const inner = admittedIf.thenStatement.statements;
  if (
    profile === 'ingress' &&
    (inner[0]?.getText(file) !==
      "const { installCopiedIngressBinding } = await import('./framework-proof-ingress-bind.mjs');" ||
      inner[1]?.getText(file) !== 'installCopiedIngressBinding(control.proof);' ||
      nodes.filter((node) => ts.isIdentifier(node) && node.text === 'installCopiedIngressBinding')
        .length !== 2)
  )
    return refuse(file.fileName);
  const [importStatement, startStatement] = profile === 'ingress' ? inner.slice(2) : inner;
  if (
    !importStatement ||
    !ts.isVariableStatement(importStatement) ||
    !(importStatement.declarationList.flags & ts.NodeFlags.Const) ||
    importStatement.declarationList.declarations.length !== 1
  )
    return refuse(file.fileName);
  const declaration = importStatement.declarationList.declarations[0]!;
  if (
    !ts.isObjectBindingPattern(declaration.name) ||
    declaration.name.elements.length !== 1 ||
    declaration.name.elements[0]!.name.getText(file) !== 'startPublicRuntime' ||
    declaration.name.elements[0]!.propertyName ||
    !declaration.initializer ||
    !ts.isAwaitExpression(declaration.initializer) ||
    !ts.isCallExpression(declaration.initializer.expression) ||
    declaration.initializer.expression.expression.kind !== ts.SyntaxKind.ImportKeyword ||
    declaration.initializer.expression.arguments.length !== 1 ||
    !ts.isStringLiteral(declaration.initializer.expression.arguments[0]!) ||
    declaration.initializer.expression.arguments[0]!.text !== runtime
  )
    return refuse(file.fileName);
  if (
    !startStatement ||
    !ts.isIfStatement(startStatement) ||
    startStatement.elseStatement ||
    startStatement.expression.getText(file) !== '!control.stopRequested()' ||
    !ts.isExpressionStatement(startStatement.thenStatement) ||
    startStatement.thenStatement.expression.getText(file) !== 'await startPublicRuntime(control)'
  )
    return refuse(file.fileName);
  const runtimeReferences = nodes.filter(
    (node) => ts.isIdentifier(node) && node.text === 'startPublicRuntime',
  );
  const admissionCalls = nodes.filter(
    (node) => ts.isCallExpression(node) && node.expression.getText(file) === 'child.admit',
  );
  if (runtimeReferences.length !== 2 || admissionCalls.length !== 1) return refuse(file.fileName);
  return declaration.initializer.expression;
}

function copiedOwnerBoundary(file: ts.SourceFile, nodes: ts.Node[], profile: Profile): void {
  const tries = file.statements.filter(ts.isTryStatement);
  if (tries.length !== 1) return refuse(file.fileName);
  const statements = tries[0]!.tryBlock.statements;
  const index = statements.findIndex(
    (statement) =>
      statement.getText(file) ===
      "const { createCopiedParentProof } = await import('./framework-proof-relay.mjs');",
  );
  if (
    index < 0 ||
    statements[index + 1]?.getText(file) !==
      `copiedProof = createCopiedParentProof('${profile}');` ||
    file.statements.filter((statement) => statement.getText(file) === 'let copiedProof;').length !==
      1 ||
    nodes.filter((node) => ts.isIdentifier(node) && node.text === 'createCopiedParentProof')
      .length !== 2
  )
    return refuse(file.fileName);
  const proofWrites = nodes.filter(
    (node) =>
      ts.isBinaryExpression(node) &&
      node.operatorToken.kind >= ts.SyntaxKind.FirstAssignment &&
      node.operatorToken.kind <= ts.SyntaxKind.LastAssignment &&
      node.left.getText(file) === 'copiedProof',
  );
  if (
    proofWrites.length !== 1 ||
    proofWrites[0]!.getText(file) !== `copiedProof = createCopiedParentProof('${profile}')`
  )
    return refuse(file.fileName);
  const development = statements.findIndex(
    (statement) =>
      ts.isIfStatement(statement) &&
      statement.expression.getText(file) === "mode === 'development'",
  );
  if (development <= index + 1) return refuse(file.fileName);
  const calls = nodes.filter(
    (node): node is ts.CallExpression =>
      ts.isCallExpression(node) && node.expression.getText(file) === 'runFrameworkRuntimeFamily',
  );
  if (
    calls.length !== 1 ||
    calls[0]!.arguments.length !== 1 ||
    !ts.isObjectLiteralExpression(calls[0]!.arguments[0]!)
  )
    return refuse(file.fileName);
  const properties = calls[0]!.arguments[0]!.properties;
  const names = properties.map((property) =>
    ts.isPropertyAssignment(property) || ts.isShorthandPropertyAssignment(property)
      ? property.name.getText(file)
      : '',
  );
  if (
    JSON.stringify(names.sort()) !==
    JSON.stringify(
      ['entry', 'cwd', 'environment', 'mode', 'leaseRecord', 'signal', 'proof', 'onFamily'].sort(),
    )
  )
    return refuse(file.fileName);
  for (const [key, value] of [
    ['proof', 'copiedProof'],
    ['onFamily', 'copiedProof.onRuntimeFamily'],
  ]) {
    const matches = properties.filter(
      (property) => ts.isPropertyAssignment(property) && property.name.getText(file) === key,
    );
    if (
      matches.length !== 1 ||
      !ts.isPropertyAssignment(matches[0]!) ||
      matches[0]!.initializer.getText(file) !== value
    )
      return refuse(file.fileName);
  }
}

/** Exhaustive modeled resolver factory uses; resolving a pathname does not load the module. */
function resolverBoundary(path: string, file: ts.SourceFile, nodes: ts.Node[]): void {
  const factories: string[] = [];
  const resolutions: string[] = [];
  const processMembers = new Set([
    'argv',
    'connected',
    'cwd',
    'disconnect',
    'env',
    'execArgv',
    'execPath',
    'exit',
    'exitCode',
    'getuid',
    'kill',
    'off',
    'on',
    'once',
    'pid',
    'platform',
    'ppid',
    'removeListener',
    'send',
    'stderr',
    'versions',
  ]);
  for (const node of nodes) {
    if (!ts.isIdentifier(node)) continue;
    if (['eval', 'Function', 'global', 'globalThis'].includes(node.text)) return refuse(path);
    if (
      node.text === 'process' &&
      !(
        ts.isPropertyAccessExpression(node.parent) &&
        node.parent.expression === node &&
        processMembers.has(node.parent.name.text)
      )
    )
      return refuse(path);
    if (node.text === 'createRequire') {
      if (path !== guard) return refuse(path);
      if (
        ts.isImportSpecifier(node.parent) &&
        node.parent.name === node &&
        !node.parent.propertyName
      )
        continue;
      if (!ts.isCallExpression(node.parent) || node.parent.expression !== node) return refuse(path);
      const call = node.parent;
      const declaration = call.parent;
      if (
        !ts.isVariableDeclaration(declaration) ||
        !ts.isIdentifier(declaration.name) ||
        declaration.name.text !== 'require' ||
        !ts.isVariableDeclarationList(declaration.parent) ||
        !(declaration.parent.flags & ts.NodeFlags.Const)
      )
        return refuse(path);
      const signature = `${ancestorFunction(call)}:${call.getText(file)}`;
      factories.push(signature);
      continue;
    }
    if (node.text !== 'require') continue;
    if (path !== guard) return refuse(path);
    if (
      ts.isVariableDeclaration(node.parent) &&
      node.parent.name === node &&
      node.parent.initializer &&
      ts.isCallExpression(node.parent.initializer) &&
      node.parent.initializer.expression.getText(file) === 'createRequire'
    )
      continue;
    const property = node.parent;
    if (
      !ts.isPropertyAccessExpression(property) ||
      property.expression !== node ||
      property.name.text !== 'resolve' ||
      !ts.isCallExpression(property.parent) ||
      property.parent.expression !== property ||
      property.parent.arguments.length !== 1 ||
      !ts.isStringLiteral(property.parent.arguments[0]!)
    )
      return refuse(path);
    resolutions.push(`${ancestorFunction(property.parent)}:${property.parent.arguments[0]!.text}`);
  }
  const expectedFactories =
    path === guard
      ? [
          "compileFrameworkNative:createRequire(join(identity.appRoot, 'package.json'))",
          "verifyFrameworkInstallation:createRequire(join(appRoot, 'package.json'))",
        ]
      : [];
  const expectedResolutions =
    path === guard
      ? [
          'compileFrameworkNative:typescript/bin/tsc',
          'verifyFrameworkInstallation:next',
          'verifyFrameworkInstallation:next/dist/bin/next',
        ]
      : [];
  if (
    JSON.stringify(factories.sort()) !== JSON.stringify(expectedFactories.sort()) ||
    JSON.stringify(resolutions.sort()) !== JSON.stringify(expectedResolutions.sort())
  )
    return refuse(path);
}

function assertBoundary(sources: Map<string, string>, profile?: Profile): void {
  const graph = new Map(edges);
  if (profile !== undefined) {
    graph.set(owner, [...graph.get(owner)!, relay]);
    graph.set(child, [
      ...graph.get(child)!,
      relay,
      ...(profile === 'ingress' ? [ingressBinding] : []),
    ]);
    graph.set(relay, []);
    if (profile === 'ingress') graph.set(ingressBinding, []);
  }
  const visited = new Set<string>();
  const visit = (path: string) => {
    if (visited.has(path)) return;
    const text = sources.get(path);
    if (text === undefined || !graph.has(path)) return refuse(path);
    visited.add(path);
    const file = ts.createSourceFile(path, text, ts.ScriptTarget.Latest, true, ts.ScriptKind.JS);
    if (
      (file as ts.SourceFile & { parseDiagnostics: readonly ts.Diagnostic[] }).parseDiagnostics
        .length
    )
      return refuse(path);
    const nodes = allNodes(file);
    const runtimeImport = path === child ? runtimeBoundary(file, nodes, profile) : undefined;
    if (path === owner && profile !== undefined) copiedOwnerBoundary(file, nodes, profile);
    resolverBoundary(path, file, nodes);
    const found: string[] = [];
    let factoryImports = 0;
    const add = (specifier: string) => {
      if (specifier.startsWith('node:') && isBuiltin(specifier)) {
        if (
          ['node:module', 'node:process'].includes(specifier) &&
          !(path === guard && specifier === 'node:module')
        )
          return refuse(path);
        return;
      }
      if (!specifier.startsWith('.')) return refuse(path);
      const target = posix.normalize(posix.join(posix.dirname(path), specifier));
      if (!graph.has(target) || !sources.has(target)) return refuse(path);
      found.push(target);
      visit(target);
    };
    for (const node of nodes) {
      if ((ts.isImportDeclaration(node) || ts.isExportDeclaration(node)) && node.moduleSpecifier) {
        if (!ts.isStringLiteral(node.moduleSpecifier)) return refuse(path);
        if (node.moduleSpecifier.text === 'node:module') {
          if (
            !ts.isImportDeclaration(node) ||
            !node.importClause?.namedBindings ||
            !ts.isNamedImports(node.importClause.namedBindings) ||
            node.importClause.name ||
            node.importClause.namedBindings.elements.length !== 1 ||
            node.importClause.namedBindings.elements[0]!.name.text !== 'createRequire' ||
            node.importClause.namedBindings.elements[0]!.propertyName
          )
            return refuse(path);
          factoryImports++;
        }
        add(node.moduleSpecifier.text);
      }
      if (ts.isImportEqualsDeclaration(node)) return refuse(path);
      if (ts.isCallExpression(node) && node.expression.kind === ts.SyntaxKind.ImportKeyword) {
        if (node === runtimeImport) continue;
        if (node.arguments.length !== 1 || !ts.isStringLiteral(node.arguments[0]!))
          return refuse(path);
        if (['node:module', 'node:process'].includes(node.arguments[0]!.text)) return refuse(path);
        add(node.arguments[0]!.text);
      }
    }
    if (factoryImports !== (path === guard ? 1 : 0)) return refuse(path);
    if (JSON.stringify(found.sort()) !== JSON.stringify([...(graph.get(path) ?? [])].sort()))
      return refuse(path);
  };
  visit(owner);
  visit(child);
  if (visited.size !== graph.size) return refuse('closure');
}

describe('ADR-0084 builtins owner and private child import boundary', () => {
  it('admits only the actual qualified closure and the guarded native runtime edge', () => {
    expect(() => assertBoundary(readSources())).not.toThrow();
  });
  it.each([
    ['transitive Next', generic, "\nimport 'next';"],
    ['transitive SDK', family, "\nimport '@learnstack/sdk';"],
    ['transitive application', controller, "\nimport '../apps/web/src/server/ingress.js';"],
    ['computed dynamic', build, '\nawait import(selected);'],
    ['unknown local helper', family, "\nimport './unreviewed-helper.mjs';"],
    [
      'loader invocation',
      guard,
      "\nfunction hidden() { const require = createRequire(join(appRoot, 'package.json')); return require('next'); }",
    ],
    ['loader alias', guard, '\nconst loader = createRequire;'],
    ['dynamic loader factory', guard, "\nconst hidden = await import('node:module');"],
    ['computed process loader', guard, "\nprocess['getBuiltinModule']('module');"],
  ])('refuses %s in the closure', (_name, path, addition) => {
    const sources = readSources();
    sources.set(path, sources.get(path)! + addition);
    expect(() => assertBoundary(sources)).toThrow('Unmodeled owner import boundary');
  });
  it('refuses a missing expected helper', () => {
    const sources = readSources();
    sources.delete(controller);
    expect(() => assertBoundary(sources)).toThrow('Unmodeled owner import boundary');
  });
  it('refuses an extra local helper even when its source is present', () => {
    const sources = readSources();
    sources.set(family, sources.get(family)! + "\nimport './unreviewed-helper.mjs';");
    sources.set('frontend/scripts/unreviewed-helper.mjs', "import 'node:fs';");
    expect(() => assertBoundary(sources)).toThrow('Unmodeled owner import boundary');
  });
  it.each(['none', 'ingress', 'admission', 'bff'] as const)(
    'checks the actual copied %s profile and its closed helper graph',
    (profile) => {
      const sources = copiedSources(profile);
      assertBoundary(sources, profile);
      for (const external of ['next', '@learnstack/sdk']) {
        const dirty = new Map(sources);
        dirty.set(relay, dirty.get(relay)! + `\nimport '${external}';`);
        expect(() => assertBoundary(dirty, profile)).toThrow('Unmodeled owner import boundary');
      }
      const missing = new Map(sources);
      missing.delete(relay);
      expect(() => assertBoundary(missing, profile)).toThrow('Unmodeled owner import boundary');
      const selected = new Map(sources);
      selected.set(
        child,
        selected
          .get(child)!
          .replace(
            `createCopiedChildProof('${profile}')`,
            'createCopiedChildProof(process.env.PROFILE)',
          ),
      );
      expect(selected.get(child)).not.toBe(sources.get(child));
      expect(() => assertBoundary(selected, profile)).toThrow('Unmodeled owner import boundary');
      const selectedOwner = new Map(sources);
      selectedOwner.set(
        owner,
        selectedOwner
          .get(owner)!
          .replace(
            `createCopiedParentProof('${profile}')`,
            'createCopiedParentProof(process.env.PROFILE)',
          ),
      );
      expect(selectedOwner.get(owner)).not.toBe(sources.get(owner));
      expect(() => assertBoundary(selectedOwner, profile)).toThrow(
        'Unmodeled owner import boundary',
      );
      const eager = new Map(sources);
      eager.set(child, eager.get(child)! + "\nawait import('./public-server-runtime.mjs');");
      expect(() => assertBoundary(eager, profile)).toThrow('Unmodeled owner import boundary');
      const duplicated = new Map(sources);
      duplicated.set(
        owner,
        duplicated.get(owner)! + "\nawait import('./framework-proof-relay.mjs');",
      );
      expect(() => assertBoundary(duplicated, profile)).toThrow('Unmodeled owner import boundary');
      const override = new Map(sources);
      override.set(
        owner,
        override
          .get(owner)!
          .replace('    proof: copiedProof,', '    proof: copiedProof,\n    ...unmodeledOptions,'),
      );
      expect(override.get(owner)).not.toBe(sources.get(owner));
      expect(() => assertBoundary(override, profile)).toThrow('Unmodeled owner import boundary');
      if (profile === 'ingress') {
        const dirty = new Map(sources);
        dirty.set(ingressBinding, dirty.get(ingressBinding)! + "\nimport '@learnstack/sdk';");
        expect(() => assertBoundary(dirty, profile)).toThrow('Unmodeled owner import boundary');
        dirty.delete(ingressBinding);
        expect(() => assertBoundary(dirty, profile)).toThrow('Unmodeled owner import boundary');
      }
    },
  );
  it.each([
    [
      'static native import',
      (source: string) => `import './public-server-runtime.mjs';\n${source}`,
    ],
    [
      'eager native import',
      (source: string) => `await import('./public-server-runtime.mjs');\n${source}`,
    ],
    [
      'native import before admission',
      (source: string) =>
        source
          .replace(
            "    const { startPublicRuntime } = await import('./public-server-runtime.mjs');\n",
            '',
          )
          .replace(
            '  const control = await child.admit(framework);',
            "  const { startPublicRuntime } = await import('./public-server-runtime.mjs');\n  const control = await child.admit(framework);",
          ),
    ],
    [
      'unconditional native start',
      (source: string) =>
        source.replace(
          'if (!control.stopRequested()) await startPublicRuntime(control);',
          'await startPublicRuntime(control);',
        ),
    ],
  ])('refuses %s', (_name, mutate) => {
    const sources = readSources();
    const original = sources.get(child)!;
    const changed = mutate(original);
    expect(changed).not.toBe(original);
    sources.set(child, changed);
    expect(() => assertBoundary(sources)).toThrow('Unmodeled owner import boundary');
  });
});
