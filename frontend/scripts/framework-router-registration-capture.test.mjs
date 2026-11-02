// Coordinator component controls only. Exact extracted coordinator methods run
// against the authored owner and synthetic middleware; no Next runtime proof.
import assert from 'node:assert/strict';
import { EventEmitter } from 'node:events';
import { readFileSync } from 'node:fs';
import { createRequire } from 'node:module';
import test from 'node:test';

import { createResponseCompressionOwner } from '../framework/next-response-cleanup/owner.mjs';
import { requireRegisteredOwner } from '../apps/web/scripts/framework-router-fault-gate.mjs';

const appPackage = new URL('../apps/web/package.json', import.meta.url);
const runtimePath = new URL(
  '../apps/web/scripts/framework-router-fault-runtime.mjs',
  import.meta.url,
);
const ts = createRequire(appPackage)('typescript');
const source = readFileSync(runtimePath, 'utf8');
const tree = ts.createSourceFile(
  'runtime.mjs',
  source,
  ts.ScriptTarget.Latest,
  true,
  ts.ScriptKind.JS,
);
assert.equal(tree.parseDiagnostics.length, 0);
function one(predicate) {
  const found = [];
  const visit = (node) => {
    if (predicate(node)) found.push(node);
    ts.forEachChild(node, visit);
  };
  visit(tree);
  assert.equal(found.length, 1, 'One exact coordinator source subject');
  return found[0].getText(tree);
}
const variable = (name) =>
  one((node) => ts.isVariableDeclaration(node) && node.name.getText(tree) === name);
const method = (name) =>
  one((node) => ts.isMethodDeclaration(node) && node.name.getText(tree) === name);
const registration = one(
  (node) => ts.isFunctionDeclaration(node) && node.name?.text === 'registration',
);
const OWNER = Symbol.for('learnstack.next.responseCompressionOwner');

function fixture() {
  const response = new EventEmitter();
  const request = Object.freeze({});
  const owner = createResponseCompressionOwner(response);
  const terminal = response.rawListeners('finish')[0];
  const state = { response };
  const events = [];
  // Keep the coordinator's actual response check, capture, installation guard
  // and probes together. Only the response/request and middleware are controls.
  const api = new Function(
    'assert',
    'requireRegisteredOwner',
    'OWNER',
    'selected',
    'event',
    `const ${variable('refuse')};
     const ${variable('current')};
     ${registration}
     return {
       ${method('registrationPath')},
       ${method('ownerInstall')},
       registrationProbes: () => registration(selected),
     };`,
  )(assert, requireRegisteredOwner, OWNER, state, (kind) => events.push(kind));
  return {
    response,
    request,
    owner,
    terminal,
    state,
    events,
    api,
    install(middleware) {
      api.registrationPath(request, response, middleware);
      api.ownerInstall(response, owner, terminal);
    },
    async close() {
      owner.markNoCompression();
      await owner.join();
    },
  };
}
async function withFixture(work) {
  const value = fixture();
  try {
    await work(value);
  } finally {
    await value.close();
  }
}
const duplicate = () => {
  throw new Error('Duplicate response compression owner');
};
const refusedProbe = (value) => {
  assert.throws(() => value.api.registrationProbes(), { code: 'ERR_ASSERTION' });
  assert.deepEqual(value.events, ['missing-refused']);
};

test('captured middleware receives the exact request and response before duplicate credit', async () => {
  await withFixture((value) => {
    const before = ['finish', 'close', 'drain'].map((name) => value.response.rawListeners(name));
    const descriptor = Object.getOwnPropertyDescriptor(value.response, OWNER);
    let calls = 0;
    const middleware = (request, response, next) => {
      calls++;
      assert.equal(request, value.request);
      assert.equal(response, value.response);
      assert.equal(typeof next, 'function');
      duplicate();
    };
    value.install(middleware);
    assert.equal(value.state.request, value.request);
    assert.equal(value.state.middleware, middleware);
    value.api.registrationProbes();
    assert.equal(calls, 1);
    assert.deepEqual(value.events, ['missing-refused', 'duplicate-refused']);
    assert.deepEqual(Object.getOwnPropertyDescriptor(value.response, OWNER), descriptor);
    assert.deepEqual(
      ['finish', 'close', 'drain'].map((name) => value.response.rawListeners(name)),
      before,
    );
    assert.equal(value.owner.failureState().constructionUnconfirmed, false);
  });
});

test('missing or nonfunction capture refuses before storing an owner or capture', async () => {
  await withFixture((value) => {
    assert.throws(() => value.api.ownerInstall(value.response, value.owner, value.terminal), {
      code: 'ERR_ASSERTION',
    });
    for (const middleware of [undefined, null, {}])
      assert.throws(() => value.api.registrationPath(value.request, value.response, middleware), {
        code: 'ERR_ASSERTION',
      });
    assert.deepEqual(value.state, { response: value.response });
    assert.deepEqual(value.events, []);
  });
});

test('duplicate capture refuses and preserves the original request and middleware', async () => {
  await withFixture((value) => {
    value.api.registrationPath(value.request, value.response, duplicate);
    assert.throws(() => value.api.registrationPath({}, value.response, () => {}), {
      code: 'ERR_ASSERTION',
    });
    assert.equal(value.state.request, value.request);
    assert.equal(value.state.middleware, duplicate);
    assert.equal(value.state.owner, undefined);
    assert.deepEqual(value.events, []);
  });
});

test('foreign response refuses both capture and owner installation without state changes', async () => {
  await withFixture((value) => {
    const foreign = new EventEmitter();
    const refused = { message: 'Copied router fault observation refused' };
    assert.throws(() => value.api.registrationPath(value.request, foreign, duplicate), refused);
    assert.deepEqual(value.state, { response: value.response });
    value.api.registrationPath(value.request, value.response, duplicate);
    assert.throws(() => value.api.ownerInstall(foreign, value.owner, value.terminal), refused);
    assert.equal(value.state.owner, undefined);
    assert.equal(value.state.middleware, duplicate);
    assert.deepEqual(value.events, []);
  });
});

test('missing owner witness cannot earn even missing-registration credit', async () => {
  await withFixture((value) => {
    let calls = 0;
    value.api.registrationPath(value.request, value.response, () => {
      calls++;
      duplicate();
    });
    assert.throws(() => value.api.registrationProbes(), { code: 'ERR_ASSERTION' });
    assert.equal(calls, 0);
    assert.deepEqual(value.events, []);
  });
});

test('callback invocation before the expected duplicate error refuses duplicate credit', async () => {
  await withFixture((value) => {
    let calls = 0;
    value.install((_request, _response, next) => {
      calls++;
      next();
      duplicate();
    });
    refusedProbe(value);
    assert.equal(calls, 1);
  });
});

test('missing throw or wrong exception cannot earn duplicate credit', async () => {
  for (const middleware of [
    () => {},
    () => {
      throw new Error('Different registration error');
    },
    () => {
      throw new TypeError('Duplicate response compression owner');
    },
  ])
    await withFixture((value) => {
      value.install(middleware);
      refusedProbe(value);
    });
});

test('finish, close or drain listener mutation cannot earn duplicate credit', async () => {
  for (const name of ['finish', 'close', 'drain'])
    await withFixture((value) => {
      value.install((_request, response) => {
        response.on(name, () => {});
        duplicate();
      });
      refusedProbe(value);
    });
});

test('a recorded allocation cannot earn duplicate-registration credit', async () => {
  await withFixture((value) => {
    value.install(duplicate);
    value.state.stream = Object.freeze({});
    refusedProbe(value);
  });
});
