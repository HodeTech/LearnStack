const stages = new Set([
  'preflight', 'discovery', 'authentication', 'version', 'manifest', 'lock',
  'plan', 'apply', 'readback', 'cleanup',
]);
const reasons = new Set([
  'configuration', 'identity_conflict', 'mapper_conflict', 'secret_conflict',
  'unsupported', 'response', 'status', 'redirect', 'deadline', 'cancelled',
  'transport', 'lock_busy', 'body_limit', 'readback_conflict', 'cleanup',
  'missing_web_secret',
]);

/** Neither upstream objects nor arbitrary caller text can become a diagnostic. */
export class RealmError extends Error {
  constructor(stage, reason) {
    const safeStage = stages.has(stage) ? stage : 'preflight';
    const safeReason = reasons.has(reason) ? reason : 'configuration';
    super(`realm: ${safeStage}/${safeReason}`);
    this.stage = safeStage;
    this.reason = safeReason;
  }
}

export function refuse(stage, reason) {
  throw new RealmError(stage, reason);
}

export function realmFailure(error) {
  return error instanceof RealmError ? error.message : 'realm: preflight/configuration';
}

/** The three development entry points share the same closed setup instruction. */
export function realmFailureNotice(error) {
  const message = realmFailure(error);
  return error instanceof RealmError && error.reason === 'missing_web_secret'
    ? message + '\nrealm: add KEYCLOAK_WEB_CLIENT_SECRET explicitly to the selected private file; see infra/keycloak/README.md'
    : message;
}
