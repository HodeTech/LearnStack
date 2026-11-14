import { isDeepStrictEqual as equal } from 'node:util';
import { refuse } from './development-realm-errors.mjs';
import { claimRoot } from './development-realm-mappers.mjs';

// ADR-0099 realm invariants owned by reconciliation: the link-only first-broker flow,
// the disabled idp_link application-initiated action and the `idp` ID-token mapper on
// learnstack-web. Each is created when absent, verified exactly when present and never
// repaired by overwriting an administrator's differing definition.
const encode = encodeURIComponent;
const omit = (value, keys) => Object.fromEntries(Object.entries(value).filter(([key]) => !keys.includes(key)));
export const firstBrokerFlow = 'learnstack-first-broker-login';
const reauthenticationFlow = 'learnstack-first-broker-reauthentication';
const otpFlow = 'learnstack-first-broker-conditional-otp';
export const idpMapperName = 'learnstack-identity-provider';

// The exact pinned 26.7.5 execution listing of the flow (verified against the image):
// [level, index, providerId or subflow alias, requirement].
const expectedExecutions = Object.freeze([
  [0, 0, 'idp-detect-existing-broker-user', 'REQUIRED'],
  [0, 1, 'idp-confirm-link', 'REQUIRED'],
  [0, 2, reauthenticationFlow, 'REQUIRED'],
  [1, 0, 'idp-username-password-form', 'REQUIRED'],
  [1, 1, otpFlow, 'CONDITIONAL'],
  [2, 0, 'conditional-user-configured', 'REQUIRED'],
  [2, 1, 'auth-otp-form', 'REQUIRED'],
]);

function list(value, stage, limit = 200) {
  if (!Array.isArray(value) || value.length > limit) refuse(stage, 'unsupported');
  return value;
}

function declared(desired) {
  const flows = list(desired.authenticationFlows ?? [], 'plan');
  const flow = flows.find(value => value?.alias === firstBrokerFlow);
  const action = list(desired.requiredActions ?? [], 'plan').find(value => value?.alias === 'idp_link');
  const web = list(desired.clients, 'plan').find(value => value?.clientId === 'learnstack-web');
  const mapper = (web?.protocolMappers ?? []).find(value => value?.name === idpMapperName);
  if (!flow || flows.filter(value => value.alias.startsWith('learnstack-first-broker-')).length !== 3
    || !action || action.enabled !== false || !mapper || mapper.protocolMapper !== 'oidc-usersessionmodel-note-mapper'
    || mapper.config?.['user.session.note'] !== 'identity_provider' || mapper.config?.['claim.name'] !== 'idp'
    || mapper.config?.['id.token.claim'] !== 'true' || mapper.config?.['access.token.claim'] !== 'false')
    refuse('plan', 'unsupported');
  return { flows, flow, mapper };
}

function executionShape(executions) {
  return list(executions, 'plan').map(execution => [execution.level, execution.index,
    execution.authenticationFlow === true ? execution.displayName : execution.providerId, execution.requirement]);
}

async function readFlow(admin, stage) {
  const flows = list(await admin.get('authentication/flows'), stage);
  const matches = flows.filter(value => value?.alias === firstBrokerFlow);
  if (matches.length > 1) refuse(stage, 'flow_conflict');
  if (!matches.length) {
    // A same-named orphan subflow would make creation adopt someone else's definition.
    if (flows.some(value => [reauthenticationFlow, otpFlow].includes(value?.alias))) refuse(stage, 'flow_conflict');
    return undefined;
  }
  const flow = matches[0];
  if (flow.providerId !== 'basic-flow' || flow.topLevel !== true || flow.builtIn !== false) refuse(stage, 'flow_conflict');
  const executions = await admin.get(`authentication/flows/${encode(firstBrokerFlow)}/executions`);
  if (!equal(executionShape(executions), expectedExecutions)) refuse(stage, 'flow_conflict');
  return { flow, executions };
}

async function readIdpLink(admin, stage) {
  const action = await admin.get('authentication/required-actions/idp_link');
  if (!action || action.alias !== 'idp_link' || action.providerId !== 'idp_link' || typeof action.enabled !== 'boolean')
    refuse(stage, 'unsupported');
  return action;
}

async function readWebMappers(admin, stage) {
  const found = list(await admin.get('clients?clientId=learnstack-web&max=2'), stage, 2);
  if (found.length !== 1 || found[0].clientId !== 'learnstack-web' || !found[0].id) refuse(stage, 'unsupported');
  const mappers = list(await admin.get(`clients/${encode(found[0].id)}/protocol-mappers/models`), stage);
  return { clientId: found[0].id, mappers };
}

function qualifyMapper(mappers, canonical, stage) {
  const owned = mappers.filter(mapper => mapper?.name === idpMapperName);
  // Another emitter of the `idp` claim would let a mapper other than the session note decide provenance.
  if (mappers.some(mapper => mapper?.name !== idpMapperName && claimRoot(mapper?.config?.['claim.name']) === 'idp'))
    refuse(stage, 'mapper_conflict');
  if (owned.length > 1) refuse(stage, 'mapper_conflict');
  if (owned.length && !equal(omit(owned[0], ['id']), canonical)) refuse(stage, 'mapper_conflict');
  return owned.length === 1;
}

/** Read-only preflight; the returned plan applies only creations and the one idp_link disable. */
export async function planFederationRealm(admin, desired) {
  const { flow: declaredFlow, mapper } = declared(desired);
  const canonical = structuredClone(mapper);
  const flow = await readFlow(admin, 'plan');
  const idpLink = await readIdpLink(admin, 'plan');
  const web = await readWebMappers(admin, 'plan');
  const hasMapper = qualifyMapper(web.mappers, canonical, 'plan');
  const actions = [];
  if (!flow) actions.push(async () => {
    const accepted = result => { if (![200, 201, 204].includes(result?.status ?? result)) refuse('apply', 'status'); };
    const base = 'authentication/flows';
    accepted(await admin.post(base, { alias: firstBrokerFlow, description: declaredFlow.description,
      providerId: 'basic-flow', topLevel: true, builtIn: false }));
    const step = async (parent, body, flowStep = false) =>
      accepted(await admin.post(`${base}/${encode(parent)}/executions/${flowStep ? 'flow' : 'execution'}`, body));
    const subflow = alias => desired.authenticationFlows.find(value => value.alias === alias);
    await step(firstBrokerFlow, { provider: 'idp-detect-existing-broker-user' });
    await step(firstBrokerFlow, { provider: 'idp-confirm-link' });
    await step(firstBrokerFlow, { alias: reauthenticationFlow, type: 'basic-flow', provider: 'registration-page-form',
      description: subflow(reauthenticationFlow).description }, true);
    await step(reauthenticationFlow, { provider: 'idp-username-password-form' });
    await step(reauthenticationFlow, { alias: otpFlow, type: 'basic-flow', provider: 'registration-page-form',
      description: subflow(otpFlow).description }, true);
    await step(otpFlow, { provider: 'conditional-user-configured' });
    await step(otpFlow, { provider: 'auth-otp-form' });
    // New executions start DISABLED (except a required-only choice); set each requirement exactly.
    const created = list(await admin.get(`${base}/${encode(firstBrokerFlow)}/executions`), 'apply');
    for (const [level, index, name, requirement] of expectedExecutions) {
      const execution = created.find(value => value.level === level && value.index === index);
      const actual = execution?.authenticationFlow === true ? execution.displayName : execution?.providerId;
      if (!execution || actual !== name) refuse('apply', 'flow_conflict');
      if (execution.requirement !== requirement)
        accepted(await admin.put(`${base}/${encode(firstBrokerFlow)}/executions`, { ...execution, requirement }));
    }
  });
  if (idpLink.enabled) actions.push(async () => {
    const status = await admin.put('authentication/required-actions/idp_link', { ...idpLink, enabled: false });
    if (![200, 204].includes(status)) refuse('apply', 'status');
  });
  if (!hasMapper) actions.push(async () => {
    const result = await admin.post(`clients/${encode(web.clientId)}/protocol-mappers/models`, structuredClone(canonical));
    if (result.status !== 201) refuse('apply', 'status');
  });
  return Object.freeze({
    changed: actions.length !== 0,
    actions: actions.length,
    async apply() { for (const action of actions) await action(); },
    async verify() {
      if (!await readFlow(admin, 'readback')) refuse('readback', 'readback_conflict');
      if ((await readIdpLink(admin, 'readback')).enabled !== false) refuse('readback', 'readback_conflict');
      const after = await readWebMappers(admin, 'readback');
      if (after.clientId !== web.clientId || !qualifyMapper(after.mappers, canonical, 'readback')) refuse('readback', 'readback_conflict');
      // Only the owned mapper was added; every other learnstack-web mapper is untouched.
      for (const original of web.mappers)
        if (!after.mappers.some(value => equal(value, original))) refuse('readback', 'readback_conflict');
      if (after.mappers.length !== web.mappers.length + (hasMapper ? 0 : 1)) refuse('readback', 'readback_conflict');
    },
  });
}
