import { isDeepStrictEqual } from 'node:util';
import { refuse } from './development-realm-errors.mjs';

export const developmentEventRetentionSeconds = 604800;
export const requiredDevelopmentEvents = Object.freeze([
  'LOGIN', 'LOGIN_ERROR', 'LOGOUT', 'LOGOUT_ERROR', 'CODE_TO_TOKEN',
  'CODE_TO_TOKEN_ERROR', 'REFRESH_TOKEN', 'REFRESH_TOKEN_ERROR',
]);

const object = value => value !== null && typeof value === 'object' && !Array.isArray(value);
const clone = value => structuredClone(value);

function strings(value, stage) {
  if (!Array.isArray(value) || value.length > 256
      || value.some(item => typeof item !== 'string' || !/^[A-Za-z0-9_.-]{1,128}$/.test(item))
      || new Set(value).size !== value.length) refuse(stage, 'event_policy_conflict');
  return [...value].sort();
}

function normalize(value, stage) {
  // Pinned ModelToRepresentation omits eventsExpiration when its stored value
  // is zero. This is the provider's default/indefinite state, not a new policy.
  const retention = value?.eventsExpiration == null ? 0 : value.eventsExpiration;
  if (!object(value) || typeof value.eventsEnabled !== 'boolean'
      || !Number.isSafeInteger(retention) || retention < 0)
    refuse(stage, 'event_policy_conflict');
  const result = clone(value);
  result.eventsExpiration = retention;
  result.enabledEventTypes = strings(value.enabledEventTypes, stage);
  // Pinned GET events/config expands the implicit default-saved selection.
  // Never interpret an unavailable/empty effective selection as permission to
  // replace the provider's other events with only our eight types.
  if (!result.enabledEventTypes.length) refuse(stage, 'event_policy_conflict');
  if (value.eventsListeners !== undefined) result.eventsListeners = strings(value.eventsListeners, stage);
  for (const field of ['adminEventsEnabled', 'adminEventsDetailsEnabled'])
    if (value[field] !== undefined && typeof value[field] !== 'boolean')
      refuse(stage, 'event_policy_conflict');
  if (Buffer.byteLength(JSON.stringify(result)) > 256 * 1024) refuse(stage, 'body_limit');
  return result;
}

/** Read-only qualification, including before ADR-0068's metadata preparation. */
export async function planRealmEvents(admin, stage = 'plan') {
  const before = normalize(await admin.get('events/config'), stage);
  let retention = before.eventsExpiration;
  if (retention === 0 && before.eventsEnabled === false) {
    // One bounded row is enough to disprove empty history. Do not enumerate,
    // discard or return provider event details to diagnostics/artifacts.
    const history = await admin.get('events?first=0&max=1');
    if (!Array.isArray(history) || history.length !== 0) refuse(stage, 'event_policy_conflict');
    retention = developmentEventRetentionSeconds;
  } else if (retention < developmentEventRetentionSeconds) {
    refuse(stage, 'event_policy_conflict');
  }
  const expected = { ...before, eventsEnabled: true, eventsExpiration: retention,
    enabledEventTypes: [...new Set([...before.enabledEventTypes, ...requiredDevelopmentEvents])].sort() };
  if (expected.enabledEventTypes.length > 256
      || Buffer.byteLength(JSON.stringify(expected)) > 256 * 1024) refuse(stage, 'body_limit');
  return Object.freeze({
    changed: !isDeepStrictEqual(before, expected),
    body: clone(expected),
    async verify() {
      const after = normalize(await admin.get('events/config'), 'readback');
      if (!isDeepStrictEqual(after, expected)) refuse('readback', 'readback_conflict');
    },
  });
}
