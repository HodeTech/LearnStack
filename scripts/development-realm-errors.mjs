const stages = new Set([
  'preflight', 'discovery', 'authentication', 'version', 'manifest', 'lock',
  'plan', 'prepare', 'apply', 'readback', 'cleanup',
]);
const reasons = new Set([
  'configuration', 'identity_conflict', 'mapper_conflict', 'secret_conflict',
  'unsupported', 'response', 'status', 'redirect', 'deadline', 'cancelled',
  'transport', 'lock_busy', 'body_limit', 'readback_conflict', 'cleanup',
  'missing_web_secret', 'private_file',
]);

/** Neither upstream objects nor arbitrary caller text can become a diagnostic. */
export class RealmError extends Error {
  constructor(stage, reason, preparationDispatched = false) {
    const safeStage = stages.has(stage) ? stage : 'preflight';
    const safeReason = reasons.has(reason) ? reason : 'configuration';
    super(`realm: ${safeStage}/${safeReason}`);
    this.stage = safeStage;
    this.reason = safeReason;
    if (preparationDispatched === true) this.preparationDispatched = true;
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
  let message = realmFailure(error);
  if (!(error instanceof RealmError)) return message;
  if (error.reason === 'private_file')
    message += '\nrealm: selected private environment must be a regular, single-link file owned by the current user with owner-only permissions; use chmod 600 on that file; see infra/keycloak/README.md';
  else if (error.reason === 'missing_web_secret')
    message += '\nrealm: add KEYCLOAK_WEB_CLIENT_SECRET explicitly to the selected private file; see infra/keycloak/README.md';
  else if (error.reason === 'secret_conflict')
    message += '\nrealm: retain the confidential client secret in the selected private file; see infra/keycloak/README.md#secret-continuity-and-recovery';
  else if (error.reason === 'lock_busy')
    message += '\nrealm: verify the previous owner has stopped before removing its private lock; see infra/keycloak/README.md#lock-recovery';
  if (error.preparationDispatched === true)
    message += '\nrealm: profile metadata may already be durable; inspect current realm state before an explicit rerun; see infra/keycloak/README.md';
  return message;
}
