import * as ts from 'typescript';
import { expect, it } from 'vitest';

const serverPath = ts.sys.resolvePath('src/server.ts');
const schemaPath = ts.sys.resolvePath('src/generated/schema.d.ts');
const server = ts.sys.readFile(serverPath);
const schema = ts.sys.readFile(schemaPath);
if (server === undefined || schema === undefined) throw new Error('Missing SDK compiler subjects');

function errors(source: string, generated: string): string[] {
  const options: ts.CompilerOptions = {
    target: ts.ScriptTarget.ES2022,
    module: ts.ModuleKind.ESNext,
    moduleResolution: ts.ModuleResolutionKind.Bundler,
    strict: true,
    noEmit: true,
    skipLibCheck: true,
    types: [],
  };
  const host = ts.createCompilerHost(options);
  const read = host.readFile.bind(host);
  host.readFile = (path) =>
    path === serverPath ? source : path === schemaPath ? generated : read(path);
  const program = ts.createProgram([serverPath], options, host);
  return ts
    .getPreEmitDiagnostics(program)
    .map((diagnostic) => ts.flattenDiagnosticMessageText(diagnostic.messageText, '\n'));
}

it('the production ownership binding rejects an unowned generated GET, and removing it loses that protection', () => {
  expect(server).toContain('true satisfies GetContractOwnership<');
  expect(errors(server, schema)).toEqual([]);
  const unowned =
    schema +
    "\nexport interface paths { '/api/v1/unowned': { get: operations['GetAccountSite'] }; }\n";
  expect(errors(server, unowned)).toEqual([
    expect.stringContaining("Type 'true' does not satisfy the expected type 'false'"),
  ]);
  const unchecked = server.replace(/true satisfies GetContractOwnership<[\s\S]*?>;/, 'true;');
  expect(unchecked).not.toBe(server);
  expect(errors(unchecked, unowned)).toEqual([]);
});

it('the production wrapper map refuses a valid SDK method paired with the wrong operation', () => {
  const wrong = server.replace(
    "'/api/v1/public/site': 'getSite'",
    "'/api/v1/public/site': 'getCourses'",
  );
  expect(wrong).not.toBe(server);
  expect(errors(wrong, schema)).toEqual([
    expect.stringContaining('Type \'"getCourses"\' is not assignable to type \'"getSite"\''),
  ]);
});
