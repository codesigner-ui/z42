# Tasks: add-xtask-cli
> 状态：🟢 已完成 | 创建：2026-06-04 | 类型：feat(dev CLI, z42)

- [x] 1.1 tools/xtask/xtask.z42 — Main 解析 argv[0] 子命令,dispatch build/test/run/deps/bench/help
- [x] 1.2 各 handler:cd repo root + 委托现有 script/.z42(Process bash -c),转发 stdout/stderr/exit
- [x] 1.3 full help text(统一 CLI 参考)
- [x] 1.4 xtask.z42.toml(kind=exe,deps z42.core/io,out_dir → build/toolchain/xtask)
- [x] 1.5 build xtask.zpkg + 验证 `z42 xtask.zpkg --help` / no-arg→help / `build`(空 target)→err / 未知命令→err
- [x] 1.6 tools/xtask/README + build-artifacts-layout 注记

## 备注
- MVP 委托现有脚本;Stream 2 逐个换成 native z42 + 删 .sh(等新 nightly + CI 绿)。
