#!/usr/bin/env node
// Ensures `.next/types/routes.d.ts` exists with placeholder content so the
// `/// <reference path="./.next/types/routes.d.ts" />` line that Next.js
// auto-injects into `next-env.d.ts` (App Router type system, not opt-out
// in 15.5+) resolves at IDE-rest time — i.e. before any `next dev` /
// `next build` has run.
//
// Next.js overwrites this file the moment a real route compile happens, so
// the stub is throwaway. Idempotent: skips if a real file already exists.

import { mkdirSync, existsSync, writeFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const target = resolve(here, '..', '.next', 'types', 'routes.d.ts');

if (existsSync(target)) {
  process.exit(0);
}

mkdirSync(dirname(target), { recursive: true });
writeFileSync(
  target,
  '// Auto-generated stub by scripts/ensure-routes-stub.mjs.\n' +
    '// Replaced by `next dev` / `next build` with real typed-routes definitions.\n' +
    'export {};\n',
);
