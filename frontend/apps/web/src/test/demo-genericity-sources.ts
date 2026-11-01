import { createHash } from 'node:crypto';
import { isBuiltin } from 'node:module';
import { posix } from 'node:path';
import { fileURLToPath } from 'node:url';

import ts from 'typescript';

// Exact proof entries and development declarations are bounded exemptions.
// Shared runtime helpers are not exemptions, even if tests also import them.
const exemptEntries = new Set([
  'frontend/apps/web/scripts/verify-ingress.mjs',
  'frontend/apps/web/scripts/verify-ingress-cleanup.mjs',
  'frontend/apps/web/scripts/verify-ingress-controls.mjs',
  'frontend/apps/web/scripts/verify-public-admission.mjs',
  'frontend/apps/web/scripts/verify-public-rendering.mjs',
  'frontend/apps/web/scripts/admission-browser.mjs',
  'frontend/apps/web/scripts/admission-completion.mjs',
  'frontend/apps/web/scripts/admission-hmr.mjs',
  'frontend/apps/web/scripts/admission-hmr-watch.mjs',
  'scripts/public-local.mjs',
]);

const generatedOutputs = new Set([
  'frontend/apps/web/.next',
  'frontend/apps/web/.server',
  // Runtime/install serialization records are generated machine state.
  'frontend/.framework-state',
  'frontend/apps/web/.framework-state',
  'frontend/packages/sdk/.framework-state',
  'frontend/apps/web/coverage',
  'frontend/packages/sdk/dist',
  'frontend/packages/ui/dist',
  'frontend/coverage',
]);

/** Only actual generated roots are omitted; nested source directories stay visible. */
export function omitDemoSourceDirectory(path: string): boolean {
  return dependencyRoots.has(path) || generatedOutputs.has(path);
}

const dependencyRoots = new Set([
  'frontend/node_modules',
  'frontend/apps/web/node_modules',
  'frontend/packages/config/node_modules',
  'frontend/packages/sdk/node_modules',
  'frontend/packages/ui/node_modules',
]);
const sourceExtension = /\.(?:[cm]?[jt]sx?|json)$/;
const declaration = /\.d\.[cm]?[jt]sx?$/;
const nonRuntimeFiles = new Set([
  // Parsed only by pure controls; the extracted fixture is never executed or imported.
  'frontend/scripts/fixtures/router-minified-join.txt',
  'frontend/scripts/fixtures/router-minified-join.provenance.json',
  'frontend/.gitignore',
  'frontend/.npmrc',
  'frontend/.nvmrc',
  'frontend/.prettierignore',
  'frontend/README.md',
  'frontend/apps/web/README.md',
  'frontend/apps/web/.env.local.example',
  'frontend/apps/web/src/app/globals.css',
  'frontend/apps/web/src/components/.gitkeep',
  'frontend/apps/web/tsconfig.tsbuildinfo',
  'frontend/packages/sdk/tsconfig.tsbuildinfo',
  'frontend/packages/ui/tsconfig.tsbuildinfo',
  'frontend/pnpm-lock.yaml',
  // pnpm applies this reviewed non-executable package carrier before admission.
  'frontend/patches/next@15.5.28.patch',
  'frontend/framework/next-response-cleanup/README.md',
  'frontend/pnpm-workspace.yaml',
  'scripts/adr-status.py',
  'scripts/assert-tests-ran.py',
  'scripts/ci-native-environment.py',
  'scripts/ci-native-provisioning.py',
  'scripts/ci-native-tests.py',
  'scripts/ci-test-shards.py',
  'scripts/ci-test-shards-plan.json',
  'scripts/ci-test-shard-results.py',
  'scripts/connection-string.awk',
  'scripts/delivery-log-index.py',
  'scripts/install-leakwatch.py',
  'scripts/install-oasdiff.py',
  'scripts/markdown-fragment-audit.py',
  'scripts/openapi-ci.py',
  'scripts/openapi-policy.py',
  'scripts/roadmap-structure.py',
  'scripts/seed.sh',
  'scripts/test-adr-status.py',
  'scripts/test-markdown-link-audit.py',
  'scripts/test-markdown-fragment-audit.py',
  'scripts/test-roadmap-structure.py',
  'scripts/test-ci-native-environment.py',
  'scripts/test-ci-native-provisioning.py',
  'scripts/test-ci-native-tests.py',
  'scripts/test-ci-test-shards.py',
  'scripts/test-ci-test-shard-results.py',
  'scripts/test-ci-supply-chain.py',
  'scripts/test-delivery-log-index.py',
  'scripts/test-openapi-ci.py',
  'scripts/test-realm-node-gate.py',
  'scripts/run-realm-node-tests.py',
  // Offline vendor distribution materials and isolated lifecycle experiments.
  'scripts/probe-hangfire-lifecycle.py',
  'scripts/probes/hangfire-lifecycle/BoundaryProbe.cs',
  'scripts/probes/hangfire-lifecycle/BuilderProbe.cs',
  'scripts/probes/hangfire-lifecycle/ExitProbe.cs',
  'scripts/probes/hangfire-lifecycle/Program.cs',
  'third-party/hangfire/.gitattributes',
  'third-party/hangfire/README.md',
  'third-party/hangfire/core/COPYING',
  'third-party/hangfire/core/COPYING.LESSER',
  'third-party/hangfire/core/LICENSE.md',
  'third-party/hangfire/core/NOTICES',
  'third-party/hangfire/core/corresponding-source.tar.gz',
  'third-party/hangfire/cronos/LICENSE',
  'third-party/hangfire/cronos/corresponding-source.tar.gz',
  'third-party/hangfire/dependency-notices/Apache-2.0.txt',
  'third-party/hangfire/dependency-notices/Dapper-2.0.123-LICENSE.txt',
  'third-party/hangfire/dependency-notices/Dapper.AOT-1.0.48-LICENSE.txt',
  'third-party/hangfire/dependency-notices/Newtonsoft.Json-13.0.4-LICENSE.md',
  'third-party/hangfire/material-sha256.json',
  'third-party/hangfire/original-binaries.json',
  'third-party/hangfire/postgres/COPYING',
  'third-party/hangfire/postgres/COPYING.LESSER',
  'third-party/hangfire/postgres/LICENSE.md',
  'third-party/hangfire/postgres/corresponding-source.tar.gz',
  'third-party/hangfire/qualification/MaintenanceProbe.cs',
  'third-party/hangfire/qualification/README.md',
  'third-party/hangfire/qualification/probe-maintenance.py',
  'third-party/hangfire/qualification/probe-pg-timeouts.py',
  'third-party/hangfire/script-sha256.json',
  'third-party/hangfire/source-sha256.json',
  'third-party/hangfire/verify-distribution-controls.py',
  'third-party/hangfire/verify-distribution.py',
]);
const nonRuntimeRoots = new Set([
  '.claude',
  '.config',
  '.githooks',
  '.github',
  '.vscode',
  'docs',
  'infra',
]);
const rootFiles = new Set([
  '.env.example',
  '.gitattributes',
  '.gitignore',
  '.leakwatch.yaml',
  '.leakwatchignore',
  '.prettierignore',
  'AGENTS.md',
  'CLAUDE.md',
  'Makefile',
  'README.md',
]);

/** Runtime JS/TS cannot be hidden by adding a file to the data/tooling list. */
export function assertDemoNonRuntimeFiles(files: Iterable<string> = nonRuntimeFiles): void {
  for (const path of files)
    if (/\.[cm]?[jt]sx?$/.test(path))
      throw new Error(`Runtime source cannot have non-runtime ownership: ${path}`);
}

/** Refuse new roots/formats until their production or non-runtime ownership is explicit. */
export function demoSourceClassification(path: string): 'runtime' | 'other' {
  assertDemoNonRuntimeFiles();
  if (!path || path.startsWith('/') || path.split('/').some((part) => part === '..'))
    throw new Error(`Unclassified repository source: ${path}`);
  const root = path.split('/')[0]!;
  if (nonRuntimeRoots.has(root) || rootFiles.has(path)) return 'other';
  // Backend source admission and evaluated Compile coverage are owned by its architecture test.
  if (root === 'backend') {
    if (
      /^backend\/(?:src|analyzers|tests|openapi)\//.test(path) ||
      /^backend\/(?:\.editorconfig|Directory\.(?:Build|Packages)\.props|LearnStack\.slnx|README\.md|global\.json)$/.test(
        path,
      )
    )
      return 'other';
    throw new Error(`Unclassified repository source: ${path}`);
  }
  if (nonRuntimeFiles.has(path)) return 'other';
  if (root !== 'frontend' && root !== 'scripts')
    throw new Error(`Unclassified repository source: ${path}`);
  if (declaration.test(path)) return 'other';
  if (
    !sourceExtension.test(path) ||
    [...dependencyRoots, ...generatedOutputs].some((dir) => path.startsWith(dir + '/'))
  )
    throw new Error(`Unclassified repository source: ${path}`);
  return 'runtime';
}

// A narrow machine-file exception, never a blanket exemption for ignored formats.
export function omitIgnoredDemoLocalFile(
  path: string,
  ignored: (path: string) => boolean,
): boolean {
  const name = posix.basename(path);
  const environment = new Set([
    '.env',
    '.env.local',
    '.env.development',
    '.env.test',
    '.env.production',
    '.env.development.local',
    '.env.test.local',
    '.env.production.local',
  ]);
  return (
    (environment.has(name) ||
      name === '.DS_Store' ||
      name === 'Thumbs.db' ||
      /\.(?:swp|log|suo|rsuser)$/.test(name)) &&
    ignored(path)
  );
}

export type ExternalModuleResolver = (from: string, specifier: string, anchors: string[]) => string;

function testSource(path: string): boolean {
  return (
    path.startsWith('frontend/apps/web/src/test/') || /\.(?:test|spec)\.[cm]?[jt]sx?$/.test(path)
  );
}

/** Model the closed app-relative loaders, without exempting any of their edges. */
function frameworkRequireOrigins(file: ts.SourceFile): Map<ts.Block | ts.SourceFile, string> {
  const refused: () => never = () => {
    throw new Error(`Unmodeled runtime import: ${file.fileName}`);
  };
  const provenanceControl = file.fileName === 'frontend/scripts/test-framework-provenance.mjs';
  const routerParserControl =
    file.fileName === 'frontend/apps/web/scripts/framework-router-generated-pipes.mjs';
  const loader = provenanceControl ? 'actualRequire' : 'require';
  const functions = new Map(
    file.statements
      .filter(ts.isFunctionDeclaration)
      .filter((node) => node.name && node.body)
      .map((node) => [node.name!.text, node]),
  );
  const imported = (name: string, module: string) =>
    file.statements.filter(
      (node) =>
        ts.isImportDeclaration(node) &&
        ts.isStringLiteral(node.moduleSpecifier) &&
        node.moduleSpecifier.text === module &&
        node.importClause?.namedBindings &&
        ts.isNamedImports(node.importClause.namedBindings) &&
        node.importClause.namedBindings.elements.some(
          (element) => element.name.text === name && (element.propertyName?.text ?? name) === name,
        ),
    ).length === 1;
  if (!imported('createRequire', 'node:module') || !imported('join', 'node:path')) refused();
  if (
    file.statements.filter(
      (node) =>
        ts.isImportDeclaration(node) &&
        ts.isStringLiteral(node.moduleSpecifier) &&
        ['node:module', 'module'].includes(node.moduleSpecifier.text),
    ).length !== 1
  )
    refused();
  const names = (name: ts.BindingName): string[] =>
    ts.isIdentifier(name)
      ? [name.text]
      : name.elements.flatMap((element) =>
          ts.isOmittedExpression(element) ? [] : names(element.name),
        );
  const declared = (node: ts.Node): string[] => {
    if (ts.isVariableDeclaration(node) || ts.isParameter(node) || ts.isBindingElement(node))
      return names(node.name);
    if (
      (ts.isFunctionDeclaration(node) ||
        ts.isFunctionExpression(node) ||
        ts.isClassDeclaration(node)) &&
      node.name
    )
      return [node.name.text];
    return [];
  };
  const rootOf = (node: ts.Node): string[] => {
    if (ts.isIdentifier(node)) return [node.text];
    if (ts.isParenthesizedExpression(node)) return rootOf(node.expression);
    if (ts.isPropertyAccessExpression(node) || ts.isElementAccessExpression(node))
      return rootOf(node.expression);
    if (ts.isSpreadElement(node)) return rootOf(node.expression);
    if (ts.isArrayLiteralExpression(node)) return node.elements.flatMap(rootOf);
    if (ts.isObjectLiteralExpression(node))
      return node.properties.flatMap((property) =>
        ts.isShorthandPropertyAssignment(property)
          ? [property.name.text]
          : ts.isPropertyAssignment(property)
            ? rootOf(property.initializer)
            : ts.isSpreadAssignment(property)
              ? rootOf(property.expression)
              : [],
      );
    return [];
  };
  const written = (node: ts.Node): string[] => {
    if (
      ts.isBinaryExpression(node) &&
      node.operatorToken.kind >= ts.SyntaxKind.FirstAssignment &&
      node.operatorToken.kind <= ts.SyntaxKind.LastAssignment
    )
      return rootOf(node.left);
    if (
      (ts.isPrefixUnaryExpression(node) || ts.isPostfixUnaryExpression(node)) &&
      [ts.SyntaxKind.PlusPlusToken, ts.SyntaxKind.MinusMinusToken].includes(node.operator)
    )
      return rootOf(node.operand);
    if (ts.isForInStatement(node) || ts.isForOfStatement(node)) return rootOf(node.initializer);
    return [];
  };
  const visit = (node: ts.Node, action: (node: ts.Node) => void) => {
    action(node);
    ts.forEachChild(node, (child) => visit(child, action));
  };
  if (provenanceControl && !imported('FRAMEWORK_FRONTEND_ROOT', './framework-installation.mjs'))
    refused();
  // These two sources have one closed factory acquisition form. A second
  // builtin module/process acquisition is a model change, even if its eventual
  // loader uses a different name. This does not model arbitrary JS or eval.
  const factoryModules = new Set(['module', 'node:module', 'process', 'node:process']);
  const processMembers = new Set([
    'versions',
    'cwd',
    'env',
    'execPath',
    'pid',
    'getuid',
    'ppid',
    'argv',
    // Fixed native-parent verification and retained control diagnostics. These
    // are concrete members, never a general process/builtin-loader exemption.
    ...(provenanceControl ? ['kill', 'stderr'] : ['connected', 'send', 'kill']),
  ]);
  const calls: ts.CallExpression[] = [];
  visit(file, (node) => {
    if (
      ((ts.isImportDeclaration(node) || ts.isExportDeclaration(node)) &&
        node.moduleSpecifier &&
        ts.isStringLiteralLike(node.moduleSpecifier) &&
        factoryModules.has(node.moduleSpecifier.text) &&
        !(ts.isImportDeclaration(node) && node.moduleSpecifier.text === 'node:module')) ||
      (ts.isImportEqualsDeclaration(node) &&
        ts.isExternalModuleReference(node.moduleReference) &&
        node.moduleReference.expression &&
        ts.isStringLiteralLike(node.moduleReference.expression) &&
        factoryModules.has(node.moduleReference.expression.text))
    )
      refused();
    if (
      ts.isCallExpression(node) &&
      (node.expression.kind === ts.SyntaxKind.ImportKeyword ||
        (ts.isIdentifier(node.expression) && ['require', loader].includes(node.expression.text)) ||
        (ts.isPropertyAccessExpression(node.expression) &&
          ts.isIdentifier(node.expression.expression) &&
          ['require', loader].includes(node.expression.expression.text) &&
          node.expression.name.text === 'resolve')) &&
      node.arguments[0] &&
      ts.isStringLiteralLike(node.arguments[0]) &&
      factoryModules.has(node.arguments[0].text)
    )
      refused();
    if (
      ts.isIdentifier(node) &&
      (['global', 'globalThis'].includes(node.text) ||
        (node.text === 'process' &&
          !(
            ts.isPropertyAccessExpression(node.parent) &&
            node.parent.expression === node &&
            processMembers.has(node.parent.name.text)
          )))
    )
      refused();
    if (
      ts.isImportDeclaration(node) &&
      ts.isStringLiteral(node.moduleSpecifier) &&
      node.moduleSpecifier.text === 'node:module' &&
      (!node.importClause?.namedBindings ||
        !ts.isNamedImports(node.importClause.namedBindings) ||
        node.importClause.name ||
        node.importClause.namedBindings.elements.length !== 1 ||
        node.importClause.namedBindings.elements[0]!.name.text !== 'createRequire' ||
        node.importClause.namedBindings.elements[0]!.propertyName)
    )
      refused();
    if (
      declared(node).some((name) =>
        [
          'createRequire',
          'join',
          ...(provenanceControl ? ['FRAMEWORK_FRONTEND_ROOT'] : []),
        ].includes(name),
      )
    )
      refused();
    if (
      written(node).some((name) =>
        [
          'createRequire',
          'join',
          'checkedRoot',
          ...(provenanceControl ? ['FRAMEWORK_FRONTEND_ROOT'] : []),
        ].includes(name),
      )
    )
      refused();
    if (
      ts.isIdentifier(node) &&
      node.text === 'createRequire' &&
      !ts.isImportSpecifier(node.parent) &&
      !(ts.isCallExpression(node.parent) && node.parent.expression === node)
    )
      refused();
    if (
      ts.isCallExpression(node) &&
      ts.isIdentifier(node.expression) &&
      node.expression.text === 'createRequire'
    )
      calls.push(node);
  });
  // A new loader is a new model obligation, not an automatically trusted context.
  if (calls.length !== (provenanceControl ? 1 : 2)) refused();
  const origins = new Map<ts.Block | ts.SourceFile, string>();
  for (const call of calls) {
    const declaration = call.parent;
    if (
      !ts.isVariableDeclaration(declaration) ||
      !ts.isIdentifier(declaration.name) ||
      declaration.name.text !== loader ||
      !ts.isVariableDeclarationList(declaration.parent) ||
      !(declaration.parent.flags & ts.NodeFlags.Const) ||
      declaration.parent.declarations.length !== 1 ||
      !ts.isVariableStatement(declaration.parent.parent) ||
      !(provenanceControl
        ? declaration.parent.parent.parent === file
        : ts.isBlock(declaration.parent.parent.parent))
    )
      refused();
    const body = declaration.parent.parent.parent as ts.Block | ts.SourceFile;
    const owner = body.parent;
    const root = provenanceControl
      ? 'FRAMEWORK_FRONTEND_ROOT'
      : ts.isFunctionDeclaration(owner) && owner.name?.text === 'verifyFrameworkInstallation'
        ? 'appRoot'
        : 'identity';
    if (
      (provenanceControl
        ? body !== file
        : !ts.isFunctionDeclaration(owner) ||
          !owner.name ||
          !(
            routerParserControl
              ? ['describeRouterParser', 'loadParser']
              : ['verifyFrameworkInstallation', 'compileFrameworkNative']
          ).includes(owner.name.text) ||
          functions.get(owner.name.text) !== owner) ||
      origins.has(body)
    )
      refused();
    const joined = call.arguments[0];
    const rootArgument = joined && ts.isCallExpression(joined) ? joined.arguments[0] : undefined;
    if (
      call.arguments.length !== 1 ||
      !joined ||
      !ts.isCallExpression(joined) ||
      !ts.isIdentifier(joined.expression) ||
      joined.expression.text !== 'join' ||
      joined.arguments.length !== 2 ||
      !joined.arguments[1] ||
      !ts.isStringLiteral(joined.arguments[1]) ||
      joined.arguments[1].text !== (provenanceControl ? 'apps/web/package.json' : 'package.json') ||
      !rootArgument ||
      (root !== 'identity'
        ? !ts.isIdentifier(rootArgument) || rootArgument.text !== root
        : !ts.isPropertyAccessExpression(rootArgument) ||
          !ts.isIdentifier(rootArgument.expression) ||
          rootArgument.expression.text !== root ||
          rootArgument.name.text !== 'appRoot')
    )
      refused();
    if (!provenanceControl) {
      if (!ts.isFunctionDeclaration(owner)) refused();
      const parameters = owner.parameters.flatMap((parameter) => names(parameter.name));
      if (parameters.filter((name) => name === root).length !== 1) refused();
    }
    const assignments: ts.BinaryExpression[] = [];
    visit(body, (node) => {
      if (
        node !== declaration &&
        declared(node).some((name) => [root, loader, 'checkedRoot'].includes(name))
      )
        refused();
      if (
        written(node).some((name) =>
          [root, loader, 'checkedRoot', 'createRequire', 'join'].includes(name),
        )
      ) {
        if (
          root === 'appRoot' &&
          ts.isBinaryExpression(node) &&
          ts.isIdentifier(node.left) &&
          node.left.text === root
        )
          assignments.push(node);
        else refused();
      }
      if (ts.isIdentifier(node) && node.text === loader && node !== declaration.name) {
        const parent = node.parent;
        if (
          !(
            (ts.isCallExpression(parent) && parent.expression === node) ||
            (ts.isPropertyAccessExpression(parent) &&
              parent.expression === node &&
              parent.name.text === 'resolve' &&
              ts.isCallExpression(parent.parent) &&
              parent.parent.expression === parent)
          )
        )
          refused();
      }
    });
    if (root === 'appRoot') {
      const checked = assignments[0];
      if (
        assignments.length !== 1 ||
        !checked ||
        checked.operatorToken.kind !== ts.SyntaxKind.EqualsToken ||
        !ts.isExpressionStatement(checked.parent) ||
        checked.parent.parent !== body ||
        checked.pos >= declaration.pos ||
        !ts.isCallExpression(checked.right) ||
        !ts.isIdentifier(checked.right.expression) ||
        checked.right.expression.text !== 'checkedRoot' ||
        checked.right.arguments.length !== 1 ||
        !ts.isIdentifier(checked.right.arguments[0]!) ||
        checked.right.arguments[0]!.text !== root ||
        !functions.has('checkedRoot')
      )
        refused();
    }
    // The supported application is the concrete checkout instance of appRoot;
    // every literal/local edge still goes through the ordinary source resolver.
    origins.set(body, 'frontend/apps/web/package.json');
  }
  if (routerParserControl) {
    const expected = new Map([
      ['describeRouterParser', ['resolve:typescript', 'resolve:typescript/package.json']],
      ['loadParser', ['require:typescript']],
    ]);
    for (const [body] of origins) {
      const owner = body.parent;
      if (!ts.isFunctionDeclaration(owner) || !owner.name) refused();
      const edges: string[] = [];
      visit(body, (node) => {
        if (!ts.isCallExpression(node)) return;
        const direct = ts.isIdentifier(node.expression) && node.expression.text === loader;
        const resolved =
          ts.isPropertyAccessExpression(node.expression) &&
          ts.isIdentifier(node.expression.expression) &&
          node.expression.expression.text === loader &&
          node.expression.name.text === 'resolve';
        if (!direct && !resolved) return;
        const argument = node.arguments[0];
        if (node.arguments.length !== 1 || !argument || !ts.isStringLiteral(argument)) refused();
        edges.push((direct ? 'require:' : 'resolve:') + argument.text);
      });
      if (JSON.stringify(edges) !== JSON.stringify(expected.get(owner.name.text))) refused();
    }
  }
  return origins;
}

/** A standalone exemption cannot hide a statically imported runtime helper. */
export function productionDemoSources(
  all: ReadonlyMap<string, string>,
  external?: ExternalModuleResolver,
): Map<string, string> {
  const refused = (path: string): never => {
    throw new Error(`Unmodeled runtime import: ${path}`);
  };
  const packages = [...all]
    .filter(([path]) => /^frontend\/(?:apps|packages)\/[^/]+\/package\.json$/.test(path))
    .map(([path, source]) => ({
      root: posix.dirname(path),
      manifest: JSON.parse(source),
    }));
  const generated = new Map<string, string>();
  const serverConfig = all.get('frontend/apps/web/tsconfig.server.json');
  if (serverConfig) {
    const config = JSON.parse(serverConfig);
    const expected = [
      'src/server/auth/account-client.ts',
      'src/server/auth/configuration.ts',
      'src/server/auth/http-protocol.ts',
      'src/server/auth/logout-response.ts',
      'src/server/auth/native-administration-client.ts',
      'src/server/auth/native-session-client.ts',
      'src/server/auth/provider-core.ts',
      'src/server/auth/request-runtime.ts',
      'src/server/auth/session-runtime.ts',
      'src/server/ingress.ts',
      'src/server/public-admission-runtime.ts',
    ];
    if (
      config.compilerOptions?.rootDir !== 'src/server' ||
      config.compilerOptions?.outDir !== '.server' ||
      JSON.stringify([...config.include].sort()) !== JSON.stringify(expected)
    )
      refused('tsconfig.server.json');
    for (const input of expected)
      generated.set(
        `frontend/apps/web/.server/${posix.relative('src/server', input).replace(/\.ts$/, '.js')}`,
        `frontend/apps/web/${input}`,
      );
  }
  const sdkConfig = all.get('frontend/packages/sdk/tsconfig.native.json');
  if (sdkConfig) {
    const config = JSON.parse(sdkConfig);
    const expected = ['src/authenticated-server.ts', 'src/administration-server.ts'];
    if (
      config.compilerOptions?.rootDir !== 'src' ||
      config.compilerOptions?.outDir !== 'dist' ||
      JSON.stringify(config.include) !== JSON.stringify(expected)
    )
      refused('tsconfig.native.json');
    for (const input of expected)
      for (const extension of ['js', 'd.ts'])
        generated.set(
          'frontend/packages/sdk/dist/' + posix.basename(input, '.ts') + '.' + extension,
          'frontend/packages/sdk/' + input,
        );
  }
  const candidates = (base: string) => [
    base,
    ...['.ts', '.tsx', '.js', '.jsx', '.mts', '.cts', '.mjs', '.cjs', '.json'].flatMap((ext) => [
      base + ext,
      posix.join(base, 'index' + ext),
    ]),
    ...(/\.[cm]?jsx?$/.test(base)
      ? [base.replace(/\.[cm]?jsx?$/, '.ts'), base.replace(/\.[cm]?jsx?$/, '.tsx')]
      : []),
  ];
  const local = (base: string, from: string): string[] => {
    if (base === 'frontend/apps/web/src/app/globals.css') return all.has(base) ? [] : refused(from);
    base = generated.get(base) ?? base;
    // Conservative union: native Node and TypeScript need not choose the same candidate.
    const targets = candidates(base).filter((path) => all.has(path));
    if (
      !targets.length ||
      targets.some((target) => !sourceExtension.test(target) || declaration.test(target))
    )
      return refused(from);
    return targets;
  };
  const mapped = (value: unknown, root: string, from: string): string[] => {
    if (typeof value === 'string' && value.startsWith('./'))
      return local(posix.normalize(posix.join(root, value)), from);
    if (value && typeof value === 'object' && !Array.isArray(value)) {
      const values = Object.values(value);
      if (values.length) return values.flatMap((value) => mapped(value, root, from));
    }
    return refused(from);
  };
  const resolveEdge = (edge: string, path: string, anchors: string[] = []): string[] => {
    if (isBuiltin(edge)) return [];
    if (/^(?:file:|\/)/.test(edge)) return refused(path);
    if (edge.startsWith('.')) {
      if (anchors.length) return refused(path);
      const filename = fileURLToPath(new URL(edge, `file:///learnstack/${path}`));
      if (!filename.startsWith('/learnstack/')) return refused(path);
      return local(filename.slice('/learnstack/'.length), path);
    }
    // Match actual supported tsconfig paths rather than treating alias spelling as external.
    let directory = posix.dirname(path);
    while (directory !== '.') {
      const source = all.get(posix.join(directory, 'tsconfig.json'));
      if (source) {
        const config = JSON.parse(source);
        if (config.compilerOptions?.baseUrl) return refused(path);
        const aliases: string[] = [];
        for (const [pattern, replacements] of Object.entries(config.compilerOptions?.paths ?? {})) {
          const parts = pattern.split('*');
          if (parts.length > 2 || !Array.isArray(replacements) || replacements.length === 0)
            return refused(path);
          const match =
            parts.length === 1
              ? edge === pattern
              : edge.startsWith(parts[0]!) && edge.endsWith(parts[1]!);
          if (!match) continue;
          const capture =
            parts.length === 1 ? '' : edge.slice(parts[0]!.length, edge.length - parts[1]!.length);
          aliases.push(
            ...replacements.flatMap((value) => {
              if (
                typeof value !== 'string' ||
                !value.startsWith('./') ||
                value.split('*').length > 2
              )
                return refused(path);
              return local(
                posix.normalize(posix.join(directory, value.replace('*', capture))),
                path,
              );
            }),
          );
        }
        // Inherited alias maps are not silently ignored. The shipped inherited presets have none.
        const checkExtends = (name: unknown, owner: string, seen = new Set<string>()) => {
          if (name === undefined) return;
          if (typeof name !== 'string') return refused(path);
          const targets = name.startsWith('.')
            ? local(posix.normalize(posix.join(owner, name)), path)
            : packages.flatMap((pkg) =>
                name.startsWith(pkg.manifest.name + '/')
                  ? mapped(
                      pkg.manifest.exports?.['./' + name.slice(pkg.manifest.name.length + 1)],
                      pkg.root,
                      path,
                    )
                  : [],
              );
          if (!targets.length) return refused(path);
          for (const target of targets) {
            if (seen.has(target)) return refused(path);
            const inherited = JSON.parse(all.get(target)!);
            if (inherited.compilerOptions?.paths || inherited.compilerOptions?.baseUrl)
              return refused(path);
            checkExtends(inherited.extends, posix.dirname(target), new Set([...seen, target]));
          }
        };
        checkExtends(config.extends, directory);
        // Include every supported match instead of guessing TypeScript alias precedence.
        if (aliases.length) return aliases;
        break;
      }
      directory = posix.dirname(directory);
    }
    const matches = packages.filter(
      (pkg) => edge === pkg.manifest.name || edge.startsWith(pkg.manifest.name + '/'),
    );
    if (matches.length > 1) return refused(path);
    const pkg = matches[0];
    if (pkg) {
      const key =
        edge === pkg.manifest.name ? '.' : './' + edge.slice(pkg.manifest.name.length + 1);
      return mapped(
        pkg.manifest.exports?.[key] ?? (key === '.' ? pkg.manifest.main : undefined),
        pkg.root,
        path,
      );
    }
    // #imports and unmodeled aliases require explicit modeling; bare names are not an exemption.
    if (edge.startsWith('#') || !external) return refused(path);
    const target = external(path, edge, anchors);
    if (all.has(target)) return local(target, path);
    if (![...dependencyRoots].some((root) => target.startsWith(root + '/'))) return refused(path);
    return [];
  };
  const result = new Map(
    [...all].filter(
      ([path]) =>
        sourceExtension.test(path) &&
        !declaration.test(path) &&
        !testSource(path) &&
        !exemptEntries.has(path),
    ),
  );
  for (const [path, source] of result) {
    if (path.endsWith('.json')) continue;
    const file = ts.createSourceFile(path, source, ts.ScriptTarget.Latest, true);
    const imports: { name: string; anchors?: string[]; from?: string }[] = [];
    const requireOrigins = [
      'frontend/scripts/framework-installation.mjs',
      'frontend/scripts/test-framework-provenance.mjs',
      'frontend/apps/web/scripts/framework-router-generated-pipes.mjs',
    ].includes(path)
      ? frameworkRequireOrigins(file)
      : undefined;
    const modeledLoader =
      path === 'frontend/scripts/test-framework-provenance.mjs' ? 'actualRequire' : 'require';
    const requireOrigin = (node: ts.CallExpression): string | undefined => {
      if (!requireOrigins) return undefined;
      const callee = ts.isPropertyAccessExpression(node.expression)
        ? node.expression.expression
        : node.expression;
      if (!ts.isIdentifier(callee) || callee.text !== modeledLoader) return refused(path);
      for (let scope: ts.Node | undefined = node.parent; scope; scope = scope.parent)
        if ((ts.isBlock(scope) || ts.isSourceFile(scope)) && requireOrigins.has(scope))
          return requireOrigins.get(scope);
      return refused(path);
    };
    const proofHarness =
      path === 'frontend/apps/web/scripts/test-bff-proof-runtime-observation.mjs';
    const proofTargets = new Map([
      ['server/auth/request-runtime.js', 'frontend/apps/web/src/server/auth/request-runtime.ts'],
      ['test/page-response-fixture.js', 'frontend/apps/web/src/test/page-response-fixture.ts'],
    ]);
    const proofSeen = new Set<string>();
    // Both output-generating implementations are source-bound. Substring checks
    // cannot prove generated module provenance. Only checkout line endings are
    // normalized; changing either implementation requires an explicit pin review.
    // The imported targets below still follow current source, never pinned content.
    if (
      proofHarness &&
      (
        [
          [path, 'd217cc042e967a01a245ed4d1915a068baa697498d4e2cf397032b195ec432eb'],
          [
            'frontend/apps/web/scripts/bff-proof-host.mjs',
            'f725b730eb985254c80eeebe5e56a7b77d771c6cbec67f44318acc5c46b62c44',
          ],
        ] as const
      ).some(([target, expected]) => {
        const text = all.get(target);
        return (
          text === undefined ||
          createHash('sha256').update(text.replace(/\r\n/g, '\n'), 'utf8').digest('hex') !==
            expected
        );
      })
    )
      return refused(path);
    const resolveCall = (node: ts.Node): node is ts.CallExpression =>
      ts.isCallExpression(node) &&
      ts.isPropertyAccessExpression(node.expression) &&
      ts.isIdentifier(node.expression.expression) &&
      ['require', ...(requireOrigins ? [modeledLoader] : [])].includes(
        node.expression.expression.text,
      ) &&
      node.expression.name.text === 'resolve';
    const visit = (node: ts.Node) => {
      if (
        ((ts.isImportDeclaration(node) && !node.importClause?.isTypeOnly) ||
          (ts.isExportDeclaration(node) && !node.isTypeOnly)) &&
        node.moduleSpecifier &&
        ts.isStringLiteralLike(node.moduleSpecifier)
      )
        imports.push({ name: node.moduleSpecifier.text });
      if (
        ts.isImportEqualsDeclaration(node) &&
        !node.isTypeOnly &&
        ts.isExternalModuleReference(node.moduleReference)
      ) {
        const value = node.moduleReference.expression;
        if (!value || !ts.isStringLiteralLike(value)) return refused(path);
        imports.push({ name: value.text });
      }
      if (
        ts.isCallExpression(node) &&
        (node.expression.kind === ts.SyntaxKind.ImportKeyword ||
          (ts.isIdentifier(node.expression) &&
            ['require', ...(requireOrigins ? [modeledLoader] : [])].includes(
              node.expression.text,
            )) ||
          resolveCall(node))
      ) {
        const value = node.arguments[0];
        if (proofHarness && node.expression.kind === ts.SyntaxKind.ImportKeyword) {
          // Exact syntax: import(pathToFileURL(join(root, '<one closed generated path>'))).
          if (
            node.arguments.length !== 1 ||
            !value ||
            !ts.isCallExpression(value) ||
            !ts.isIdentifier(value.expression) ||
            value.expression.text !== 'pathToFileURL' ||
            value.arguments.length !== 1 ||
            !value.arguments[0] ||
            !ts.isCallExpression(value.arguments[0])
          )
            return refused(path);
          const joined = value.arguments[0];
          const rootArg = joined.arguments[0];
          const relativeArg = joined.arguments[1];
          if (
            !ts.isIdentifier(joined.expression) ||
            joined.expression.text !== 'join' ||
            joined.arguments.length !== 2 ||
            !rootArg ||
            !ts.isIdentifier(rootArg) ||
            rootArg.text !== 'root' ||
            !relativeArg ||
            !ts.isStringLiteral(relativeArg)
          )
            return refused(path);
          const target = proofTargets.get(relativeArg.text);
          if (!target || proofSeen.has(target) || !all.has(target)) return refused(path);
          proofSeen.add(target);
          imports.push({ name: './' + posix.relative(posix.dirname(path), target) });
          ts.forEachChild(node, visit);
          return;
        }
        if (value && resolveCall(value)) {
          ts.forEachChild(node, visit);
          return;
        }
        if (!value || !ts.isStringLiteralLike(value)) return refused(path);
        const anchors: string[] = [];
        if (node.arguments.length > 1) {
          const options = node.arguments[1];
          if (
            !resolveCall(node) ||
            value.text.startsWith('.') ||
            !options ||
            !ts.isObjectLiteralExpression(options) ||
            options.properties.length !== 1
          )
            return refused(path);
          const property = options.properties[0];
          if (
            !property ||
            !ts.isPropertyAssignment(property) ||
            property.name.getText(file) !== 'paths' ||
            !ts.isArrayLiteralExpression(property.initializer) ||
            !property.initializer.elements.length
          )
            return refused(path);
          for (const anchor of property.initializer.elements) {
            if (
              !resolveCall(anchor) ||
              anchor.arguments.length !== 1 ||
              !anchor.arguments[0] ||
              !ts.isStringLiteralLike(anchor.arguments[0])
            )
              return refused(path);
            anchors.push(anchor.arguments[0].text);
          }
        }
        imports.push({
          name: value.text,
          anchors,
          from:
            node.expression.kind === ts.SyntaxKind.ImportKeyword ? undefined : requireOrigin(node),
        });
      }
      ts.forEachChild(node, visit);
    };
    visit(file);
    if (proofHarness && proofSeen.size !== proofTargets.size) return refused(path);
    for (const edge of imports)
      for (const target of resolveEdge(edge.name, edge.from ?? path, edge.anchors))
        result.set(target, all.get(target)!);
  }
  return result;
}
