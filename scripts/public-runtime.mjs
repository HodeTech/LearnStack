import { realpathSync } from 'node:fs';
import { join } from 'node:path';
import { authorizationSourceEnvironment } from './demo-database.mjs';

/** Give the API its explicit application/dispatcher credentials and bounded configuration. */
export function publicApiEnvironment(values, configuration, base = process.env) {
  const environment = {};
  for (const name of ['PATH', 'HOME', 'DOTNET_ROOT', 'TMPDIR', 'APPDATA'])
    if (base[name] !== undefined) environment[name] = base[name];
  if (!values.ConnectionStrings__Default) throw new Error('Application credential is missing');
  if (!values.ConnectionStrings__OutboxDispatcher)
    throw new Error('Outbox dispatcher credential is missing');
  for (const key of ['Authentication__Tenant__Issuer', 'Identity__AuthorityId'])
    if (values[key]) environment[key] = values[key];
  for (const key of [
    'Identity__NativeSession__CurrentSecret',
    'Identity__NativeSession__PreviousSecret',
    'Identity__NativeSession__PreviousRetiresAt',
    'Identity__NativeSession__HttpsPort',
    'Identity__SelfBootstrap__Enabled',
  ]) {
    const value = values[key];
    if (value === undefined) continue;
    // Selection is bounded and explicit; the API owns secret, timestamp,
    // Boolean and port format validation and their cross-setting invariants.
    if (
      typeof value !== 'string' ||
      !value.trim() ||
      /[\x00-\x1f\x7f]/.test(value) ||
      Buffer.byteLength(value) > 256
    )
      throw new Error('Invalid API native configuration');
    environment[key] = value;
  }
  // This launcher owns native HTTPS on 3000. The production API retains its
  // independent default of 443; no secret or bootstrap opt-in is synthesized.
  environment.Identity__NativeSession__HttpsPort ??= '3000';
  environment.ConnectionStrings__Default = values.ConnectionStrings__Default;
  environment.ConnectionStrings__OutboxDispatcher = values.ConnectionStrings__OutboxDispatcher;
  // The selected pair is atomic. The ambient prerequisite allowlist cannot
  // supply either partner; role/endpoint qualification remains the API's.
  try { Object.assign(environment, authorizationSourceEnvironment(values)); }
  catch {
    throw new Error('Authorization source configuration is invalid');
  }
  if (values.ConnectionStrings__PlatformAdmin)
    environment.ConnectionStrings__PlatformAdmin = values.ConnectionStrings__PlatformAdmin;
  return {
    ...environment,
    ASPNETCORE_ENVIRONMENT: 'Development',
    ASPNETCORE_URLS: configuration.apiOrigin,
    Tenancy__TrustedHop__Networks__0: '127.0.0.1/32',
    Tenancy__TrustedHop__Secrets__0: configuration.secret,
  };
}
/** Development configuration must not consult the workstation's user secrets. */
export function publicDemoApiEnvironment(values, configuration, privateRoot, base = process.env) {
  const profile = realpathSync(privateRoot);
  return publicApiEnvironment(values, configuration, {
    ...base,
    HOME: profile,
    APPDATA: profile,
    TMPDIR: profile,
  });
}

/** Native configuration addons need an explicit, invocation-owned cache. */
export function publicWebEnvironment(values, privateRoot, base = process.env) {
  const environment = {
    PATH: base.PATH,
    TMPDIR: privateRoot,
    SWC_NATIVE_BINDING_CACHE: join(realpathSync(privateRoot), 'swc-native-cache'),
    NODE_ENV: 'production',
    NEXT_TELEMETRY_DISABLED: '1',
    NODE_TLS_REJECT_UNAUTHORIZED: '1',
  };
  for (const key of [
    'LEARNSTACK_PUBLIC_API_ORIGIN',
    'LEARNSTACK_PUBLIC_HOP_SECRET',
    'LEARNSTACK_PUBLIC_TLS_CERT',
    'LEARNSTACK_PUBLIC_TLS_KEY',
  ])
    environment[key] = values[key];
  return environment;
}
