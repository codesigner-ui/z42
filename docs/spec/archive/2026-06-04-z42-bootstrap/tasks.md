# Tasks: z42-bootstrap
> 状态：🟢 已完成 | 创建/完成：2026-06-04 | 类型：feat(build/bootstrap, bash+batch)

- [x] 1.1 versions.toml [toolchain.z42].launcher = "nightly"
- [x] 1.2 scripts/install-z42.sh (RID detect / read ver / download / sha256 / extract → .z42 / stamp / staleness) — **验证通过**
- [x] 1.3 scripts/install-z42.command (exec install-z42.sh)
- [x] 1.4 scripts/install-z42.bat (Windows .zip via PowerShell) — 写好,**待 Windows CI 验证**
- [x] 1.5 .gitignore += .z42/
- [x] 1.6 docs: launcher.md 项目本地引导段
- [x] 1.7 验证: install-z42.sh 对当前 nightly 实跑 — 下载(43.8M)+ SHA256 ok + 解压 .z42 + stamp;二次跑跳过(up to date);改 stamp → 重下。✓

## 备注
- 当前 nightly(06-02)早于 launcher-bundling(06-03)+ Stream 4,故 .z42 暂无 launcher.zpkg/根 z42(入口 fallback 到 bin/)。**下载机制已验证**;有用的 launcher 内容需 Stream-4 之后的新 nightly(= Stream 2 最终替换的前置)。
- install-z42.bat 在 macOS 无法本地验证,逻辑镜像 .sh,留 Windows CI。
