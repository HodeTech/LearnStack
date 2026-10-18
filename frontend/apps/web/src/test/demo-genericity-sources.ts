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
  'frontend/pnpm-workspace.yaml',
  'scripts/adr-status.py',
  'scripts/assert-tests-ran.py',
  'scripts/ci-native-environment.py',
  'scripts/ci-native-provisioning.py',
  'scripts/ci-native-tests.py',
  'scripts/connection-string.awk',
  'scripts/install-oasdiff.py',
  'scripts/openapi-ci.py',
  'scripts/openapi-policy.py',
  'scripts/seed.sh',
  'scripts/test-adr-status.py',
  'scripts/test-ci-native-environment.py',
  'scripts/test-ci-native-provisioning.py',
  'scripts/test-ci-native-tests.py',
  'scripts/test-openapi-ci.py',
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

/** Refuse new roots/formats until their production or non-runtime ownership is explicit. */
export function demoSourceClassification(path: string): 'runtime' | 'other' {
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
    const expected = ['src/server/ingress.ts', 'src/server/public-admission-runtime.ts'];
    if (
      config.compilerOptions?.rootDir !== 'src/server' ||
      config.compilerOptions?.outDir !== '.server' ||
      JSON.stringify([...config.include].sort()) !== JSON.stringify(expected)
    )
      refused('tsconfig.server.json');
    for (const input of expected)
      generated.set(
        `frontend/apps/web/.server/${posix.basename(input, '.ts')}.js`,
        `frontend/apps/web/${input}`,
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
    const imports: { name: string; anchors?: string[] }[] = [];
    const resolveCall = (node: ts.Node): node is ts.CallExpression =>
      ts.isCallExpression(node) &&
      ts.isPropertyAccessExpression(node.expression) &&
      ts.isIdentifier(node.expression.expression) &&
      node.expression.expression.text === 'require' &&
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
          (ts.isIdentifier(node.expression) && node.expression.text === 'require') ||
          resolveCall(node))
      ) {
        const value = node.arguments[0];
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
        imports.push({ name: value.text, anchors });
      }
      ts.forEachChild(node, visit);
    };
    visit(file);
    for (const edge of imports)
      for (const target of resolveEdge(edge.name, path, edge.anchors))
        result.set(target, all.get(target)!);
  }
  return result;
}
