# Tasks: launcher-at-package-root
> 状态：🟢 已完成 | 创建/完成：2026-06-04 | 类型：feat(trampoline+layout)

- [x] 1.1 main.rs resolve_runtime: portable pkg = exe.parent()(去 bin→pkg 那级)+ 注释 + 错误信息
- [x] 1.2 package_desktop.sh step 2c: trampoline cp → $PKG_DIR/z42(根)+ 注释
- [x] 1.3 install.sh: trampoline 源 $PKG/bin/$tramp → $PKG/$tramp
- [x] 1.4 docs: launcher.md portable 布局/解析 + build-artifacts-layout.md package 形态
- [x] 1.5 验证: cargo build launcher + 最小 portable 布局 smoke(根 z42 → 解析 bin/z42vm+launcher.zpkg+libs → 跑 launcher;bare + `z42 list` 均 OK)
- [x] 1.6 test-dist.sh launcher smoke: $DIST_DIR/bin/z42 → $DIST_DIR/z42(实施期补入 Scope——package 布局 consumer)
- [ ] 1.7 (CI 验证) 下个 nightly / `package.sh release` 实跑确认根 z42 端到端

## 备注
- 实施期 Scope 补充:scripts/test-dist.sh(launcher smoke 用 $DIST_DIR/bin/z42,必须随布局改)。
- release.yml 经 package.sh 自动继承,无需改。
- installed 模式($Z42_HOME/bin/z42 on PATH + launcher/)布局不变,仅 portable(package)布局变。
