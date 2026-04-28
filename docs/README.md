# docs/

z42 项目文档总入口。本目录文件分两类：**对外阅读**（语言用户、潜在贡献
者）vs **内部实现**（编译器/VM 维护者）。

## 文件导航

| 文件 / 目录 | 受众 | 内容 |
|------------|------|------|
| [`features.md`](features.md) | 对外 | 语言特性 catalog（决策、当前 phase 归属） |
| [`roadmap.md`](roadmap.md) | 对外 | 阶段总览 + pipeline 实现里程碑 |
| [`version.md`](version.md) | 对外 | 版本路线（0.1.0 → 1.0.0 草案） |
| [`dev.md`](dev.md) | 内部 | 本地开发命令（build / test / package） |
| [`design/`](design/) | 混合 | 语言规范 + 实现原理（详见目录内各文件标题） |

> `review1.md` / `review2.md` 是一次性 code & docs review 报告，落实改进项后将归档到 `spec/archive/`。

## 文档语种策略

z42 仓库文档采用**双语策略**，按受众分流：

- **对外文档**（面向语言用户 / 潜在贡献者 / 公开发布）：**英文**
  - 例：[`features.md`](features.md), [`design/philosophy.md`](design/philosophy.md), [`design/language-overview.md`](design/language-overview.md), [`design/interop.md`](design/interop.md), [`design/hot-reload.md`](design/hot-reload.md), [`design/execution-model.md`](design/execution-model.md), [`design/object-protocol.md`](design/object-protocol.md), [`README.md`](../README.md)（仓库根）

- **内部文档**（面向 z42 开发者 / 协作工作流 / 实现细节）：**中文**
  - 例：[`dev.md`](dev.md), [`roadmap.md`](roadmap.md), [`design/compiler-architecture.md`](design/compiler-architecture.md), [`design/vm-architecture.md`](design/vm-architecture.md), [`design/zbc.md`](design/zbc.md), [`.claude/CLAUDE.md`](../.claude/CLAUDE.md), [`.claude/rules/*.md`](../.claude/rules/)

写新文档时按此分流；混用注释（中文文件里的英文 code comment、英文文件里
对中文术语的注音等）允许，但**主体语言**应一致。当一份对外英文文档需要
配套实现细节时，把实现细节单独拆到一份内部中文文档而不是混在一起。
