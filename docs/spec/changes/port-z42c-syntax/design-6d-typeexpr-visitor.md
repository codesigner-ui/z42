# Design: 6d — TypeExpr AST + Visitor（z42c.syntax）

> 状态：DRAFT（待 User 审批）｜归属：port-z42c-syntax，increment 6d
> 前置：6a–6c 已完成（前端在简化 AST 下完整，5 units / 86 cases 全绿）

## 动机

当前 AST 有两处临时形态，到了移植后端（semantics/codegen）前需要正式化：

1. **类型捕获为扁平字符串**：`TypeText` / `RetType` / `BaseText` / `TypeParams` / `WhereText` 全是 `string`（如 `"List<int>"`、`"Dict<string,int>"`、`"Foo.Bar"`、`"int[]"`、`"T?"`）。后端做名字解析 / 类型检查时**必须重新解析这些字符串**——既脆弱又把 parser 逻辑复制一份。
2. **遍历只靠 `Dump()`**：每个节点 `virtual string Dump()` 出 s-expression，仅供测试断言。后端需要真正的遍历机制。

本设计正式化这两处。**核心约束：所有改动保持现有 86 cases 全绿**（见各部分的 Dump-兼容策略）。

---

## Part A：TypeExpr AST（类型用法结构化）

### 节点层级（受限写法：class 继承 + virtual Dump）

```
TypeExpr（基类，virtual Dump()）
├── NamedType   { string Name(限定名); TypeExpr[] Args; int ArgCount }   // Foo / Foo.Bar / List<int>
├── ArrayType   { TypeExpr Elem }                                        // T[]
└── NullableType{ TypeExpr Inner }                                       // T?
```

- `NamedType.Name` 保留限定名点串（`Foo.Bar`），与现 `_parseTypeText` 行为一致。
- 泛型实参递归为 `TypeExpr[]`（`List<List<int>>` → NamedType("List", [NamedType("List", [NamedType("int")])])）。
- 嵌套顺序：`int[]?` → NullableType(ArrayType(NamedType "int"))；解析顺序沿用现 `_parseTypeText`（先泛型、再 `[]`、再 `?`）。

> **不含 FuncType `(T)->R`**：现 `_parseTypeText` 不解析函数类型（闭包类型位），保持延后。

### 解析：`_parseType()` → TypeExpr 替换 `_parseTypeText()` → string

逻辑 1:1 镜像现 `_parseTypeText`（限定名 + `<...>` + `[]` + `?`），只是产出节点而非拼字符串。`_consumeAngles` 的"递归解析泛型实参"改为对每个实参递归 `_parseType()`（逗号分隔，深度计数仍处理 `>>`）。

### Dump-兼容策略（关键：保证测试不破）

`TypeExpr.Dump()` **渲染出与旧 type-text 逐字符相同的规范文本**：

| 节点 | Dump() | 例 |
|------|--------|----|
| NamedType | `Name` + (有实参时 `<` + 各 Arg.Dump() 以 `,` 连 + `>`) | `Dict<string,int>`（无空格，同 `_consumeAngles`）|
| ArrayType | `Elem.Dump() + "[]"` | `string[]` |
| NullableType | `Inner.Dump() + "?"` | `T?` |

各 decl/stmt/expr 的 `Dump()` 把原来内联的 `this.TypeText` 改成 `this.Type.Dump()`，**输出完全一致** → 现有 86 cases 全绿，无需改测试断言。

### 受影响字段（string → TypeExpr，约 13 处）

| 文件 | 节点.字段 |
|------|----------|
| Ast.z42 | IsExpr.TypeText / AsExpr.TypeText / ObjNewExpr.TypeText → `Type` |
| Stmt.z42 | VarDeclStmt.TypeText / ForeachStmt.TypeText → `Type` |
| Decl.z42 | Param.TypeText / FieldDecl.TypeText / PropertyDecl.TypeText / MethodDecl.RetType / DelegateDecl.RetType → `Type` |
| Decl.z42 | ClassDecl.BaseText / EnumDecl.BaseText / RecordDecl.BaseText → `TypeExpr[] Bases + int BaseCount`（Dump 以 `, ` 连，同现）|

> `ObjNewExpr` 的 `new T(args)`、`VarDeclStmt` 的 `var x`：`var` 是推断占位——保留为 NamedType("var") 还是单独 InferredType？**建议** NamedType("var")（Dump 仍出 `var`，零特例）。

### 泛型形参 & where 的处理（scope 决策点，见 D1）

- **TypeParams**（`<T>` / `<K, V>` 形参声明）：当前是 string。**建议**结构化为 `string[] Names`（形参就是名字，variance/约束分离），Dump 仍出 `<K,V>`。轻量。
- **WhereText**（`where T : IComparable<T>`）：约束右侧其实是 TypeExpr。**建议本增量保留为 string**（已能正确解析消费），结构化留到后端真正用到约束求解时再做（标注 follow-up）。理由：where 约束求解是 semantics 的活，结构化时机随消费者走更稳。

---

## Part B：Visitor —— 建议**暂不引入泛型 Visitor，随后端首个 pass 落地**

### 现状对照

C# z42.Syntax 的 AST 是 `sealed record`，后端用 **`switch` 模式匹配**遍历（无 Visitor）。z42 无 record / 无 match，等价遍历机制有三条路：

| 选项 | 形态 | 优 | 劣 |
|------|------|----|----|
| 1. 泛型 Visitor`<T>` | 每节点 `T Accept<T>(IVisitor<T> v)` + `v.VisitClassDecl(n)` | 类型安全 + 双分派，贴"class+虚方法" | 泛型方法密集；返回类型单一，不同 pass（semantics 返回 bound 节点 / codegen 返回 IR）需各自实例化 |
| 2. 非泛型 Visitor | `object Accept(Visitor v)` | 简单 | 丢类型、到处 cast |
| 3. Kind tag + 下行转换 | 节点带 Kind，后端 `if (n.Kind==X) ((X)n)...`（is/as 已落地）| 直白、无双分派样板 | 手写 dispatch 链 |

### 建议：本增量**只做 Part A（TypeExpr）**，Visitor 推到后端起步时

理由：

1. **遍历形态应由消费者驱动**。semantics 与 codegen 是不同 pass、返回不同东西；现在凭空设计 Visitor 抽象，很可能建错（过早抽象，违反"消费者未就绪不抽象"）。
2. **`Dump()` 当前完全够用**作为调试渲染器，保留即可（不是债务，是合适的调试设施）。
3. 后端首个 pass（semantics）落地时，需求具体了，再二选一（泛型 Visitor vs Kind+下行）——那时一次定型，避免返工。

即：**6d 交付 = TypeExpr**；"真实 Visitor"并入后端 kickoff（semantics）作为其遍历基座设计。

> 若 User 倾向现在就定 Visitor，我推荐**选项 3（Kind tag + 下行转换）**：最贴近 C# 的 `switch`-on-record 心智、样板最少、不引入泛型方法复杂度；代价是手写 dispatch（但那本就是 match 的等价物）。

---

## Testing Strategy

- **新增 TypeExpr 单测**（tests/parser/ 或新 tests/type/ 单元）：解析各类型形（限定名 / 泛型 / 嵌套泛型 / 数组 / nullable / 组合 `Foo.Bar<int>[]?`），断言**结构**（节点种类 + 字段）+ Dump 往返。
- **现有 86 cases 全绿**（Dump-兼容保证），无需改断言。
- `xtask test compiler-z42` 全量。

---

## 决策点（已裁决 2026-06-08）

- **D1（TypeExpr scope）= 连 where 约束一并结构化**。范围 = 类型用法位（13 处）+ 形参轻量结构化（string[] names）+ **where 约束结构化**。
- **D2（Visitor）= 本增量不做 Visitor**，保留 `Dump()` 作调试渲染器；遍历基座并入后端 semantics 起步时一次定型。

### where 约束结构化设计（D1 追加）

`where T : C1, C2 [where U : ...]` → `WhereClause[]`：

```
WhereClause     { string TypeParam; WhereConstraint[] Constraints; int Count }
WhereConstraint { bool IsType; TypeExpr Type; string Special }   // Special ∈ {new(), class, struct}
  Dump(): IsType ? Type.Dump() : Special
```

decl（Class/Delegate/Record/Method）`string WhereText` → `WhereClause[] Wheres + int WhereCount`。
Dump 复刻现格式：每 clause `where {TypeParam} : {constraints 以 ", " 连}`，多 clause 以 " " 连 → 与现 86 cases 输出一致。

---

## 实施增量切分（D1=连 where / D2=不做 Visitor）

- **6d-1**：TypeExpr 三节点（新文件 `TypeExpr.z42`）+ `_parseType()`（含 `>>`/`>>>` 嵌套泛型 token 拆分，`_pendingGt` 计数）+ Dump-兼容；切换 Ast/Stmt 类型字段（IsExpr/AsExpr/ObjNewExpr/VarDeclStmt/ForeachStmt）。
- **6d-2**：切换 Decl 类型字段（Param/Field/Property/Method.RetType/Delegate.RetType/Class·Enum·Record.Bases → TypeExpr）+ 形参 `string[] Names` 结构化。
- **6d-3**：where 约束结构化（WhereClause/WhereConstraint）于 Class/Delegate/Record/Method；移除残留 `_parseTypeText`/`WhereText` string 路径。
- 每个增量走 `xtask test compiler-z42`，全绿后提交。
- （6e byte-identical 在 6d 之后；强依赖最终 AST 形态。Visitor 并入后端 semantics。）
