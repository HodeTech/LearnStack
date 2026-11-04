import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { createRequire } from 'node:module';
import test from 'node:test';

import { copiedRouterFaultRouteSource } from '../apps/web/scripts/framework-router-fault-route.mjs';

const nextRoot = new URL('../apps/web/node_modules/next/', import.meta.url);
function source(relative) {
  return readFileSync(new URL(relative, nextRoot), 'utf8');
}
function once(bytes, literal) {
  assert.equal(bytes.split(literal).length, 2, 'Exact pinned Next chain anchor');
}

test('copied route parses and has one fixed actual work-store import', () => {
  const ts = createRequire(new URL('../apps/web/package.json', import.meta.url))('typescript');
  const parsed = ts.createSourceFile(
    'route.ts',
    copiedRouterFaultRouteSource,
    ts.ScriptTarget.Latest,
    true,
    ts.ScriptKind.TS,
  );
  assert.equal(parsed.parseDiagnostics.length, 0);
  assert.equal(parsed.statements.filter(ts.isImportDeclaration).length, 1);
  once(
    copiedRouterFaultRouteSource,
    "import { workAsyncStorage } from 'next/dist/server/app-render/work-async-storage.external';",
  );
  once(copiedRouterFaultRouteSource, 'endStore.pendingRevalidateWrites.push(pending);');
});

test('actual Next request work is aggregated and reaches the same pipe entry', () => {
  const app = source('dist/server/route-modules/app-route/module.js');
  const revalidation = source('dist/server/revalidation-utils.js');
  const template = source('dist/build/templates/app-route.js');
  const pipe = source('dist/server/pipe-readable.js');
  once(
    app,
    'context.renderOpts.pendingWaitUntil = (0, _revalidationutils.executeRevalidates)(workStore).finally',
  );
  once(app, 'this.workAsyncStorage.run(workStore, async ()=>');
  once(revalidation, '...pendingRevalidateWrites');
  once(
    template,
    'await (0, _sendresponse.sendResponse)(nodeNextReq, nodeNextRes, response, context.renderOpts.pendingWaitUntil);',
  );
  once(pipe, '})(res,async()=>{');
  once(pipe, '},waitUntilForEnd);');
});

test('pipe source catches the operation before join and router may catch fallback', () => {
  const pipe = source('dist/server/pipe-readable.js');
  const router = source('dist/server/lib/router-server.js');
  once(
    pipe,
    'if(__learnstackNextCleanupAbort)__learnstackNextCleanupOwner.abort(err);else __learnstackNextCleanupOwner.fail(err);',
  );
  once(pipe, 'if (hasPrimary) throw primary;');
  once(router, "let invokePath = '/500';");
  once(router, "res.end('Internal Server Error');");
});
