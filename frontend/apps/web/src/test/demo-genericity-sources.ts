import { posix } from 'node:path';

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
          (ts.isIdentifier(node.expression) && node.expression.text === 'require')) &&
        node.arguments[0] &&
        ts.isStringLiteralLike(node.arguments[0])
      )
        imports.push(node.arguments[0].text);
      ts.forEachChild(node, visit);
    };
    visit(file);
    for (const edge of imports.filter((value) => value.startsWith('.'))) {
      const base = posix.normalize(posix.join(posix.dirname(path), edge));
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
