// ADR-0083. These edits are selected by parsed bindings and qualified input
// digests; a changed upstream shape is an error, never a best-effort rewrite.
import ts from 'typescript';

export const OWNER_KEY = 'learnstack.next.responseCompressionOwner';
const PREFIX = '__learnstackNextCleanup';

function invariant(condition, message) {
  if (!condition) throw new Error(`next patch: ${message}`);
}

export function parseJavascript(code, filename) {
  invariant(ts.version === '5.6.3', 'unqualified TypeScript parser');
  const source = ts.createSourceFile(
    filename,
    code,
    ts.ScriptTarget.Latest,
    true,
    ts.ScriptKind.JS,
  );
  invariant(source.parseDiagnostics.length === 0, `invalid JavaScript: ${filename}`);
  return source;
}

function nodes(root, predicate) {
  const matches = [];
  function visit(node) {
    if (predicate(node)) matches.push(node);
    ts.forEachChild(node, visit);
  }
  visit(root);
  return matches;
}

function one(matches, description) {
  invariant(matches.length === 1, `${description}: expected one, got ${matches.length}`);
  return matches[0];
}

function enclosingFunction(node) {
  while (
    node &&
    !ts.isFunctionDeclaration(node) &&
    !ts.isFunctionExpression(node) &&
    !ts.isArrowFunction(node)
  )
    node = node.parent;
  invariant(node?.body && ts.isBlock(node.body), 'expected a block-bodied function');
  return node;
}

function identifier(parameter) {
  invariant(ts.isIdentifier(parameter?.name), 'unexpected destructured parameter');
  return parameter.name.text;
}

function edit(node, source, text) {
  return { start: node.getStart(source), end: node.end, text };
}

function helperFunctions(ownerSource) {
  const ast = parseJavascript(ownerSource, 'owner.mjs');
  invariant(!nodes(ast, (n) => ts.isImportDeclaration(n)).length, 'owner imports cannot inline');
  return ['createResponseCompressionOwner', 'joinResponseCompression'].map((name) => {
    const fn = one(
      nodes(ast, (n) => ts.isFunctionDeclaration(n) && n.name?.text === name),
      name,
    );
    // Drop only the parsed export modifier. Keep the exact reviewed function text.
    return ownerSource.slice(fn.getStart(ast), fn.end).replace(/^export\s+/, '');
  });
}

function transformPipe(code, source, joinFunction) {
  const marker = one(
    nodes(source, (n) => ts.isStringLiteral(n) && n.text === 'failed to pipe response'),
    'pipe error marker',
  );
  const pipe = enclosingFunction(marker);
  invariant(
    pipe.parameters.length === 3 &&
      pipe.modifiers?.some((m) => m.kind === ts.SyntaxKind.AsyncKeyword),
    'unexpected pipe signature',
  );
  const response = identifier(pipe.parameters[1]);
  const endWork = identifier(pipe.parameters[2]);
  invariant(
    !nodes(
      pipe.body,
      (n) => n.kind === ts.SyntaxKind.ThisKeyword || (ts.isIdentifier(n) && n.text === 'arguments'),
    ).length,
    'pipe captures this/arguments',
  );
  const clause = one(
    nodes(pipe.body, ts.isCatchClause).filter(
      (c) => c.block.pos <= marker.pos && c.block.end >= marker.end,
    ),
    'pipe catch',
  );
  const abort = one(clause.block.statements.filter(ts.isIfStatement), 'abort decision');
  invariant(
    ts.isCallExpression(abort.expression) &&
      abort.expression.arguments.length === 1 &&
      ts.isReturnStatement(abort.thenStatement) &&
      !abort.elseStatement,
    'unexpected abort classification',
  );
  const error = clause.variableDeclaration?.name;
  invariant(
    ts.isIdentifier(error) && abort.expression.arguments[0].getText(source) === error.text,
    'abort error binding changed',
  );
  const responseOwner = `${response}[Symbol.for(${JSON.stringify(OWNER_KEY)})]`;
  const abortText =
    `const ${PREFIX}Abort=${abort.expression.getText(source)};` +
    `const ${PREFIX}Owner=${responseOwner};` +
    `if(${PREFIX}Owner){if(${PREFIX}Abort)${PREFIX}Owner.abort(${error.text});` +
    `else ${PREFIX}Owner.fail(${error.text});}` +
    `if(${PREFIX}Abort)return;`;
  const edits = [
    edit(abort, source, abortText),
    {
      start: pipe.body.getStart(source) + 1,
      end: pipe.body.getStart(source) + 1,
      text: `return await (${joinFunction})(${response},async()=>{`,
    },
    { start: pipe.body.end - 1, end: pipe.body.end - 1, text: `},${endWork});` },
  ];

  const chunk = one(
    nodes(source, (n) => ts.isStringLiteral(n) && n.text === 'failed to write chunk to response'),
    'writer error marker',
  );
  let writer = enclosingFunction(chunk);
  while (writer.parameters.length !== 2) writer = enclosingFunction(writer.parent);
  const res = identifier(writer.parameters[0]);
  const finish = one(
    nodes(
      writer.body,
      (n) =>
        ts.isCallExpression(n) &&
        ts.isPropertyAccessExpression(n.expression) &&
        n.expression.name.text === 'once' &&
        n.expression.expression.getText(source) === res &&
        n.arguments.length === 2 &&
        ts.isStringLiteral(n.arguments[0]) &&
        n.arguments[0].text === 'finish',
    ),
    'writer finish listener',
  );
  const callback = finish.arguments[1];
  invariant(
    ts.isArrowFunction(callback) &&
      callback.parameters.length === 0 &&
      ts.isBlock(callback.body) &&
      callback.body.statements.length === 1,
    'unexpected finish callback',
  );
  const resolve = callback.body.statements[0].getText(source);
  const terminal =
    `(()=>{const ${PREFIX}Terminal=()=>{` +
    `${res}.off('finish',${PREFIX}Terminal);${res}.off('close',${PREFIX}Terminal);${resolve};};` +
    `${res}.once('finish',${PREFIX}Terminal);return ${res}.once('close',${PREFIX}Terminal);})()`;
  edits.push(edit(finish, source, terminal));

  // The actual close resolves the current drain wait exactly once. A later
  // false write must not wait on a newly detached drain after that close.
  const drainAwait = one(
    nodes(
      writer.body,
      (n) =>
        ts.isAwaitExpression(n) &&
        ts.isPropertyAccessExpression(n.expression) &&
        n.expression.name.text === 'promise' &&
        ts.isIdentifier(n.expression.expression),
    ),
    'writer drain await',
  );
  const drainName = drainAwait.expression.expression.text;
  const drainDeclaration = one(
    nodes(
      writer.body,
      (n) =>
        ts.isVariableDeclaration(n) &&
        ts.isIdentifier(n.name) &&
        n.name.text === drainName &&
        n.initializer &&
        ts.isNewExpression(n.initializer),
    ),
    'writer drain declaration',
  );
  const reset = one(
    nodes(
      writer.body,
      (n) =>
        ts.isBinaryExpression(n) &&
        n.operatorToken.kind === ts.SyntaxKind.EqualsToken &&
        ts.isIdentifier(n.left) &&
        n.left.text === drainName &&
        ts.isNewExpression(n.right),
    ),
    'writer drain reset',
  );
  invariant(
    reset.right.getText(source) === drainDeclaration.initializer.getText(source),
    'drain constructor changed',
  );
  invariant(drainAwait.end <= reset.getStart(source), 'drain order changed');
  const covers = (outer, inner) => outer.pos <= inner.pos && outer.end >= inner.end;
  const guard = one(
    nodes(writer.body, (n) => {
      if (!covers(n, drainAwait) || !covers(n, reset)) return false;
      if (ts.isIfStatement(n))
        return (
          !n.elseStatement &&
          covers(n.thenStatement, drainAwait) &&
          covers(n.thenStatement, reset) &&
          ts.isPrefixUnaryExpression(n.expression) &&
          n.expression.operator === ts.SyntaxKind.ExclamationToken &&
          ts.isIdentifier(n.expression.operand)
        );
      return (
        ts.isBinaryExpression(n) &&
        n.operatorToken.kind === ts.SyntaxKind.BarBarToken &&
        ts.isIdentifier(n.left) &&
        covers(n.right, drainAwait) &&
        covers(n.right, reset)
      );
    }),
    'false-write drain guard',
  );
  if (ts.isBinaryExpression(guard)) {
    let expression = guard;
    while (
      ts.isParenthesizedExpression(expression.parent) ||
      (ts.isBinaryExpression(expression.parent) &&
        expression.parent.operatorToken.kind === ts.SyntaxKind.CommaToken)
    )
      expression = expression.parent;
    invariant(ts.isExpressionStatement(expression.parent), 'false-write guard result is observed');
  }
  const ok = ts.isIfStatement(guard) ? guard.expression.operand.text : guard.left.text;
  const write = enclosingFunction(drainAwait);
  one(
    nodes(
      write.body,
      (n) =>
        ts.isVariableDeclaration(n) &&
        ts.isIdentifier(n.name) &&
        n.name.text === ok &&
        n.initializer &&
        ts.isCallExpression(n.initializer) &&
        ts.isPropertyAccessExpression(n.initializer.expression) &&
        n.initializer.expression.expression.getText(source) === res &&
        n.initializer.expression.name.text === 'write',
    ),
    'actual response write binding',
  );
  const close = one(
    nodes(
      writer.body,
      (n) =>
        ts.isCallExpression(n) &&
        ts.isPropertyAccessExpression(n.expression) &&
        n.expression.expression.getText(source) === res &&
        n.expression.name.text === 'once' &&
        n.arguments.length === 2 &&
        ts.isStringLiteral(n.arguments[0]) &&
        n.arguments[0].text === 'close' &&
        ts.isArrowFunction(n.arguments[1]) &&
        ts.isBlock(n.arguments[1].body) &&
        nodes(
          n.arguments[1].body,
          (call) =>
            ts.isCallExpression(call) &&
            ts.isPropertyAccessExpression(call.expression) &&
            ts.isIdentifier(call.expression.expression) &&
            call.expression.expression.text === drainName &&
            call.expression.name.text === 'resolve',
        ).length === 1,
    ),
    'actual close drain callback',
  );
  const closeBody = close.arguments[1].body;
  const removal = one(
    nodes(
      closeBody,
      (n) =>
        ts.isCallExpression(n) &&
        ts.isPropertyAccessExpression(n.expression) &&
        n.expression.expression.getText(source) === res &&
        n.expression.name.text === 'off' &&
        n.arguments.length === 2 &&
        ts.isStringLiteral(n.arguments[0]) &&
        n.arguments[0].text === 'drain',
    ),
    'close removes actual drain listener',
  );
  one(
    nodes(
      writer.body,
      (n) =>
        ts.isCallExpression(n) &&
        ts.isPropertyAccessExpression(n.expression) &&
        n.expression.expression.getText(source) === res &&
        n.expression.name.text === 'on' &&
        n.arguments.length === 2 &&
        ts.isStringLiteral(n.arguments[0]) &&
        n.arguments[0].text === 'drain' &&
        n.arguments[1].getText(source) === removal.arguments[1].getText(source),
    ),
    'actual drain listener binding',
  );
  const closed = `${PREFIX}ResponseClosed`;
  edits.push(
    {
      start: writer.body.getStart(source) + 1,
      end: writer.body.getStart(source) + 1,
      text: `let ${closed}=false;`,
    },
    {
      start: closeBody.getStart(source) + 1,
      end: closeBody.getStart(source) + 1,
      text: `${closed}=true;`,
    },
    edit(drainAwait, source, `(${closed}?void 0:${drainAwait.getText(source)})`),
    edit(reset, source, `(!${closed}&&(${reset.getText(source)}))`),
  );
  return edits;
}

function transformCompression(code, source, createFunction) {
  const middleware = one(
    nodes(
      source,
      (n) =>
        ts.isFunctionExpression(n) && n.name?.text === 'compression' && n.parameters.length === 3,
    ),
    'compression middleware',
  );
  const response = identifier(middleware.parameters[1]);
  const owner = `${PREFIX}Owner`;
  const edits = [
    {
      start: middleware.body.getStart(source) + 1,
      end: middleware.body.getStart(source) + 1,
      text: `const ${owner}=(${createFunction})(${response});`,
    },
  ];
  const noCompression = one(
    nodes(middleware.body, (n) => ts.isFunctionDeclaration(n) && n.name?.text === 'nocompress'),
    'no-compression selection',
  );
  edits.push({
    start: noCompression.body.getStart(source) + 1,
    end: noCompression.body.getStart(source) + 1,
    text: `${owner}.markNoCompression();`,
  });
  const onHeaders = one(
    nodes(
      middleware.body,
      (n) => ts.isFunctionExpression(n) && n.name?.text === 'onResponseHeaders',
    ),
    'header selection',
  );
  edits.push({
    start: onHeaders.body.getStart(source) + 1,
    end: onHeaders.body.getStart(source) + 1,
    text: `if(${owner}.allocationClosed()){nocompress('owned allocation sealed');return;}`,
  });
  const factories = nodes(
    onHeaders.body,
    (n) =>
      ts.isCallExpression(n) &&
      ts.isPropertyAccessExpression(n.expression) &&
      ['createGzip', 'createDeflate'].includes(n.expression.name.text),
  );
  invariant(
    factories.length === 2 && new Set(factories.map((n) => n.expression.name.text)).size === 2,
    'unexpected compressor factories',
  );
  for (const factory of factories) {
    invariant(
      factory.arguments.length === 1 && ts.isIdentifier(factory.expression.expression),
      'unexpected compressor factory binding',
    );
    edits.push(edit(factory, source, `${owner}.createCompressor(()=>${factory.getText(source)})`));
  }
  const drain = one(
    nodes(onHeaders.body, (n) => ts.isFunctionExpression(n) && n.name?.text === 'onResponseDrain'),
    'compression internal drain',
  );
  let drainArgument = drain;
  while (ts.isParenthesizedExpression(drainArgument.parent)) drainArgument = drainArgument.parent;
  invariant(
    ts.isCallExpression(drainArgument.parent) &&
      drainArgument.parent.arguments.at(-1) === drainArgument &&
      ts.isPropertyAccessExpression(drainArgument.parent.expression) &&
      drainArgument.parent.expression.name.text === 'call' &&
      drainArgument.parent.arguments[0].getText(source) === response &&
      ts.isStringLiteral(drainArgument.parent.arguments[1]) &&
      drainArgument.parent.arguments[1].text === 'drain',
    'drain listener no longer uses captured native on',
  );
  // The parent can wrap the function in a ParenthesizedExpression; do not broaden
  // this hook to user drain listeners or a live res.on wrapper.
  edits.push(edit(drain, source, `${owner}.setResponseDrainListener(${drain.getText(source)})`));
  return edits;
}

function transformRouter(source, joinFunction) {
  const declaration = one(
    nodes(
      source,
      (n) =>
        ts.isVariableDeclaration(n) &&
        ts.isIdentifier(n.name) &&
        n.name.text === 'requestHandler' &&
        ts.isIdentifier(n.initializer) &&
        n.initializer.text === 'requestHandlerImpl',
    ),
    'router handler initializer',
  );
  return [
    edit(
      declaration.initializer,
      source,
      `function(...${PREFIX}Args){return (${joinFunction})(${PREFIX}Args[1],` +
        `()=>requestHandlerImpl.apply(this,${PREFIX}Args));}`,
    ),
  ];
}

export function transformJavascript({ code, filename, kind, ownerSource }) {
  const source = parseJavascript(code, filename);
  invariant(
    !code.includes(PREFIX) && !code.includes(OWNER_KEY),
    'already patched/colliding source',
  );
  const [createFunction, joinFunction] = helperFunctions(ownerSource);
  const edits =
    kind === 'pipe'
      ? transformPipe(code, source, joinFunction)
      : kind === 'compression'
        ? transformCompression(code, source, createFunction)
        : kind === 'router'
          ? transformRouter(source, joinFunction)
          : null;
  invariant(edits, 'unknown transformation kind');
  return edits.map((e, i) => ({ ...e, source: `learnstack-next-cleanup/${kind}-${i}.js` }));
}
