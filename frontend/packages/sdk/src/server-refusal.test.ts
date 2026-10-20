import { expect, it, vi } from 'vitest';

import * as root from './index';
import { createServerSdk, PublicRequestRefusalError } from './server';
import type { PublicFetch } from './server';

it('keeps a typed local transport refusal distinct from network failure without retrying', async () => {
  const error = new PublicRequestRefusalError();
  expect(error.message).toBe('Public request refused');
  expect(root).not.toHaveProperty('PublicRequestRefusalError');
  const transport = vi.fn<PublicFetch>().mockRejectedValue(error);
  await expect(createServerSdk(transport).getSite()).resolves.toEqual({ kind: 'invalid-request' });
  expect(transport).toHaveBeenCalledOnce();
});

it.each([
  new Error('Public request refused'),
  Object.assign(new Error('Public request refused'), { name: 'PublicRequestRefusalError' }),
  { name: 'PublicRequestRefusalError', message: 'Public request refused' },
  new TypeError('Network failed'),
])('does not classify provider text or lookalike errors as local refusal (%#)', async (error) => {
  const transport = vi.fn<PublicFetch>().mockRejectedValue(error);
  await expect(createServerSdk(transport).getSite()).resolves.toEqual({ kind: 'transport-error' });
  expect(transport).toHaveBeenCalledOnce();
});

it('retains cancellation precedence when transport refuses the same operation', async () => {
  const controller = new AbortController();
  const transport = vi.fn<PublicFetch>().mockImplementation(async () => {
    controller.abort();
    throw new PublicRequestRefusalError();
  });
  await expect(createServerSdk(transport).getSite({ signal: controller.signal })).resolves.toEqual({
    kind: 'cancelled',
  });
  expect(transport).toHaveBeenCalledOnce();
});

it('does not treat a response-body failure as local operation construction refusal', async () => {
  const response = new Response(null);
  vi.spyOn(response, 'json').mockRejectedValue(new PublicRequestRefusalError());
  await expect(createServerSdk(async () => response).getSite()).resolves.toEqual({
    kind: 'transport-error',
  });
});
