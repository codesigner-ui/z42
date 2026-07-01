# Proposal: `params` 变长参数（typed `T[]` + `object[]` + 重载决议）

## Why

z42 目前没有变长参数。`Path.Join` 等"多段同类型 / 混类型实参"的 API 只能靠
**显式多重载**（3-arg / 4-arg）或调用方**嵌套**（`Path.Join(Path.Join(a, b), c)`）绕过。
这既丑陋又有 arity 上限。`params` 是被长期延后的特性（language-overview.md
`params-future-impl`），其两个前置依赖**现已全部满足**：

1. **自举完成**：C# 编译器已删除（2026-06-26），`src/compiler/` 即 z42c 自举编译器；
2. **`object` + boxing**：`add-boxing-conversions`（方案 A，0.3.11）已归档（2026-06-29）——
   `params object[]` 依赖的装箱机制就绪（box = codegen no-op，object 槽直接持 tagged `Value`）。

本变更把 Deferred 段的设计转为正式实现：`params T[]`（含 `string[]`）、`params object[]`
（混类型，借 boxing）、以及两者并存时的 C# 风格重载决议。

## What Changes

- **新语法**：`params` 关键字放最后一个形参前，形参类型必须是 `T[]`：
  `string Join(params string[] parts)` / `void Write(string fmt, params object[] args)`。
- **调用两形态**：
  - **normal form**：直接传一个 `T[]`（`Join(arr)`）→ 不打包，1:1 直传；
  - **expanded form**：传 0..N 个散列实参（`Join(a, b, c)`）→ 编译器在调用点隐式打包
    `new T[]{...}`。`object[]` 时各实参装箱进数组（no-op）。
- **重载决议**（对齐 C#）：normal form 优先 expanded form；多个 params 重载均走 expanded form
  时，element type 更具体者胜（`params string[]` 优于 `params object[]`）。
- **零新 IR / 零新 zbc opcode**：纯编译器前端 lowering，VM / IR 层不感知 `params`
  （expanded form 复用既有 `ArrayNewLitInstr`）。
- **zpkg TSIG 加一字节 `paramsFrom` 标记**（跨包 `params` 调用所需）→ **zpkg minor 格式 bump**。
- **分阶段引入纪律**：本变更只落"支持"（z42c 认/编 `params`），stdlib/z42c 自身源码本阶段
  **不使用** `params`；待新 nightly 发布后的后续变更再让 `Path.Join` 等 use（见 design D5）。

## Scope（允许改动的文件）

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `src/compiler/z42c.syntax/src/TokenKind.z42` | MODIFY | 新增 `TokenKind.Params` |
| `src/compiler/z42c.syntax/src/Lexer.z42` | MODIFY | `_kw("params", TokenKind.Params)` |
| `src/compiler/z42c.syntax/src/Decl.z42` | MODIFY | `Param.IsParams: bool`（紧邻 `IsRef`/`Default`） |
| `src/compiler/z42c.syntax/src/Parser.z42` | MODIFY | `_parseParamList` 识别 `params` 修饰；末参 + `T[]` + 互斥校验 |
| `src/compiler/z42c.semantics/src/Z42Type.z42` | MODIFY | `Z42FuncType.ParamsFrom: int`（params 形参索引，-1=无） |
| `src/compiler/z42c.semantics/src/SymbolCollector.z42` | MODIFY | 建签名时从 `Param.IsParams` 填 `ParamsFrom` |
| `src/compiler/z42c.semantics/src/TypeChecker.z42` | MODIFY | 约束校验 + expanded-form 绑定（trailing args→`BoundArrayLit`）+ 重载决议 normal/expanded |
| `src/compiler/z42c.semantics/src/ExportedTypeExtractor.z42` | MODIFY | TSIG 导出 `paramsFrom` 标记 |
| `src/compiler/z42c.semantics/src/ImportedSymbolLoader.z42` | MODIFY | 从 imported TSIG 读 `paramsFrom` 回填签名 |
| `src/compiler/z42c.ir/src/ExportedTypes.z42` | MODIFY | `ExportedFuncZ` / `ExportedMethodZ` 加 `ParamsFrom` 字段 |
| `src/compiler/z42c.project/src/ZpkgWriter.z42` | MODIFY | TSIG 方法/函数记录写 `paramsFrom` 字节 + 版本 minor bump |
| `src/compiler/z42c.project/src/ZpkgReader.z42` | MODIFY | 对称读 `paramsFrom` 字节 + strict-pin 版本 |
| `docs/design/language/language-overview.md` | MODIFY | Deferred `params-future-impl` 段 → 正式语法节 |
| `docs/design/runtime/ir.md` | MODIFY | 注明 params 为前端 lowering、VM 不感知 |
| `docs/spec/changes/ACTIVE.md` | MODIFY | 登记/释放子系统占用 |
| `examples/params_varargs.z42` | NEW | 示例（normal + expanded + object[]） |
| `src/tests/params/params_varargs_expanded.z42` | NEW | golden：expanded form 端到端 |
| `src/tests/params/params_varargs_normal.z42` | NEW | golden：normal form 直传 |
| `src/tests/params/params_object_mixed.z42` | NEW | golden：`params object[]` 混类型 + box |
| `src/tests/cross-zpkg/params_cross_pkg/` | NEW | 跨 zpkg：被调方 params 经 TSIG 标记，调用方 expanded |

**只读引用**（理解上下文，不修改）：

- `src/compiler/z42c.semantics/src/ExprEmitter.z42` — `BoundArrayLit`→`ArrayNewLitInstr` 既有 codegen（expanded form 复用）
- `docs/spec/archive/2026-06-29-add-boxing-conversions/design.md` — boxing no-op 语义
- `.claude/rules/bootstrap-seed.md` — 分阶段引入新语法/格式纪律（D5 依据）
- `.claude/rules/version-bumping.md` — zpkg minor bump checklist

## Out of Scope

- **stdlib 实际使用 `params`**（`Path.Join(params string[])` 等）：分阶段纪律要求晚一个
  nightly，归独立 follow-up 变更（design D5）。本变更只落"支持"。
- **`params` 与默认参数 / `ref`/`out` 组合**：互斥（直接报错），不支持组合。
- **JIT / AOT**：纯前端 lowering，VM 路径不变，无 JIT 特定工作。

## Open Questions

- [ ] 跨包 `params` 是否本变更必做？（motivating `Path.Join` 在 stdlib，调用方跨包 → 倾向必做，
      连带 zpkg minor bump。见 design D2）
- [ ] `paramsFrom` 字节编码位置与默认值（建议 0xFF=无；见 design D2）
