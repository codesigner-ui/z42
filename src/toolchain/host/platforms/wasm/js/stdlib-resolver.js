// Built-in ZpkgResolver helpers for `@z42/wasm`. Both flavours return a
// resolver function `(namespace) => Uint8Array | null` matching the
// ZpkgResolverFn shape in index.d.ts.
//
// Resolution model — namespace index + filename fallback:
//   1. Each helper pre-fetches/reads `stdlib/index.json` which maps
//      namespaces (`"Std.IO"`) → zpkg filenames (`"z42.io.zpkg"`).
//      That index is produced by host `scripts/build-stdlib.sh`.
//   2. Each known stdlib zpkg is fetched/read once, by *filename*.
//   3. The returned resolver looks up the requested namespace in a
//      `Map<string, Uint8Array>` built from the index. When the index
//      is missing or doesn't list the namespace, the resolver falls
//      back to `<namespace>.zpkg` (legacy "one namespace per file"
//      convention; preserved for custom hosts that publish single-
//      namespace packages without shipping an index).
//
// Browser: fetch() each zpkg + index upfront; resolver call is sync.
// Node.js: synchronous fs.readFileSync from the package's `stdlib/` dir.
//
// Spec: docs/spec/archive/2026-05-12-fix-bundle-resolver-namespace-index/
//       docs/spec/archive/2026-05-12-add-wasm-tests/  (wasm side)

const STDLIB_NAMES = [
    'z42.core',
    'z42.io',
    'z42.collections',
    'z42.math',
    'z42.test',
    'z42.text',
];

/**
 * Build a synchronous resolver from a pre-populated namespace → bytes
 * map. The runtime calls this once per dependency at `loadZbc` time;
 * backing storage is a plain Map for O(1) lookup.
 */
export function mapResolver(byNamespace) {
    return function resolve(namespace) {
        return byNamespace.get(namespace) ?? null;
    };
}

/**
 * Node-only: synchronously load every namespace known via the stdlib
 * index + every legacy `<name>.zpkg` from the package's `stdlib/` dir.
 * Returns a resolver function.
 *
 *     import { bundleStdlibNode } from '@z42/wasm/stdlib-resolver';
 *     const resolver = await bundleStdlibNode();
 *     const vm = new Z42VM({ zpkgResolver: resolver });
 */
export async function bundleStdlibNode() {
    const fs = await import('node:fs');
    const path = await import('node:path');
    const url = await import('node:url');

    const here = path.dirname(url.fileURLToPath(import.meta.url));
    const stdlibDir = path.join(here, 'stdlib');

    const index = readIndexNode(fs, path, stdlibDir);
    const byFilename = readZpkgsNode(fs, path, stdlibDir, index);
    return mapResolver(buildNamespaceMap(index, byFilename));
}

/**
 * Browser-only: pre-fetch the stdlib index + every referenced zpkg via
 * `fetch` relative to `baseUrl`. Returns a Promise that resolves to a
 * synchronous resolver function once all fetches complete.
 *
 *     import { bundleStdlibBrowser } from '@z42/wasm/stdlib-resolver';
 *     const resolver = await bundleStdlibBrowser(
 *         new URL('./stdlib/', import.meta.url)
 *     );
 *     const vm = new Z42VM({ zpkgResolver: resolver });
 */
export async function bundleStdlibBrowser(baseUrl) {
    if (!(baseUrl instanceof URL)) {
        baseUrl = new URL(baseUrl, location.href);
    }
    const index = await fetchIndexBrowser(baseUrl);
    const byFilename = await fetchZpkgsBrowser(baseUrl, index);
    return mapResolver(buildNamespaceMap(index, byFilename));
}

// ── Helpers — index loaders. ────────────────────────────────────────

function readIndexNode(fs, path, stdlibDir) {
    const file = path.join(stdlibDir, 'index.json');
    if (!fs.existsSync(file)) return {};
    try {
        return JSON.parse(fs.readFileSync(file, 'utf8'));
    } catch {
        return {};
    }
}

async function fetchIndexBrowser(baseUrl) {
    try {
        const res = await fetch(new URL('index.json', baseUrl));
        if (!res.ok) return {};
        return await res.json();
    } catch {
        return {};
    }
}

// ── Helpers — zpkg loaders (load every file referenced by the index
// plus each legacy `<STDLIB_NAME>.zpkg`; de-dupe by filename). ──────

function readZpkgsNode(fs, path, stdlibDir, index) {
    const byFilename = new Map();
    const filenames = new Set(Object.values(index));
    for (const name of STDLIB_NAMES) {
        filenames.add(`${name}.zpkg`);  // legacy fallback
    }
    for (const filename of filenames) {
        const file = path.join(stdlibDir, filename);
        if (fs.existsSync(file)) {
            byFilename.set(filename, new Uint8Array(fs.readFileSync(file)));
        }
    }
    return byFilename;
}

async function fetchZpkgsBrowser(baseUrl, index) {
    const filenames = new Set(Object.values(index));
    for (const name of STDLIB_NAMES) {
        filenames.add(`${name}.zpkg`);  // legacy fallback
    }
    const byFilename = new Map();
    await Promise.all([...filenames].map(async (filename) => {
        const url = new URL(filename, baseUrl);
        const res = await fetch(url);
        if (res.ok) {
            byFilename.set(filename, new Uint8Array(await res.arrayBuffer()));
        }
    }));
    return byFilename;
}

// ── Helpers — assemble namespace → bytes from index + fallback. ─────

function buildNamespaceMap(index, byFilename) {
    const byNamespace = new Map();
    // 1. Index-driven (covers multi-namespace-per-file: z42.core ships
    //    Std + Std.Exceptions etc).
    for (const [namespace, filename] of Object.entries(index)) {
        const bytes = byFilename.get(filename);
        if (bytes) byNamespace.set(namespace, bytes);
    }
    // 2. Legacy filename fallback: namespace == basename (one zpkg per
    //    namespace). Hosts without an index still resolve correctly.
    for (const name of STDLIB_NAMES) {
        if (!byNamespace.has(name)) {
            const bytes = byFilename.get(`${name}.zpkg`);
            if (bytes) byNamespace.set(name, bytes);
        }
    }
    return byNamespace;
}
