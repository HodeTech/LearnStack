import ts from 'typescript';

type Vocabulary = {
  readonly values: readonly { readonly kind: string; readonly value: string }[];
  readonly platformValues: readonly string[];
};

function guid(value: string): string | null {
  const compact = value.replace(/\s/g, '');
  const hex = compact.match(
    /^\{0x([0-9a-f]{1,8}),0x([0-9a-f]{1,4}),0x([0-9a-f]{1,4}),\{0x([0-9a-f]{1,2}),0x([0-9a-f]{1,2}),0x([0-9a-f]{1,2}),0x([0-9a-f]{1,2}),0x([0-9a-f]{1,2}),0x([0-9a-f]{1,2}),0x([0-9a-f]{1,2}),0x([0-9a-f]{1,2})\}\}$/i,
  );
  if (hex)
    return hex
      .slice(1)
      .map((part, index) => part!.padStart(index === 0 ? 8 : index < 3 ? 4 : 2, '0'))
      .join('')
      .toLowerCase();
  const bare = value.trim().replace(/^\{(.+)\}$|^\((.+)\)$/, '$1$2');
  if (!/^(?:[0-9a-f]{32}|[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12})$/i.test(bare)) return null;
  return bare.replaceAll('-', '').toLowerCase();
}

function jsonValues(value: unknown): string[] {
  if (typeof value === 'string') return [value];
  if (Array.isArray(value)) return value.flatMap(jsonValues);
  if (value !== null && typeof value === 'object') return Object.values(value).flatMap(jsonValues);
  return [];
}

/** G20 compares whole decoded values; it does not prove arbitrary computed branches. */
export function demoIdentityFindings(
  path: string,
  source: string,
  vocabulary: Vocabulary,
): string[] {
  if (!vocabulary.values.length || !vocabulary.platformValues.length)
    throw new Error('The typed demo identity source is empty');
  const ids = new Set(
    vocabulary.values.filter((row) => row.kind === 'id').map((row) => guid(row.value)),
  );
  if (!ids.size || ids.has(null)) throw new Error('The typed demo ID source is malformed');
  const platform = new Set(vocabulary.platformValues);
  const identities = new Set(
    vocabulary.values
      .filter((row) => row.kind !== 'id' && !platform.has(row.value))
      .map((row) => row.value),
  );
  const values: string[] = [];
  if (path.endsWith('.json')) values.push(...jsonValues(JSON.parse(source)));
  else {
    if (!/\.(?:[cm]?[jt]sx?)$/.test(path))
      throw new Error(`Unsupported genericity subject: ${path}`);
    const file = ts.createSourceFile(
      path,
      source,
      ts.ScriptTarget.Latest,
      true,
      /\.[jt]sx$/.test(path)
        ? ts.ScriptKind.TSX
        : /\.[cm]?js$/.test(path)
          ? ts.ScriptKind.JS
          : ts.ScriptKind.TS,
    );
    const diagnostics = (file as ts.SourceFile & { parseDiagnostics: readonly ts.Diagnostic[] })
      .parseDiagnostics;
    if (diagnostics.length) throw new Error(`Unreadable genericity subject: ${path}`);
    const visit = (node: ts.Node) => {
      if (ts.isStringLiteral(node) || ts.isNoSubstitutionTemplateLiteral(node)) {
        values.push(node.text);
        // Authored JSON carried by a source literal is not a hidden second vocabulary.
        if (node.text.trimStart().startsWith('{') || node.text.trimStart().startsWith('[')) {
          try {
            values.push(...jsonValues(JSON.parse(node.text)));
          } catch {
            /* Ordinary text may start with a bracket. Standalone JSON is strict. */
          }
        }
      }
      ts.forEachChild(node, visit);
    };
    visit(file);
    if (/\.[jt]sx$/.test(path)) {
      // TypeScript emits JSX entity decoding and whitespace exactly as runtime
      // strings. Reading the emitted tree avoids inventing a second JSX parser.
      const emitted = ts.transpileModule(source, {
        compilerOptions: { jsx: ts.JsxEmit.React, target: ts.ScriptTarget.ESNext },
        fileName: path,
      }).outputText;
      visit(ts.createSourceFile('jsx.js', emitted, ts.ScriptTarget.Latest, true, ts.ScriptKind.JS));
    }
  }
  return [
    ...new Set(values.filter((value) => identities.has(value) || ids.has(guid(value)))),
  ].sort();
}
