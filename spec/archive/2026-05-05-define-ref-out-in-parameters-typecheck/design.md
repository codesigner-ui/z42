# Design: 引入 `ref` / `out` / `in` 参数修饰符（编译期验证）

> **Spec 拆分说明（2026-05-05）**：本 spec 仅覆盖编译期验证。Decision 1（IR 调用约定）/ Decision 2（VM Value 表达）/ Decision 8（引用类型 ref VM 表达）/ Decision 9（GC 协调）属于运行时决策，本文档保留作为 follow-up spec `impl-ref-out-in-runtime` 的设计基础——这些决议已经过 User 审批，follow-up spec 直接实施无需重新决策。
>
> Decision 3（callee 透明 deref）/ Decision 4（`out var x` 作用域）/ Decision 5（`ref a[i]` / `ref obj.f`）/ Decision 6（`out` DA 与 throw）/ Decision 7（modifier 参与 overload）/ Decision 10（错误码分配）属于编译期决策，本 spec 已实施。

## Architecture

数据流（pipeline 各阶段对修饰符的处理）：

```
源码                void Increment(ref int x) { x += 1 }
                    var c = 0; Increment(ref c)
                                ↓ Lexer
Tokens              ... KW_REF KW_INT IDENT(x) ... KW_REF IDENT(c) ...
                                ↓ Parser
AST                 FuncDecl {
                      params: [Param { Modifier: Ref, Type: Int, Name: "x" }]
                    }
                    CallExpr {
                      args: [ModifiedArg { Modifier: Ref, Inner: Ident("c") }]
                    }
                                ↓ TypeChecker
                    ✓ 修饰符匹配 / lvalue / 类型 / DA / 4 限制 / overload
                                ↓ IR Codegen
IR                  IrFunc { params: [IrParam{Modifier:Ref, ...}] }
                    Call { target, args:[<取址 c 的 IR>], ref_mask: 0b1 }
                                ↓ Bytecode
                    CALL idx_increment, args, ref_mask
                                ↓ VM interp
                    检查 ref_mask → arg[0] 计算 Value::Ref{Stack{frame_id, slot_c}}
                    callee 进入：param[0] = 上述 Ref
                    callee `x += 1` 透明 deref → 读 frame[slot_c]=0 → 加1 → 写回=1
                    返回，c==1
```

三种 ref kind 在 VM Value 中统一表达：

```rust
Value::Ref {
  kind: RefKind,
}

enum RefKind {
  Stack { frame_id: u32, slot: u32 },                  // 调用方 local
  Array { gc_ref: GcRef<Vec<Value>>, idx: usize },     // 调用方 arr[i]
  Field { gc_ref: GcRef<ScriptObject>, offset: u32 },  // 调用方 obj.f
}
```

---

## Decisions

### Decision 1: IR 调用约定（Q1）
**问题：** 如何在 IR 表达"按引用传参"？
**选项：**
- **A. 扩展 `Call` 指令**：增加 `ref_mask: BitVec`（每参数 1 bit），ref 实参的 IR 输出"地址计算"序列；callee 端 `LoadParam` / `StoreParam` 内部根据 modifier 决定走值路径还是 deref 路径
- **B. 新增三件套**：`LoadAddr` / `LoadThruRef` / `StoreThruRef` 三条新指令；ref 实参显式生成 `LoadAddr`，callee 端显式 `LoadThruRef` / `StoreThruRef`

**决定：选 A**。理由：
- z42 当前 ref 仅出现在参数位（D1-D6 延后），不需要 B 路径的通用性
- A 让 IR 体积更小、更紧凑；callee 端透明 deref 与 Decision 3 一致
- 未来如启动 D1-D6（ref local/return/field 等），按需独立升级 IR opcode（与新 spec 同期）

### Decision 2: VM Value 表达（Q2）
**问题：** Value 枚举如何表达 ref？
**选项：**
- **A. 单 variant + 内嵌 enum**：`Value::Ref { kind: RefKind }` 内含 `Stack/Array/Field`
- **B. 三个独立 variant**：`Value::StackRef`, `Value::ArrayRef`, `Value::FieldRef`

**决定：选 A**。理由：
- Rust enum 大小 = max variant，A 让 Value 整体更紧凑（与现有 `PinnedView { ptr, len, kind }` 同模式）
- Match 时双层结构（先 Ref，再 RefKind）成本可忽略
- 三种 RefKind 共享代码路径（如 GC keep-alive 协调），单 variant 更易在一处统一处理

### Decision 3: callee 端 deref 语义（Q3）
**问题：** callee 内对 ref 参数读写——透明还是显式？
**决定：透明 deref（`x` 即等价于"deref ref 拿真值"）**。理由：与 C# 一致；显式 `*x` 与 raw pointer 易混淆；用户书写体验自然。
**实现：** TypeChecker / Codegen 在 callee 解析参数引用时根据 `Param.Modifier != None` 自动插入 deref 指令；用户不写也不见 `*`。

### Decision 4: `out var x` 作用域（Q4）
**问题：** `if (TryParse(s, out var v)) ...` 的 `v` 可见到哪里？
**决定：扩展至包含的 if/while/return-after**。理由：
- 与 C# 7+ pattern matching `out var` 行为一致
- `if (TryParse(s, out var v)) print(v)` 是核心人体工学场景
- 实现：`OutVarDecl` 在 Parser 期作为 `LocalVarDecl` 注入到包含 statement 的 scope，而非 expression scope

### Decision 5: `ref a[i]` / `ref obj.f`（Q5）
**问题：** 是否支持非局部变量的 ref？
**决定：支持，对应 RefKind::Array / Field**。理由：
- 实现成本低（VM 已有 GcRef）
- 大量真实场景（in-place 排序、累加器）依赖此能力
- 不引入额外语义复杂性（lvalue 检查统一规则）

### Decision 6: `out` DA 与 throw（Q6）
**问题：** callee 抛异常的路径是否要求赋值 `out` 参数？
**决定：不要求（throw 路径无须满足 DA，仅 normal-return 路径要求）**。理由：与 C# 一致；caller 在 catch 块中也不应假设 out 已赋值；`FlowAnalyzer` 现有"throw 终结路径"的语义可直接复用。

### Decision 7: 修饰符参与 overload（Q7）
**问题：** `Foo(int)` 与 `Foo(ref int)` 视为不同重载？
**决定：是**。理由：
- 与 C# 一致；API 设计可同时提供 by-value 与 by-ref 版本
- callsite 强制写修饰符（spec Requirement 1）已让歧义不可能发生：`Foo(c)` 只匹配 by-value，`Foo(ref c)` 只匹配 by-ref

### Decision 8: 引用类型 ref 的 VM 表达（Q8）
**问题：** `ref string s` 的 Address 怎么表达？
**决定：复用 `RefKind::Stack { frame_id, slot }` 同一路径**。理由：
- z42 主语言体所有 local 都在 frame 上（无论值类型还是引用类型）
- `ref string` 指向的是 frame slot，slot 内容是 GcRef→string；reseat 改 GcRef 即可
- 无需为引用类型单独路径，模型一致

### Decision 9: GC 协调
**问题：** `RefKind::Array { gc_ref, ... }` / `Field { gc_ref, ... }` 持有 GcRef，GC 怎么 keep-alive？
**决定：Value::Ref 参与正常 GC 标记**。理由：
- VM GC 当前已 mark Value 中所有 GcRef
- Value::Ref 内的 gc_ref 字段同样被 mark，自然 keep-alive containing object
- Stack kind 不持 GcRef（仅 frame_id + slot），frame 自身在调用栈上自然存活

### Decision 10: 错误码分配
**问题：** 15 个 scenarios 的错误码怎么定？
**决定：实施期按 z42 现有惯例分配，documented in `docs/design/error-codes.md` as part of Phase 6 文档同步**。理由：
- 现有 z42 惯例（E0907 / E0908a / E0911-E0916 等）是按特性 landing 时增量分配，而非提前预留段位
- 实施期才能确认每个 diagnostic 的精确语义边界
- 参考段位：E04xx (TypeCheck) 段为修饰符一致性 / lvalue / DA / 4 限制 / overload 错误；E02xx (Parser) 段为 callsite 缺修饰符 / 语法不合法
- 具体编号由实施 Phase 3 决定，归档前在 error-codes.md 留下完整条目

---

## Implementation Notes

### AST 设计

```csharp
public enum ParamModifier { None, Ref, Out, In }

public sealed record Param(
    string Name, TypeExpr Type, Expr? Default,
    ParamModifier Modifier,   // 新增
    Span Span);

public enum ArgModifier { None, Ref, Out, In }

public sealed record ModifiedArg(
    Expr Inner,
    ArgModifier Modifier,
    OutVarDecl? OutDecl,    // 仅 Out 且使用 `out var x` 时非空
    Span Span) : Expr(Span);

public sealed record OutVarDecl(string Name, TypeExpr? AnnotatedType, Span Span);
```

> 选择"单一 ModifiedArg 节点 + Modifier 字段"而非"RefArg/OutArg/InArg 三类节点"——减少 AST 类型数量，对 visitor 更友好。

### TypeChecker 关键逻辑

- **Calls.cs**：在 overload resolution 中把 `ArgModifier` 作为匹配维度；strict type match（不允许跨修饰符隐式转换）
- **FlowAnalyzer.cs DA 扩展**：
  - **Caller 端**：`Call` 表达式后，对每个 `out` 实参对应的 lvalue 标记为 initialized
  - **Callee 端**：函数体进入时，所有 `out` 参数初始化为 uninitialized；函数体退出（normal return / fall-through）时，每个 `out` 参数必须在 initialized 状态——遍历 control flow graph 验证
- **lvalue check**：utility `IsLvalue(BoundExpr)` 检查 IdentExpr → local/field/array index；其他形式拒绝

### IR / Bytecode 编码

- `IrParam` 新增 `Modifier: ParamModifier` 字段（zbc 格式：在 Param 后加 1 byte modifier tag）
- `Call` 指令格式扩展：`CALL <target> <argc> <args...> [ref_mask: u32]`
  - `ref_mask` bit i = 1 表示 args[i] 是 ref/out/in 实参
  - 当所有 modifier 为 None 时 `ref_mask = 0`（参考 workflow.md "不为旧版本提供兼容"原则，新格式不向后兼容旧 zbc，需 regen）
- ref 实参的 IR 输出：根据 lvalue 形态，emit `LoadLocalAddr` / `LoadElemAddr` / `LoadFieldAddr` 三条新指令之一，产生 Address 值压栈
- callee 端 `LoadParam` 指令读取参数：检测到 `Modifier != None` 时自动 deref（透明语义，Decision 3）

### VM 解释器

- `Value::Ref` 新 variant；GC 标记时遍历内部 gc_ref（Decision 9）
- `Call` 指令处理：解析 ref_mask，逐参数计算 Address；callee param 表初始化为 Value::Ref
- callee 内 LoadParam(i) → 若是 ref param，自动按 RefKind 路径 deref：
  - Stack → 读 caller frame[frame_id].slots[slot]
  - Array → 读 gc_ref.borrow()[idx]
  - Field → 读 gc_ref.borrow().fields[offset]
- StoreParam(i) 类似，自动写回 Address

### 跨 Spec / 跨阶段的影响

- async / iterator 占位诊断现在加，但允许 async / iterator spec 启动时自然放开（届时再讨论 ref 与状态机的交互）
- D1-D6 延后特性的实施会复用本 spec 的 Address 表达（Value::Ref + RefKind）；当时只需要扩展 RefKind 与新增对应 IR opcode

---

## Testing Strategy

### 单元测试
| 文件 | 覆盖点 | 测试数 |
|---|---|---|
| `LexerTests.cs` | `ref` / `out` token 词法 | 3 |
| `ParserTests.cs` | 参数 modifier / callsite modifier / `out var x` 解析 | 6-8 |
| `TypeCheckerTests.cs` | 15 scenarios 对应的语义验证（含错误码）| 15+ |

### Golden 测试（端到端）

位置 `src/runtime/tests/golden/run/21_ref_out_in/`：
| 子目录 | 验证场景 |
|---|---|
| `21a_ref_increment` | 基础 ref 修改原语 |
| `21b_out_tryparse` | 基础 out + DA |
| `21c_in_readonly` | 基础 in 只读 |
| `21d_array_elem_ref` | `ref a[i]` Array kind |
| `21e_field_ref` | `ref obj.f` Field kind |
| `21f_ref_string_reseat` | 引用类型 reseat |
| `21g_overload_distinct` | 修饰符参与 overload |

### VM 验证

- `dotnet build src/compiler/z42.slnx` 无错
- `cargo build --manifest-path src/runtime/Cargo.toml` 无错
- `dotnet test src/compiler/z42.Tests/z42.Tests.csproj` 100% 通过
- `./scripts/test-vm.sh` 100% 通过（含上述 golden 7 个新增场景）
