import { posix } from 'node:path';

import ts from 'typescript';

/**
 * ADR-0053's source fences. This is a bounded AST analysis, not a JavaScript
 * interpreter: it follows static module paths, declaration aliases and constant
 * strings. Runtime reassignment, eval and third-party implementation bodies are
 * outside its claim. Unknown local module paths fail the graph census separately.
 */
export type SourceCensus = Readonly<Record<string, string>>;
export type Finding = { readonly file: string; readonly line: number; readonly reason: string };
export type SourceGraph = {
  readonly files: ReadonlyMap<string, ts.SourceFile>;
  readonly checker: ts.TypeChecker;
  readonly edges: ReadonlyMap<string, readonly string[]>;
  readonly unresolved: readonly Finding[];
  readonly resolveModule: (specifier: string, from: string) => string | undefined;
};

export const ADAPTER = 'apps/web/src/server/configured-public-client.ts';
export const INGRESS = 'apps/web/src/server/ingress.ts';
export const MIDDLEWARE = 'apps/web/src/middleware.ts';
export const PUBLIC_LAYOUT = 'apps/web/src/app/(public)/layout.tsx';
export const SDK_SERVER = 'packages/sdk/src/server.ts';
const HOP_HEADERS = new Set([
  'x-learnstack-host',
  'x-learnstack-hop-secret',
  'x-learnstack-visitor-address',
]);
const RAW_AUTHORITY_HEADERS = new Set([
  'host',
  'forwarded',
  'x-forwarded-host',
  'x-forwarded-for',
  'x-real-ip',
  ...HOP_HEADERS,
]);

export function walk(node: ts.Node, visitor: (node: ts.Node) => void): void {
  visitor(node);
  ts.forEachChild(node, (child) => walk(child, visitor));
}

function unwrapped(node: ts.Node): ts.Node {
  if (
    ts.isParenthesizedExpression(node) ||
    ts.isAsExpression(node) ||
    ts.isTypeAssertionExpression(node) ||
    ts.isNonNullExpression(node) ||
    ts.isSatisfiesExpression(node) ||
    ts.isAwaitExpression(node)
  )
    return unwrapped(node.expression);
  return node;
}

function declarations(node: ts.Node, checker: ts.TypeChecker): readonly ts.Declaration[] {
  if (!ts.isIdentifier(node)) return [];
  let symbol = checker.getSymbolAtLocation(node);
  if (symbol && symbol.flags & ts.SymbolFlags.Alias) symbol = checker.getAliasedSymbol(symbol);
  return symbol?.declarations ?? [];
}

export function constantString(
  node: ts.Node | undefined,
  checker: ts.TypeChecker,
  visited = new Set<ts.Node>(),
): string | undefined {
  if (!node || visited.has(node)) return undefined;
  visited.add(node);
  node = unwrapped(node);
  if (ts.isStringLiteralLike(node)) return node.text;
  if (ts.isBinaryExpression(node) && node.operatorToken.kind === ts.SyntaxKind.PlusToken) {
    const left = constantString(node.left, checker, new Set(visited));
    const right = constantString(node.right, checker, new Set(visited));
    return left === undefined || right === undefined ? undefined : left + right;
  }
  for (const declaration of declarations(node, checker))
    if (
      ts.isVariableDeclaration(declaration) &&
      ts.isVariableDeclarationList(declaration.parent) &&
      declaration.parent.flags & ts.NodeFlags.Const
    ) {
      const value = constantString(declaration.initializer, checker, new Set(visited));
      if (value !== undefined) return value;
    }
  return undefined;
}

function propertyName(node: ts.PropertyName, checker: ts.TypeChecker): string | undefined {
  if (ts.isIdentifier(node)) return node.text;
  return constantString(ts.isComputedPropertyName(node) ? node.expression : node, checker);
}

function memberName(node: ts.Node, checker: ts.TypeChecker): string | undefined {
  node = unwrapped(node);
  if (ts.isPropertyAccessExpression(node)) return node.name.text;
  if (ts.isElementAccessExpression(node)) return constantString(node.argumentExpression, checker);
  return undefined;
}

function memberReceiver(node: ts.Node): ts.Expression | undefined {
  node = unwrapped(node);
  return ts.isPropertyAccessExpression(node) || ts.isElementAccessExpression(node)
    ? node.expression
    : undefined;
}

function objectBinding(
  node: ts.BindingElement,
  checker: ts.TypeChecker,
): { readonly name: string; readonly receiver: ts.Expression } | undefined {
  if (node.dotDotDotToken || !ts.isObjectBindingPattern(node.parent)) return undefined;
  const declaration = node.parent.parent;
  const property = node.propertyName ?? (ts.isIdentifier(node.name) ? node.name : undefined);
  const name = property ? propertyName(property, checker) : undefined;
  return name && ts.isVariableDeclaration(declaration) && declaration.initializer
    ? { name, receiver: declaration.initializer }
    : undefined;
}

function globalObject(
  node: ts.Node,
  checker: ts.TypeChecker,
  visited = new Set<ts.Node>(),
): boolean {
  node = unwrapped(node);
  if (visited.has(node)) return false;
  visited.add(node);
  if (ts.isIdentifier(node)) {
    const bindings = declarations(node, checker);
    if (['globalThis', 'window', 'self', 'global'].includes(node.text) && bindings.length === 0)
      return true;
    return bindings.some(
      (declaration) =>
        ts.isVariableDeclaration(declaration) &&
        declaration.initializer !== undefined &&
        globalObject(declaration.initializer, checker, visited),
    );
  }
  return false;
}

function globalFetch(node: ts.Node, graph: SourceGraph, visited = new Set<ts.Node>()): boolean {
  const { checker } = graph;
  node = unwrapped(node);
  if (visited.has(node)) return false;
  visited.add(node);
  const receiver = memberReceiver(node);
  if (
    receiver &&
    ['bind', 'call', 'apply'].includes(memberName(node, checker) ?? '') &&
    globalFetch(receiver, graph, visited)
  )
    return true;
  if (ts.isCallExpression(node) && memberName(node.expression, checker) === 'bind')
    return globalFetch(node.expression, graph, visited);
  if (receiver && memberName(node, checker) === 'fetch' && globalObject(receiver, checker))
    return true;
  if (!ts.isIdentifier(node)) return false;
  const bindings = new Set([
    ...(checker.getSymbolAtLocation(node)?.declarations ?? []),
    ...declarations(node, checker),
  ]);
  if (node.text === 'fetch' && bindings.size === 0) return true;
  return [...bindings].some((declaration) => {
    if (ts.isExportAssignment(declaration))
      return globalFetch(declaration.expression, graph, visited);
    if (ts.isImportClause(declaration) && !declaration.isTypeOnly) {
      const statement = declaration.parent;
      const specifier = constantString(statement.moduleSpecifier, checker);
      const fileName = specifier
        ? graph.resolveModule(specifier, statement.getSourceFile().fileName)
        : undefined;
      return (
        (fileName ? graph.files.get(fileName)?.statements : [])?.some(
          (exported) =>
            ts.isExportAssignment(exported) &&
            !exported.isExportEquals &&
            globalFetch(exported.expression, graph, visited),
        ) ?? false
      );
    }
    if (ts.isVariableDeclaration(declaration) && declaration.initializer)
      return globalFetch(declaration.initializer, graph, visited);
    if (ts.isBindingElement(declaration) && ts.isObjectBindingPattern(declaration.parent)) {
      const container = declaration.parent.parent;
      return (
        ts.isVariableDeclaration(container) &&
        container.initializer !== undefined &&
        (declaration.propertyName
          ? propertyName(declaration.propertyName, checker)
          : ts.isIdentifier(declaration.name)
            ? declaration.name.text
            : undefined) === 'fetch' &&
        globalObject(container.initializer, checker)
      );
    }
    return false;
  });
}

function finding(node: ts.Node, reason: string): Finding {
  const file = node.getSourceFile();
  return {
    file: file.fileName,
    line: file.getLineAndCharacterOfPosition(node.getStart()).line + 1,
    reason,
  };
}

function runtimeImport(node: ts.ImportDeclaration): boolean {
  // Conservative source policy: tsc verbatim emission retains inline type-only
  // edges; pinned Next SWC erases them. Declaration-level type edges are excluded.
  return !node.importClause?.isTypeOnly;
}

function runtimeExport(node: ts.ExportDeclaration): boolean {
  return !node.isTypeOnly;
}

export type ResolutionConfig = {
  readonly baseUrl?: string;
  readonly pathsBasePath?: string;
  readonly paths?: Readonly<Record<string, readonly string[]>>;
};
export type WorkspaceEntry = {
  readonly name?: string;
  readonly main?: string;
  readonly exports?: unknown;
};

/** Validate the real inherited configuration against this deliberately bounded resolver. */
export function resolutionCensusFindings(
  configs: Readonly<Record<string, ResolutionConfig>>,
  packages: Readonly<Record<string, WorkspaceEntry>>,
): Finding[] {
  const findings: Finding[] = [];
  for (const [file, config] of Object.entries(configs)) {
    const web = file.startsWith('apps/web/');
    if (
      config.baseUrl !== undefined ||
      JSON.stringify(config.paths ?? {}) !== JSON.stringify(web ? { '@/*': ['./src/*'] } : {}) ||
      (config.paths !== undefined && config.pathsBasePath !== 'apps/web')
    )
      findings.push({
        file,
        line: 1,
        reason:
          'Unsupported inherited module aliases: update the bounded source resolver and its controls.',
      });
  }
  for (const [directory, entry] of Object.entries(packages)) {
    const file = `${directory}/package.json`;
    const exports = entry.exports;
    if (
      entry.name !== '@learnstack/' + directory.split('/').at(-1) ||
      typeof exports !== 'object' ||
      exports === null ||
      Array.isArray(exports) ||
      !Object.hasOwn(exports, '.') ||
      Object.entries(exports).some(([key, value]) => {
        const subpath = key === '.' ? 'index' : key.startsWith('./') ? key.slice(2) : undefined;
        return (
          !subpath ||
          subpath.includes('*') ||
          (value !== `./src/${subpath}.ts` && value !== `./src/${subpath}.tsx`)
        );
      }) ||
      (entry.main !== undefined && entry.main !== (exports as Record<string, unknown>)['.'])
    )
      findings.push({
        file,
        line: 1,
        reason:
          'Unsupported workspace exports: update the bounded source resolver and its controls.',
      });
  }
  return findings;
}

export function unsupportedSourceFindings(names: readonly string[]): Finding[] {
  return names
    .filter((name) => /\.(?:mts|cts)$/.test(name) && !/\.(?:test|spec|d)\.(?:mts|cts)$/.test(name))
    .map((file) => ({
      file,
      line: 1,
      reason:
        'Unsupported production module extension: include it in the source census and resolver before use.',
    }));
}

export function buildSourceGraph(sources: SourceCensus): SourceGraph {
  const files = new Map(
    Object.entries(sources).map(([name, source]) => [
      name,
      ts.createSourceFile(
        name,
        source,
        ts.ScriptTarget.ES2022,
        true,
        name.endsWith('.tsx') ? ts.ScriptKind.TSX : ts.ScriptKind.TS,
      ),
    ]),
  );
  const resolveModule = (specifier: string, from: string): string | undefined => {
    let base: string;
    if (specifier.startsWith('.'))
      base = posix.normalize(posix.join(posix.dirname(from), specifier));
    else if (specifier.startsWith('@/')) base = 'apps/web/src/' + specifier.slice(2);
    else if (specifier.startsWith('@learnstack/')) {
      const [pkg, ...path] = specifier.slice('@learnstack/'.length).split('/');
      base = `packages/${pkg}/src/${path.length === 0 ? 'index' : path.join('/')}`;
    } else return undefined;
    return [base, base + '.ts', base + '.tsx', base + '/index.ts', base + '/index.tsx'].find(
      (name) => files.has(name),
    );
  };
  const options: ts.CompilerOptions = {
    noLib: true,
    target: ts.ScriptTarget.ES2022,
    jsx: ts.JsxEmit.Preserve,
  };
  const host: ts.CompilerHost = {
    getSourceFile: (name) => files.get(name),
    getDefaultLibFileName: () => '',
    writeFile: () => {},
    getCurrentDirectory: () => '',
    getDirectories: () => [],
    fileExists: (name) => files.has(name),
    readFile: (name) => sources[name],
    getCanonicalFileName: (name) => name,
    useCaseSensitiveFileNames: () => true,
    getNewLine: () => '\n',
    resolveModuleNames: (names, from) =>
      names.map((name) => {
        const resolved = resolveModule(name, from);
        return resolved ? { resolvedFileName: resolved } : undefined;
      }),
  };
  const checker = ts.createProgram([...files.keys()], options, host).getTypeChecker();
  const edges = new Map<string, string[]>();
  const unresolved: Finding[] = [];
  for (const [name, file] of files) {
    const targets: string[] = [];
    const add = (expression: ts.Node | undefined, node: ts.Node) => {
      const specifier = constantString(expression, checker);
      if (specifier === undefined) {
        unresolved.push(
          finding(node, 'Non-static module path: use a literal or constant local module path.'),
        );
        return;
      }
      const resolved = resolveModule(specifier, name);
      if (resolved) targets.push(resolved);
      else if (specifier.endsWith('.css'))
        return; // Inert stylesheet, never a JS authority/caching module.
      else if (
        specifier.startsWith('.') ||
        specifier.startsWith('@/') ||
        specifier.startsWith('@learnstack/')
      )
        unresolved.push(
          finding(
            node,
            `Unresolved local module ${specifier}: include its production source in the census.`,
          ),
        );
    };
    walk(file, (node) => {
      if (ts.isImportDeclaration(node) && runtimeImport(node)) add(node.moduleSpecifier, node);
      else if (ts.isExportDeclaration(node) && node.moduleSpecifier && runtimeExport(node))
        add(node.moduleSpecifier, node);
      else if (
        ts.isImportEqualsDeclaration(node) &&
        !node.isTypeOnly &&
        ts.isExternalModuleReference(node.moduleReference)
      )
        add(node.moduleReference.expression, node);
      else if (
        ts.isCallExpression(node) &&
        (node.expression.kind === ts.SyntaxKind.ImportKeyword ||
          (ts.isIdentifier(node.expression) && node.expression.text === 'require'))
      )
        add(node.arguments[0], node);
    });
    edges.set(name, [...new Set(targets)].sort());
  }
  return { files, checker, edges, unresolved, resolveModule };
}

export function reachable(graph: SourceGraph, roots: readonly string[]): string[] {
  const seen = new Set<string>();
  const visit = (name: string) => {
    if (seen.has(name) || !graph.files.has(name)) return;
    seen.add(name);
    for (const next of graph.edges.get(name) ?? []) visit(next);
  };
  roots.forEach(visit);
  return [...seen].sort();
}

export function hasDirective(file: ts.SourceFile, directive: string): boolean {
  for (const statement of file.statements) {
    if (!ts.isExpressionStatement(statement) || !ts.isStringLiteral(statement.expression)) break;
    if (statement.expression.text === directive) return true;
  }
  return false;
}

export function hasServerOnlyMarker(file: ts.SourceFile): boolean {
  return file.statements.some(
    (node) =>
      ts.isImportDeclaration(node) &&
      runtimeImport(node) &&
      ts.isStringLiteral(node.moduleSpecifier) &&
      node.moduleSpecifier.text === 'server-only',
  );
}

export function publicLayoutPolicyFindings(graph: SourceGraph): Finding[] {
  const file = graph.files.get(PUBLIC_LAYOUT);
  if (!file)
    return [
      {
        file: PUBLIC_LAYOUT,
        line: 1,
        reason: 'Fix: retain the public layout and its dynamic/no-store policy.',
      },
    ];
  const settings = new Map<string, ts.Expression>();
  for (const statement of file.statements) {
    if (
      !ts.isVariableStatement(statement) ||
      !statement.modifiers?.some((modifier) => modifier.kind === ts.SyntaxKind.ExportKeyword)
    )
      continue;
    for (const declaration of statement.declarationList.declarations)
      if (ts.isIdentifier(declaration.name) && declaration.initializer)
        settings.set(declaration.name.text, declaration.initializer);
  }
  const findings: Finding[] = [];
  for (const [name, expected] of [
    ['dynamic', 'force-dynamic'],
    ['fetchCache', 'force-no-store'],
  ] as const)
    if (constantString(settings.get(name), graph.checker) !== expected)
      findings.push(finding(file, `Fix: export ${name} = '${expected}' from the public layout.`));
  const revalidate = settings.get('revalidate');
  if (!revalidate || !ts.isNumericLiteral(revalidate) || Number(revalidate.text) !== 0)
    findings.push(finding(file, 'Fix: export revalidate = 0 from the public layout.'));
  return findings;
}

export function transportFindings(graph: SourceGraph): Finding[] {
  const findings: Finding[] = [];
  for (const [name, file] of graph.files) {
    if (name === ADAPTER) continue;
    walk(file, (node) => {
      if (ts.isCallExpression(node) && globalFetch(node.expression, graph))
        findings.push(
          finding(node, 'Fix: call the configured public SDK adapter instead of global fetch.'),
        );
      if (
        (ts.isVariableDeclaration(node) || ts.isParameter(node) || ts.isPropertyAssignment(node)) &&
        node.initializer &&
        globalFetch(node.initializer, graph)
      )
        findings.push(
          finding(
            node,
            'Fix: inject transport; do not store or default to global fetch outside the configured adapter.',
          ),
        );
      if (ts.isExportAssignment(node) && globalFetch(node.expression, graph))
        findings.push(
          finding(
            node,
            'Fix: inject transport; do not export global fetch outside the configured adapter.',
          ),
        );
      let key: string | undefined;
      if (ts.isPropertyAssignment(node) || ts.isShorthandPropertyAssignment(node))
        key = propertyName(node.name, graph.checker);
      if (ts.isBinaryExpression(node) && node.operatorToken.kind === ts.SyntaxKind.EqualsToken)
        key = memberName(node.left, graph.checker);
      if (
        ts.isCallExpression(node) &&
        ['set', 'append'].includes(memberName(node.expression, graph.checker) ?? '')
      )
        key = constantString(node.arguments[0], graph.checker);
      // Headers' iterable initializer is another ordinary setter spelling.
      if (
        ts.isNewExpression(node) &&
        ts.isIdentifier(node.expression) &&
        node.expression.text === 'Headers'
      ) {
        const pairs = node.arguments?.[0];
        if (pairs && ts.isArrayLiteralExpression(pairs))
          for (const pair of pairs.elements)
            if (ts.isArrayLiteralExpression(pair)) {
              const header = constantString(pair.elements[0], graph.checker);
              if (header && HOP_HEADERS.has(header.toLowerCase()))
                findings.push(
                  finding(pair, 'Fix: construct API-hop headers only in the configured adapter.'),
                );
            }
      }
      if (key && HOP_HEADERS.has(key.toLowerCase()))
        findings.push(
          finding(node, 'Fix: construct API-hop headers only in the configured adapter.'),
        );
    });
  }
  return findings;
}

function processObject(
  node: ts.Node,
  checker: ts.TypeChecker,
  visited = new Set<ts.Node>(),
): boolean {
  node = unwrapped(node);
  if (visited.has(node)) return false;
  visited.add(node);
  if (ts.isIdentifier(node) && node.text === 'process' && declarations(node, checker).length === 0)
    return true;
  return declarations(node, checker).some(
    (declaration) =>
      ts.isVariableDeclaration(declaration) &&
      declaration.initializer !== undefined &&
      processObject(declaration.initializer, checker, visited),
  );
}

function environmentCollection(
  node: ts.Node,
  checker: ts.TypeChecker,
  visited = new Set<ts.Node>(),
): boolean {
  node = unwrapped(node);
  if (visited.has(node)) return false;
  visited.add(node);
  const receiver = memberReceiver(node);
  if (memberName(node, checker) === 'env' && receiver && processObject(receiver, checker))
    return true;
  return declarations(node, checker).some((declaration) => {
    if (ts.isBindingElement(declaration)) {
      const binding = objectBinding(declaration, checker);
      return binding?.name === 'env' && processObject(binding.receiver, checker);
    }
    return (
      ts.isVariableDeclaration(declaration) &&
      declaration.initializer !== undefined &&
      environmentCollection(declaration.initializer, checker, visited)
    );
  });
}

export function privateServerFiles(graph: SourceGraph): string[] {
  return [...graph.files]
    .filter(([name, file]) => {
      if (name.includes('/src/server/') || name === SDK_SERVER || hasServerOnlyMarker(file))
        return true;
      let privateEnvironment = false;
      walk(file, (node) => {
        const receiver = memberReceiver(node);
        if (
          receiver &&
          environmentCollection(receiver, graph.checker) &&
          (memberName(node, graph.checker) ?? '').startsWith('LEARNSTACK_PUBLIC_')
        )
          privateEnvironment = true;
        if (ts.isBindingElement(node)) {
          const binding = objectBinding(node, graph.checker);
          if (
            binding?.name.startsWith('LEARNSTACK_PUBLIC_') &&
            environmentCollection(binding.receiver, graph.checker)
          )
            privateEnvironment = true;
        }
      });
      return privateEnvironment;
    })
    .map(([name]) => name)
    .sort();
}

export function clientBoundaryFindings(graph: SourceGraph): Finding[] {
  const privateFiles = new Set(privateServerFiles(graph));
  return [...graph.files].flatMap(([name, file]) =>
    hasDirective(file, 'use client')
      ? reachable(graph, [name])
          .filter((target) => privateFiles.has(target))
          .map((target) => ({
            file: name,
            line: 1,
            reason: `Fix: pass public data through server props; client imports reach private module ${target}.`,
          }))
      : [],
  );
}

type ModuleValueOrigin =
  | { readonly kind: 'external'; readonly specifier: string; readonly name: string }
  | { readonly kind: 'namespace'; readonly specifier: string; readonly from: string };

type ValueResolution = {
  readonly nodes: Set<ts.Node>;
  readonly exports: Set<string>;
};

function namespaceMemberOrigins(
  origins: readonly ModuleValueOrigin[],
  member: string,
  graph: SourceGraph,
  resolution: ValueResolution,
): ModuleValueOrigin[] {
  return origins.flatMap((origin) =>
    origin.kind === 'namespace'
      ? moduleValueOrigins(origin.specifier, origin.from, member, graph, resolution)
      : [],
  );
}

function declarationValueOrigins(
  declaration: ts.Node,
  graph: SourceGraph,
  resolution: ValueResolution,
): ModuleValueOrigin[] {
  if (ts.isNamespaceImport(declaration) || ts.isNamespaceExport(declaration)) {
    const statement = ts.isNamespaceImport(declaration)
      ? declaration.parent.parent
      : declaration.parent;
    if (
      (!ts.isImportDeclaration(statement) && !ts.isExportDeclaration(statement)) ||
      (ts.isImportDeclaration(statement) && statement.importClause?.isTypeOnly) ||
      (ts.isExportDeclaration(statement) && statement.isTypeOnly)
    )
      return [];
    const specifier = constantString(statement.moduleSpecifier, graph.checker);
    return specifier === undefined
      ? []
      : [{ kind: 'namespace', specifier, from: statement.getSourceFile().fileName }];
  }
  if (
    ts.isImportSpecifier(declaration) ||
    ts.isExportSpecifier(declaration) ||
    ts.isImportClause(declaration)
  )
    return bindingValueOrigins(declaration, graph, resolution);
  if (ts.isExportAssignment(declaration))
    return valueOrigins(declaration.expression, graph, resolution);
  if (ts.isVariableDeclaration(declaration) && declaration.initializer)
    return valueOrigins(declaration.initializer, graph, resolution);
  if (ts.isBindingElement(declaration)) {
    const binding = objectBinding(declaration, graph.checker);
    return binding
      ? namespaceMemberOrigins(
          valueOrigins(binding.receiver, graph, resolution),
          binding.name,
          graph,
          resolution,
        )
      : [];
  }
  return [];
}

function valueOrigins(
  node: ts.Node,
  graph: SourceGraph,
  resolution: ValueResolution,
): ModuleValueOrigin[] {
  node = unwrapped(node);
  if (resolution.nodes.has(node)) return [];
  resolution.nodes.add(node);
  try {
    if (ts.isIdentifier(node)) {
      // Preserve the original import: external bodies and erased type bindings are opaque.
      const bindings = graph.checker.getSymbolAtLocation(node)?.declarations ?? [];
      return bindings.flatMap((declaration) =>
        declarationValueOrigins(declaration, graph, resolution),
      );
    }
    const receiver = memberReceiver(node);
    const member = memberName(node, graph.checker);
    return receiver && member !== undefined
      ? namespaceMemberOrigins(valueOrigins(receiver, graph, resolution), member, graph, resolution)
      : [];
  } finally {
    resolution.nodes.delete(node);
  }
}

function bindingValueOrigins(
  binding: ts.ImportSpecifier | ts.ExportSpecifier | ts.ImportClause,
  graph: SourceGraph,
  resolution: ValueResolution,
): ModuleValueOrigin[] {
  if (binding.isTypeOnly) return [];
  const statement = ts.isImportClause(binding)
    ? binding.parent
    : ts.isImportSpecifier(binding)
      ? binding.parent.parent.parent
      : binding.parent.parent;
  if (!ts.isImportDeclaration(statement) && !ts.isExportDeclaration(statement)) return [];
  if (
    (ts.isExportDeclaration(statement) && statement.isTypeOnly) ||
    (ts.isImportDeclaration(statement) && statement.importClause?.isTypeOnly)
  )
    return [];
  if (!statement.moduleSpecifier && ts.isExportSpecifier(binding)) {
    const target = graph.checker.getExportSpecifierLocalTargetSymbol(binding);
    return (target?.declarations ?? []).flatMap((declaration) =>
      declarationValueOrigins(declaration, graph, resolution),
    );
  }
  const specifier = constantString(statement.moduleSpecifier, graph.checker);
  const name = ts.isImportClause(binding) ? 'default' : (binding.propertyName ?? binding.name).text;
  return specifier === undefined
    ? []
    : moduleValueOrigins(specifier, statement.getSourceFile().fileName, name, graph, resolution);
}

function explicitValueExports(file: ts.SourceFile, name: string): ts.Node[] {
  const exports: ts.Node[] = [];
  for (const statement of file.statements) {
    if (ts.isExportAssignment(statement)) {
      if (name === 'default' && !statement.isExportEquals) exports.push(statement);
      continue;
    }
    if (ts.isExportDeclaration(statement)) {
      if (!runtimeExport(statement) || !statement.exportClause) continue;
      if (ts.isNamedExports(statement.exportClause))
        exports.push(
          ...statement.exportClause.elements.filter(
            (item) => item.name.text === name && !item.isTypeOnly,
          ),
        );
      else if (statement.exportClause.name.text === name) exports.push(statement.exportClause);
      continue;
    }
    const modifiers = ts.canHaveModifiers(statement) ? ts.getModifiers(statement) : undefined;
    if (
      !modifiers?.some((modifier) => modifier.kind === ts.SyntaxKind.ExportKeyword) ||
      modifiers.some((modifier) => modifier.kind === ts.SyntaxKind.DeclareKeyword)
    )
      continue;
    if (ts.isVariableStatement(statement)) {
      for (const declaration of statement.declarationList.declarations) {
        if (ts.isIdentifier(declaration.name)) {
          if (declaration.name.text === name) exports.push(declaration);
        } else
          walk(declaration.name, (node) => {
            if (ts.isBindingElement(node) && ts.isIdentifier(node.name) && node.name.text === name)
              exports.push(node);
          });
      }
    } else if (
      ts.isFunctionDeclaration(statement) ||
      ts.isClassDeclaration(statement) ||
      ts.isEnumDeclaration(statement) ||
      ts.isModuleDeclaration(statement)
    ) {
      const exportedName = modifiers.some(
        (modifier) => modifier.kind === ts.SyntaxKind.DefaultKeyword,
      )
        ? 'default'
        : statement.name?.text;
      if (exportedName === name) exports.push(statement);
    }
  }
  return exports;
}

/** Static ESM values only: aliases, namespaces and exports; never function bodies or object tables. */
function moduleValueOrigins(
  specifier: string,
  from: string,
  name: string,
  graph: SourceGraph,
  resolution: ValueResolution,
): ModuleValueOrigin[] {
  const fileName = graph.resolveModule(specifier, from);
  const file = fileName ? graph.files.get(fileName) : undefined;
  if (!file) return [{ kind: 'external', specifier, name }];
  const query = file.fileName + '\0' + name;
  if (resolution.exports.has(query)) return [];
  resolution.exports.add(query);
  try {
    const explicit = explicitValueExports(file, name);
    // A pure explicit export still overrides every same-named star export.
    if (explicit.length > 0)
      return explicit.flatMap((declaration) =>
        declarationValueOrigins(declaration, graph, resolution),
      );
    // ESM export stars never forward a default export.
    if (name === 'default') return [];
    return file.statements.flatMap((statement) => {
      if (!ts.isExportDeclaration(statement) || !runtimeExport(statement) || statement.exportClause)
        return [];
      const next = constantString(statement.moduleSpecifier, graph.checker);
      return next === undefined
        ? []
        : moduleValueOrigins(next, file.fileName, name, graph, resolution);
    });
  } finally {
    resolution.exports.delete(query);
  }
}

function importedFunction(
  node: ts.Node,
  graph: SourceGraph,
  modules: readonly string[],
  names: readonly string[],
): boolean {
  return valueOrigins(node, graph, { nodes: new Set(), exports: new Set() }).some(
    (origin) =>
      origin.kind === 'external' &&
      modules.includes(origin.specifier) &&
      names.includes(origin.name),
  );
}

function moduleScope(node: ts.Node): boolean {
  for (let parent: ts.Node | undefined = node.parent; parent; parent = parent.parent)
    if (ts.isFunctionLike(parent) || ts.isClassLike(parent)) return false;
  return true;
}

function globalCollection(
  node: ts.Node,
  checker: ts.TypeChecker,
  visited = new Set<ts.Node>(),
): boolean {
  node = unwrapped(node);
  if (visited.has(node)) return false;
  visited.add(node);
  const receiver = memberReceiver(node);
  if (
    receiver &&
    ['Map', 'WeakMap'].includes(memberName(node, checker) ?? '') &&
    globalObject(receiver, checker)
  )
    return true;
  if (!ts.isIdentifier(node)) return false;
  const bindings = declarations(node, checker);
  if (['Map', 'WeakMap'].includes(node.text) && bindings.length === 0) return true;
  return bindings.some(
    (declaration) =>
      ts.isVariableDeclaration(declaration) &&
      declaration.initializer !== undefined &&
      globalCollection(declaration.initializer, checker, visited),
  );
}

export function cacheFindings(graph: SourceGraph, subjects: readonly string[]): Finding[] {
  const findings: Finding[] = [];
  for (const name of subjects) {
    const file = graph.files.get(name);
    if (!file) continue;
    walk(file, (node) => {
      if (
        (ts.isVariableDeclaration(node) &&
          ts.isIdentifier(node.name) &&
          node.name.text === 'generateStaticParams') ||
        (ts.isFunctionDeclaration(node) && node.name?.text === 'generateStaticParams') ||
        (ts.isExportSpecifier(node) &&
          !node.isTypeOnly &&
          ts.isNamedExports(node.parent) &&
          ts.isExportDeclaration(node.parent.parent) &&
          !node.parent.parent.isTypeOnly &&
          node.name.text === 'generateStaticParams')
      )
        findings.push(
          finding(node, 'Fix: remove generateStaticParams from the dynamic public graph.'),
        );
      const assignment =
        ts.isBinaryExpression(node) && node.operatorToken.kind === ts.SyntaxKind.EqualsToken;
      if (ts.isPropertyAssignment(node) || ts.isVariableDeclaration(node) || assignment) {
        const key = ts.isPropertyAssignment(node)
          ? propertyName(node.name, graph.checker)
          : ts.isBinaryExpression(node)
            ? memberName(node.left, graph.checker)
            : ts.isIdentifier(node.name)
              ? node.name.text
              : undefined;
        const initializer = ts.isBinaryExpression(node) ? node.right : node.initializer;
        const value = constantString(initializer, graph.checker);
        if (
          (key === 'cache' && value === 'force-cache') ||
          (key === 'dynamic' && value === 'force-static') ||
          (key === 'fetchCache' &&
            ['force-cache', 'only-cache', 'default-cache'].includes(value ?? ''))
        )
          findings.push(
            finding(node, 'Fix: use force-dynamic and no-store throughout the public graph.'),
          );
        if (
          key === 'revalidate' &&
          initializer &&
          !(ts.isNumericLiteral(initializer) && Number(initializer.text) === 0)
        )
          findings.push(
            finding(
              node,
              'Fix: public revalidate must be literal zero; shared revalidation is forbidden.',
            ),
          );
      }
      if (
        ts.isCallExpression(node) &&
        importedFunction(node.expression, graph, ['next/cache'], ['unstable_cache'])
      )
        findings.push(
          finding(
            node,
            'Fix: remove unstable_cache; public data reuse must stay inside the incoming request.',
          ),
        );
      if (
        ts.isNewExpression(node) &&
        globalCollection(node.expression, graph.checker) &&
        moduleScope(node)
      )
        findings.push(
          finding(
            node,
            'Fix: allocate public data collections inside the request, never at module scope.',
          ),
        );
      if (
        ts.isVariableDeclaration(node) &&
        moduleScope(node) &&
        /(?:bootstrap|site|data|route|response).*(?:cache|cached|promise)|(?:cache|cached|promise).*(?:bootstrap|site|data|route|response)/i.test(
          node.name.getText(file),
        ) &&
        !(
          node.initializer &&
          ts.isCallExpression(node.initializer) &&
          importedFunction(node.initializer.expression, graph, ['react'], ['cache'])
        )
      )
        findings.push(finding(node, 'Fix: remove the shared public bootstrap/data/route cache.'));
      if (
        ts.isCallExpression(node) &&
        moduleScope(node) &&
        ['getSite', 'getCourses', 'getCourse', 'getLesson'].includes(
          memberName(node.expression, graph.checker) ?? '',
        )
      )
        findings.push(
          finding(
            node,
            'Fix: start public SDK reads inside the incoming request, never retain a module-scope response promise.',
          ),
        );
    });
  }
  return findings;
}

function requestUrl(node: ts.Node, checker: ts.TypeChecker, visited = new Set<ts.Node>()): boolean {
  node = unwrapped(node);
  if (visited.has(node)) return false;
  visited.add(node);
  if (memberName(node, checker) === 'nextUrl') return true;
  if (
    ts.isNewExpression(node) &&
    ts.isIdentifier(node.expression) &&
    node.expression.text === 'URL'
  ) {
    const input = node.arguments?.[0];
    if (input && memberName(input, checker) === 'url') return true;
    return input !== undefined && requestUrl(input, checker, visited);
  }
  return declarations(node, checker).some((declaration) => {
    if (ts.isBindingElement(declaration))
      return objectBinding(declaration, checker)?.name === 'nextUrl';
    return (
      ts.isVariableDeclaration(declaration) &&
      declaration.initializer !== undefined &&
      requestUrl(declaration.initializer, checker, visited)
    );
  });
}

function globalBuiltin(
  node: ts.Node,
  name: string,
  checker: ts.TypeChecker,
  visited = new Set<ts.Node>(),
): boolean {
  node = unwrapped(node);
  if (visited.has(node)) return false;
  visited.add(node);
  const receiver = memberReceiver(node);
  const dot = name.lastIndexOf('.');
  if (receiver && memberName(node, checker) === name.slice(dot + 1)) {
    if (
      dot < 0
        ? globalObject(receiver, checker)
        : globalBuiltin(receiver, name.slice(0, dot), checker, new Set(visited))
    )
      return true;
  }
  if (ts.isIdentifier(node) && node.text === name && declarations(node, checker).length === 0)
    return true;
  return declarations(node, checker).some((declaration) => {
    if (ts.isVariableDeclaration(declaration) && declaration.initializer)
      return globalBuiltin(declaration.initializer, name, checker, new Set(visited));
    if (ts.isBindingElement(declaration) && dot >= 0) {
      const binding = objectBinding(declaration, checker);
      return (
        binding?.name === name.slice(dot + 1) &&
        globalBuiltin(binding.receiver, name.slice(0, dot), checker, new Set(visited))
      );
    }
    return false;
  });
}

type HeaderOrigin = 'headers' | 'record' | undefined;

function headerOrigin(
  node: ts.Node | undefined,
  graph: SourceGraph,
  visited = new Set<ts.Node>(),
): HeaderOrigin {
  if (!node) return undefined;
  node = unwrapped(node);
  if (visited.has(node)) return undefined;
  visited.add(node);
  const { checker } = graph;
  if (memberName(node, checker) === 'headers') return 'headers';
  if (
    ts.isCallExpression(node) &&
    (importedFunction(node.expression, graph, ['next/headers'], ['headers']) ||
      (ts.isIdentifier(node.expression) && node.expression.text === 'headers'))
  )
    return 'headers';
  if (ts.isIdentifier(node) && node.text === 'headers' && declarations(node, checker).length === 0)
    return 'headers';
  if (
    ts.isNewExpression(node) &&
    globalBuiltin(node.expression, 'Headers', checker) &&
    headerOrigin(node.arguments?.[0], graph, new Set(visited))
  )
    return 'headers';
  if (ts.isCallExpression(node) && globalBuiltin(node.expression, 'Object.fromEntries', checker)) {
    const input = node.arguments[0] ? unwrapped(node.arguments[0]) : undefined;
    if (
      input &&
      (headerOrigin(input, graph, new Set(visited)) === 'headers' ||
        (ts.isCallExpression(input) &&
          memberName(input.expression, checker) === 'entries' &&
          headerOrigin(memberReceiver(input.expression), graph, new Set(visited)) === 'headers'))
    )
      return 'record';
  }
  // A Headers instance has no enumerable string host property. Only converted
  // records propagate through object spread; plain {...request.headers} stays inert.
  if (
    ts.isObjectLiteralExpression(node) &&
    node.properties.some(
      (property) =>
        ts.isSpreadAssignment(property) &&
        headerOrigin(property.expression, graph, new Set(visited)) === 'record',
    )
  )
    return 'record';
  for (const declaration of declarations(node, checker)) {
    if (ts.isBindingElement(declaration) && ts.isObjectBindingPattern(declaration.parent)) {
      const container = declaration.parent.parent;
      if (
        declaration.dotDotDotToken &&
        ts.isVariableDeclaration(container) &&
        headerOrigin(container.initializer, graph, new Set(visited)) === 'record'
      )
        return 'record';
      const property =
        declaration.propertyName ??
        (ts.isIdentifier(declaration.name) ? declaration.name : undefined);
      if (property !== undefined && propertyName(property, checker) === 'headers') return 'headers';
    }
    if (ts.isVariableDeclaration(declaration) && declaration.initializer) {
      const origin = headerOrigin(declaration.initializer, graph, new Set(visited));
      if (origin) return origin;
    }
  }
  return undefined;
}

function headerCollection(node: ts.Node | undefined, graph: SourceGraph): boolean {
  return headerOrigin(node, graph) !== undefined;
}

export function rawAuthorityFindings(graph: SourceGraph, subjects: readonly string[]): Finding[] {
  const findings: Finding[] = [];
  for (const name of subjects) {
    if (name === INGRESS) continue; // Native socket/Host capture is the accepted source of provenance.
    const file = graph.files.get(name);
    if (!file) continue;
    walk(file, (node) => {
      const member = memberName(node, graph.checker);
      const receiver = memberReceiver(node);
      if (
        member === 'remoteAddress' ||
        member === 'rawHeaders' ||
        member === 'ip' ||
        (['hostname', 'host'].includes(member ?? '') &&
          requestUrl(memberReceiver(node) ?? node, graph.checker)) ||
        (member &&
          RAW_AUTHORITY_HEADERS.has(member.toLowerCase()) &&
          receiver &&
          headerCollection(receiver, graph))
      )
        findings.push(
          finding(node, 'Fix: derive visitor host/peer only from verified ingress provenance.'),
        );
      if (ts.isBindingElement(node)) {
        const binding = objectBinding(node, graph.checker);
        if (
          binding &&
          ((RAW_AUTHORITY_HEADERS.has(binding.name.toLowerCase()) &&
            headerCollection(binding.receiver, graph)) ||
            (['hostname', 'host'].includes(binding.name) &&
              requestUrl(binding.receiver, graph.checker)))
        )
          findings.push(
            finding(
              node,
              'Fix: do not bind unsigned host/peer authority; use verified ingress provenance.',
            ),
          );
      }
      if (
        ts.isCallExpression(node) &&
        ['get', 'getAll'].includes(memberName(node.expression, graph.checker) ?? '') &&
        headerCollection(memberReceiver(node.expression), graph)
      ) {
        const header = constantString(node.arguments[0], graph.checker)?.toLowerCase();
        if (header && RAW_AUTHORITY_HEADERS.has(header))
          findings.push(
            finding(node, 'Fix: derive visitor host/peer only from verified ingress provenance.'),
          );
      }
      if (ts.isElementAccessExpression(node) && headerCollection(node.expression, graph)) {
        const header = constantString(node.argumentExpression, graph.checker)?.toLowerCase();
        if (header && RAW_AUTHORITY_HEADERS.has(header))
          findings.push(
            finding(node, 'Fix: do not read unsigned authority headers in public page helpers.'),
          );
      }
    });
  }
  return findings;
}
