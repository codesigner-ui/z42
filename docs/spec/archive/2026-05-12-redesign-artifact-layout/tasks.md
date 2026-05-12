# Tasks: redesign-artifact-layout

> 状态：🟢 已完成 | 创建：2026-05-12 | 完成：2026-05-12 | 类型：refactor（build infra）

## 变更说明

`artifacts/` 顶层只 2 个目录（`build/` + `packages/`）；`build/` 镜像 `src/` 子目录命名；引入 packages/ 组装包形态。

## 进度概览

- [x] Phase 1: cargo crate name + CARGO_TARGET_DIR
- [x] Phase 2: MSBuild output 重定向
- [x] Phase 3: build-stdlib.sh 输出路径
- [x] Phase 4: VM resolve_libs_dir() 新路径
- [x] Phase 5: package.sh 重写为 assembly
- [x] Phase 6: 其他 scripts 读取路径更新
- [x] Phase 7: docs/workflow + .gitignore + 删老路径
- [x] Phase 8: GREEN 验证 + commit + archive

## Phase 1: Cargo / VM lib 命名

- [x] 1.1 `src/runtime/Cargo.toml`：`[lib] name = "z42"` (current `z42_vm`)；调整 examples / tests 中对 `z42_vm` 的引用为新 crate 名
- [x] 1.2 `.cargo/config.toml`：新增 `[build] target-dir = "artifacts/build/runtime"`
- [x] 1.3 `cargo build --manifest-path src/runtime/Cargo.toml` 验证产物在新位置

## Phase 2: MSBuild output 重定向

- [x] 2.1 `src/compiler/Directory.Build.props`：`<BaseOutputPath>` / `<BaseIntermediateOutputPath>` 重定向 + `<UseAppHost>false</UseAppHost>` 避免 apphost framework-resolution 错误
- [x] 2.2 `dotnet build src/compiler/z42.slnx` 验证产物：`artifacts/build/compiler/<project>/bin/<config>/net10.0/`
- [x] 2.3 `dotnet test` 验证仍跑通：1233/1233 ✅

## Phase 3: stdlib build 输出路径

- [x] 3.1 `src/libraries/z42.workspace.toml`：`out_dir` / `cache_dir` 改为 `../../artifacts/build/libraries/${member_name}/${profile}/{dist,cache}`
- [x] 3.2 `scripts/build-stdlib.sh`：产物落新位置 + flat view 同步到 `artifacts/build/libs/<profile>/`
- [x] 3.3 验证：`./scripts/build-stdlib.sh` ✅ 6/6 zpkg

## Phase 4: VM resolve_libs_dir() 新路径

- [x] 4.1 `src/runtime/src/main.rs::resolve_libs_dir()`：搜索顺序 `$Z42_LIBS` → `<binary>/../libs/` → `artifacts/build/libs/{release,debug}/` → 老 `artifacts/z42/libs/`
- [x] 4.2 `src/toolchain/test-runner/src/bootstrap.rs::resolve_libs_dir()`：同步更新（in-process runner）
- [x] 4.3 `src/compiler/z42.Pipeline/SingleFileCompiler.cs::LocateDepIndex` + `LocateImportedSymbols`：扫 `artifacts/build/libs/{release,debug}/`
- [x] 4.4 `src/compiler/z42.Pipeline/PackageCompiler.BuildTarget.cs::BuildLibsDirs`：扫 `artifacts/build/libraries/<member>/<profile>/dist/` + `artifacts/packages/<pkg>/libs/`

## Phase 5: package.sh 重写为 assembly

- [x] 5.1 `scripts/package.sh`：组装 `artifacts/packages/z42-<version>-<rid>-<config>[-<variant>]/{bin,libs,native/include}/`
- [x] 5.2 验证：`./scripts/package.sh release` 产 `artifacts/packages/z42-0.1.0-osx-arm64-release/` ✅
  - bin/ ：z42c + z42vm + z42c.pdb
  - libs/：6 zpkg + 6 zsym
  - native/：libz42.a + libz42.dylib + include/{z42_abi.h, z42_host.h}
  - manifest.toml：version / rid / config / created

## Phase 6: 其他脚本读取路径更新

- [x] 6.1 `scripts/test-vm.sh`：z42c / z42vm 路径
- [x] 6.2 `scripts/regen-golden-tests.sh`：z42c 路径
- [x] 6.3 `scripts/test-stdlib.sh`：z42-test-runner / z42c 路径
- [x] 6.4 `scripts/test-cross-zpkg.sh`：z42c + z42vm 路径
- [x] 6.5 `scripts/test-dist.sh`：从 `artifacts/packages/<host-name>/` 读取 + `interp_only` 跳过
- [x] 6.6 `scripts/bench-run.sh`：z42vm 路径
- [x] 6.7 `src/runtime/build.rs`：z42c.dll 路径（`artifacts/build/compiler/z42.Driver/bin/`）

## Phase 7: docs/workflow + .gitignore + 删老路径

- [x] 7.1 `docs/workflow/building/{compiler,vm,stdlib,ios,wasm}.md`：output 路径
- [x] 7.2 `docs/workflow/testing/vm-tests.md`：测试路径
- [x] 7.3 `docs/workflow/debugging.md`：路径
- [x] 7.4 `docs/design/runtime/vm-architecture.md`：lookup 路径段
- [x] 7.5 `docs/design/stdlib/overview.md`：artifacts 布局段
- [x] 7.6 `docs/design/language/generics.md`：路径
- [x] 7.7 老 artifacts/ 子目录已 rm（`artifacts/compiler`, `artifacts/libraries`, `artifacts/rust`, `artifacts/z42`）

## Phase 8: GREEN 验证 + commit + archive

- [x] 8.1 `dotnet build src/compiler/z42.slnx` ✅
- [x] 8.2 `cargo build --manifest-path src/runtime/Cargo.toml --release` ✅
- [x] 8.3 `./scripts/build-stdlib.sh` —— 6/6 zpkg ✅
- [x] 8.4 `dotnet test src/compiler/z42.Tests/` —— 1233/1233 ✅
- [x] 8.5 `./scripts/test-vm.sh` —— 320/320 (interp 162 + JIT 158) ✅
- [x] 8.6 `./scripts/test-cross-zpkg.sh` —— 1/1 ✅
- [x] 8.7 `./scripts/test-stdlib.sh` —— 5/6 lib clean + z42.test 24/26（2 已知 failures 由 spec `rewrite-z42-test-runner-compile-time` 跟踪）
- [x] 8.8 `./scripts/package.sh release` —— packages/<name>/ 完整 ✅
- [x] 8.9 `./scripts/test-dist.sh` —— 320/320 ✅
- [ ] 8.10 commit + push + archive 到 `docs/spec/archive/2026-05-12-redesign-artifact-layout/`

## 备注

- 整个 refactor 不改 z42 语言 / IR / VM 行为（只改 artifact 路径 + crate 名）
- crate 重命名 `z42_vm` → `z42` 是个 breaking change for downstream Rust crates；本仓所有 consumer（test-runner / host crates / examples）已同步更新
- packages/ 是 v1 形态（无 tarball 压缩；只是目录）；release 自动化时加 `tar -czf`
- `cdylib`/`staticlib` 当前是历史构建残留；clean build 仅产 rlib（避免 anyhow 多版本冲突）。package.sh 在分发流程独立 cargo 命令产 cdylib/staticlib 留 follow-up（不阻塞本次 refactor）
- z42.test 库内 2 个 dogfood 测试失败是 in-process test-runner 的已知行为（subprocess legacy 一致），由 `rewrite-z42-test-runner-compile-time` spec 跟踪，不属于本 refactor scope
