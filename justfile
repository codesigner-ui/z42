# z42 task runner
# Usage: just <task> [args]
# Run `just --list` (or just `just`) for all tasks.
#
# Top-level tasks:
#   build       Build compiler + runtime
#   test        Run all tests
#   bench       Run benchmarks (P1, not yet implemented)
#   clean       Remove build artifacts
#   ci          CI pipeline (= build + test)
#   platform    Cross-platform tasks (P4, not yet implemented)
#
# Conventions:
#   - Verbs first, kebab-case forbidden in task combos (use space-separated subtargets)
#   - Wraps existing scripts/*.sh; scripts remain independently callable
#   - Placeholder tasks for P1/P2/P3/P4 print "待实施" and exit non-zero

# Default: list available tasks
default:
    @just --list --unsorted

# ──────────── Build ────────────

# Build everything: runtime + compiler
build: build-runtime build-compiler

# Build the Rust VM
build-runtime:
    cargo build --manifest-path src/runtime/Cargo.toml

# Build the C# compiler
build-compiler:
    dotnet build src/compiler/z42.slnx

# Build standard library (zpkg artifacts)
build-stdlib *args:
    ./scripts/build-stdlib.sh {{args}}

# ──────────── Test ────────────

# Run all tests: compiler + VM + cross-zpkg
test: test-compiler test-vm test-cross-zpkg

# Run C# compiler tests (xUnit)
test-compiler:
    dotnet test src/compiler/z42.Tests/z42.Tests.csproj

# Run VM golden tests (mode: interp | jit, default interp)
test-vm mode="interp":
    ./scripts/test-vm.sh {{mode}}

# Run cross-zpkg integration tests (mode: interp | jit, default interp)
test-cross-zpkg mode="interp":
    ./scripts/test-cross-zpkg.sh {{mode}}

# Run only tests affected by changes vs a base ref (default: HEAD).
#   just test-changed                # working tree + staged changes
#   just test-changed main           # branch delta vs main
#   just test-changed --dry-run      # print plan, don't execute
# Maps file paths to coarse-grained test scopes (see docs/design/testing.md
# "增量测试").
test-changed *args:
    ./scripts/test-changed.sh {{args}}

# Run stdlib library [Test] tests via z42-test-runner. (R3 minimal v0.2 + R5)
#
#   just test-stdlib                # run every stdlib lib's tests
#   just test-stdlib z42.math       # run only z42.math's tests
#
# Each test file `src/libraries/<lib>/tests/*.z42` is compiled to .zbc then
# fed to z42-test-runner, which subprocesses to z42vm with --entry per [Test]
# method. Setup/Teardown not yet supported (R3 full version).
test-stdlib lib="":
    ./scripts/test-stdlib.sh {{lib}}

# (P3 placeholder) Run cross-stdlib integration tests
test-integration:
    @echo "❌ P3 待实施：integration 测试" && exit 1

# ──────────── Benchmark ────────────

# Run all benchmarks (P1.A: Rust criterion only; BDN + e2e in P1.B/C)
bench: bench-rust

# Run Rust micro-benchmarks (criterion)
bench-rust *args:
    cargo bench --manifest-path src/runtime/Cargo.toml {{args}}

# Run C# compiler benchmarks (BenchmarkDotNet).
#   just bench-compiler                    # interactive picker
#   just bench-compiler --list flat        # inspect available
#   just bench-compiler --filter "*Lex*"   # filter (quote glob to avoid shell expansion)
#   just bench-compiler --filter "*"       # run all (also: just bench-compiler-all)
# Recipe runs in shebang shell with globbing disabled (set -f) so {{args}}
# pass through unmolested even when they contain `*`.
bench-compiler *args:
    #!/usr/bin/env bash
    set -euf -o pipefail
    dotnet run --project src/compiler/z42.Bench -c Release -- {{args}}

# Convenience: run all C# compiler benchmarks (no interactive prompt; CI-friendly).
bench-compiler-all:
    #!/usr/bin/env bash
    set -euf -o pipefail
    dotnet run --project src/compiler/z42.Bench -c Release -- --filter '*'

# Run end-to-end .z42 scenarios with hyperfine.
#   just bench-e2e          # full: warmup=3, runs=10, all scenarios (~1-2 min)
#   just bench-e2e --quick  # quick: warmup=1, runs=3, 2 scenarios only (~30s)
# Output: bench/results/e2e.json (conforms to bench/baseline-schema.json)
bench-e2e *args:
    ./scripts/bench-run.sh {{args}}

# Diff current bench results against a baseline JSON.
#   just bench-diff                                 # auto-baseline (bench/baselines/main-<os>.json)
#   just bench-diff bench/baselines/main-linux.json # explicit baseline path
# Exit codes: 0 = no regression; 1 = regression > threshold; 2 = error.
bench-diff baseline="":
    #!/usr/bin/env bash
    set -euo pipefail
    if [[ -z "{{baseline}}" ]]; then
        ./scripts/bench-diff.sh --current bench/results/e2e.json
    else
        ./scripts/bench-diff.sh --current bench/results/e2e.json --baseline {{baseline}}
    fi

# ──────────── Platform (P4 placeholder) ────────────

# (P4 placeholder) Cross-platform tasks: just platform <name> <action>
#   <name>: wasm | android | ios
#   <action>: build | test | demo | ...
platform name action *args:
    @echo "❌ P4 待实施：platform {{name}} {{action}} {{args}}" && exit 1

# ──────────── Distribution ────────────

# Build distributable package: artifacts/z42/
package mode="release":
    ./scripts/package.sh {{mode}}

# End-to-end test of the distribution package (mode: interp | jit, default interp)
test-dist mode="interp":
    ./scripts/test-dist.sh {{mode}}

# ──────────── Misc ────────────

# Remove all build artifacts
clean:
    cargo clean --manifest-path src/runtime/Cargo.toml
    dotnet clean src/compiler/z42.slnx
    rm -rf artifacts/

# Regenerate golden test .zbc files from .z42 sources
regen-golden:
    ./scripts/regen-golden-tests.sh

# Audit C# code for missing using directives
audit-csharp:
    ./scripts/audit-missing-usings.sh

# CI pipeline: build + test (extended in P1 with bench-quick)
ci: build test
    @echo "✅ CI passed"
