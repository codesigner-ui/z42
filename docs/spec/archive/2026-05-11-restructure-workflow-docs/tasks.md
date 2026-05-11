# Tasks: restructure-workflow-docs

> 状态：🟢 已完成 (2026-05-11) | 创建：2026-05-11 | 类型：docs / refactor（最小化模式）

## 变更说明

参考 dotnet/runtime + rust-lang/rust 的 contributors guide 结构，把 `docs/dev.md` 单文件拆为 `docs/workflow/` 主题子目录，覆盖：编译（compiler / vm / stdlib / cross-platform）、测试（unit / vm / stdlib / cross-zpkg / changed-only）、CI、发布、调试。

## 原因

随着 build/test 命令增多（已有 14 个 shell 脚本 + 多种 dotnet/cargo 入口），单文件 dev.md 信息密度过高、按主题查询困难。dotnet/runtime 的 `docs/workflow/` 模式经验证可扩展。

## 文档影响

- 删除 `docs/dev.md`
- 新建 `docs/workflow/` 子目录 + 12 个 .md + 4 个 README.md
- 更新 `docs/README.md`、`.claude/CLAUDE.md`、`.claude/rules/workflow.md`、`docs/spec/archive/*/tasks.md`（如有跨引用）

## 文件清单

```
docs/workflow/
├── README.md                       # 入口 + 决策树
├── building/
│   ├── README.md
│   ├── compiler.md                 # dotnet build / z42c 测试
│   ├── vm.md                       # cargo build / 配置
│   ├── stdlib.md                   # 6 stdlib zpkg 流程
│   └── cross-platform.md           # placeholder (0.2.5)
├── testing/
│   ├── README.md
│   ├── unit-tests.md               # dotnet test
│   ├── vm-tests.md                 # test-vm.sh + interp/JIT
│   ├── stdlib-tests.md             # test-stdlib.sh
│   ├── cross-zpkg.md               # test-cross-zpkg.sh
│   └── changed-only.md             # test-changed.sh
├── ci.md                           # GitHub Actions + GREEN
├── release.md                      # placeholder (0.2.6)
└── debugging.md                    # placeholder (0.8.7)
```

## 进度

- [ ] 1.1 创建目录骨架 + 4 个 README
- [ ] 1.2 dev.md 内容拆到 9 个实质文件
- [ ] 1.3 3 个 placeholder（cross-platform / release / debugging）
- [ ] 1.4 `git rm docs/dev.md`
- [ ] 1.5 更新跨引用：`docs/README.md` / `.claude/CLAUDE.md` / `.claude/rules/workflow.md`
- [ ] 1.6 GREEN 验证（编译 + 关键测试不破）
- [ ] 1.7 commit + push + archive

## 备注

- 不修改 `src/`，纯文档变更
- 与 `docs/design/testing/testing.md`（测试设计）边界：design = 为什么这样设计；workflow = 如何运行
