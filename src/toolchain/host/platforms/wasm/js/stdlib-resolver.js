// Built-in ZpkgResolver helpers for `@z42/wasm`. Both flavours return a
// resolver function `(namespace) => Uint8Array | null` matching the
// ZpkgResolverFn shape in index.d.ts.
//
// Browser: fetch() each requested zpkg lazily; resolver is async-prepped
// upfront so the runtime call stays synchronous.
//
// Node.js: synchronous fs.readFileSync from the package's `stdlib/` dir.
//
// Both helpers ship a `bundleStdlib()` convenience that pre-loads ALL
// stdlib zpkgs into a `Map<string, Uint8Array>`-backed resolver, which
// is the recommended mode (no per-namespace I/O during invoke).

const STDLIB_NAMES = [
    'z42.core',
    'z42.io',
    'z42.collections',
    'z42.math',
    'z42.test',
];

/**
 * Build a synchronous resolver from a pre-populated map. The runtime
 * calls this once per dependency at `loadZbc` time; backing storage is
 * a plain Map for O(1) lookup.
 */
export function mapResolver(map) {
    return function resolve(namespace) {
        return map.get(namespace) ?? null;
    };
}

/**
 * Node-only: synchronously load every known stdlib zpkg from the
 * package's `stdlib/` directory. Returns a resolver function.
 *
 *     import { bundleStdlibNode } from '@z42/wasm/stdlib-resolver';
 *     const resolver = bundleStdlibNode();
 *     const vm = new Z42VM({ zpkgResolver: resolver });
 */
export async function bundleStdlibNode() {
    const fs = await import('node:fs');
    const path = await import('node:path');
    const url = await import('node:url');

    const here = path.dirname(url.fileURLToPath(import.meta.url));
    const stdlibDir = path.join(here, 'stdlib');

    const map = new Map();
    for (const name of STDLIB_NAMES) {
        const file = path.join(stdlibDir, `${name}.zpkg`);
        if (fs.existsSync(file)) {
            map.set(name, new Uint8Array(fs.readFileSync(file)));
        }
    }
    return mapResolver(map);
}

/**
 * Browser-only: pre-fetch every known stdlib zpkg via `fetch` relative
 * to `baseUrl`. Returns a Promise that resolves to a synchronous
 * resolver function once all fetches complete.
 *
 *     import { bundleStdlibBrowser } from '@z42/wasm/stdlib-resolver';
 *     const resolver = await bundleStdlibBrowser(new URL('./stdlib/', import.meta.url));
 *     const vm = new Z42VM({ zpkgResolver: resolver });
 */
export async function bundleStdlibBrowser(baseUrl) {
    if (!(baseUrl instanceof URL)) {
        baseUrl = new URL(baseUrl, location.href);
    }
    const map = new Map();
    await Promise.all(STDLIB_NAMES.map(async (name) => {
        const url = new URL(`${name}.zpkg`, baseUrl);
        const res = await fetch(url);
        if (res.ok) {
            const bytes = new Uint8Array(await res.arrayBuffer());
            map.set(name, bytes);
        }
    }));
    return mapResolver(map);
}
