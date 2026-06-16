# Tasks: migrate-facades-to-workload (S3') — 🟢 已完成

**变更说明：** consolidate-platform-into-workload 迁移 S3'——`host/platforms/{ios,android,wasm,desktop}` 整体搬迁到 `workload/platforms/`，更新所有 build/CI/doc 路径引用。
**原因：** 把平台 facade + 工程 + 测试从解散中的 `host/` 移入 workload（机械搬迁，不含 workload 子系统新基础设施——那归 B 独立立项）。
**文档影响：** embedding.md、roadmap.md、stdlib/roadmap.md、cross-platform-testing.md、workflow/building/*、host/README（tombstone）、workload/platforms/wasm/README。
**锁：** `toolchain`。

- [x] 1.1 `git mv src/toolchain/host/platforms src/toolchain/workload/platforms`
- [x] 1.2 批量改路径前缀 `src/toolchain/host/platforms/`→`workload/platforms/`：9 xtask + ci.yml + 8 docs + wasm/README（19 文件）
- [x] 1.3 ci.yml paths-filter `src/toolchain/host/**`→`src/toolchain/workload/**`
- [x] 1.4 验证：facade Cargo.toml 相对路径**无需改**（host/ 与 workload/ 同为 toolchain/ 子目录，facade→host-api/runtime 路径不变，已逐 dep ls 校验）；移动树内零绝对路径残留
- [x] 1.5 host/README 重写为 tombstone（embed S1 / platforms S3' 已迁出指引）
- [x] 1.6 外来 lock 处理：android/ios `rust/Cargo.lock` 的 pre-existing 未提交改动随目录搬到新路径但**留工作树未暂存**（仅提交干净 HEAD rename），不吞并并行 WIP
- [x] 1.7 GREEN：facade 各 path 解析 OK + 零残留引用；平台构建归 CI（需 Xcode/NDK/wasm）
- [x] 1.8 COMMIT + 归档 S3'，释放 toolchain 锁

## 备注
- happy invariant：因 host/ 与 workload/ 同层，搬迁后 facade 的 Cargo.toml 相对 path **完全不用动**。
- 剩余 S2（apphost+workload 脚手架引擎）/ S4（测试改 workload 驱动）依赖 B（workload 子系统）立项后做。
