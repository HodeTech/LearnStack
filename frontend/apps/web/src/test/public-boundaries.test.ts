import { readdirSync, readFileSync } from 'node:fs';
import { dirname, join, relative, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

import { describe, expect, it } from 'vitest';

import {
  ADAPTER,
  buildSourceGraph,
  cacheFindings,
  clientBoundaryFindings,
  hasDirective,
  hasServerOnlyMarker,
  INGRESS,
  MIDDLEWARE,
  privateServerFiles,
  publicLayoutPolicyFindings,
  PUBLIC_LAYOUT,
  rawAuthorityFindings,
  reachable,
  SDK_SERVER,
  transportFindings,
} from './public-boundary-analysis';
import type { Finding, SourceCensus } from './public-boundary-analysis';

const frontend = resolve(dirname(fileURLToPath(import.meta.url)), '../../../..');
function productionSources(): SourceCensus {
  const sources: Record<string, string> = {};
  const visit = (directory: string) => {
    for (const entry of readdirSync(directory, { withFileTypes: true })) {
      const path = join(directory, entry.name);
      if (entry.isDirectory()) {
        // Runtime/test fixtures never enter the production graph.
        if (!['test', '__tests__', '__fixtures__', 'fixtures'].includes(entry.name)) visit(path);
      } else if (/\.tsx?$/.test(entry.name) && !/\.(?:test|spec|d)\.tsx?$/.test(entry.name)) {
        sources[relative(frontend, path).split('\\').join('/')] = readFileSync(path, 'utf8');
      }
    }
  };
  visit(join(frontend, 'apps/web/src'));
  for (const entry of readdirSync(join(frontend, 'packages'), { withFileTypes: true })) {
    if (!entry.isDirectory() || entry.name === 'config') continue;
    visit(join(frontend, 'packages', entry.name, 'src'));
  }
  return sources;
}
const sources = productionSources();
const production = buildSourceGraph(sources);
const publicRoots = [...production.files.keys()].filter((name) => name.includes('/app/(public)/'));
// P5 middleware supplies bootstrap; public page composition remains P6.
const ROOT_LAYOUT = 'apps/web/src/app/layout.tsx';
const renderRoots = [...publicRoots, ROOT_LAYOUT, MIDDLEWARE];
const publicGraph = reachable(production, renderRoots);
const clean = (findings: readonly Finding[], fix: string) =>
  expect(findings, fix + '\n' + JSON.stringify(findings, null, 2)).toEqual([]);
const graphWith = (file: string, source: string) =>
  buildSourceGraph({ ...sources, [file]: source });
const probe = 'apps/web/src/app/(public)/probe.ts';

/** ADR-0053 § Architecture Tests; Standards 07 § SDK / Public Site Renderer. */
describe('ADR-0053 production public boundaries', () => {
  it('the production census pins the actual public/private/SDK module graph', () => {
    expect([...production.files.keys()]).toEqual(
      expect.arrayContaining([
        ADAPTER,
        INGRESS,
        MIDDLEWARE,
        PUBLIC_LAYOUT,
        ROOT_LAYOUT,
        SDK_SERVER,
        'packages/sdk/src/index.ts',
        'packages/sdk/src/response-policy.ts',
        'packages/ui/src/index.ts',
        'apps/web/src/server/public-entry.ts',
      ]),
    );
    expect(publicRoots).toContain(PUBLIC_LAYOUT);
    expect(privateServerFiles(production)).toEqual(
      expect.arrayContaining([ADAPTER, INGRESS, SDK_SERVER]),
    );
    expect(reachable(production, [MIDDLEWARE])).toEqual(
      expect.arrayContaining([
        MIDDLEWARE,
        ADAPTER,
        INGRESS,
        'apps/web/src/server/public-entry.ts',
        SDK_SERVER,
        'packages/sdk/src/response-policy.ts',
      ]),
    );
    clean(production.unresolved, 'Fix: keep every runtime local import in the production census.');
  });
  it('Public_Renderer_Uses_Trusted_Ingress_And_Server_Only_Transport', () => {
    const missingMarker: Finding[] = hasServerOnlyMarker(production.files.get(ADAPTER)!)
      ? []
      : [
          {
            file: ADAPTER,
            line: 1,
            reason: 'Fix: import server-only in the configured public adapter.',
          },
        ];
    clean(
      [
        ...transportFindings(production),
        ...clientBoundaryFindings(production),
        ...rawAuthorityFindings(production, publicGraph),
        ...missingMarker,
      ],
      'Fix: keep authority behind native provenance and the configured server-only adapter.',
    );
  });
  it('Public_Renderer_Does_Not_Share_Tenant_Representations', () => {
    clean(
      [...cacheFindings(production, publicGraph), ...publicLayoutPolicyFindings(production)],
      'Fix: retain the dynamic/no-store public layout and request-local public data.',
    );
  });
  it('global fetch and all three hop-header setters have exactly one production adapter', () => {
    clean(transportFindings(production), 'Fix: use the configured public adapter.');
    // Allowed code must contain actual transport/setters: renaming it removes the exception.
    const renamed = Object.fromEntries(
      Object.entries(sources).map(([name, source]) => [name === ADAPTER ? probe : name, source]),
    );
    expect(
      transportFindings(buildSourceGraph(renamed)).filter((item) => item.file === probe),
    ).toHaveLength(4);
  });
  it('the configured production caller imports the server-only marker', () => {
    expect(hasServerOnlyMarker(production.files.get(ADAPTER)!)).toBe(true);
  });
  it('production Client Components cannot reach private public-server modules', () => {
    // This production census may be empty at P5. Planted graph cases below prove the mechanism.
    clean(clientBoundaryFindings(production), 'Fix: pass public data through server props.');
  });
  it('the actual public render closure contains no shared cache', () => {
    expect(publicGraph).toEqual(
      expect.arrayContaining([PUBLIC_LAYOUT, MIDDLEWARE, ADAPTER, SDK_SERVER]),
    );
    clean(
      cacheFindings(production, publicGraph),
      'Fix: keep public rendering dynamic and no-store.',
    );
  });
  it('public pages and their transitive helpers do not read raw authority', () => {
    const helpers = publicGraph.filter((name) => name !== INGRESS);
    expect(helpers).toEqual(
      expect.arrayContaining([PUBLIC_LAYOUT, MIDDLEWARE, 'apps/web/src/server/public-entry.ts']),
    );
    clean(rawAuthorityFindings(production, helpers), 'Fix: use verified ingress host/peer.');
  });
});

describe('public transport fence planted controls', () => {
  it.each([
    'fetch("/api");',
    'globalThis.fetch("/api");',
    'window["fetch"]("/api");',
    'const key = "fetch"; globalThis[key]("/api");',
    'const get = fetch; get("/api");',
    'const root = globalThis; const get = root.fetch; get("/api");',
    'const { fetch: get } = globalThis; get("/api");',
    'const get = globalThis.fetch.bind(globalThis); get("/api");',
    'globalThis.fetch.call(globalThis, "/api");',
    'export function createServerSdk(transport = globalThis.fetch) { return transport(url); }',
    'headers.set("X-LearnStack-Hop-Secret", secret);',
    'const name = "x-learnstack-visitor-address"; headers.append(name, peer);',
    'const headers = { "X-LearnStack-Host": host };',
    'const name = "X-LearnStack-Hop-Secret"; const headers = { [name]: secret };',
    'headers["x-learnstack-host"] = host;',
    'new Headers([["X-LearnStack-Hop-Secret", secret]]);',
  ])('refuses transport or setters: %s', (source) => {
    expect(transportFindings(buildSourceGraph({ [probe]: source }))).not.toEqual([]);
  });
  it('leaves the narrow adapter exemption, injected transport and ordinary headers clean', () => {
    clean(
      transportFindings(
        buildSourceGraph({
          [ADAPTER]: 'fetch(url); headers.set("X-LearnStack-Hop-Secret", secret);',
          [SDK_SERVER]:
            'export const request = (transport: Function) => transport(url, { cache: "no-store", headers: { Accept: "application/json" } });',
          [probe]:
            'export function load(fetch: Function) { return fetch("local"); } const message = "fetch"; headers.set("content-type", message);',
        }),
      ),
      'Injected transports/local functions are permitted.',
    );
  });
  it('follows an imported alias of a global-fetch declaration', () => {
    const graph = buildSourceGraph({
      [probe]: 'import { get } from "./helper"; get("/api");',
      'apps/web/src/app/(public)/helper.ts': 'export const get = globalThis.fetch;',
    });
    expect(transportFindings(graph).map((item) => item.file)).toContain(probe);
  });
});

describe('server boundary planted controls', () => {
  it('refuses a deleted or type-only server marker', () => {
    expect(
      hasServerOnlyMarker(
        graphWith(ADAPTER, sources[ADAPTER]!.replace("import 'server-only';", '')).files.get(
          ADAPTER,
        )!,
      ),
    ).toBe(false);
    expect(
      hasServerOnlyMarker(
        buildSourceGraph({ [ADAPTER]: 'import type {} from "server-only";' }).files.get(ADAPTER)!,
      ),
    ).toBe(false);
  });
  it.each([
    'import { createConfiguredPublicClient } from "@/server/configured-public-client";',
    'export { createConfiguredPublicClient } from "@/server/configured-public-client";',
    'export * from "@/server/configured-public-client";',
    'const load = () => import("@/server/configured-public-client");',
    'const path = "@/server/configured-public-client"; const load = () => import(path);',
  ])('detects a client reaching the adapter through a helper: %s', (edge) => {
    const graph = buildSourceGraph({
      ...sources,
      [probe]: '"use client"; import "./helper"; export const View = () => null;',
      'apps/web/src/app/(public)/helper.ts': edge,
    });
    expect(clientBoundaryFindings(graph)).toEqual(
      expect.arrayContaining([
        expect.objectContaining({ file: probe, reason: expect.stringContaining(ADAPTER) }),
      ]),
    );
  });
  it('detects an indirect private-env module outside the server directory', () => {
    const graph = buildSourceGraph({
      [probe]: '"use client"; import "./helper";',
      'apps/web/src/app/(public)/helper.ts':
        'export const secret = process.env.LEARNSTACK_PUBLIC_HOP_SECRET;',
    });
    expect(clientBoundaryFindings(graph)).toHaveLength(1);
  });
  it('permits UI-only imports and erased type imports/re-exports', () => {
    const graph = buildSourceGraph({
      ...sources,
      [probe]:
        '"use client"; import { View } from "@learnstack/ui"; import type { ConfiguredPublicClient } from "@/server/configured-public-client"; import { type IngressContext } from "@/server/ingress"; export type { PublicSite } from "@/server/public-entry"; export { type ConfiguredPublicClient } from "@/server/configured-public-client";',
      'packages/ui/src/index.ts': 'export const View = () => null;',
    });
    expect(hasDirective(graph.files.get(probe)!, 'use client')).toBe(true);
    expect(reachable(graph, [probe])).toEqual([probe, 'packages/ui/src/index.ts'].sort());
    clean(clientBoundaryFindings(graph), 'UI and erased type edges are clean.');
  });
  it('refuses an omitted helper and a runtime-generated module path', () => {
    expect(buildSourceGraph({ [probe]: 'import "./missing";' }).unresolved).toHaveLength(1);
    expect(buildSourceGraph({ [probe]: 'import(computeModule());' }).unresolved).toHaveLength(1);
  });
});

describe('public cache fence planted controls', () => {
  it('the layout policy detector refuses missing or weakened actual production declarations', () => {
    const withoutDynamic = graphWith(
      PUBLIC_LAYOUT,
      sources[PUBLIC_LAYOUT]!.replace("export const dynamic = 'force-dynamic';", ''),
    );
    expect(publicLayoutPolicyFindings(withoutDynamic)).toHaveLength(1);
    const withCaching = graphWith(
      PUBLIC_LAYOUT,
      sources[PUBLIC_LAYOUT]!.replace(
        "fetchCache = 'force-no-store'",
        "fetchCache = 'default-cache'",
      ),
    );
    expect(publicLayoutPolicyFindings(withCaching)).toHaveLength(1);
  });
  it.each([
    'export const revalidate = 60;',
    'export const dynamic = "force-static";',
    'export const fetchCache = "force-cache";',
    'export const fetchCache = "only-cache";',
    'export function generateStaticParams() { return []; }',
    'export const generateStaticParams = () => [];',
    'const params = () => []; export { params as generateStaticParams };',
    'import { unstable_cache as memo } from "next/cache"; memo(load);',
    'import * as caching from "next/cache"; caching.unstable_cache(load);',
    'const shared = new Map();',
    'const shared = new WeakMap();',
    'const Collection = globalThis.Map; const shared = new Collection();',
    'const bootstrapCache = {};',
    'let cachedSite;',
    'const bootstrapPromise = Promise.resolve(load());',
    'const bootstrapPromise = new Promise(load);',
    'const bootstrap = client.getSite();',
    'export const options = { cache: "force-cache" };',
    'const options = {}; options.cache = "force-cache";',
    'const mode = "force-cache"; export const options = { ["cache"]: mode };',
    'export const options = { next: { revalidate: 60 } };',
  ])('refuses caches in a transitive public helper: %s', (source) => {
    const helper = 'apps/web/src/lib/public-data.ts';
    const graph = buildSourceGraph({
      [PUBLIC_LAYOUT]: 'import "@/lib/public-data";',
      [helper]: source,
    });
    expect(
      cacheFindings(graph, reachable(graph, [PUBLIC_LAYOUT])).map((item) => item.file),
    ).toContain(helper);
  });
  it('allows dynamic/no-store and request-local data reuse', () => {
    const graph = buildSourceGraph({
      [PUBLIC_LAYOUT]:
        'export const dynamic = "force-dynamic"; export const revalidate = 0; export const fetchCache = "force-no-store"; const options = { cache: "no-store", next: { revalidate: 0 } }; export function load() { const byCourse = new Map(); return byCourse; }',
    });
    clean(
      cacheFindings(graph, [PUBLIC_LAYOUT]),
      'Dynamic/no-store/request-local reuse is permitted.',
    );
  });
  it('allows React cache because its RSC value lifetime is request-local', () => {
    const graph = buildSourceGraph({
      [PUBLIC_LAYOUT]:
        'import { cache as memo } from "react"; import * as React from "react"; const siteCache = memo(load); const bootstrapCache = React.cache(load); export function render() { const site = siteCache(); return site; }',
    });
    clean(
      cacheFindings(graph, [PUBLIC_LAYOUT]),
      'ADR-0053 permits request-local reuse including React.cache.',
    );
  });
  it('detects actual production layout and SDK no-store mutations', () => {
    const layout = graphWith(
      PUBLIC_LAYOUT,
      sources[PUBLIC_LAYOUT]!.replace('revalidate = 0', 'revalidate = 60'),
    );
    expect(
      cacheFindings(layout, reachable(layout, renderRoots)).map((item) => item.file),
    ).toContain(PUBLIC_LAYOUT);
    const sdk = graphWith(
      SDK_SERVER,
      sources[SDK_SERVER]!.replace("cache: 'no-store'", "cache: 'force-cache'"),
    );
    expect(cacheFindings(sdk, reachable(sdk, [MIDDLEWARE])).map((item) => item.file)).toContain(
      SDK_SERVER,
    );
  });
});

describe('raw authority fence planted controls', () => {
  it.each([
    'request.headers.get("host");',
    'const name = "x-forwarded-for"; request.headers.get(name);',
    'headers().get("X-LearnStack-Host");',
    'import { headers as readHeaders } from "next/headers"; readHeaders().get("host");',
    'request.headers["x-forwarded-host"];',
    'request.headers.host;',
    'request.socket.remoteAddress;',
    'request.nextUrl.hostname;',
    'const url = new URL(request.url); url.host;',
    'request.ip;',
  ])('refuses unsigned host/peer reads: %s', (source) => {
    expect(rawAuthorityFindings(buildSourceGraph({ [probe]: source }), [probe])).not.toEqual([]);
  });
  it('allows native capture and verified provenance in public helpers', () => {
    const graph = buildSourceGraph({
      [INGRESS]: 'request.socket.remoteAddress; request.headers.host;',
      [probe]:
        'const context = verifyProvenance(envelope, secret); const host = context.host; const peer = context.peer; headers.get("x-learnstack-ingress-provenance"); new URL("https://fixed.invalid").hostname; function describe(values: Map<string, string>) { return values.get("host"); }',
    });
    clean(
      rawAuthorityFindings(graph, [INGRESS, probe]),
      'Native ingress and authenticated payload reads are permitted.',
    );
  });
  it('detects a raw-host read planted into the actual public-entry helper', () => {
    const helper = 'apps/web/src/server/public-entry.ts';
    const graph = graphWith(
      helper,
      sources[helper]! + '\nconst unsignedHost = headers().get("host");\n',
    );
    expect(rawAuthorityFindings(graph, [helper]).map((item) => item.file)).toContain(helper);
  });
});
