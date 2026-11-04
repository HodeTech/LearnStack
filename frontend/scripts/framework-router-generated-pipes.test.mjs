// Pure controls parse a small recorded join fixture and installed reference without executing either.
// Admission is neither called nor mocked; inert inspector checks execute only authored controls.
import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import fs, {
  chmodSync,
  mkdirSync,
  mkdtempSync,
  readFileSync,
  realpathSync,
  rmSync,
  symlinkSync,
  writeFileSync,
} from 'node:fs';
import { Session } from 'node:inspector';
import { createRequire, syncBuiltinESMExports } from 'node:module';
import { tmpdir } from 'node:os';
import { dirname, join, resolve } from 'node:path';
import test, { after } from 'node:test';
import { fileURLToPath, pathToFileURL } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const root = resolve(here, '../..');
const app = join(root, 'frontend/apps/web');
const require = createRequire(join(app, 'package.json'));
const ts = require('typescript');
const temporary = realpathSync(mkdtempSync(join(tmpdir(), 'learnstack-generated-pipe-light-')));
after(() => rmSync(temporary, { recursive: true, force: true }));
const candidate = join(app, 'scripts/framework-router-generated-pipes.mjs');
const source = readFileSync(candidate, 'utf8');
const guardAnchor = "'../../../scripts/framework-installation.mjs'";
assert.equal(source.split(guardAnchor).length, 2);
// The only derivative changes resolve the helper's import to the
// actual guard and expose pure internals. The guard remains real and uncalled.
const derivative =
  source.replace(
    guardAnchor,
    JSON.stringify(pathToFileURL(join(root, 'frontend/scripts/framework-installation.mjs')).href),
  ) + '\nexport { bytesAt, parsed, joinShape, candidatePoints };\n';
const derivativePath = join(temporary, 'subjects.mjs');
writeFileSync(derivativePath, derivative, { mode: 0o600, flag: 'wx' });
const subject = await import(pathToFileURL(derivativePath).href);
const { createGeneratedPipeLedger } = await import(
  pathToFileURL(join(app, 'scripts/framework-router-pipe-ledger.mjs')).href
);
const referenceBytes = readFileSync(join(app, 'node_modules/next/dist/server/pipe-readable.js'));
function visit(node, predicate) {
  const found = [];
  function walk(item) {
    if (predicate(item)) found.push(item);
    ts.forEachChild(item, walk);
  }
  walk(node);
  return found;
}
const reference = subject.parsed(ts, referenceBytes.toString('utf8'));
const referenceFunction = visit(
  reference.file,
  (node) => ts.isFunctionExpression(node) && node.name?.text === 'joinResponseCompression',
);
assert.equal(referenceFunction.length, 1);
const shape = subject.joinShape(ts, reference.checker, referenceFunction[0]);
const fixtureBytes = readFileSync(join(here, 'fixtures/router-minified-join.txt'));
const provenance = JSON.parse(
  readFileSync(join(here, 'fixtures/router-minified-join.provenance.json'), 'utf8'),
);
const snippet = fixtureBytes.toString('utf8').trimEnd();
function replaceOnce(text, before, after) {
  assert.equal(text.split(before).length, 2, 'one current minified anchor');
  return text.replace(before, after);
}
function match(text) {
  return subject.candidatePoints(ts, Buffer.from('(' + text + ');'), shape);
}
function refuses(text) {
  try {
    assert.equal(match(text).length, 0);
  } catch (error) {
    if (error?.message === 'Copied generated pipe subject refused') return;
    throw error;
  }
}
let ordinal = 0;
function fixture() {
  const owned = join(temporary, String(++ordinal));
  mkdirSync(owned, { mode: 0o700 });
  const appRoot = join(owned, 'frontend/apps/web');
  mkdirSync(appRoot, { recursive: true });
  const framework = Object.freeze({ mode: 'production', identity: Object.freeze({ appRoot }) });
  return { owned, appRoot, framework, counts: Array(27).fill(1) };
}
function ledgerRows(f) {
  const bytes = readFileSync(join(f.owned, '.router-pipe-trace.events'), 'utf8');
  return bytes
    .trimEnd()
    .split('\n')
    .map((line) => {
      assert.ok(Buffer.byteLength(line + '\n') <= 128);
      return JSON.parse(line);
    });
}

test('copied helper syntax, without executing admission or generated source', () => {
  for (const path of [candidate, join(app, 'scripts/framework-router-pipe-ledger.mjs')])
    execFileSync(process.execPath, ['--check', path], { timeout: 2000, stdio: 'pipe' });
});
test('installed reference parses into exactly one semantic join entry', () => {
  const points = subject.candidatePoints(ts, referenceBytes, shape);
  assert.equal(points.length, 1);
  assert.equal(points[0].responseName, 'response');
});
test('exact small minified fixture retains original tuple/hash provenance without .next', () => {
  assert.equal(provenance.format, 1);
  assert.equal(
    provenance.scope,
    'exact-extracted-minified-function-parser-fixture-only-no-build-admission',
  );
  assert.deepEqual(provenance.tuple, {
    node: '22.23.1',
    pnpm: '9.12.3',
    next: '15.5.28',
    typescript: '5.6.3',
  });
  assert.equal(provenance.fixture.bytes, fixtureBytes.length);
  assert.equal(provenance.fixture.sha256, createHash('sha256').update(fixtureBytes).digest('hex'));
  assert.equal(provenance.origins.length, 2);
  for (const origin of provenance.origins) {
    assert.match(origin.sha256, /^[a-f0-9]{64}$/);
    assert.ok(origin.bytes > fixtureBytes.length && origin.endUtf16 > origin.startUtf16);
  }
  assert.equal(match(snippet).length, 1);
  assert.equal(match(snippet)[0].responseName, 'a');
});
test('current minified shape preserves renamed response and shadowed catch bindings', () => {
  const renamed = snippet.replace(/\ba\b/g, 'responseProof');
  assert.equal(match(renamed).length, 1);
  assert.equal(match(renamed)[0].responseName, 'responseProof');
});
test('two exact join copies remain two independent entry sites', () => {
  assert.equal(
    subject.candidatePoints(ts, Buffer.from('(' + snippet + ');(' + snippet + ');'), shape).length,
    2,
  );
});
const dirty = [
  ['wrong response binding', 'a[h]?.recordEndFailure(b)', 'b[h]?.recordEndFailure(b)'],
  [
    'foreign Symbol',
    'learnstack.next.responseCompressionOwner',
    'foreign.responseCompressionOwner',
  ],
  [
    'removed actual await',
    'e=await ("function"==typeof b?b():b)',
    'e=("function"==typeof b?b():b)',
  ],
  ['wrong end task', 'Promise.resolve(c)', 'Promise.resolve(b)'],
  ['removed end failure', 'a[h]?.recordEndFailure(b)', 'void b'],
  ['removed cleanup await', 'await l.join()', 'l.join()'],
  ['removed cleanup failure', 'l.recordCleanupFailure(a)', 'void a'],
  ['changed throw order', 'if(k)throw f;if(i)throw d;', 'if(i)throw d;if(k)throw f;'],
  ['changed return binding', 'return e', 'return b'],
  ['unmodeled transform', 'let m=!1;', 'let m=Boolean(0);'],
];
for (const [name, before, afterText] of dirty)
  test('dirty semantic mutant: ' + name, () => {
    refuses(replaceOnce(snippet, before, afterText));
  });
test('string/comment and abort Symbol are not join entry', () => {
  assert.equal(
    subject.candidatePoints(
      ts,
      Buffer.from(
        '/* Symbol.for("learnstack.next.responseCompressionOwner") */\n' +
          'const text="learnstack.next.responseCompressionOwner";\n' +
          'async function abort(a,b,c) { const owner=a[Symbol.for("learnstack.next.responseCompressionOwner")]; owner?.abort(b); }',
      ),
      shape,
    ).length,
    0,
  );
});
test('stable exact byte snapshot refuses aliases and oversize', () => {
  const path = join(temporary, 'bytes');
  writeFileSync(path, 'bounded', { mode: 0o600 });
  assert.equal(subject.bytesAt(path, 7).toString(), 'bounded');
  assert.throws(() => subject.bytesAt(path, 6));
  const alias = join(temporary, 'alias');
  symlinkSync(path, alias);
  assert.throws(() => subject.bytesAt(alias, 7));
  assert.equal(subject.bytesAt(path, 7).toString(), 'bounded');
});
for (const change of ['same-inode-write', 'path-replacement', 'mode-change'])
  test(
    'stable snapshot detects ' + change + ' during read and closes the actual descriptor',
    () => {
      const path = join(temporary, change);
      writeFileSync(path, 'bounded', { mode: 0o600 });
      const original = fs.readSync;
      let seen;
      try {
        fs.readSync = (fd, ...args) => {
          const count = original(fd, ...args);
          if (seen === undefined) {
            seen = fd;
            if (change === 'same-inode-write') writeFileSync(path, 'changed');
            else if (change === 'path-replacement') {
              fs.renameSync(path, path + '.previous');
              writeFileSync(path, 'bounded', { mode: 0o600 });
            } else chmodSync(path, 0o640);
          }
          return count;
        };
        syncBuiltinESMExports();
        assert.throws(() => subject.bytesAt(path, 7));
      } finally {
        fs.readSync = original;
        syncBuiltinESMExports();
      }
      assert.equal(Number.isInteger(seen), true);
      assert.throws(
        () => fs.fstatSync(seen),
        (error) => error.code === 'EBADF',
      );
    },
  );
test('ledger preserves an exact three-case trace and independent first fault', () => {
  const f = fixture(),
    ledger = createGeneratedPipeLedger(f.framework, f.counts);
  for (const phase of ['case-0', 'case-1', 'case-2'])
    assert.equal(ledger.record('entry', 1, 0, phase, 'router-seen'), true);
  assert.equal(ledger.fault('source-mismatch'), true);
  assert.equal(ledger.record('entry', 1, 0, 'case-2', 'router-seen'), false);
  assert.equal(ledger.fault('close-active'), false);
  ledger.close();
  ledger.close();
  const rows = ledgerRows(f);
  assert.equal(rows.length, 4);
  assert.deepEqual(
    rows.map((row) => row.n),
    [1, 2, 3, 4],
  );
  assert.equal(rows[3].x, 'source-mismatch');
});
test('ledger reserves row128 for overflow and never resets', () => {
  const f = fixture(),
    ledger = createGeneratedPipeLedger(f.framework, f.counts);
  for (let n = 0; n < 127; n++)
    assert.equal(ledger.record('script', 0, -1, 'setup', 'ready'), true);
  assert.equal(ledger.record('script', 0, -1, 'setup', 'ready'), false);
  assert.deepEqual(ledger.summary(), { rows: 128, failed: true, firstFault: 'ledger-limit' });
  ledger.close();
  assert.equal(ledgerRows(f).length, 128);
});
test('ledger refuses unfrozen state, modeled foreign UID, wrong mode and aliased roots', () => {
  const f = fixture();
  assert.throws(() => createGeneratedPipeLedger({ ...f.framework }, f.counts));
  assert.throws(() =>
    createGeneratedPipeLedger(
      Object.freeze({ mode: 'production', identity: { appRoot: f.appRoot } }),
      f.counts,
    ),
  );
  assert.throws(() =>
    createGeneratedPipeLedger(Object.freeze({ ...f.framework, mode: 'development' }), f.counts),
  );
  const actualGetuid = process.getuid;
  try {
    process.getuid = () => actualGetuid() + 1;
    assert.throws(() => createGeneratedPipeLedger(f.framework, f.counts));
  } finally {
    process.getuid = actualGetuid;
  }
  chmodSync(f.owned, 0o755);
  assert.throws(() => createGeneratedPipeLedger(f.framework, f.counts));
  chmodSync(f.owned, 0o700);
  const alias = join(temporary, 'root-alias');
  symlinkSync(f.owned, alias);
  assert.throws(() =>
    createGeneratedPipeLedger(
      Object.freeze({
        mode: 'production',
        identity: Object.freeze({ appRoot: join(alias, 'frontend/apps/web') }),
      }),
      f.counts,
    ),
  );
});
test('ledger invalid shapes fail closed and caller mutation cannot expand subject indexes', () => {
  const f = fixture(),
    ledger = createGeneratedPipeLedger(f.framework, f.counts);
  f.counts.push(1);
  assert.equal(ledger.record('entry', 27, 0, 'case-0', 'router-seen'), false);
  assert.equal(ledger.summary().firstFault, 'ledger-shape');
  ledger.close();
  assert.equal(ledgerRows(f)[0].x, 'ledger-shape');
});

for (const mode of ['cjs', 'esm'])
  test(
    'inert actual ' + mode + ' loader: source identity and marked execution are separate',
    async () => {
      // Fresh literal inert code. Neither stored Next source nor an extracted Next
      // function is ever evaluated by this inspector control.
      const code =
        (mode === 'esm' ? 'export ' : '') +
        'function inert(a) {\n  const key = 1;\n  return key;\n}\n' +
        (mode === 'cjs' ? 'exports.inert = inert;\n' : '');
      const path = join(temporary, 'inspector.' + (mode === 'cjs' ? 'cjs' : 'mjs'));
      writeFileSync(path, code, { mode: 0o600 });
      const url = pathToFileURL(path).href,
        inspector = new Session();
      const post = (method, params = {}) =>
        new Promise((resolve, reject) =>
          inspector.post(method, params, (error, result) =>
            error ? reject(error) : resolve(result),
          ),
        );
      let scriptId,
        breakpointId,
        parsedCount = 0,
        hits = 0,
        marked = 0,
        fault = false;
      inspector.on('Debugger.scriptParsed', ({ params }) => {
        if (params.url === url) {
          scriptId = params.scriptId;
          parsedCount++;
        }
      });
      inspector.on('Debugger.paused', ({ params }) => {
        try {
          if (
            params.hitBreakpoints?.length !== 1 ||
            params.hitBreakpoints[0] !== breakpointId ||
            params.callFrames[0]?.location.scriptId !== scriptId ||
            params.callFrames[0]?.location.lineNumber !== 1
          ) {
            fault = true;
            return;
          }
          hits++;
          let sourceAnswered = false;
          inspector.post('Debugger.getScriptSource', { scriptId }, (error, result) => {
            sourceAnswered = true;
            if (
              error ||
              typeof result?.scriptSource !== 'string' ||
              createHash('sha256').update(result.scriptSource).digest('hex') !==
                createHash('sha256').update(code).digest('hex')
            )
              fault = true;
          });
          if (!sourceAnswered) fault = true;
          let answered = false;
          inspector.post(
            'Debugger.evaluateOnCallFrame',
            {
              callFrameId: params.callFrames[0].callFrameId,
              expression: 'a.__learnstackTransportProof === 1',
              returnByValue: true,
            },
            (error, result) => {
              answered = true;
              if (error || result.exceptionDetails || typeof result.result.value !== 'boolean')
                fault = true;
              else if (result.result.value) marked++;
            },
          );
          if (!answered) fault = true;
        } finally {
          inspector.post('Debugger.resume', (error) => {
            if (error) fault = true;
          });
        }
      });
      try {
        inspector.connect();
        await post('Debugger.enable');
        const result = await post('Debugger.setBreakpointByUrl', {
          url,
          lineNumber: 1,
          columnNumber: 2,
        });
        breakpointId = result.breakpointId;
        assert.equal(result.locations.length, 0);
        const module = mode === 'cjs' ? require(path) : await import(url);
        assert.equal(parsedCount, 1);
        assert.equal(hits, 0); // Parsing alone is no entry.
        const source = await post('Debugger.getScriptSource', { scriptId });
        const digest = createHash('sha256').update(source.scriptSource).digest('hex');
        assert.equal(digest, createHash('sha256').update(code).digest('hex'));
        assert.notEqual(
          digest,
          createHash('sha256')
            .update(code + '// foreign bytes\n')
            .digest('hex'),
        );
        module.inert({ __learnstackTransportProof: 0 });
        assert.equal(marked, 0);
        module.inert({ __learnstackTransportProof: 1 });
        assert.equal(marked, 1);
        assert.equal(hits, 2);
        assert.equal(fault, false);
        await post('Debugger.removeBreakpoint', { breakpointId });
      } finally {
        inspector.disconnect();
      }
    },
  );
