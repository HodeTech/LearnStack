import type { ServerResponse } from 'node:http';
import type { Transform } from 'node:stream';

export interface ResponseCompressionFailureState {
  readonly hasOperationFailure: boolean;
  readonly operationFailure: unknown;
  readonly hasCleanupFailure: boolean;
  readonly cleanupFailure: unknown;
  readonly hasEndFailure: boolean;
  readonly endFailure: unknown;
  readonly constructionUnconfirmed: boolean;
}

export interface ResponseCompressionOwner {
  markNoCompression(): void;
  allocationClosed(): boolean;
  createCompressor<T extends Transform>(factory: () => T): T;
  setResponseDrainListener<T extends (...args: unknown[]) => void>(listener: T): T;
  fail(error: unknown): void;
  abort(error: unknown): void;
  join(): Promise<void>;
  recordCleanupFailure(error: unknown): void;
  recordEndFailure(error: unknown): void;
  failureState(): ResponseCompressionFailureState;
}

export function createResponseCompressionOwner(response: ServerResponse): ResponseCompressionOwner;

export function joinResponseCompression<T>(
  response: ServerResponse,
  actualOperation: PromiseLike<T> | (() => T | PromiseLike<T>),
  explicitEndWork?: PromiseLike<unknown>,
): Promise<T>;
