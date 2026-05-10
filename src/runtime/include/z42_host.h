/* z42_host.h — Tier 1 Embedding / Hosting ABI
 *
 * Stable C ABI for host applications to embed the z42 VM:
 *   initialize → load .zbc → resolve entry → invoke → shutdown.
 *
 * Sibling to z42_abi.h, which covers "native code registers types into z42".
 * This header covers the opposite direction: "host app drives the VM".
 *
 * Spec: docs/design/runtime/embedding.md (Tier 1 C ABI §4)
 *       docs/spec/archive/2026-05-10-add-embedding-api/
 *
 * Status: H1 scaffold — declarations + single-instance lifecycle.
 *         load_zbc / resolve_entry / invoke return ERR_INTERNAL
 *         (placeholder for H2 implementation).
 *
 * ABI evolution rules (mirror z42_abi.h):
 *   - `abi_version` MUST stay at offset 0 across all versions.
 *   - New struct fields are appended only; layout never reordered.
 *   - All access is through z42_host_* functions, not direct struct manipulation.
 *   - Major version bumps are explicit semver-major breaks.
 *
 * v0.1 lifecycle: single VM instance per process.
 *   z42_host_initialize() succeeds at most once at a time;
 *   shutdown returns the process to "uninitialized" so initialize may be
 *   called again. Multi-instance / ALC-like contexts are tracked in
 *   docs/design/runtime/embedding.md §12 Deferred.
 */

#ifndef Z42_HOST_H
#define Z42_HOST_H

#include "z42_abi.h"   /* Z42Value / Z42Args / Z42Error */

#ifdef __cplusplus
extern "C" {
#endif

/* ── Version ─────────────────────────────────────────────────────────────── */

#define Z42_HOST_ABI_VERSION 1

/* ── Opaque handles ──────────────────────────────────────────────────────── */

/* Handle to the (singleton, in v0.1) VM instance. NULL = invalid. */
typedef struct Z42Host*   Z42HostRef;

/* Handle to a loaded .zbc module. NULL = invalid. */
typedef struct Z42Module* Z42ModuleRef;

/* Handle to a resolved entry (function / static method). NULL = invalid. */
typedef struct Z42Entry*  Z42EntryRef;

/* ── Execution mode ──────────────────────────────────────────────────────── */

typedef enum Z42ExecMode {
    Z42_EXEC_MODE_DEFAULT = 0,  /* decided by .zbc metadata + compile-time features */
    Z42_EXEC_MODE_INTERP  = 1,
    Z42_EXEC_MODE_JIT     = 2,  /* feature=jit must be enabled */
    Z42_EXEC_MODE_AOT     = 3   /* feature=aot must be enabled */
} Z42ExecMode;

/* ── Output sink ─────────────────────────────────────────────────────────── */

/*
 * stdout / stderr write sink. `length` does not include a NUL terminator
 * and the buffer is NOT guaranteed to be NUL-terminated. The sink callback
 * MUST NOT retain `bytes` past the call.
 *
 * Threading: callback runs on whichever thread emitted the output. Hosts
 * are responsible for thread-safe handling of `user_data`.
 */
typedef void (*Z42WriteSink)(const char* bytes, size_t length, void* user_data);

/* ── Configuration ───────────────────────────────────────────────────────── */

typedef struct Z42HostConfig {
    uint32_t      abi_version;        /* MUST equal Z42_HOST_ABI_VERSION */
    uint32_t      reserved;

    Z42ExecMode   exec_mode;
    size_t        heap_initial_bytes; /* 0 = VM default */
    size_t        heap_max_bytes;     /* 0 = unbounded */

    Z42WriteSink  stdout_sink;        /* NULL = real stdout */
    Z42WriteSink  stderr_sink;        /* NULL = real stderr */
    void*         sink_user_data;

    /* NULL-terminated array of module search paths. NULL = in-memory only. */
    const char* const* search_paths;
} Z42HostConfig;

/* ── Status codes ────────────────────────────────────────────────────────── */

typedef enum Z42HostStatus {
    Z42_HOST_OK                  = 0,

    /* Lifecycle */
    Z42_HOST_ERR_ALREADY_INIT    = 1,
    Z42_HOST_ERR_NOT_INIT        = 2,
    Z42_HOST_ERR_BAD_CONFIG      = 3,   /* abi_version mismatch / null cfg / etc. */
    Z42_HOST_ERR_FEATURE_OFF     = 4,   /* requested mode disabled at compile time */

    /* Module loading */
    Z42_HOST_ERR_BAD_ZBC         = 10,  /* magic / checksum failure */
    Z42_HOST_ERR_VERIFICATION    = 11,  /* IR verification failure */

    /* Resolution / invocation */
    Z42_HOST_ERR_ENTRY_NOT_FOUND = 20,
    Z42_HOST_ERR_ARG_MISMATCH    = 21,

    /* Execution */
    Z42_HOST_ERR_VM_EXCEPTION    = 30,  /* z42 throw escaped the entry */

    /* Catch-all */
    Z42_HOST_ERR_INTERNAL        = 99
} Z42HostStatus;

/* ── Lifecycle API ───────────────────────────────────────────────────────── */

/*
 * Initialize the singleton VM instance. Thread-safe: serialized internally.
 *
 * Preconditions:
 *   - cfg != NULL and cfg->abi_version == Z42_HOST_ABI_VERSION
 *   - The process has no live VM (otherwise returns ERR_ALREADY_INIT).
 *
 * On success, *out_host is set to a non-NULL handle.
 */
Z42HostStatus z42_host_initialize(const Z42HostConfig* cfg,
                                  Z42HostRef* out_host);

/*
 * Load a .zbc module from a byte buffer. The bytes must remain valid
 * for the duration of the call; the VM copies what it needs.
 *
 * H1 status: returns ERR_INTERNAL with message "H2: load_zbc not yet
 * implemented". Real loading lands in H2.
 */
Z42HostStatus z42_host_load_zbc(Z42HostRef host,
                                const uint8_t* bytes, size_t length,
                                Z42ModuleRef* out_module);

/*
 * Resolve an entry by fully qualified name. FQN format:
 *   "namespace.Type::method"  (static method or member)
 *   "namespace::function"     (top-level function)
 *
 * H1 status: returns ERR_INTERNAL ("H2: resolve_entry not yet implemented").
 */
Z42HostStatus z42_host_resolve_entry(Z42HostRef host, Z42ModuleRef module,
                                     const char* fqn,
                                     Z42EntryRef* out_entry);

/*
 * Synchronously invoke an entry. `args` and `n` must match the entry's
 * signature. `out_result` may be NULL to discard the return value.
 *
 * H1 status: returns ERR_INTERNAL ("H2: invoke not yet implemented").
 */
Z42HostStatus z42_host_invoke(Z42EntryRef entry,
                              const Z42Value* args, size_t n,
                              Z42Value* out_result);

/*
 * Replace stdout / stderr sink at runtime. NULL restores the configured
 * default. user_data is passed to subsequent callbacks.
 */
Z42HostStatus z42_host_set_stdout_sink(Z42HostRef host,
                                       Z42WriteSink sink, void* user_data);
Z42HostStatus z42_host_set_stderr_sink(Z42HostRef host,
                                       Z42WriteSink sink, void* user_data);

/*
 * Last error from the calling thread (TLS). On success Z42HostStatus
 * resets `code` to 0 and `message` to a stable empty string.
 */
Z42Error z42_host_last_error(Z42HostRef host);

/*
 * Tear down the singleton VM. After this returns OK, all Z42ModuleRef /
 * Z42EntryRef issued by this VM are invalidated; calling host APIs with
 * stale handles returns ERR_NOT_INIT. A subsequent z42_host_initialize
 * is allowed.
 */
Z42HostStatus z42_host_shutdown(Z42HostRef host);

#ifdef __cplusplus
} /* extern "C" */
#endif

#endif /* Z42_HOST_H */
