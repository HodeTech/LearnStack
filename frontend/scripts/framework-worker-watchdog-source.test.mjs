// Pure emitted-render controls. The installed Next runtime is never imported or executed.
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { mkdtempSync, mkdirSync, readFileSync, realpathSync, rmSync, writeFileSync } from 'node:fs';
import { createRequire } from 'node:module';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import test from 'node:test';
import { fileURLToPath, pathToFileURL } from 'node:url';

import {
  instrumentFrameworkOwnershipMatrix,
  ownershipMatrixCases,
} from '../apps/web/scripts/framework-ownership-matrix-source.mjs';

const frontend = fileURLToPath(new URL('../', import.meta.url));
const ts = createRequire(join(frontend, 'apps/web/package.json'))('typescript');
const runtime = readFileSync(
  join(frontend, 'apps/web/scripts/public-server-runtime.mjs'),
  'utf8',
).replace(
  "server.listen(PUBLIC_HTTPS_PORT, '127.0.0.1', () => {",
  "server.listen(0, '127.0.0.1', () => {",
);
const generated = instrumentFrameworkOwnershipMatrix({
  configuration: 'export default nextConfig;',
  runtime,
  child: '  child = createFrameworkRuntimeChildControl();',
  owner: '  const graceful =',
  rootPage: 'export default function Page() { return <main>Public</main>; }',
});

function execute(mutation) {
  const root = realpathSync(mkdtempSync(join(tmpdir(), 'learnstack-worker-render-control-')));
  const app = join(root, 'frontend/apps/web'),
    controls = join(root, 'ownership-control');
  mkdirSync(app, { recursive: true });
  mkdirSync(controls, { mode: 0o700 });
  writeFileSync(join(controls, 'scenario'), 'worker-ordered-stop', { mode: 0o600 });
  writeFileSync(join(controls, 'events.jsonl'), '', { mode: 0o600 });
  writeFileSync(join(controls, 'render.arm'), '', { mode: 0o600 });
  try {
    // Replace only the irrelevant JSX return; execute the emitted gate itself.
    let source = generated.rootPage.replace('return <main>Public</main>;', "return 'Public';");
    assert.equal(source.split('framework.waitUntil(work);').length, 2);
    if (mutation === 'omit') source = source.replace('framework.waitUntil(work);', 'void work;');
    if (mutation === 'substitute')
      source = source.replace(
        'framework.waitUntil(work);',
        'framework.waitUntil(Promise.resolve());',
      );
    const output = ts.transpileModule(source, {
      compilerOptions: { module: ts.ModuleKind.ES2022, target: ts.ScriptTarget.ES2022 },
      reportDiagnostics: true,
    });
    assert.equal(output.diagnostics.length, 0);
    writeFileSync(join(app, 'page.mjs'), output.outputText, { mode: 0o600 });
    const probe = `import assert from 'node:assert/strict';
import {writeFileSync,readFileSync} from 'node:fs';
import Page from './page.mjs';
const registered=[];Object.defineProperty(globalThis,Symbol.for('@next/request-context'),{value:{get:()=>({waitUntil:work=>registered.push(work)})}});
const render=Page();assert.equal(registered.length,1);let settled=false;
registered[0].then(()=>{settled=true;});await new Promise(resolve=>setImmediate(resolve));
assert.equal(settled,false,'the actual registered promise remains pending with the render');
writeFileSync('../../../ownership-control/render.release','',{flag:'wx',mode:0o600});
assert.equal(await render,'Public');await registered[0];assert.equal(settled,true);
const events=readFileSync('../../../ownership-control/events.jsonl','utf8').trimEnd().split('\\n').map(JSON.parse);
assert.deepEqual(events.map(event=>event.event),['render-work-entered','render-work-returned']);`;
    const result = spawnSync(process.execPath, ['--input-type=module'], {
      cwd: app,
      input: probe,
      encoding: 'utf8',
      timeout: 2000,
    });
    assert.equal(result.signal, null);
    return result;
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
}
test('emitted page registers and awaits the same actual held promise', () => {
  const result = execute('none');
  assert.equal(result.status, 0, result.stderr);
  assert.equal(result.stdout, '');
  assert.equal(result.stderr, '');
});
for (const mutation of ['omit', 'substitute'])
  test('dirty ' + mutation + ' cannot stand in for held registered work', () => {
    const result = execute(mutation);
    assert.equal(result.status, 1);
    assert.equal(result.stdout, '');
    assert.match(result.stderr, /AssertionError/);
  });

// Execute the actual validator declarations, with no driver/Next/fixture startup imports.
// The extracted functions and finite vocabularies remain byte-exact source slices.
async function withFailureValidator(source, run) {
  const file = ts.createSourceFile(
    'matrix.mjs',
    source,
    ts.ScriptTarget.Latest,
    true,
    ts.ScriptKind.JS,
  );
  assert.equal(file.parseDiagnostics.length, 0);
  const names = new Set([
    'exact',
    'eventNames',
    'admissionReasons',
    'validateEvents',
    'validateOwnerReceipt',
    'validateCompletedCase',
    'validateFailureEvidence',
    'projectFailureEvidence',
  ]);
  const selected = [];
  for (const statement of file.statements) {
    const name = ts.isFunctionDeclaration(statement)
      ? statement.name?.text
      : ts.isVariableStatement(statement) &&
          statement.declarationList.declarations.length === 1 &&
          ts.isIdentifier(statement.declarationList.declarations[0].name)
        ? statement.declarationList.declarations[0].name.text
        : undefined;
    if (names.has(name)) {
      names.delete(name);
      selected.push(statement.getText(file));
    }
  }
  assert.equal(names.size, 0, 'all actual validator dependencies selected exactly once');
  const root = realpathSync(mkdtempSync(join(tmpdir(), 'learnstack-worker-failure-control-')));
  try {
    const path = join(root, 'validator.mjs');
    writeFileSync(
      path,
      "import assert from 'node:assert/strict';\nconst ownershipMatrixCases = " +
        JSON.stringify(ownershipMatrixCases) +
        ';\n' +
        selected.join('\n') +
        '\nexport {validateFailureEvidence, projectFailureEvidence};\n',
      { flag: 'wx', mode: 0o600 },
    );
    const module = await import(pathToFileURL(path).href);
    await run(module.validateFailureEvidence, module.projectFailureEvidence);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
}
function failureCapsule() {
  return {
    group: 'workers',
    stage: 'worker-ordered-stop:hold-real-render',
    current: {
      case: 'worker-ordered-stop',
      mode: 'development',
      events: [],
      ownerReceipt: null,
      admissionReason: null,
    },
    completedCases: [],
    retained: true,
    cleanupConfirmed: false,
  };
}

test('failure and deadline paths share the actual closed held-render validator; omitted stage refuses', async () => {
  const source = readFileSync(
    join(frontend, 'apps/web/scripts/framework-ownership-matrix.mjs'),
    'utf8',
  );
  const file = ts.createSourceFile(
    'matrix.mjs',
    source,
    ts.ScriptTarget.Latest,
    true,
    ts.ScriptKind.JS,
  );
  const entry = file.statements.find(
    (statement) =>
      ts.isFunctionDeclaration(statement) && statement.name?.text === 'runPrivateMatrix',
  );
  assert.ok(entry);
  const paths = [];
  function visit(node) {
    if (
      ts.isCallExpression(node) &&
      ts.isIdentifier(node.expression) &&
      node.expression.text === 'validateFailureEvidence'
    ) {
      assert.deepEqual(
        node.arguments.map((argument) => argument.getText(file)),
        ['evidence', 'group'],
      );
      let parent = node.parent,
        owner;
      while (parent && parent !== entry) {
        if (ts.isFunctionDeclaration(parent) && parent.name?.text === 'cancel') {
          owner = 'deadline';
          break;
        }
        if (ts.isCatchClause(parent)) owner = 'failure';
        parent = parent.parent;
      }
      paths.push(owner);
    }
    ts.forEachChild(node, visit);
  }
  visit(entry);
  assert.deepEqual(paths, ['deadline', 'failure']);
  await withFailureValidator(source, (validate, project) => {
    for (const path of paths)
      assert.doesNotThrow(
        () => validate(failureCapsule(), 'workers'),
        path + ' finite capsule remains writable',
      );
    const clean = failureCapsule();
    clean.current.events = [
      {
        event: 'native-imported',
        case: 'worker-ordered-stop',
        pid: 8765,
        parentPid: 1234,
        milliseconds: 5,
      },
    ];
    assert.deepEqual(project(clean, 'workers'), {
      group: 'workers',
      stage: 'worker-ordered-stop:hold-real-render',
      currentCase: 'worker-ordered-stop',
      admissionReason: null,
      retained: true,
      cleanupConfirmed: false,
    });
    assert.doesNotMatch(JSON.stringify(project(clean, 'workers')), /pid|8765|1234/i);
    for (const mutate of [
      (v) => {
        v.extra = 'PRIVATE-CANARY';
      },
      (v) => {
        v.current.events[0].extra = 'PRIVATE-CANARY';
      },
      (v) => {
        v.current.admissionReason = 'PRIVATE-CANARY';
      },
      (v) => {
        v.stage = 'PRIVATE-CANARY';
      },
      (v) => {
        v.current.case = 'PRIVATE-CANARY';
      },
      (v) => {
        v.completedCases = Array(18).fill({});
      },
    ]) {
      const dirty = structuredClone(clean);
      mutate(dirty);
      assert.throws(() => project(dirty, 'workers'));
    }
    assert.throws(() => project(clean, 'startup'));
  });
  const entryLine = "    'worker-ordered-stop:hold-real-render',\n";
  assert.equal(source.split(entryLine).length, 2);
  await withFailureValidator(source.replace(entryLine, ''), (validate) => {
    for (const path of paths)
      assert.throws(
        () => validate(failureCapsule(), 'workers'),
        undefined,
        path + ' dirty omission refuses',
      );
  });
});

test('held-render failure stage is limited to the one worker case, development mode and workers group', async () => {
  const source = readFileSync(
    join(frontend, 'apps/web/scripts/framework-ownership-matrix.mjs'),
    'utf8',
  );
  await withFailureValidator(source, (validate) => {
    for (const mutate of [
      (value) => {
        value.stage = 'worker-positive:hold-real-render';
      },
      (value) => {
        value.stage = 'worker-ordered-stop:arbitrary-render';
      },
      (value) => {
        value.current.case = 'worker-positive';
      },
      (value) => {
        value.current.mode = 'production';
      },
      (value) => {
        value.current = null;
      },
    ]) {
      const value = failureCapsule();
      mutate(value);
      assert.throws(() => validate(value, 'workers'));
    }
    const wrongGroup = failureCapsule();
    wrongGroup.group = 'recovery';
    assert.throws(() => validate(wrongGroup, 'recovery'));
  });
});
