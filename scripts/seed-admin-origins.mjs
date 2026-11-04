import { join } from 'node:path';
import { repositoryRoot } from './compose.mjs';
import { developmentSeederRuntimeEnvironment } from './demo-database.mjs';
import { DemoCommandError } from './demo-process.mjs';
import { RealmError } from './development-realm-errors.mjs';

const resultPrefix = 'seed-result: ';
const canonicalId = /^[0-9a-f]{8}-[0-9a-f]{4}-7[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/;

/** A bounded result from this owned successful process; never a persisted installation receipt. */
export function seedAdministrationOriginTenantIds(output) {
  const rows = typeof output === 'string' ? output.split(/\r?\n/).filter(line => line.startsWith(resultPrefix)) : [];
  if (rows.length !== 1 || Buffer.byteLength(rows[0]) > 512) throw refused();
  const payload = rows[0].slice(resultPrefix.length);
  let result;
  try { result = JSON.parse(payload); } catch { throw refused(); }
  if (!result || Array.isArray(result) || JSON.stringify(result) !== payload
      || Object.keys(result).join(',') !== 'version,freshTenantIds,qualifiedOriginTenantIds' || result.version !== 2
      || [result.freshTenantIds, result.qualifiedOriginTenantIds].some(ids => !Array.isArray(ids) || ids.length > 2
        || ids.some(id => typeof id !== 'string' || !canonicalId.test(id)) || new Set(ids).size !== ids.length)) throw refused();
  // The later fixed runner rechecks actual markers and mappings. This process
  // output is selection data, never completion or mutation authority.
  const selected = [...new Set([...result.freshTenantIds, ...result.qualifiedOriginTenantIds])];
  if (selected.length > 2) throw refused();
  return Object.freeze(selected);
}

/** The fixed .NET owner validates every selected ID against its compiled two-origin manifest. */
export async function adoptSeedAdministrationOrigins(processes, values, tenantIds, preparationDispatched = false) {
  if (Array.isArray(tenantIds) && tenantIds.length === 0) return;
  if (tenantIds !== 'all' && (!Array.isArray(tenantIds) || tenantIds.length > 2
      || tenantIds.some(id => typeof id !== 'string' || !canonicalId.test(id))
      || new Set(tenantIds).size !== tenantIds.length)) throw refused();
  try { await processes.runPrivateInput('dotnet', [join(repositoryRoot,
    'backend/src/LearnStack.Tools.Seeder/bin/Release/net10.0/LearnStack.Tools.Seeder.dll'), '--seed-admin-origins-stdin'],
  JSON.stringify({ version: 1, tenantIds }), {
    cwd: join(repositoryRoot, 'backend'), env: developmentSeederRuntimeEnvironment(values),
    label: 'Fixed administration-origin adoption', timeout: 180000,
  }); } catch (error) {
    throw new RealmError('identity', error instanceof DemoCommandError
      && ['deadline', 'cancelled', 'cleanup'].includes(error.reason) ? error.reason : 'seed_refused', preparationDispatched);
  }
}

function refused() { return new Error('seed: administration-origin selection was refused.'); }
