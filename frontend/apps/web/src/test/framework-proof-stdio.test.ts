// @vitest-environment node
import { execFileSync } from 'node:child_process';
import type { ExecFileSyncOptionsWithStringEncoding } from 'node:child_process';
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { runInNewContext } from 'node:vm';

import ts from 'typescript';
import { expect, it } from 'vitest';

import { closedFrameworkProofSync } from './framework-proof-closed-boundary';

const directory = fileURLToPath(new URL('./', import.meta.url));
const sources = [
  ['framework-response-transport.test.ts', 2],
  ['framework-response-overlap.test.ts', 2],
  ['framework-runtime-ownership.test.ts', 1],
  ['framework-external-provider-ca.test.ts', 1],
  ['ingress-upgrade-evidence.test.ts', 1],
  ['framework-router-matrix.test.ts', 2],
  ['framework-router-faults.cases.ts', 2],
  ['demo-dotnet-owner.test.ts', 1],
  ['framework-provenance.test.ts', 1],
] as const;

function optionsFromWrappers(): ExecFileSyncOptionsWithStringEncoding[] {
  const all: ExecFileSyncOptionsWithStringEncoding[] = [];
  for (const [name, expected] of sources) {
    const text = readFileSync(join(directory, name), 'utf8');
    const file = ts.createSourceFile(name, text, ts.ScriptTarget.Latest, true, ts.ScriptKind.TS);
    const imports = file.statements
      .flatMap((statement) => {
        if (
          !ts.isImportDeclaration(statement) ||
          statement.moduleSpecifier.getText(file) !== "'node:child_process'"
        )
          return [];
        const bindings = statement.importClause?.namedBindings;
        return bindings && ts.isNamedImports(bindings) ? [...bindings.elements] : [];
      })
      .filter((binding) => (binding.propertyName?.text ?? binding.name.text) === 'execFileSync');
    expect(imports.length).toBe(1);
    expect(imports[0]?.name.text).toBe('execFileSync');
    let environment: unknown;
    file.statements.forEach((statement) => {
      if (!ts.isVariableStatement(statement)) return;
      for (const declaration of statement.declarationList.declarations) {
        if (declaration.name.getText(file) === 'environment' && declaration.initializer)
          environment = runInNewContext(`(${declaration.initializer.getText(file)})`, { process });
      }
    });
    let count = 0;
    function visit(node: ts.Node) {
      if (ts.isIdentifier(node) && node.text === 'execFileSync') {
        expect(
          ts.isImportSpecifier(node.parent) ||
            (ts.isCallExpression(node.parent) && node.parent.expression === node),
        ).toBe(true);
      }
      if (ts.isCallExpression(node) && node.expression.getText(file) === 'execFileSync') {
        count++;
        const options = node.arguments[2];
        expect(options !== undefined && ts.isObjectLiteralExpression(options)).toBe(true);
        if (!options || !ts.isObjectLiteralExpression(options))
          throw new Error('Subprocess options refused');
        expect(options.properties.some(ts.isSpreadAssignment)).toBe(false);
        const stdio = options.properties.filter(
          (property) =>
            ts.isPropertyAssignment(property) && property.name.getText(file) === 'stdio',
        );
        expect(stdio.length).toBe(1);
        if (stdio.length !== 1 || !ts.isPropertyAssignment(stdio[0]!))
          throw new Error('Subprocess stdio refused');
        const value = stdio[0]!.initializer;
        expect(ts.isArrayLiteralExpression(value)).toBe(true);
        if (!ts.isArrayLiteralExpression(value)) throw new Error('Subprocess stdio refused');
        expect(
          value.elements.map((element) => (ts.isStringLiteral(element) ? element.text : '')),
        ).toEqual(['ignore', 'pipe', 'pipe']);
        all.push(
          runInNewContext(
            `(${options.getText(file).replaceAll('import.meta.url', JSON.stringify(pathToFileURL(join(directory, name)).href))})`,
            {
              process,
              URL,
              fileURLToPath,
              environment,
              frontend: directory,
              script: 'framework-router-fault.test.mjs',
            },
          ) as ExecFileSyncOptionsWithStringEncoding,
        );
      }
      ts.forEachChild(node, visit);
    }
    visit(file);
    expect(count).toBe(expected);
  }
  return all;
}

type Outcome = {
  stdoutBytes: number;
  stderrBytes: number;
  returned: boolean;
  refused: boolean;
  cleanError: boolean;
};
function probe(options: ExecFileSyncOptionsWithStringEncoding, exit: number): Outcome {
  const canary = 'PRIVATE-STDIO-CANARY-NEVER-PRINT';
  const originalOut = process.stdout.write;
  const originalErr = process.stderr.write;
  let stdoutBytes = 0;
  let stderrBytes = 0;
  let returned = false;
  let refused = false;
  let cleanError = false;
  process.stdout.write = ((chunk: string | Uint8Array) => {
    stdoutBytes += Buffer.byteLength(chunk);
    return true;
  }) as typeof process.stdout.write;
  process.stderr.write = ((chunk: string | Uint8Array) => {
    stderrBytes += Buffer.byteLength(chunk);
    return true;
  }) as typeof process.stderr.write;
  try {
    try {
      const result = closedFrameworkProofSync(() =>
        execFileSync(
          process.execPath,
          [
            '-e',
            `process.stdout.write(${JSON.stringify(canary)});process.stderr.write(${JSON.stringify(canary)});process.exit(${exit})`,
          ],
          options,
        ),
      );
      returned = result === canary;
    } catch (error) {
      refused = error instanceof Error && error.message === 'Framework proof refused';
      cleanError =
        error instanceof Error &&
        error.cause === undefined &&
        !String(error.stack).includes(canary) &&
        !JSON.stringify(Object.getOwnPropertyDescriptors(error)).includes(canary);
    }
  } finally {
    process.stdout.write = originalOut;
    process.stderr.write = originalErr;
  }
  return { stdoutBytes, stderrBytes, returned, refused, cleanError };
}

it('captures both actual parent channels on successful and failed subprocesses at every declared site', () => {
  const options = optionsFromWrappers();
  expect(options.length).toBe(13);
  for (const option of options) {
    expect(probe(option, 0)).toEqual({
      stdoutBytes: 0,
      stderrBytes: 0,
      returned: true,
      refused: false,
      cleanError: false,
    });
    expect(probe(option, 3)).toEqual({
      stdoutBytes: 0,
      stderrBytes: 0,
      returned: false,
      refused: true,
      cleanError: true,
    });
  }
});

it('detects Node stderr forwarding when each actual wrapper omits stdio', () => {
  const options = optionsFromWrappers();
  expect(options.length).toBe(13);
  for (const option of options) {
    const unsafe = { ...option };
    delete unsafe.stdio;
    for (const code of [0, 3]) {
      const observed = probe(unsafe, code);
      expect(observed.stdoutBytes).toBe(0);
      expect(observed.stderrBytes > 0).toBe(true);
      expect(observed.returned).toBe(code === 0);
      expect(observed.refused).toBe(code !== 0);
    }
  }
});
