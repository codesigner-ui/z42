# Proposal: 重设计 artifacts/ 目录布局 + 引入 packages/ 发布形态

## Why

现有 `artifacts/` 散乱：
- `artifacts/compiler/<project>/bin/` (dotnet)
- `artifacts/rust/{debug,release}/` (cargo)
- `artifacts/libraries/<lib>/{dist,cache}/` (z42c workspace)
- `artifacts/z42/{bin,libs}/` (package.sh sync 目标)

四套并存，路径耦合在 ~10 个 shell 脚本里；新引入 RID / 跨平台时无统一规则。同时缺一个"组装好的可分发包"形态 —— 想直接 `cp -r ... ~/.z42/` 装到机器上没现成产物。

## What Changes

### 新 `artifacts/` 布局（顶层只 2 个目录）

```
artifacts/
├── build/                                             # 直接编译结果（toolchain 原生 + 我们的归一化）
│   ├── compiler/                                      # dotnet 默认布局
│   ├── runtime/                                       # cargo 默认布局（target/<triple>/<config>/）
│   ├── libraries/
│   │   └── <lib>/<Config>/                            # <Config> 只 Debug/Release（当前 stdlib 平台无关）
│   │       ├── dist/{*.zpkg, *.zsym}
│   │       └── cache/*.zbc
│   │   # 未来平台相关 lib：<lib>/<rid>.<Config>/
│   └── tests/                                         # golden / cross-zpkg 编译 zbc
└── packages/                                          # 直接可用的组装包
    └── z42-<version>-<rid>-<config>[-<variant>]/
        ├── bin/                                       # z42c, z42vm (+ .pdb / .dSYM)
        ├── libs/                                      # *.zpkg + *.zsym
        └── native/
            ├── libz42.{dylib,so,a} / z42.{dll,lib}
            └── include/{z42_host.h, z42_abi.h}
```

### packages/ 命名

`z42-<version>-<rid>-<config>[-<variant>]`：
- `<version>`: SemVer（`0.5.0` / `1.0.0-rc1`）
- `<rid>`: .NET RID（`osx-arm64` / `linux-x64` / `win-x64` / `wasi-wasm`...）
- `<config>`: `Debug` / `Release`（PascalCase，与 dotnet 一致）
- `<variant>`（可选）: 多变体内部用 `.`（`mt.simd` / `musl.static`）

例：
- `z42-0.5.0-osx-arm64-Release/`
- `z42-0.5.0-wasi-wasm-Release-mt/`
- `z42-1.0.0-rc1-linux-x64-Release-musl.static/`

### 副改动

- Rust crate `[lib] name = "z42"` —— 动态库变 `libz42.{dylib,so}` / `z42.dll`（去掉 `_vm` 后缀）
- VM `resolve_libs_dir()` 加 `<binary>/../libs/` 优先（packages 布局直接命中）
- 全 `artifacts/` 进 `.gitignore`

## Scope

| 文件 | 类型 | 说明 |
|------|------|------|
| `src/runtime/Cargo.toml` | MODIFY | `[lib] name = "z42"` |
| `src/runtime/.cargo/config.toml` | NEW | `[build] target-dir = "../../artifacts/build/runtime"` |
| `src/compiler/Directory.Build.props` | MODIFY | `<BaseOutputPath>` + `<BaseIntermediateOutputPath>` 重定向到 `artifacts/build/compiler/` |
| `src/runtime/src/main.rs` | MODIFY | `resolve_libs_dir()` 优先 `<binary>/../libs/` |
| `src/compiler/z42.Project/*` 工程文件输出 | MODIFY | workspace 模式输出 `artifacts/build/libraries/<lib>/<Config>/{dist,cache}/` |
| `scripts/build-stdlib.sh` | MODIFY | 改输出路径 + sync 路径 |
| `scripts/package.sh` | REWRITE | 组装 `artifacts/packages/z42-...` 目录 |
| `scripts/test-vm.sh` | MODIFY | 读取新路径（libs / z42c / z42vm 位置变）|
| `scripts/regen-golden-tests.sh` | MODIFY | 同上 |
| `scripts/test-stdlib.sh` | MODIFY | 同上 |
| `scripts/test-cross-zpkg.sh` | MODIFY | 同上 |
| `scripts/test-dist.sh` | MODIFY | 读 `packages/<host-name>/` 而非 `artifacts/z42/` |
| `justfile` | MODIFY | 路径 |
| `.github/workflows/ci.yml` | MODIFY | 路径（若 ref artifacts/）|
| `.gitignore` | MODIFY | 确保 artifacts/ 整体忽略 |
| `docs/workflow/building/{compiler,vm,stdlib}.md` | MODIFY | 路径描述更新 |
| `docs/workflow/README.md` | MODIFY | artifacts 布局段重写 |
| `docs/design/runtime/vm-architecture.md` | MODIFY | "VM 启动流程" 段 lookup 路径更新 |

只读引用：
- 既有 `docs/spec/archive/2026-04-*/` 历史归档不动
- `.claude/` 不动（已无 artifacts 硬编码路径）

## Out of Scope

- 跨编译（`--target` 多 RID build 矩阵）—— 0.2.5 spec 处理
- Release 自动化（tag → multi-RID build → upload）—— 0.2.6 spec
- Bootstrap z42 download（stage0/1/2 stages）—— 1.0-rc spec
- `cargo xtask` 迁移（去 just）—— 单独 spec
- WASM `wasi-wasm` 实际编译路径 —— 0.9.7 spec（layout 已留好坑）

## Open Questions

无 —— 全部决策已 settle，直接实施。
