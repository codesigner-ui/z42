/* hello_c — embedding API reference example (C).
 *
 * Spec: docs/design/embedding.md §9.2.
 *
 * This file is a *reference implementation* of the same flow as
 * `examples/hello_rust`, written against the Tier 1 C ABI in
 * `src/runtime/include/z42_host.h`. It compiles standalone against
 * the public headers but linking it against the runtime requires a
 * `staticlib` / `cdylib` crate-type for `z42_vm`, which is not enabled
 * by default in v0.1 (the desktop build path is rlib-only).
 *
 * See README.md in this directory for build status and the H4 mobile
 * platform plans that ship the staticlib configuration end-to-end.
 *
 * Once the staticlib is available, the build is roughly:
 *
 *   cargo build --manifest-path src/runtime/Cargo.toml --release \
 *       --features staticlib    # not yet wired
 *   gcc -I src/runtime/include \
 *       -o hello_c main.c \
 *       -L artifacts/rust/release -lz42_vm \
 *       $(cargo rustc -- --print=native-static-libs 2>&1 | grep "native-static-libs:" | sed "s/.*native-static-libs: //")
 *
 * Run:
 *   ./hello_c <hello.zbc> <libs_dir>
 *
 * Expected stdout:
 *   [host] Hello, World!
 */

#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include "z42_host.h"

static void on_stdout(const char* bytes, size_t length, void* user_data) {
    (void)user_data;
    /* Tag with [host] so it's obvious the host sink fired. */
    fputs("[host] ", stdout);
    fwrite(bytes, 1, length, stdout);
}

int main(int argc, char** argv) {
    if (argc != 3) {
        fprintf(stderr, "usage: %s <hello.zbc> <libs_dir>\n", argv[0]);
        return 2;
    }
    const char* zbc_path = argv[1];
    const char* libs_dir = argv[2];

    /* NULL-terminated array for search_paths. */
    const char* search_paths[2] = { libs_dir, NULL };

    Z42HostConfig cfg = (Z42HostConfig){0};
    cfg.abi_version       = Z42_HOST_ABI_VERSION;
    cfg.exec_mode         = Z42_EXEC_MODE_INTERP;
    cfg.stdout_sink       = on_stdout;
    cfg.search_paths      = search_paths;

    Z42HostRef host = NULL;
    if (z42_host_initialize(&cfg, &host) != Z42_HOST_OK) {
        Z42Error e = z42_host_last_error(NULL);
        fprintf(stderr, "z42_host_initialize: %s\n", e.message);
        return 1;
    }

    FILE* f = fopen(zbc_path, "rb");
    if (!f) { perror("fopen"); z42_host_shutdown(host); return 1; }
    fseek(f, 0, SEEK_END);
    long len = ftell(f);
    fseek(f, 0, SEEK_SET);
    unsigned char* buf = (unsigned char*)malloc((size_t)len);
    if (fread(buf, 1, (size_t)len, f) != (size_t)len) {
        fprintf(stderr, "short read of %s\n", zbc_path);
        free(buf); fclose(f); z42_host_shutdown(host); return 1;
    }
    fclose(f);

    Z42ModuleRef module = NULL;
    if (z42_host_load_zbc(host, buf, (size_t)len, &module) != Z42_HOST_OK) {
        Z42Error e = z42_host_last_error(NULL);
        fprintf(stderr, "z42_host_load_zbc: %s\n", e.message);
        free(buf); z42_host_shutdown(host); return 1;
    }
    free(buf);

    Z42EntryRef entry = NULL;
    if (z42_host_resolve_entry(host, module, "Embedding.Hello.Main", &entry) != Z42_HOST_OK) {
        Z42Error e = z42_host_last_error(NULL);
        fprintf(stderr, "z42_host_resolve_entry: %s\n", e.message);
        z42_host_shutdown(host); return 1;
    }

    Z42Value result;
    memset(&result, 0, sizeof(result));
    if (z42_host_invoke(entry, NULL, 0, &result) != Z42_HOST_OK) {
        Z42Error e = z42_host_last_error(NULL);
        fprintf(stderr, "z42_host_invoke: %s\n", e.message);
        z42_host_shutdown(host); return 1;
    }

    z42_host_shutdown(host);
    return 0;
}
