// `@z42/wasm` umbrella entry — re-export everything from the resolved
// target (web by default; bundlers / node pick up the package.json
// `exports` map).
//
// `pkg-web/z42_wasm.js` exposes `init` (default) + `Z42VM` / `Z42VMModule`
// / `Z42VMEntry` after wasm-pack runs.

export { default } from './pkg-web/z42_wasm.js';
export * from './pkg-web/z42_wasm.js';
