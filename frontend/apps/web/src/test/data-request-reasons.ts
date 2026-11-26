import { readdirSync, readFileSync } from 'node:fs';
import { join } from 'node:path';

import snapshot from '../../../../../backend/openapi/v1.json';

const repository = join(process.cwd(), '../../..');
const DATA_REQUESTS = join(
  repository,
  'backend/src/Modules/Identity/LearnStack.Modules.Identity.Application/DataRequests',
);

/** The `lockey_*` reasons a C# source spells as a string literal, digits included. */
export function lockeyReasons(source: string): string[] {
  return [...source.matchAll(/"(lockey_[a-z0-9_]+)"/g)].map((match) => match[1]!);
}
/**
 * The reasons a response description names: with the wire prefix, or an artifact reason the document names without
 * it. Digits are part of a reason, so a key such as `lockey_limit_2` is never invisible.
 */
export function documentedReasons(description: string): string[] {
  return [...description.matchAll(/\b(?:lockey_([a-z0-9_]+)|(data_export_[a-z0-9_]+))\b/g)].map(
    (match) => 'lockey_' + (match[1] ?? match[2]),
  );
}
/** Every C# file of the Identity data-request code, however deep, relative to its directory. */
export function dataRequestSources(): string[] {
  return readdirSync(DATA_REQUESTS, { recursive: true, encoding: 'utf8' })
    .filter((name) => name.endsWith('.cs'))
    .sort();
}
/** Every `lockey_*` reason the committed OpenAPI document names in a response of a route under `routePrefixes`. */
export function documentedRouteReasons(routePrefixes: readonly string[]): Set<string> {
  const reasons = new Set<string>();
  for (const [path, item] of Object.entries(
    snapshot.paths as Record<
      string,
      Record<string, { responses?: Record<string, { description?: string }> }>
    >,
  )) {
    if (!routePrefixes.some((prefix) => path.startsWith(prefix))) continue;
    for (const operation of Object.values(item))
      for (const response of Object.values(operation.responses ?? {}))
        for (const reason of documentedReasons(response.description ?? '')) reasons.add(reason);
  }
  return reasons;
}
/** Every `lockey_*` reason the Identity data-request code spells, whatever file it is in. */
export function spelledReasons(): Set<string> {
  const reasons = new Set<string>();
  for (const file of dataRequestSources())
    for (const reason of lockeyReasons(readFileSync(join(DATA_REQUESTS, file), 'utf8')))
      reasons.add(reason);
  return reasons;
}
/**
 * What a catalogue must answer: the reasons the routes under `routePrefixes` document, and every one the Identity
 * data-request code spells. One scanner, read by the person's and the administrator's catalogue tests, each with its
 * own route prefixes.
 */
export function identityDataRequestReasons(routePrefixes: readonly string[]): Set<string> {
  return new Set([...documentedRouteReasons(routePrefixes), ...spelledReasons()]);
}
/** The routes a person reaches: the own routes and the retained entry's. */
export const PERSON_ROUTES = [
  '/api/v1/identity/me/data-requests',
  '/api/v1/identity/data-request-entry',
] as const;
/** The routes of an administrator of the context's group. */
export const ADMINISTRATOR_ROUTES = ['/api/v1/identity/data-requests'] as const;
/** The generic envelope a refusal maps by HTTP status and code, never by a reason sentence. */
export const ENVELOPE_REASONS: ReadonlySet<string> = new Set([
  'lockey_business_rule_violation',
  'lockey_not_found',
  'lockey_forbidden',
  'lockey_dependency_unavailable',
  'lockey_validation_failed',
  'lockey_invalid_value',
  'lockey_concurrency_conflict',
  'lockey_identifier_required',
]);
