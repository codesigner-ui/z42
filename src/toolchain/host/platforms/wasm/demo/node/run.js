// Node.js hello-world demo for `@z42/wasm`.
//
// Mirrors the Tier 2 `hello_rust` example: load corelib via stdlib
// bundle resolver, invoke `Wasm.Hello.Main`, capture stdout, assert
// "Hello, World!\n".
//
// Run after `./build.sh` succeeds:
//     node demo/node/run.js
//
// Expected output:  [host] Hello, World!

import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

import { Z42VM } from '../../pkg-nodejs/z42_wasm.js';
import { bundleStdlibNode } from '../../js/stdlib-resolver.js';

const HERE = path.dirname(fileURLToPath(import.meta.url));
const ZBC_PATH = path.join(HERE, '..', 'fixtures', 'hello.zbc');

async function main() {
    if (!fs.existsSync(ZBC_PATH)) {
        console.error(`fixture missing: ${ZBC_PATH}`);
        console.error("re-run build.sh after compiling z42c");
        process.exit(1);
    }

    // 1. Build the stdlib resolver from packaged zpkg bytes.
    const resolver = await bundleStdlibNode();

    // 2. Set up stdout capture (with a [host] tag so it's obvious the
    //    sink fired rather than direct console.log).
    let captured = '';
    const stdoutHandler = (bytes) => {
        const text = new TextDecoder().decode(bytes);
        process.stdout.write(`[host] ${text}`);
        captured += text;
    };

    // 3. Construct VM, load .zbc, resolve entry, invoke.
    const vm = new Z42VM({ zpkgResolver: resolver, stdoutHandler });
    const zbcBytes = new Uint8Array(fs.readFileSync(ZBC_PATH));
    const module = vm.loadZbc(zbcBytes);
    const entry  = vm.resolveEntry(module, 'Wasm.Hello.Main');
    vm.invoke(entry);
    vm.dispose();

    // 4. Assert output.
    const expected = 'Hello, World!\n';
    if (captured !== expected) {
        console.error(`\nERROR: expected stdout ${JSON.stringify(expected)}, got ${JSON.stringify(captured)}`);
        process.exit(1);
    }
}

main().catch((err) => {
    console.error(`demo failed: ${err.stack ?? err}`);
    process.exit(1);
});
