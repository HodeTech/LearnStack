/** Parse before projection: duplicate aliases and lossy numeric input never reach JSON.stringify. */
export function parseCustomFieldJson(source: string, maximum = 4096): unknown {
  if (new TextEncoder().encode(source).length > maximum)
    throw new SyntaxError('Custom field input exceeds its limit');
  let offset = 0;
  const fail = (): never => {
    throw new SyntaxError('Custom field JSON is invalid');
  };
  const space = () => {
    while (/[\t\r\n ]/.test(source[offset] ?? '\0')) ++offset;
  };
  const string = (): string => {
    space();
    const token = /"(?:[\x20-\x21\x23-\x5b\x5d-\uffff]|\\(?:["\\/bfnrt]|u[0-9a-fA-F]{4}))*"/y;
    token.lastIndex = offset;
    const match = token.exec(source);
    if (!match) return fail();
    offset = token.lastIndex;
    const value = JSON.parse(match[0]) as string;
    for (const point of value) {
      const code = point.codePointAt(0)!;
      if (code === 0 || (code >= 0xd800 && code <= 0xdfff)) fail();
    }
    return value;
  };
  const value = (depth: number): unknown => {
    if (depth > 16) return fail();
    space();
    const first = source[offset];
    if (first === '"') return string();
    if (first === '{') {
      ++offset;
      space();
      const result: Record<string, unknown> = Object.create(null) as Record<string, unknown>;
      if (source[offset] === '}') {
        ++offset;
        return result;
      }
      for (;;) {
        const key = string();
        if (Object.hasOwn(result, key) || ['__proto__', 'prototype', 'constructor'].includes(key))
          return fail();
        space();
        if (source[offset++] !== ':') return fail();
        result[key] = value(depth + 1);
        space();
        const end = source[offset++];
        if (end === '}') return result;
        if (end !== ',') return fail();
      }
    }
    if (first === '[') {
      ++offset;
      space();
      const result: unknown[] = [];
      if (source[offset] === ']') {
        ++offset;
        return result;
      }
      for (;;) {
        if (result.length >= 1000) return fail();
        result.push(value(depth + 1));
        space();
        const end = source[offset++];
        if (end === ']') return result;
        if (end !== ',') return fail();
      }
    }
    for (const [token, result] of [
      ['true', true],
      ['false', false],
      ['null', null],
    ] as const)
      if (source.startsWith(token, offset)) {
        offset += token.length;
        return result;
      }
    const token = /-?(?:0|[1-9][0-9]*)(?:\.[0-9]+)?(?:[eE][+-]?[0-9]+)?/y;
    token.lastIndex = offset;
    const match = token.exec(source);
    if (!match) return fail();
    offset = token.lastIndex;
    const number = Number(match[0]);
    if (
      !Number.isFinite(number) ||
      (Number.isInteger(number) && !Number.isSafeInteger(number)) ||
      (number === 0 && /[1-9]/.test(match[0].split(/[eE]/)[0]!))
    )
      return fail();
    return number;
  };
  const result = value(0);
  space();
  if (offset !== source.length) fail();
  return result;
}

/** Integer controls preserve mathematical integrality before binary64 conversion can round it away. */
export function parseCustomFieldNumber(source: string, integer: boolean): number {
  const parsed = parseCustomFieldJson(source, 128);
  if (typeof parsed !== 'number') throw new SyntaxError('Custom field number is invalid');
  if (!integer) return parsed;
  const match = /^\s*(-?)(0|[1-9][0-9]*)(?:\.([0-9]+))?(?:[eE]([+-]?[0-9]+))?\s*$/.exec(source);
  if (!match) throw new SyntaxError('Custom field number is invalid');
  const coefficient = BigInt(match[2]! + (match[3] ?? ''));
  const exponent = BigInt(match[4] ?? '0') - BigInt((match[3] ?? '').length);
  let value: bigint;
  if (coefficient === 0n) value = 0n;
  else if (exponent >= 0n) {
    if (exponent > 15n) throw new SyntaxError('Custom field number is invalid');
    value = coefficient * 10n ** exponent;
  } else {
    if (-exponent > BigInt(match[2]!.length + (match[3] ?? '').length))
      throw new SyntaxError('Custom field number is invalid');
    const denominator = 10n ** -exponent;
    if (coefficient % denominator !== 0n) throw new SyntaxError('Custom field number is invalid');
    value = coefficient / denominator;
  }
  if (match[1] === '-') value = -value;
  if (value < -9007199254740991n || value > 9007199254740991n)
    throw new SyntaxError('Custom field number is invalid');
  return Number(value);
}
