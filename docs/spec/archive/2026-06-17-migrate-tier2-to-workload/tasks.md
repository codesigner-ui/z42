# Tasks: migrate-tier2-to-workload (S1) — 🟢 已完成

**变更说明：** consolidate-platform-into-workload 迁移 S1——Tier2 `z42-host`（embed）crate 从 `host/embed` 迁到 `workload/host-api`。
**原因：** 把平台无关的 Tier2 人因层从解散中的 `host/` 移入 workload（目标结构见 consolidate-platform-into-workload/design.md）。
**文档影响：** embedding.md（§5 + 文件表）、src/toolchain/README.md、host/platforms/README.md、hello_rust 模板。
**锁：** `toolchain`（不碰 src/runtime/，放弃可选的 runtime host 简化以守 scope）。

- [x] 1.1 `git mv src/toolchain/host/embed src/toolchain/workload/host-api`
- [x] 1.2 改 3 个 facade Cargo.toml 的 `z42-host` path：wasm→`../../../workload/host-api`；ios/android→`../../../../workload/host-api`
- [x] 1.3 验证 3 facade path 解析到真实 Cargo.toml + `cargo build` host-api（lock 0.2.0→0.3.0 刷新，修 reflection change 遗留陈旧）
- [x] 1.4 文档引用更新：embedding.md ×2、src/toolchain/README.md、host/platforms/README.md ×2、hello_rust Cargo.toml+README
- [x] 1.5 GREEN：host-api ✓ + 真实消费者 hello_rust ✓ 经新路径构建；facade 平台构建归 CI（需 Xcode/NDK/wasm 工具链）。核心门 vm/cross-zpkg/lib 不编译 host-api/facade，本变更证明性不影响
- [x] 1.6 COMMIT + 归档 S1，释放 toolchain 锁

## 备注
- crate 名保持 `z42-host`（lib `z42_host`），仅移目录——避免 `use z42_host` 全量改名churn；目录名 host-api 表意。
- facade Cargo.lock 未动（path-dep 不入 lock 内容；android/ios lock 的 pre-existing 改动属并行变更，未触）。
