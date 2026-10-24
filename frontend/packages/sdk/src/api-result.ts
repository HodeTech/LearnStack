import type { ApiProblem, AppError } from './response-policy.js';

/** Shared wire outcomes, with no provider exceptions or diagnostic bodies. */
export type ApiResult<T> =
  | { readonly kind: 'success'; readonly status: 200; readonly data: T }
  | {
      readonly kind: 'api-error';
      readonly status: number;
      readonly error: AppError;
      readonly problem: ApiProblem;
    }
  | {
      readonly kind: 'invalid-response';
      readonly reason:
        | 'malformed-json'
        | 'invalid-problem'
        | 'invalid-success'
        | 'unexpected-status';
    }
  | { readonly kind: 'transport-error' }
  | { readonly kind: 'invalid-request' }
  | { readonly kind: 'cancelled' };
