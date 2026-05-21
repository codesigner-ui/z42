# design/compiler/

z42 C# Bootstrap 编译器内部架构与工程文件规范。

## 职责

- 描述编译器 pipeline 内部数据结构、关键 pass、扩展点
- 描述编译产物（`.zbc` / `.zpkg` / `.zmod`）生成策略
- 描述工程文件（`z42.toml` / workspace）格式与构建编排
- 描述错误码体系（E#### / W#### / WS### / Z####）

## 核心文件

| 文件 | 职责 |
|------|------|
| [`compiler-architecture.md`](compiler-architecture.md) | C# 编译器内部：Parser / TypeChecker / IrGen 数据结构、关键 pass、ManifestLoader、Workspace 编译 |
| [`compilation.md`](compilation.md) | 编译产物粒度策略（策略 D：文件 → 类）、partial class 规则、zbc / zmod 生成 |
| [`project.md`](project.md) | `z42.toml` / `z42.workspace.toml` 格式、依赖 DAG、workspace include / preset 机制 |
| [`manifest-schema.json`](manifest-schema.json) | manifest JSON Schema（机器可读）|
| [`error-codes.md`](error-codes.md) | 错误码体系：E#### / W#### / WS### / Z#### 四套空间的来源、catalog 位置、加入流程 |
| [`scripting-charter.md`](scripting-charter.md) | **Charter / not-scheduled**：compiler 拆分为 8 个 stdlib 包 + `z42.scripting` API 长期目标；路径 2b（自举后全平台），1.0+ 触发 |

## 入口点

- 新接手编译器代码：[`compiler-architecture.md`](compiler-architecture.md)
- 修改工程文件 / 增加 manifest 字段：[`project.md`](project.md) + [`manifest-schema.json`](manifest-schema.json)
- 加新错误码：[`error-codes.md`](error-codes.md)（编译期 E#### / W#### / WS### 经
  `DiagnosticCatalog`；运行期错误改为 `Std.*Exception` 类，2026-05-11 retire-z-codes）

## 依赖关系

- 上游：[`../language/`](../language/)（编译器要承载的语言特性定义）
- 下游：[`../runtime/`](../runtime/)（编译产物给 VM 消费）
