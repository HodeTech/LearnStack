'use strict';

// pnpm 9 evaluates the project hook before dependency mutation, including its
// frozen/headless path. Lifecycle preinstall runs too late for output ownership.
// pnpm also evaluates this file for run/exec. Keep their closed command paths
// usable; unknown or mutating commands require the wrapper's installation owner.
const { assertFrameworkInstallOwner } = require('./scripts/framework-installation.mjs');
const valueOptions = new Set(['--dir', '-C', '--filter', '-F', '--filter-prod', '--reporter']);
const values = process.argv.slice(2);
let command;
for (let at = 0; at < values.length; at++) {
  const value = values[at];
  if (valueOptions.has(value)) {
    at++;
    continue;
  }
  if (value.startsWith('-')) continue;
  command = value;
  break;
}
const mutations = new Set([
  'install',
  'i',
  'add',
  'update',
  'up',
  'remove',
  'rm',
  'uninstall',
  'link',
  'unlink',
  'import',
  'rebuild',
  'dedupe',
  'patch-commit',
  'prune',
  'deploy',
  'fetch',
]);
const readCommands = new Set(['run', 'exec', 'help', 'list', 'ls', 'why', 'root', 'bin']);
const scripts = new Set(
  [
    require('./package.json'),
    require('./apps/web/package.json'),
    require('./packages/sdk/package.json'),
    require('./packages/ui/package.json'),
    require('./packages/config/package.json'),
  ].flatMap((manifest) => Object.keys(manifest.scripts ?? {})),
);
const displayOnly =
  !command && values.some((value) => ['--version', '-v', '--help', '-h'].includes(value));
if (
  !displayOnly &&
  (mutations.has(command) || !command || (!readCommands.has(command) && !scripts.has(command)))
) {
  try {
    assertFrameworkInstallOwner();
  } catch {
    throw new Error('Use node frontend/scripts/install-frontend.mjs for an owned frontend restore');
  }
}

module.exports = { hooks: {} };
