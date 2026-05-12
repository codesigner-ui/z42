/* hello_c — embedding API end-to-end example (C).
 *
 * Spec:
 *   docs/design/runtime/embedding.md §9.2
 *   docs/spec/archive/2026-05-12-enable-hello-c-desktop/
 *
 * Companion to `examples/hello_rust` — same hello-world flow, but
 * against the raw Tier 1 C ABI in `src/runtime/include/z42_host.h`.
 *
 * Build + run end-to-end via the sibling `build.sh`. The script:
 *   - ensures runtime `libz42.a` exists (cargo build --release)
 *   - ensures z42c + stdlib zpkgs exist (./scripts/build-stdlib.sh)
 *   - compiles `examples/embedding/hello.z42` → out/hello.zbc
 *   - cc main.c + links libz42.a + platform native libs
 *   - runs the binary and asserts stdout == "[host] hello, world\n"
 *
 * Manual run:
 *   ./out/hello_c ./out/hello.zbc <libs_dir>
 *
 * Expected stdout:
 *   [host] hello, world
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
    if (z42_host_resolve_entry(host, module, "Hello.Main", &entry) != Z42_HOST_OK) {
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
