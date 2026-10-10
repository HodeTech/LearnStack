// @vitest-environment node
import { describe, expect, it } from 'vitest';

import { assertFlightFallback } from '../../scripts/admission-browser.mjs';

const origin = 'https://fixture.localhost:3000';
const target = '/en/courses/fixture-course';
const flight = {
  url: origin + target + '?_rsc=fixture',
  type: 'Fetch',
  rsc: true,
  status: 503,
};
const document = { url: origin + target, type: 'Document', rsc: false, status: 200 };

describe('stock browser Flight fallback observation', () => {
  it.each([429, 503])('accepts one refused Flight then recovered document: %s', (status) => {
    expect(() =>
      assertFlightFallback(
        [
          { url: origin + '/_next/static/fixture.js', type: 'Script', rsc: false, status: 200 },
          { ...flight, status },
          document,
        ],
        origin,
        target,
        status,
      ),
    ).not.toThrow();
  });

  it.each([
    ['direct document only', [document]],
    ['failed Flight only', [flight]],
    ['reversed order', [document, flight]],
    ['extra Flight', [flight, flight, document]],
    ['Flight without RSC', [{ ...flight, rsc: false }, document]],
    ['Flight misclassified as document', [{ ...flight, type: 'Document' }, document]],
    ['wrong refusal status', [{ ...flight, status: 200 }, document]],
    ['failed recovered document', [flight, { ...document, status: 503 }]],
    ['document still marked RSC', [flight, { ...document, rsc: true }]],
    ['different target', [{ ...flight, url: origin + '/en/courses/other' }, document]],
    ['opposite origin', [{ ...flight, url: 'https://other.localhost:3000' + target }, document]],
    ['response not observed', [{ ...flight, status: null }, document]],
  ])('rejects a broken navigation proof: %s', (_name, events) => {
    expect(() => assertFlightFallback(events, origin, target, 503)).toThrow();
  });
});
