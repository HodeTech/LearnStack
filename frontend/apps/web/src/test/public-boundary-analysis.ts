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
    ts.isSatisfiesExpression(node)
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
    const left = constantString(node.left, checker, visited);
    const right = constantString(node.right, checker, visited);
    return left === undefined || right === undefined ? undefined : left + right;
  }
  for (const declaration of declarations(node, checker))
    if (
      ts.isVariableDeclaration(declaration) &&
      ts.isVariableDeclarationList(declaration.parent) &&
      declaration.parent.flags & ts.NodeFlags.Const
    ) {
      const value = constantString(declaration.initializer, checker, visited);
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

function globalFetch(
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
    ['bind', 'call', 'apply'].includes(memberName(node, checker) ?? '') &&
    globalFetch(receiver, checker, visited)
  )
    return true;
  if (ts.isCallExpression(node) && memberName(node.expression, checker) === 'bind')
    return globalFetch(node.expression, checker, visited);
  if (receiver && memberName(node, checker) === 'fetch' && globalObject(receiver, checker))
    return true;
  if (!ts.isIdentifier(node)) return false;
  const bindings = declarations(node, checker);
  if (node.text === 'fetch' && bindings.length === 0) return true;
  return bindings.some((declaration) => {
    if (ts.isVariableDeclaration(declaration) && declaration.initializer)
      return globalFetch(declaration.initializer, checker, visited);
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
  const clause = node.importClause;
  if (!clause) return true;
  if (clause.isTypeOnly) return false;
  if (clause.name || (clause.namedBindings && ts.isNamespaceImport(clause.namedBindings)))
    return true;
  return (
    clause.namedBindings !== undefined &&
    ts.isNamedImports(clause.namedBindings) &&
    (clause.namedBindings.elements.length === 0 ||
      clause.namedBindings.elements.some((item) => !item.isTypeOnly))
  );
}

function runtimeExport(node: ts.ExportDeclaration): boolean {
  return (
    !node.isTypeOnly &&
    (!node.exportClause ||
      !ts.isNamedExports(node.exportClause) ||
      node.exportClause.elements.length === 0 ||
      node.exportClause.elements.some((item) => !item.isTypeOnly))
  );
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
  return { files, checker, edges, unresolved };
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
      if (ts.isCallExpression(node) && globalFetch(node.expression, graph.checker))
        findings.push(
          finding(node, 'Fix: call the configured public SDK adapter instead of global fetch.'),
        );
      if (
        (ts.isVariableDeclaration(node) || ts.isParameter(node) || ts.isPropertyAssignment(node)) &&
        node.initializer &&
        globalFetch(node.initializer, graph.checker)
      )
        findings.push(
          finding(
            node,
            'Fix: inject transport; do not store or default to global fetch outside the configured adapter.',
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
          memberName(receiver, graph.checker) === 'env' &&
          (memberName(node, graph.checker) ?? '').startsWith('LEARNSTACK_PUBLIC_')
        )
          privateEnvironment = true;
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

function importedNamespace(
  node: ts.Node,
  graph: SourceGraph,
  modules: readonly string[],
  visited = new Set<ts.Node>(),
): boolean {
  node = unwrapped(node);
  if (visited.has(node) || !ts.isIdentifier(node)) return false;
  visited.add(node);
  // Preserve namespace bindings even when the external module has no type body.
  const bindings = new Set([
    ...(graph.checker.getSymbolAtLocation(node)?.declarations ?? []),
    ...declarations(node, graph.checker),
  ]);
  return [...bindings].some((declaration) => {
    if (ts.isNamespaceImport(declaration)) {
      const parent = declaration.parent.parent;
      return (
        ts.isImportDeclaration(parent) &&
        modules.includes(constantString(parent.moduleSpecifier, graph.checker) ?? '')
      );
    }
    return (
      ts.isVariableDeclaration(declaration) &&
      declaration.initializer !== undefined &&
      importedNamespace(declaration.initializer, graph, modules, visited)
    );
  });
}

function importedFunction(
  node: ts.Node,
  graph: SourceGraph,
  modules: readonly string[],
  names: readonly string[],
  visited = new Set<ts.Node>(),
): boolean {
  node = unwrapped(node);
  if (visited.has(node)) return false;
  visited.add(node);
  for (const declaration of declarations(node, graph.checker)) {
    // External modules have no source body in the census, so inspect their import bindings.
    if (
      ts.isImportSpecifier(declaration) &&
      names.includes((declaration.propertyName ?? declaration.name).text)
    ) {
      const parent = declaration.parent.parent.parent;
      if (
        ts.isImportDeclaration(parent) &&
        modules.includes(constantString(parent.moduleSpecifier, graph.checker) ?? '')
      )
        return true;
    }
    if (ts.isVariableDeclaration(declaration) && declaration.initializer)
      return importedFunction(declaration.initializer, graph, modules, names, visited);
    if (ts.isBindingElement(declaration) && ts.isObjectBindingPattern(declaration.parent)) {
      const parent = declaration.parent.parent;
      const property =
        declaration.propertyName ??
        (ts.isIdentifier(declaration.name) ? declaration.name : undefined);
      const name = property ? propertyName(property, graph.checker) : undefined;
      if (
        name &&
        names.includes(name) &&
        ts.isVariableDeclaration(parent) &&
        parent.initializer &&
        importedNamespace(parent.initializer, graph, modules)
      )
        return true;
    }
  }
  if (ts.isIdentifier(node)) {
    // getAliasedSymbol returns an unknown symbol for external modules: retain original import declarations.
    const original = graph.checker.getSymbolAtLocation(node);
    for (const declaration of original?.declarations ?? [])
      if (
        ts.isImportSpecifier(declaration) &&
        names.includes((declaration.propertyName ?? declaration.name).text)
      ) {
        const parent = declaration.parent.parent.parent;
        if (
          ts.isImportDeclaration(parent) &&
          modules.includes(constantString(parent.moduleSpecifier, graph.checker) ?? '')
        )
          return true;
      }
  }
  const receiver = memberReceiver(node);
  if (!receiver || !names.includes(memberName(node, graph.checker) ?? '')) return false;
  return importedNamespace(receiver, graph, modules);
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
  return declarations(node, checker).some(
    (declaration) =>
      ts.isVariableDeclaration(declaration) &&
      declaration.initializer !== undefined &&
      requestUrl(declaration.initializer, checker, visited),
  );
}

function headerCollection(
  node: ts.Node | undefined,
  graph: SourceGraph,
  visited = new Set<ts.Node>(),
): boolean {
  if (!node || visited.has(node)) return false;
  node = unwrapped(node);
  visited.add(node);
  const { checker } = graph;
  if (memberName(node, checker) === 'headers') return true;
  if (
    ts.isCallExpression(node) &&
    (importedFunction(node.expression, graph, ['next/headers'], ['headers']) ||
      (ts.isIdentifier(node.expression) && node.expression.text === 'headers'))
  )
    return true;
  if (ts.isIdentifier(node) && node.text === 'headers' && declarations(node, checker).length === 0)
    return true;
  return declarations(node, checker).some((declaration) => {
    if (ts.isBindingElement(declaration) && ts.isObjectBindingPattern(declaration.parent)) {
      const property =
        declaration.propertyName ??
        (ts.isIdentifier(declaration.name) ? declaration.name : undefined);
      return property !== undefined && propertyName(property, checker) === 'headers';
    }
    return (
      ts.isVariableDeclaration(declaration) &&
      declaration.initializer !== undefined &&
      headerCollection(declaration.initializer, graph, visited)
    );
  });
}

export function rawAuthorityFindings(graph: SourceGraph, subjects: readonly string[]): Finding[] {
  const findings: Finding[] = [];
  for (const name of subjects) {
    if (name === INGRESS) continue; // Native socket/Host capture is the accepted source of provenance.
    const file = graph.files.get(name);
    if (!file) continue;
    walk(file, (node) => {
      const member = memberName(node, graph.checker);
      if (
        member === 'remoteAddress' ||
        member === 'rawHeaders' ||
        member === 'ip' ||
        (['hostname', 'host'].includes(member ?? '') &&
          requestUrl(memberReceiver(node) ?? node, graph.checker)) ||
        (member === 'host' && memberName(memberReceiver(node) ?? node, graph.checker) === 'headers')
      )
        findings.push(
          finding(node, 'Fix: derive visitor host/peer only from verified ingress provenance.'),
        );
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
