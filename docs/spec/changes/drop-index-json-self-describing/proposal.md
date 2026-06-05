# Proposal: 以 zpkg 自描述取代 index.json

## Why

`index.json` 是一张**手维护**的 `namespace → zpkg 文件名` 映射：由
[`scripts/xtask_stdlib.z42`](../../../../scripts/xtask_stdlib.z42) 的 `_indexJson()`
**硬编码 22 条**字符串生成。而每个 zpkg 自身的 `NSPC` section 已经权威记录了它导出哪些
namespace —— 两者构成**第二真相源**，新增包 / 给某包加 namespace 时一旦忘改 `_indexJson()`
就漂移（正是 [common-pitfalls §1](../../../../.claude/rules/common-pitfalls.md) 反对的
"手维护 first-wins 注册表"陷阱）。

index.json 存在的**唯一理由**是运行期的 **pull resolver 模型**
（`ZpkgResolver::resolve(ns) -> bytes`）：它要求宿主自己知道 "哪个 namespace 在哪个文件"。
一旦把模型改成 **"宿主主动注册 zpkg 字节 / 桌面扫描目录，统一由 runtime 读 NSPC 认领
namespace"**，index.json 在所有平台都失去存在必要。

> 编译器侧（`BuildDepIndex`）本就扫 `libs/*.zpkg` 读元数据、从不读 index.json；
> 桌面运行期（`resolve_namespace` → `find_namespace_in_zpkg_dirs`）也已在读 NSPC。
> 本变更把"读 NSPC 自描述"统一成唯一机制，并消除 index.json 这个唯一的手维护残留。

## What Changes

1. **删除 pull resolver hook**：`ZpkgResolver` trait、C 函数指针 `Z42ZpkgResolverFn`、
   `CHookResolver`、`arc_from_c_pair`、`install_zpkg_resolver` 全部移除（pre-1.0 不留兼容）。
2. **新增 push 注入 API** `z42_host_add_zpkg(handle, bytes, len)`：宿主在 load 前注入依赖
   zpkg 字节；runtime 读其 `NSPC` 建内部 `namespace → module` 索引。
3. **`build_host_module` 解析链改造**：去掉 resolver-first 分支，改为
   `注入索引(NSPC) → corelib → 桌面 search_paths 扫描(NSPC) → silent miss`。
4. **删除 index.json**：`_indexJson()` 删除；xtask / package / test-cross 停止生成与拷贝；
   WASM JS resolver 改为读 NSPC 取代读 index.json；浏览器 WASM 改用 build 时生成的文件名
   清单；iOS / Android `build.sh` 停止拷贝 index.json。
5. **文档同步**：embedding.md §11 重写（resolver hook → 注入模型）、删除各处 index.json 描述。

## Scope（允许改动的文件）

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `src/runtime/src/host/resolver.rs` | MODIFY | 删 `ZpkgResolver`/`CHookResolver`/`arc_from_c_pair`；改为注入字节 → NSPC 索引的 helper |
| `src/runtime/src/host/config.rs` | MODIFY | 删 `Z42ZpkgResolverFn` 字段 + 相关 validate |
| `src/runtime/src/host/ops.rs` | MODIFY | `build_host_module` 解析循环改造（去 pull、加注入索引） |
| `src/runtime/src/host/mod.rs` | MODIFY | 新增 `z42_host_add_zpkg`；删 `install_zpkg_resolver`；注入状态接入 `HostState` |
| `src/runtime/src/host/README.md` | MODIFY | 同步入口点 / API 表 |
| `src/toolchain/host/platforms/wasm/src/resolver.rs` | MODIFY | `JsCallbackResolver`(pull) → 注入字节 + NSPC |
| `src/toolchain/host/platforms/wasm/src/lib.rs` | MODIFY | 暴露注入 + `read_namespaces(bytes)` wasm-bindgen 绑定 |
| `src/toolchain/host/platforms/wasm/js/stdlib-resolver.js` | MODIFY | 读 NSPC 取代读 index.json（Node 扫目录；浏览器读生成的文件名清单） |
| `src/toolchain/host/platforms/wasm/js/index.d.ts` | MODIFY | API 签名同步 |
| `src/toolchain/host/platforms/wasm/build.sh` | MODIFY | 停拷 index.json；浏览器侧生成 zpkg 文件名清单 |
| `src/toolchain/host/platforms/ios/build.sh` | MODIFY | 停拷 index.json |
| `src/toolchain/host/platforms/android/build.sh` | MODIFY | 停拷 index.json |
| `scripts/xtask_stdlib.z42` | MODIFY | 删 `_indexJson()` + 其写入调用 |
| `scripts/xtask_package.z42` | MODIFY | 停止拷贝 index.json 进发行包 |
| `scripts/xtask_test_cross.z42` | MODIFY | 停止拷贝 index.json |
| `scripts/xtask.z42` | MODIFY | 注释（line ~101）去掉 index.json 措辞 |
| `scripts/README.md` | MODIFY | 去 index.json 描述 |
| `docs/design/runtime/embedding.md` | MODIFY | §11 重写：resolver hook → 注入模型；删 §11.7.1 index.json；含实现原理 |
| `docs/design/stdlib/overview.md` | MODIFY | 删 index.json 描述（line 278/284） |
| `docs/design/compiler/build-artifacts-layout.md` | MODIFY | 删 index.json（line 40/51） |
| `docs/workflow/packaging.md` | MODIFY | 删 index.json 行（line 110） |
| `docs/workflow/README.md` | MODIFY | 删 index.json（line 61） |
| `docs/workflow/windows.md` | MODIFY | 删 index.json（line 167） |
| `docs/workflow/building/stdlib.md` | MODIFY | 删 index.json（line 17/24/32/54/57） |
| `src/runtime/src/host/inject_tests.rs` | NEW | 注入 + NSPC 索引 + 确定性单测 |
| `src/tests/host/inject-multi-namespace/` | NEW | e2e：注入 z42.core 后解析 `Std.Exceptions` |

**只读引用**（理解上下文，不修改）：

- `src/runtime/src/metadata/zbc_reader.rs` — `read_zpkg_namespaces` 复用
- `src/runtime/src/metadata/loader.rs` — `resolve_namespace` / `find_namespace_in_zpkg_dirs` 现有扫描逻辑
- `src/compiler/z42.Project/ZpkgReader.cs` — NSPC/EXPT 读取参照

## Out of Scope

- **stdlib.lock / 版本钉死 / 完整性校验**：本次不做，列入 Deferred（单独 spec）。
- **iOS / Android resolver 实现**：当前尚未实现（仅 `build.sh` 拷文件），真正 resolver 实现归
  P4.3 / P4.4 后续 spec；本次只删它们 `build.sh` 的 index.json 拷贝步骤。
- **编译器 import 解析链**：本就不读 index.json，不改。

## Open Questions

- [ ] 删除 `Z42ZpkgResolverFn` 是否触发 host C ABI version 常量 bump（实施时核查 host ABI 是否有版本号）。
