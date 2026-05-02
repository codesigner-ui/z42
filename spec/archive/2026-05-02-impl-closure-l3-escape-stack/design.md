# Design: 闭包 env 栈分配（Escape Analysis）

## Architecture

```
┌─────────────────── 编译期（C#） ───────────────────┐
│                                                    │
│  TypeChecker → BoundBlock                          │
│              │                                     │
│              ▼                                     │
│  ClosureEscapeAnalyzer (NEW pass)                  │
│   ├─ 收集所有 BoundLambda / closure 创建点         │
│   ├─ 扫描函数内的"逃逸点"：                        │
│   │    Return / FieldSet / ArraySet / Call args   │
│   ├─ 任一可达逃逸 → 标 Escape                     │
│   └─ 否则标 Stack                                 │
│              │                                     │
│              ▼                                     │
│  Codegen → MkClosInstr.StackAlloc=bool             │
│              │                                     │
│              ▼                                     │
│  zbc 编码（+1 byte flag）                          │
└────────────────────────────────────────────────────┘
                        │
                        ▼
┌─────────────────── 运行期（Rust） ────────────────────┐
│  exec_instr / jit_mk_clos                            │
│   ├─ if stack_alloc:                                │
│   │     frame.env_arena.push(env_vec)               │
│   │     Value::StackClosure { idx, fn_name }        │
│   │  (或 Decision 1 选项的等价路径)                  │
│   └─ else:                                          │
│        ctx.heap().alloc_array(env)                  │
│        Value::Closure { GcRef, fn_name }            │
│                                                     │
│  CallIndirect dispatch:                              │
│   ├─ Value::FuncRef → 静态 call                     │
│   ├─ Value::Closure → prepend GcRef env arg         │
│   └─ Value::StackClosure → prepend frame slice arg  │
│                                                     │
│  GC root scanner:                                    │
│   ├─ 不扫 env_arena（其元素由 frame lifetime 管理）│
│   └─ 仍扫 frame.regs（含 stack closure value 自身） │
└──────────────────────────────────────────────────────┘
```

## Decisions

### 🔴 Decision 1: Value 表示扩展（**待 User 裁决**）

**问题**：栈/arena 分配的 env 不能持 GcRef（GC 会跟踪并尝试管理）。三个候选：

#### 选项 A — 新增 `Value::StackClosure` variant（**推荐**）

```rust
pub enum Value {
    // ... 现有 variants ...
    Closure { env: GcRef<Vec<Value>>, fn_name: String },
    StackClosure { env_idx: u32, fn_name: String },  // NEW
}
```

frame 增加 `env_arena: Vec<Vec<Value>>`，`env_idx` 索引该数组。

- ✅ **优点**：variant 明确，dispatch 端 match 即可；无 unsafe；现有 `Value::Closure` 不变；GC root scanner 可显式跳过 StackClosure
- ⚠️ **缺点**：CallIndirect 需要 caller frame 引用才能解 env_idx → callee 必须知道哪个 frame 是 caller；要么传 frame_ptr，要么 closure 创建点把 env 从 caller 拷贝/复制到 callee

> 复制方案：`MkClos` 时 env Vec 在 frame.env_arena[idx]；CallIndirect 时把 env 数据**复制**到 callee 的 args（与现有 `args.push(Value::Array(env))` 类似）。Caller frame 不释放期间 env_arena 数据稳定。

#### 选项 B — `Value::Closure.env` 改为枚举

```rust
pub enum ClosureEnv {
    Heap(GcRef<Vec<Value>>),
    Stack(*const Vec<Value>),    // unsafe raw pointer
}

pub enum Value {
    Closure { env: ClosureEnv, fn_name: String },
}
```

- ✅ **优点**：单 variant 内部分流，调用端代码紧凑
- ❌ **缺点**：`*const` 是 unsafe；lifetime 完全靠人脑保证；与 z42 "non-test code 禁 unsafe / unwrap" 风格冲突；调试困难

#### 选项 C — Frame-local arena + 现有 GcRef 但 GC root 排除

env 仍走 `Value::Closure { GcRef }`，但 frame 持一个**专用 arena GcRef pool**。该 pool 的 GcRef 在创建时打 "frame-scoped" 标记，GC scanner 不当成可回收对象（由 frame drop 时手动释放）。

- ✅ **优点**：Value 表示零变更；lifetime 由 frame.recycle 触发显式释放
- ❌ **缺点**：需要给 GcRef 加"frame-scoped" 标记机制（侵入 GC 子系统）；MagrGC 当前没这个 hook；与 GC 实现耦合

#### 决策建议

**选 A**（StackClosure variant）：

1. 类型层显式区分 → 没有"以为是 heap 实际是 stack"的误用
2. 不引入 unsafe
3. 与现有 `Value::FuncRef` / `Value::Closure` 模式对称（运行时 dispatch match 三个 variant）
4. GC scanner 改动局限：跳过 StackClosure variant 即可
5. CallIndirect 端把 env 内容**复制**到 callee args（与堆 closure 流程对称：`args.push(Value::Array(env))`），无 lifetime 跨帧问题
6. 与未来"closure 栈分配 → 真栈复用"扩展兼容（StackClosure 内部表示可演进）

**User 需要做的决定**：选 A / B / C 还是其他方案。本 design.md 后续部分按 A 写。

### Decision 2: 逃逸分析的精度

**问题**：要做多精细的分析？

**选项**：
- **基础版**：扫 BoundReturn / BoundFieldSet / BoundArraySet / BoundCall args 中是否 reach 到 closure
- **流敏感版**：跨 if/else / for / try-catch 路径分析
- **跨函数版**：分析 callee 是否会逃逸 closure（需要标注 / 全局分析）

**决定**：**基础版**。

理由：
1. 基础版覆盖 80% 模式（map / filter / event handler）
2. 误判 fallback 到 heap 不破坏语义
3. 流敏感版 / 跨函数版引入数据流框架（dominator / interprocedural），与当前编译器的"轻量分析"基调不符
4. 未来若有 hot-path 数据可作独立 spec 增量

### Decision 3: Escape 检测点（基础版）

closure 标记为 escape 当满足任一：

| 场景 | IR / Bound 节点 | 说明 |
|------|----------------|------|
| 作为返回值 | `BoundReturn(value)` 中 value 链可达 closure | `return (x) => x + n;` |
| 写入字段 | `BoundFieldSet(obj, field, value)` value 是 closure | `this.handler = closure;` |
| 写入数组 | `BoundArraySet(arr, idx, value)` value 是 closure | `cache[i] = closure;` |
| 写入 static field | `BoundStaticSet(name, value)` value 是 closure | 全局 escape |
| 传给 call/vcall arg | `BoundCall.Args` / `BoundVCall.Args` 含 closure | 保守判定为逃逸（除非 callee 标 [NoEscape]） |
| 传给 closure-typed local var → 后续被以上消费 | 别名链跟踪 | 与 mono 的 alias 跟踪机制可复用 |

**别名传播**：closure 赋给 var → 该 var 在后续如果被以上任一消费，原 closure 标 escape。可用与 mono spec 相似的 per-scope alias 跟踪。

### Decision 4: callee no-escape 标注

为了让 `Filter` / `Map` / `Each` 这种"用完即丢"的 callee 不污染调用方分析，需要一种"我承诺不逃逸 closure 参数"的标注。

**选项**：
- A: `[NoEscape]` 参数级 attribute（C# 风格）
- B: 推断（callee body 内部分析） —— 跨函数分析不在 scope
- C: stdlib 关键 API 硬编码白名单（`Std.Collections.List.Filter` 等）

**决定**：**(A) + (C) 组合**：

- 用户代码用 `[NoEscape]` 显式标注，eg `int Filter([NoEscape] (T) -> bool pred)`
- stdlib 已有 API 在编译器内置白名单（首批：`Filter` / `Map` / `Each` / `ForEach` / `Sort`）—— 减少 stdlib 改动量
- 未标注的 callee 一律保守判逃逸

> 备注：`[NoEscape]` 是新 attribute，需要 Lexer / Parser / TypeChecker 三层都识别；首版可以只解析 + 在 escape 分析消费，不做"违反 [NoEscape] 报错"的强校验（违反时 callee 内部不能 escape 仍由 callee 自己的分析保证）。

### Decision 5: GC root scanner 处理

GC scanner 当前扫 frame.regs。现状：
- frame.regs 中的 `Value::Closure { GcRef, ... }` 通过 GcRef 引用 env → env 被 mark
- 改后：`Value::StackClosure { idx, fn_name }` 不持 GcRef → scanner 不会经此遍历到 env

env_arena 中的 Value 自身可能是 GC-tracked（如 `Value::Array(GcRef)`、`Value::Object(GcRef)`）→ scanner 必须扫 env_arena。

**实现**：frame 增加 `gc_root_envs: Vec<*const Vec<Value>>` 暴露给 scanner（与 `frame.regs` 平行）。scanner 遍历 env_arena 中每个 Value 做 mark。

### Decision 6: arena 释放时机

frame.env_arena 在 `frame.recycle` 时整个 drop（与 frame.regs 同生命周期）。env_arena 中的 Value 走 normal Drop（GcRef 减引用计数即可）。

> 不需要复杂 lifetime 管理 —— Vec<Vec<Value>> 是 owned 的，frame drop = arena drop。

## Implementation Notes

### Bound 层

```csharp
// BoundExpr.cs
public sealed record BoundLambda(
    string LiftedName,
    IReadOnlyList<BoundCapture> Captures,
    Z42Type Type,
    Span Span,
    bool StackAllocEnv = false   // NEW; default heap
);
```

### TypeChecker 后置 pass

`ClosureEscapeAnalyzer.cs`（NEW）：

```csharp
internal sealed class ClosureEscapeAnalyzer
{
    public void Analyze(BoundBlock body, FunctionDecl owner)
    {
        // Pass 1: 收集所有 BoundLambda 创建点（按 ref-equality）
        // Pass 2: 扫描 body 中的 BoundReturn / BoundFieldSet / BoundArraySet /
        //         BoundCall.Args，标记可达到的 lambda 为 escape
        // Pass 3: 别名跟踪（var f = lambda; 后续 f 的去向）
        // Pass 4: 给未标 escape 的 BoundLambda record `with` 出 StackAllocEnv=true
    }
}
```

挂在 `TypeChecker.Infer` 后跑：

```csharp
foreach (var fn in cu.Functions) TryBindFunction(fn);
new ClosureEscapeAnalyzer().Analyze(...);  // ← 在 BindBodies 之后
```

### IR / zbc

```csharp
// IrModule.cs
public sealed record MkClosInstr(
    TypedReg Dst,
    string FnName,
    List<TypedReg> Captures,
    bool StackAlloc = false   // NEW
) : IrInstr;
```

zbc 编码：MkClos opcode 后追加 1 字节 flag（0=heap、1=stack）。读端默认 0 兼容旧 zbc（实际上不会有旧 zbc，pre-1.0 不留兼容）。

### VM interp

```rust
// frame.rs
pub struct InterpFrame {
    pub regs: Vec<Value>,
    pub env_arena: Vec<Vec<Value>>,  // NEW
    // ... existing fields ...
}

// exec_instr.rs MkClos
Instruction::MkClos { dst, fn_name, captures, stack_alloc } => {
    let env: Vec<Value> = captures.iter().map(|&r| frame.regs[r as usize].clone()).collect();
    let value = if *stack_alloc {
        let idx = frame.env_arena.len() as u32;
        frame.env_arena.push(env);
        Value::StackClosure { env_idx: idx, fn_name: fn_name.clone() }
    } else {
        let env_val = ctx.heap().alloc_array(env);
        let env_ref = match env_val { Value::Array(r) => r, _ => unreachable!() };
        Value::Closure { env: env_ref, fn_name: fn_name.clone() }
    };
    frame.set(*dst, value);
}

// exec_instr.rs CallIndirect
match callee {
    Value::FuncRef(name) => /* static */
    Value::Closure { env, fn_name } => {
        args.push(Value::Array(env));  // existing path
        // ...
    }
    Value::StackClosure { env_idx, fn_name } => {
        // NEW: copy env contents into a fresh Vec for callee
        let env_data = frame.env_arena[env_idx as usize].clone();
        args.push(Value::Array(ctx.heap().alloc_array_inner(env_data)));
        // ↑ 注意：这里堆分配是给 callee 用的临时 GcRef；caller frame 不释放
        //   时 env_data 数据有效；为了 callee 内部代码不区分 heap/stack closure，
        //   传入时统一升格成 GcRef（短期 alloc，但 callee 内部仍享受"无 GC root
        //   长期持有"的好处）
    }
}
```

> ⚠️ 上面 CallIndirect 的"传入时 alloc 一个临时 GcRef" 是简化方案。**如果性能数据显示这个临时 alloc 抹消了 stack 收益，再改进为传 frame slice / index**。第一版求正确性。

### VM JIT

`jit_mk_clos` / `jit_call_indirect` 镜像同样路径。`JitFrame` 增加 `env_arena`。

## Testing Strategy

### 单元测试

`src/compiler/z42.Tests/ClosureEscapeAnalyzerTests.cs`（NEW）：

1. `Local_Use_Marked_Stack` —— `var c = (x) => x + n; c(5);` 标 stack
2. `Returned_Closure_Marked_Heap` —— `return (x) => x + n;` 标 heap
3. `Field_Stored_Closure_Marked_Heap` —— `this.h = closure;`
4. `Array_Stored_Closure_Marked_Heap`
5. `Filter_NoEscape_Param_Marks_Stack` —— stdlib Filter 白名单生效
6. `Unannotated_Callee_Conservative_Heap` —— 未标 [NoEscape] 的自定义 callee → 保守 heap

### Golden test

`src/runtime/tests/golden/run/closure_l3_stack/`（NEW）：包含 stack 和 heap 两条路径的样本，端到端验证执行结果一致；附 IR dump 检查 `MkClos.StackAlloc` 标记正确。

### 验证命令

```bash
dotnet build src/compiler/z42.slnx
cargo build --manifest-path src/runtime/Cargo.toml
dotnet test src/compiler/z42.Tests/z42.Tests.csproj   # +6
./scripts/regen-golden-tests.sh                        # zbc 因 MkClos 编码变化全部 regen
./scripts/test-vm.sh                                   # +1×2 modes
```

## Risks & Mitigations

| 风险 | 缓解 |
|------|------|
| 误判 escape closure 为 stack → use-after-free | 分析器**保守优先**：任何不确定情形 fallback heap；单元测试覆盖典型逃逸点 |
| GC root scanner 遗漏 env_arena 中的 GcRef value | frame 暴露 `gc_root_envs` 给 scanner；测试用 GC stress 跑非平凡 closure 场景 |
| `[NoEscape]` 标注被滥用（callee 实际逃逸了 closure） | 第一版不强校验；callee 自己的 escape 分析保证 closure 在 callee 内部也不逃逸（callee 是 stdlib 时由作者负责） |
| zbc 格式微调，所有 golden zbc 必须 regen | pre-1.0 不留兼容；regen-golden-tests.sh 一次性处理 |
| CallIndirect 临时 alloc 抹消 stack 收益 | Decision 已声明"第一版求正确性，性能优化作 follow-up"；预期 stack closure 节约的是 MkClos 时的堆 alloc，不是 callee 调用时的转换 alloc |
| Mono 与 escape 双 spec 同时改 BoundLambda | tasks 上 Mono 先合并、再 escape；批量授权下 mono GREEN 后才进 escape，避免 BoundLambda 同步冲突 |
