# Tasks: install z42 to $Z42_HOME (model B)
> 状态：🟢 已完成 | 创建：2026-06-03 | 类型：feat

- [x] 1.1 scripts/install.sh：读 manifest version → 铺 $Z42_HOME（bin/z42+z42c、launcher/*）→ `z42 link`+`default` → 打印 PATH
- [x] 2.1 package_desktop.sh：拷 install.sh 进包
- [x] 3.1 本地验证：install 到临时 $Z42_HOME → `z42 run app`（installed 模式）
- [x] 3.2 test-dist.sh：installed-mode smoke
- [x] 3.3 docs/design/runtime/launcher.md：installed 模式
- [x] 备注：install.ps1（Windows）+ 自动 PATH = 后续
