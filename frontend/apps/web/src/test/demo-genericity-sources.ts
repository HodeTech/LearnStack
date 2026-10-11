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
  return path.split('/').at(-1) === 'node_modules' || generatedOutputs.has(path);
}

function testSource(path: string): boolean {
  return (
    path.startsWith('frontend/apps/web/src/test/') || /\.(?:test|spec)\.[cm]?[jt]sx?$/.test(path)
  );
}

/** A test name/directory cannot hide a helper imported by runtime code. */
export function productionDemoSources(all: ReadonlyMap<string, string>): Map<string, string> {
  const result = new Map(
    [...all].filter(([path]) => !testSource(path) && !exemptEntries.has(path)),
  );
  for (const [path, source] of result) {
    if (path.endsWith('.json')) continue;
    const file = ts.createSourceFile(path, source, ts.ScriptTarget.Latest, true);
    const imports: string[] = [];
    const visit = (node: ts.Node) => {
      if (
        ((ts.isImportDeclaration(node) && !node.importClause?.isTypeOnly) ||
          (ts.isExportDeclaration(node) && !node.isTypeOnly)) &&
        node.moduleSpecifier &&
        ts.isStringLiteralLike(node.moduleSpecifier)
      )
        imports.push(node.moduleSpecifier.text);
      if (
        ts.isCallExpression(node) &&
        (node.expression.kind === ts.SyntaxKind.ImportKeyword ||
          (ts.isIdentifier(node.expression) && node.expression.text === 'require'))
      ) {
        let specifier = node.arguments[0];
        // ESLint resolves an external package before loading it. This remains a
        // statically named edge; computed resolver inputs are refused below.
        if (
          specifier &&
          ts.isCallExpression(specifier) &&
          ts.isPropertyAccessExpression(specifier.expression) &&
          ts.isIdentifier(specifier.expression.expression) &&
          specifier.expression.expression.text === 'require' &&
          specifier.expression.name.text === 'resolve'
        ) {
          if (
            specifier.arguments.length > 1 &&
            (!specifier.arguments[0] ||
              !ts.isStringLiteralLike(specifier.arguments[0]) ||
              specifier.arguments[0].text.startsWith('.'))
          )
            throw new Error(`Unmodeled runtime import: ${path}`);
          specifier = specifier.arguments[0];
        }
        if (!specifier || !ts.isStringLiteralLike(specifier))
          throw new Error(`Unmodeled runtime import: ${path}`);
        imports.push(specifier.text);
      }
      ts.forEachChild(node, visit);
    };
    visit(file);
    for (const edge of imports) {
      if (/^(?:file:|\/)/.test(edge)) throw new Error(`Unmodeled runtime import: ${path}`);
      if (!edge.startsWith('.')) continue;
      // Node ESM resolves query/fragment and percent escapes as a module URL.
      const resolved = new URL(edge, `file:///learnstack/${path}`);
      const filename = fileURLToPath(resolved);
      if (!filename.startsWith('/learnstack/'))
        throw new Error(`Unmodeled runtime import: ${path}`);
      const base = filename.slice('/learnstack/'.length);
      const candidates = [
        base,
        ...['.ts', '.tsx', '.js', '.jsx', '.mts', '.cts', '.mjs', '.cjs', '.json'].flatMap(
          (extension) => [base + extension, posix.join(base, 'index' + extension)],
        ),
      ];
      if (/\.[cm]?jsx?$/.test(base))
        candidates.push(base.replace(/\.[cm]?jsx?$/, '.ts'), base.replace(/\.[cm]?jsx?$/, '.tsx'));
      const target = candidates.find((candidate) => all.has(candidate));
      // Exemptions apply only to standalone entries, never imported runtime helpers.
      if (target) result.set(target, all.get(target)!);
    }
  }
  return result;
}
