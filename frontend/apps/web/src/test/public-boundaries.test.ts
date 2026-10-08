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
    'import * as caching from "next/cache"; const alias = caching; alias.unstable_cache(load);',
    'import * as caching from "next/cache"; const { unstable_cache: memo } = caching; memo(load);',
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
  it('refuses an imported namespace alias reexported by a local helper', () => {
    const helper = 'apps/web/src/lib/cache-alias.ts';
    const barrel = 'apps/web/src/lib/cache-barrel.ts';
    const graph = buildSourceGraph({
      [PUBLIC_LAYOUT]: 'import { alias } from "@/lib/cache-barrel"; alias.unstable_cache(load);',
      [barrel]: 'export { alias } from "./cache-alias";',
      [helper]: 'import * as caching from "next/cache"; export const alias = caching;',
    });
    clean(graph.unresolved, 'The imported alias belongs to the supported local source graph.');
    expect(cacheFindings(graph, reachable(graph, [PUBLIC_LAYOUT]))).toEqual(
      expect.arrayContaining([expect.objectContaining({ file: PUBLIC_LAYOUT })]),
    );
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
    'const { headers: incoming } = request; incoming.get("host");',
    'const incoming = request.headers; incoming["host"];',
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

// R2 regressions stay within static declarations and the already supported module graph.
describe('R2 bounded source-proof regressions', () => {
  it.each([
    'export { unstable_cache as memo } from "next/cache";',
    'import { unstable_cache as externalMemo } from "next/cache"; export { externalMemo as memo };',
  ])('refuses a named external cache function through a local barrel: %s', (barrelSource) => {
    const barrel = 'apps/web/src/lib/named-cache-barrel.ts';
    const graph = buildSourceGraph({
      [PUBLIC_LAYOUT]: 'import { memo } from "@/lib/named-cache-barrel"; memo(load);',
      [barrel]: barrelSource,
    });
    clean(graph.unresolved, 'The named barrel is a complete supported local module graph.');
    expect(cacheFindings(graph, reachable(graph, [PUBLIC_LAYOUT]))).toEqual(
      expect.arrayContaining([expect.objectContaining({ file: PUBLIC_LAYOUT })]),
    );
  });

  it.each([
    '(await headers()).get("host");',
    'import { headers as readHeaders } from "next/headers"; const incoming = await readHeaders(); incoming.get("x-forwarded-host");',
    'const incoming = request.headers; incoming.host;',
    'const { host } = request.headers;',
    'const { hostname } = request.nextUrl;',
  ])('refuses awaited or bound raw authority reads: %s', (source) => {
    const graph = buildSourceGraph({ [probe]: source });
    clean(graph.unresolved, 'The authority control has no unresolved local modules.');
    expect(rawAuthorityFindings(graph, [probe])).not.toEqual([]);
  });

  it.each([
    'const env = process.env; export const secret = env.LEARNSTACK_PUBLIC_HOP_SECRET;',
    'const { LEARNSTACK_PUBLIC_HOP_SECRET } = process.env; export const secret = LEARNSTACK_PUBLIC_HOP_SECRET;',
  ])('classifies aliased or destructured private environment modules: %s', (source) => {
    const helper = 'apps/web/src/lib/private-environment.ts';
    const graph = buildSourceGraph({
      [probe]: '"use client"; import { secret } from "@/lib/private-environment";',
      [helper]: source,
    });
    clean(graph.unresolved, 'The environment helper is a complete supported local module graph.');
    expect(privateServerFiles(graph)).toContain(helper);
    expect(clientBoundaryFindings(graph)).toEqual(
      expect.arrayContaining([expect.objectContaining({ file: probe })]),
    );
  });

  it('refuses a default global-fetch export and its imported call', () => {
    const helper = 'apps/web/src/lib/default-transport.ts';
    const graph = buildSourceGraph({
      [probe]: 'import get from "@/lib/default-transport"; get("/api");',
      [helper]: 'export default globalThis.fetch;',
    });
    clean(graph.unresolved, 'The default-export transport is a complete supported local graph.');
    expect(transportFindings(graph).map((item) => item.file)).toEqual(
      expect.arrayContaining([probe, helper]),
    );
  });

  it('permits a default injected transport and request-local React cache through a named barrel', () => {
    const transport = 'apps/web/src/lib/default-transport.ts';
    const barrel = 'apps/web/src/lib/request-local-cache.ts';
    const graph = buildSourceGraph({
      [PUBLIC_LAYOUT]:
        'import get from "@/lib/default-transport"; import { memo } from "@/lib/request-local-cache"; const siteCache = memo(load); get(transport, url);',
      [transport]: 'export default (transport: Function, url: string) => transport(url);',
      [barrel]: 'export { cache as memo } from "react";',
    });
    clean(graph.unresolved, 'Both clean exports belong to a complete supported local graph.');
    clean(
      transportFindings(graph),
      'A default export remains legal when its transport is injected.',
    );
    clean(
      cacheFindings(graph, reachable(graph, [PUBLIC_LAYOUT])),
      'React cache remains request-local through a named barrel.',
    );
  });

  it('leaves ordinary public environment aliases and bindings clean', () => {
    const helper = 'apps/web/src/lib/public-environment.ts';
    const graph = buildSourceGraph({
      [probe]: '"use client"; import { title } from "@/lib/public-environment";',
      [helper]:
        'const env = process.env; const { NEXT_PUBLIC_TITLE } = env; export const title = env.NEXT_PUBLIC_TITLE ?? NEXT_PUBLIC_TITLE;',
    });
    clean(graph.unresolved, 'Ordinary public environment controls have a complete graph.');
    expect(privateServerFiles(graph)).toEqual([]);
    clean(
      clientBoundaryFindings(graph),
      'Public environment variables carry no private authority.',
    );
  });

  it('leaves verified payload bindings, inert header reads and erased private type edges clean', () => {
    const helper = 'apps/web/src/lib/private-environment.ts';
    const graph = buildSourceGraph({
      [probe]:
        '"use client"; import type { SecretShape } from "@/lib/private-environment"; const { host, peer } = verifyProvenance(envelope, secret); const incoming = await headers(); incoming.get("x-learnstack-ingress-provenance"); const { pathname } = request.nextUrl;',
      [helper]:
        'const { LEARNSTACK_PUBLIC_HOP_SECRET } = process.env; export type SecretShape = string;',
    });
    clean(graph.unresolved, 'The clean control has no unresolved runtime module edges.');
    clean(clientBoundaryFindings(graph), 'Type-only private edges are erased.');
    clean(
      rawAuthorityFindings(graph, [probe]),
      'Verified payloads and inert request inputs carry no raw authority.',
    );
  });
});

describe('R2 static export and environment collection regressions', () => {
  it('resolves repeated constants on separate branches of a hop-header expression', () => {
    const graph = buildSourceGraph({
      [probe]:
        'const hyphen = "-"; const header = "x" + hyphen + "learnstack" + hyphen + "hop" + hyphen + "secret"; headers.set(header, secret);',
    });
    clean(graph.unresolved, 'The reused constants have no unresolved local module edges.');
    expect(transportFindings(graph)).not.toEqual([]);
  });

  it('terminates cyclic constant declarations as an unresolved static module path', () => {
    const graph = buildSourceGraph({
      [probe]: 'const left = right; const right = left; import(left);',
    });
    expect(graph.unresolved).toHaveLength(1);
  });
  it.each([
    [
      'export * from "next/cache";',
      'import { unstable_cache as memo } from "@/lib/cache-exports"; memo(load);',
    ],
    [
      'import { unstable_cache } from "next/cache"; export default unstable_cache;',
      'import memo from "@/lib/cache-exports"; memo(load);',
    ],
    [
      'export { unstable_cache as memo } from "next/cache";',
      'import * as caching from "@/lib/cache-exports"; caching.memo(load);',
    ],
  ])('refuses cache functions through static export forms: %s', (helperSource, consumerSource) => {
    const graph = buildSourceGraph({
      [PUBLIC_LAYOUT]: consumerSource,
      'apps/web/src/lib/cache-exports.ts': helperSource,
    });
    clean(graph.unresolved, 'The static exports belong to a complete local source graph.');
    expect(cacheFindings(graph, reachable(graph, [PUBLIC_LAYOUT]))).toEqual(
      expect.arrayContaining([expect.objectContaining({ file: PUBLIC_LAYOUT })]),
    );
  });

  it('refuses an awaited next/headers function reached through a local namespace barrel', () => {
    const graph = buildSourceGraph({
      [probe]:
        'import * as edge from "@/lib/header-exports"; (await edge.readHeaders()).get("host");',
      'apps/web/src/lib/header-exports.ts':
        'export { headers as readHeaders } from "next/headers";',
    });
    clean(graph.unresolved, 'The headers namespace belongs to a complete local source graph.');
    expect(rawAuthorityFindings(graph, [probe])).not.toEqual([]);
  });

  it('classifies private environment reads through a destructured process collection', () => {
    const helper = 'apps/web/src/lib/environment-collection.ts';
    const graph = buildSourceGraph({
      [probe]: '"use client"; import { secret } from "@/lib/environment-collection";',
      [helper]: 'const { env } = process; export const secret = env.LEARNSTACK_PUBLIC_HOP_SECRET;',
    });
    clean(
      graph.unresolved,
      'The destructured collection belongs to a complete local source graph.',
    );
    expect(privateServerFiles(graph)).toContain(helper);
    expect(clientBoundaryFindings(graph)).not.toEqual([]);
  });

  it('keeps request-local React cache clean across star, default and namespace exports', () => {
    const graph = buildSourceGraph({
      [PUBLIC_LAYOUT]:
        'import { cache as memo } from "@/lib/react-star"; import defaultMemo from "@/lib/react-default"; import * as ReactMemo from "@/lib/react-named"; const siteCache = memo(load); const bootstrapCache = defaultMemo(load); const dataCache = ReactMemo.memo(load);',
      'apps/web/src/lib/react-star.ts': 'export * from "react";',
      'apps/web/src/lib/react-default.ts': 'import { cache } from "react"; export default cache;',
      'apps/web/src/lib/react-named.ts': 'export { cache as memo } from "react";',
    });
    clean(graph.unresolved, 'All request-local export forms have a complete local source graph.');
    clean(
      cacheFindings(graph, reachable(graph, [PUBLIC_LAYOUT])),
      'React cache preserves its request-local lifetime through static exports.',
    );
  });

  it('keeps public environment collection bindings and type-only star exports clean', () => {
    const graph = buildSourceGraph({
      [probe]:
        '"use client"; import { title } from "@/lib/environment-collection"; import type { SecretShape } from "@/lib/private-types";',
      'apps/web/src/lib/environment-collection.ts':
        'const { env: publicEnvironment } = process; export const title = publicEnvironment.NEXT_PUBLIC_TITLE;',
      'apps/web/src/lib/private-types.ts': 'export type * from "@/server/private-types";',
      'apps/web/src/server/private-types.ts': 'export type SecretShape = string;',
    });
    clean(graph.unresolved, 'The clean controls have no unresolved runtime edges.');
    clean(
      clientBoundaryFindings(graph),
      'Public environment data and erased exports carry no private authority.',
    );
  });

  it('terminates when ordinary static export stars form a cycle', () => {
    const graph = buildSourceGraph({
      [PUBLIC_LAYOUT]: 'import { memo } from "@/lib/cycle-a"; memo(load);',
      'apps/web/src/lib/cycle-a.ts': 'export * from "./cycle-b";',
      'apps/web/src/lib/cycle-b.ts':
        'export * from "./cycle-a"; export const memo = (value: unknown) => value;',
    });
    clean(graph.unresolved, 'The cycle remains a complete local source graph.');
    clean(
      cacheFindings(graph, reachable(graph, [PUBLIC_LAYOUT])),
      'A local pure function is not an external shared cache.',
    );
  });
});

describe('R2 namespace exports and explicit export precedence', () => {
  it('refuses shared cache through a named import of an exported namespace', () => {
    const graph = buildSourceGraph({
      [PUBLIC_LAYOUT]:
        'import { caching } from "@/lib/namespace-cache"; caching.unstable_cache(load);',
      'apps/web/src/lib/namespace-cache.ts': 'export * as caching from "next/cache";',
    });
    clean(graph.unresolved, 'The exported namespace belongs to a complete local graph.');
    expect(cacheFindings(graph, reachable(graph, [PUBLIC_LAYOUT]))).not.toEqual([]);
  });

  it('refuses awaited raw headers through an exported namespace property path', () => {
    const graph = buildSourceGraph({
      [probe]:
        'import * as edge from "@/lib/namespace-headers"; (await edge.requestHeaders.headers()).get("host");',
      'apps/web/src/lib/namespace-headers.ts': 'export * as requestHeaders from "next/headers";',
    });
    clean(graph.unresolved, 'The namespace property path belongs to a complete local graph.');
    expect(rawAuthorityFindings(graph, [probe])).not.toEqual([]);
  });

  it('allows request-local React cache through an exported namespace alias', () => {
    const graph = buildSourceGraph({
      [PUBLIC_LAYOUT]:
        'import { requestCache as ReactCache } from "@/lib/namespace-react"; const siteCache = ReactCache.cache(load);',
      'apps/web/src/lib/namespace-react.ts': 'export * as requestCache from "react";',
    });
    clean(graph.unresolved, 'The request-local namespace belongs to a complete local graph.');
    clean(
      cacheFindings(graph, reachable(graph, [PUBLIC_LAYOUT])),
      'A namespace export preserves React cache request-local lifetime.',
    );
  });

  it('lets an explicit pure runtime export override an external cache star', () => {
    const graph = buildSourceGraph({
      [PUBLIC_LAYOUT]: 'import { unstable_cache as memo } from "@/lib/explicit-cache"; memo(load);',
      'apps/web/src/lib/explicit-cache.ts':
        'export * from "next/cache"; export const unstable_cache = (value: unknown) => value;',
    });
    clean(graph.unresolved, 'The explicit override belongs to a complete local graph.');
    clean(
      cacheFindings(graph, reachable(graph, [PUBLIC_LAYOUT])),
      'Explicit runtime exports override same-named star exports.',
    );
  });
});
