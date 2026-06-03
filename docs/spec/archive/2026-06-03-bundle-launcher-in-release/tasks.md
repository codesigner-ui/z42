# Tasks: bundle the z42 launcher in release packages (model A, portable)

> 状态：🟢 已完成 | 创建：2026-06-03 | 类型：feat（packaging + launcher 行为）

## 阶段 1: 便携 runtime 解析
- [x] 1.1 trampoline (`main.rs`)：`$Z42_HOME/launcher` 缺失时回退包相对路径
      (`bin/z42vm` + `<pkg>/launcher.zpkg` + `<pkg>/libs`)；设 `Z42_PORTABLE_VM`/`Z42_PORTABLE_LIBS`
- [x] 1.2 launcher 核心 (`launcher.z42`)：`run`/`which` 在无显式 runtime 时优先用
      `Z42_PORTABLE_VM`/`Z42_PORTABLE_LIBS`

## 阶段 2: 打包
- [x] 2.1 `package_desktop.sh`：cargo build trampoline → 拷 `bin/z42`
- [x] 2.2 `package_desktop.sh`：z42c build launcher 核心 → 拷 `<pkg>/launcher.zpkg`
- [x] 2.3 `package.sh`：必要时加 launcher build 步骤（host 工具链）

## 阶段 3: 验证 + 文档
- [x] 3.1 本地 `package.sh release` 产包 → `<pkg>/bin/z42 run <bundled exe-zpkg>` 跑通
- [x] 3.2 `test-dist.sh`：加包内 `z42 run` 验证
- [x] 3.3 `docs/design/runtime/launcher.md`：portable 模式 + 包布局
- [x] 3.4 spec scenarios 覆盖确认

## 备注
- 不重复 z42vm/libs、不用 symlink（Windows 友好）—— 靠 portable env hint。
- 安装模型 B（`~/.z42`）+ P2 下载 = 后续。
