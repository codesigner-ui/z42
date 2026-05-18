# docs/

z42 项目文档总入口。文件分两类：**对外阅读**（语言用户、潜在贡献者）vs **内部实现**（编译器/VM 维护者）。

## 顶层文件

| 文件 / 目录 | 受众 | 内容 |
|------------|------|------|
| [`features.md`](features.md) | 对外 | 语言特性 catalog（决策、当前 phase 归属） |
| [`roadmap.md`](roadmap.md) | 对外 | 唯一迭代计划：当前焦点 + 下一阶段 + SemVer 路线 + Feature→Version 映射 + Deferred Backlog |
| [`workflow/`](workflow/) | 内部 | 本地构建 / 测试 / CI / release / 调试工作流（按主题分子目录）|
| [`design/`](design/) | 混合 | 设计文档（按主题分 5 子目录）|
| [`spec/`](spec/) | 内部 | OpenSpec 风变更工作目录（`changes/` 进行中 + `archive/` 已归档）|
| [`error-codes/`](error-codes/) | 数据 | `Z.json`：Z#### runtime 错误码 catalog（Rust + C# 共享）|

## design/ 子目录

| 子目录 | 内容 |
|------|------|
| [`design/philosophy.md`](design/philosophy.md) | 设计哲学（顶层不动）|
| [`design/language/`](design/language/) | 语法 / 类型系统 / 内置协议 / FFI 表面（20 文件）|
| [`design/compiler/`](design/compiler/) | C# Bootstrap 编译器内部 + 工程文件 + 错误码体系（5 文件）|
| [`design/runtime/`](design/runtime/) | Rust VM 架构 + IR/zbc + 嵌入 + 跨平台（10 文件）|
| [`design/stdlib/`](design/stdlib/) | 三层架构 + 包边界 + 缺失包排期（3 文件）|
| [`design/testing/`](design/testing/) | z42.test 框架 + runner + 跨平台测试（3 文件）|

每个子目录有自己的 `README.md` 作为索引；新读者从那进入。

## 文档风格模板（2026-05-10 起）

详见 [`design/README.md`](design/README.md)：

- **A 长设计**（spec 风）：generics / closure / interop / static-abstract-interface 等
- **B 短规范**：parameter-modifiers / properties / arrays / foreach 等
- **C 参考手册**：仅 `language-overview.md`

## 延后特性管理

所有延后项一律就近写入对应 design doc 的 "Deferred / Future Work" 段；`roadmap.md` "Deferred Backlog Index" 横向索引。规则见 [`.claude/rules/philosophy.md`](../.claude/rules/philosophy.md#延后特性管理必须遵守) "延后特性管理"。

## 文档语种策略

z42 仓库文档采用**双语策略**，按受众分流：

- **对外文档**（面向语言用户 / 潜在贡献者 / 公开发布）：**英文**
  - 例：[`features.md`](features.md), [`design/philosophy.md`](design/philosophy.md), [`design/language/language-overview.md`](design/language/language-overview.md), [`design/language/interop.md`](design/language/interop.md), [`design/runtime/hot-reload.md`](design/runtime/hot-reload.md), [`design/runtime/execution-model.md`](design/runtime/execution-model.md), [`design/language/object-protocol.md`](design/language/object-protocol.md), [`README.md`](../README.md)（仓库根）

- **内部文档**（面向 z42 开发者 / 协作工作流 / 实现细节）：**中文**
  - 例：[`workflow/`](workflow/), [`roadmap.md`](roadmap.md), [`design/compiler/compiler-architecture.md`](design/compiler/compiler-architecture.md), [`design/runtime/vm-architecture.md`](design/runtime/vm-architecture.md), [`design/runtime/zbc.md`](design/runtime/zbc.md), [`.claude/CLAUDE.md`](../.claude/CLAUDE.md), [`.claude/rules/*.md`](../.claude/rules/)

写新文档时按此分流；混用注释（中文文件里的英文 code comment、英文文件里对中文术语的注音等）允许，但**主体语言**应一致。当一份对外英文文档需要配套实现细节时，把实现细节单独拆到一份内部中文文档而不是混在一起。
