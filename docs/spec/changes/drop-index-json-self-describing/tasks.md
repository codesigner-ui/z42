# Tasks: 以 zpkg 自描述取代 index.json

> 状态：🟡 进行中 | 创建：2026-06-05 | 类型：vm

## 进度概览
- [ ] 阶段 1: Rust core — push 注入 + NSPC 索引，删 pull
- [ ] 阶段 2: WASM facade — 读 NSPC 取代 index.json
- [ ] 阶段 3: xtask / build.sh — 停止生成与拷贝 index.json
- [ ] 阶段 4: 文档同步
- [ ] 阶段 5: 测试与 GREEN 验证

## 阶段 1: Rust core
- [ ] 1.1 `host/config.rs`：删 `Z42ZpkgResolverFn` 字段 + validate 分支
- [ ] 1.2 `host/resolver.rs`：删 `ZpkgResolver`/`CHookResolver`/`arc_from_c_pair`；新增"注入字节 → (namespaces, Module)"helper（复用 `read_zpkg_namespaces`）
- [ ] 1.3 `host/mod.rs`：`HostState` 加 `injected` 字段；新增 `z42_host_add_zpkg`；删 `install_zpkg_resolver`
- [ ] 1.4 `host/ops.rs`：`build_host_module` 去 resolver-first，加"注入索引优先"分支；保留 corelib + search_paths
- [ ] 1.5 核查 host C ABI 是否有 version 常量需 bump（Open Question）
- [ ] 1.6 `host/README.md` 同步入口点 / API 表

## 阶段 2: WASM facade
- [ ] 2.1 `wasm/src/lib.rs`：新增 wasm-bindgen `read_namespaces(bytes) -> string[]`；接 `z42_host_add_zpkg`
- [ ] 2.2 `wasm/src/resolver.rs`：删 `JsCallbackResolver`(pull)，改注入路径
- [ ] 2.3 `wasm/js/stdlib-resolver.js`：Node `readdirSync` + read NSPC；浏览器 fetch 文件名清单 + read NSPC（删 index.json 读取）
- [ ] 2.4 `wasm/js/index.d.ts`：API 签名同步
- [ ] 2.5 `wasm/build.sh`：停拷 index.json；生成 `stdlib/files.json` 文件名清单

## 阶段 3: xtask / build.sh
- [ ] 3.1 `scripts/xtask_stdlib.z42`：删 `_indexJson()` + 写入调用
- [ ] 3.2 `scripts/xtask_package.z42`：删 index.json 拷贝（line ~265-267）
- [ ] 3.3 `scripts/xtask_test_cross.z42`：删 index.json 拷贝（line ~228-235）
- [ ] 3.4 `scripts/xtask.z42` line ~101 注释 + `scripts/README.md` 去 index.json 措辞
- [ ] 3.5 `ios/build.sh`、`android/build.sh`：删 index.json 拷贝

## 阶段 4: 文档
- [ ] 4.1 `docs/design/runtime/embedding.md` §11 重写（注入模型 + 实现原理；删 §11.7.1）
- [ ] 4.2 `docs/design/stdlib/overview.md` / `compiler/build-artifacts-layout.md` 删 index.json
- [ ] 4.3 `docs/workflow/{packaging,README,windows,building/stdlib}.md` 删 index.json
- [ ] 4.4 Deferred 登记：stdlib.lock（版本钉死+完整性）入 `embedding.md` Deferred 段 + `docs/roadmap.md` Deferred Backlog Index

## 阶段 5: 测试与验证
- [ ] 5.1 `src/runtime/src/host/inject_tests.rs`：注入解析 / 多 namespace / 确定性 / silent miss
- [ ] 5.2 `src/tests/host/inject-multi-namespace/`：e2e 注入解析
- [ ] 5.3 WASM `tests/r1-r7.spec.ts` / `host.js` 更新
- [ ] 5.4 `z42 xtask.zpkg test`（含 `test dist`）全绿
- [ ] 5.5 spec scenarios 逐条覆盖确认

## 备注
- pull → push 是破坏性 API 变更，pre-1.0 不留兼容（philosophy.md）。
- stdlib.lock 本次 Out of Scope，仅登记 Deferred。
