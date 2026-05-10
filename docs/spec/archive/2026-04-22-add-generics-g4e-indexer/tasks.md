# Tasks: L3-G4e 索引器语法 `T this[int] { get; set; }`

> 状态：🟢 已完成 | 创建：2026-04-22 | 完成：2026-04-22 | 类型：lang (小-中)

**变更说明**：添加 C# 风格的索引器语法，让类可以用 `obj[i]` 访问/赋值。
desugar 为普通方法 `get_Item(params) → T` 和 `set_Item(params, T value) → void`；
零新 IR 指令（沿用 VCall）。这是 L3-G4f（List/Dict pseudo-class 替换）的前置。

**原因**：当前 user-defined 泛型类只能写 `list.Get(i)` / `list.Set(i, v)`，不能用
`list[i]` 也不能用 `list[i] = v`。Stdlib 若要真替换 pseudo-class List/Dict，
索引器是必需语言特性。

**文档影响**：`docs/design/generics.md` + `docs/design/language-overview.md` +
`docs/roadmap.md` + 可能 `docs/design/ir.md`（若新增 IR 指令 — 本次不新增）。

## 进度概览
- [x] 阶段 1: Parser — 识别 `T this[params] { get/set }` 并 desugar 为方法
- [x] 阶段 2: TypeChecker — `obj[i]` 指向 get_Item；`obj[i] = v` 指向 set_Item
- [x] 阶段 3: IR Codegen — IndexExpr / assign-to-IndexExpr 对 class 走 VCall
- [x] 阶段 4: 测试 + golden
- [x] 阶段 5: 文档 + GREEN

## 阶段 1: Parser + AST

- [x] 1.1 `TopLevelParser.cs` ParseClassDecl body loop：识别 `<vis>? <type> this [` 作为索引器
- [x] 1.2 `TopLevelParser.Helpers.cs`：新增 `ParseIndexerDecl`：读 params、`{`、`get { block }` 和/或 `set { block }`
- [x] 1.3 desugar：生成两个 FunctionDecl：`get_Item(params) → T` + `set_Item(params, T value) → void`（value 为隐式参数名）
- [x] 1.4 visibility 继承到 desugared methods
- [x] 1.5 `dotnet build` 全绿
- [x] 1.6 新增 ParserTests（可选）

## 阶段 2: TypeChecker

- [x] 2.1 `TypeChecker.Exprs.cs` BindIndexExpr：
  - target 为 Z42ArrayType → 现有 ArrayGet 路径（不变）
  - target 为 Z42PrimType "string" 等 → 现有 built-in 路径
  - target 为 Z42ClassType / Z42InstantiatedType 且有 `get_Item` 方法 → 转为 BoundCall VCall `get_Item`
  - 其他 → 既有报错路径
- [x] 2.2 赋值左侧为 IndexExpr 的 target 是 class 且有 `set_Item` → BoundCall VCall `set_Item` with value 作额外参数
- [x] 2.3 instantiated generic 的 get_Item / set_Item 用 type-param 替换（复用 L3-G4a 基础设施）
- [x] 2.4 `dotnet build` 全绿

## 阶段 3: IR Codegen

- [x] 3.1 `FunctionEmitterExprs.cs`：IndexExpr + 类接收者路径 — 生成 VCall 到 get_Item
- [x] 3.2 `FunctionEmitterStmts.cs`：赋值左侧是 IndexExpr + 类接收者 — 生成 VCall 到 set_Item
- [x] 3.3 既有数组 / 字符串 / List pseudo-class 路径不受影响
- [x] 3.4 `dotnet build` 全绿

## 阶段 4: 测试 + golden

- [x] 4.1 TypeCheckerTests：索引器定义 + 调用 + 赋值用例
- [x] 4.2 Golden `run/79_indexer_basic`：user class `Box<T>` with indexer，基本读写
- [x] 4.3 Golden `run/80_indexer_multi_param`（可选）：`T this[int row, int col]` 二维
- [x] 4.4 既有 L1-L3 golden 全部不回归
- [x] 4.5 `dotnet test` + `cargo test --lib` + `./scripts/test-vm.sh` 全绿

## 阶段 5: 文档 + 验证

- [x] 5.1 `docs/design/language-overview.md`：新增索引器语法介绍
- [x] 5.2 `docs/design/generics.md`：L3-G4e 小节
- [x] 5.3 `docs/roadmap.md`：L3-G4e → ✅
- [x] 5.4 全绿验证

## 备注

- **不新增 IR 指令**：`IndexGet` / `IndexSet` 指令不必要，VCall 足够且零 VM 改动
- **不支持 auto-indexer**（`{ get; set; }` 无 body）— 语义不明确；要求 explicit body
- **不支持 `ref this[int]`** — z42 无 ref 语义
- **单维 + 多维参数均支持** — params 列表无限制
- 关联 L3-G4f：索引器上线后可真正替换 `List<T>` / `Dictionary<K,V>` pseudo-class
