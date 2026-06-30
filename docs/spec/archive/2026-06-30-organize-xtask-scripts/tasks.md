# Tasks: 按命令组织 xtask 脚本到子目录 + 刷新 README

> 状态：🟢 已完成 | 创建：2026-06-30 | 完成：2026-06-30
> 类型：refactor（文件搬移，不改外部行为）+ docs（README 刷新）

**变更说明：** scripts/ 下 32 个扁平 xtask_*.z42 按命令分子目录；scripts/README.md 去过时 dotnet/C# 描述 + 补命令处理流程图。
**原因：** 32 个文件全平铺在 scripts/ 难导航；README 仍写 dotnet/C#（已于 2026-06-26 移除）。
**文档影响：** scripts/README.md 重写；CI ci.yml 路径过滤器 + ~6 处 design/workflow doc 路径引用同步。

## 子系统锁
`toolchain`（ACTIVE.md 已登记；锁此前空闲）。`docs` 不上锁。

## 目录布局（User 确认：保留 xtask_ 前缀，单文件命令留根，toml 改 glob）
```
scripts/
├── xtask.z42 · xtask.z42.toml · xtask_cli.z42        # 入口 / manifest / 路由（根）
├── xtask_deps.z42 · xtask_regen.z42 · xtask_audit.z42 · xtask_bench.z42 · xtask_release.z42  # 单文件命令（根）
├── common/   xtask_common.z42 · xtask_versions.z42 · xtask_golden.z42
├── build/    xtask_stdlib.z42 · xtask_compiler.z42 · xtask_compiler_e2e.z42 · xtask_bootstrap_check.z42
├── test/     xtask_test.z42 · xtask_test_lib.z42 · xtask_test_vm.z42 · xtask_test_cross.z42 · xtask_test_dist.z42
│             · xtask_test_changed.z42 · xtask_test_platform.z42 · xtask_test_wasm.z42 · xtask_test_ios.z42
│             · xtask_test_android.z42 · xtask_test_desktop.z42
├── package/  xtask_package.z42 · xtask_package_desktop.z42 · xtask_package_ios.z42 · xtask_package_android.z42 · xtask_package_wasm.z42
└── install/  xtask_install.z42 · xtask_install_android.z42
```

## 进度
### #2 reorg
- [x] 2.1 toml `[sources].include` → `["**/*.z42"]`（已验证 glob 可建）
- [x] 2.2 git mv 25 文件入 common/build/test/package/install（保留前缀；namespace 扁平不受影响）
- [x] 2.3 rebuild xtask.zpkg + `test compiler` GREEN（纯搬移、行为不变）
- [x] 2.4 CI ci.yml 路径过滤器 5 处 → 新子目录路径
- [x] 2.5 docs 路径引用同步（launcher.md / cross-platform-testing.md / toml.md×2 / self-hosting.md / building/stdlib.md）

### #1 README 刷新
- [x] 1.1 去过时描述：dotnet / C# / z42.Driver / "dotnet -- build" 残留 → C#-free 现状
- [x] 1.2 补各命令处理流程图（test / build stdlib / regen / package / deps install），据代码逻辑
- [x] 1.3 源码结构表更新为新子目录布局（含 ⑥ 拆分后的 test_lib / compiler_e2e）

## 验证
- [x] V.1 rebuild xtask.zpkg（glob + 子目录）无错
- [x] V.2 `test compiler` GREEN（搬移后跨文件解析正常）
- [x] V.3 `test stdlib z42.math` 抽查（test/ 子目录 harness 路径）

## 备注
- 保留 xtask_ 前缀 = 文件名不变只移目录 → git rename 检测顺畅、历史连续。
- namespace 扁平（Z42Xtask），子目录不影响跨文件 bare-name 调用（⑥ 拆分已证）。
