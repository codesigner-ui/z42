# Design: 实施 ref / out / in 参数修饰符的运行时语义

> **设计基础**：[前置 spec design.md](../../archive/2026-05-05-define-ref-out-in-parameters-typecheck/design.md) Decisions 1/2/3/5/8/9 已经过 User 审批，本文档不重复；以下补充本 spec 范围内的实施细节。

## Architecture

```
源码        Increment(ref c)
            ↓ Codegen (FunctionEmitterCalls.EmitBoundCall)
IR          %addr = LoadLocalAddr slot_of_c    // 产 Value::Ref::Stack { frame_idx, slot }
            Call @Increment, [%addr]
            ↓ VM interp
exec        Call: collect_args(&frame.regs, args) → arg_vals 含 Value::Ref::Stack{...}
            exec_function(callee, &arg_vals)
              callee Frame::new 把 arg_vals[0] 设入 reg[0]
              callee 执行 `x = x + 1`：
                Add { dst, a, b }: 用 frame.get(a) / frame.get(b) → 透明 deref
                  → Value::Ref::Stack 走 ctx.frame_state_at(frame_idx).regs[slot]
                Copy { dst, src }: 同样透明 deref
              return: frame.regs 持有 Ref；caller frame 已自然更新
            ↓
caller 看见 c == 1
```

## Decisions

### Decision R1: `RefKind::Stack` 用 frame index

**问题**: Stack ref 怎么定位 caller frame？
**选项**:
- A. `frame_idx: u32`（栈索引，0 = root，递增）
- B. unique frame ID（每个 Frame 创建递增 ID）

**决定**: A - frame_idx
**理由**: 前置 spec design Decision 9 已决议 ref 不离开调用栈帧，所以 frame_idx 不存在 stale 问题（callee 返回前 ref 已用完）。frame_idx 实施简单（直接 push_frame_state 列表的索引），无需额外 ID 分配。

### Decision R2: 透明 deref 位置

**问题**: 检测 Value::Ref 自动 deref 在哪一层？
**选项**:
- A. `frame.get/set` 内部
- B. 每个 exec_instr 入口前置 + 出口后置

**决定**: A - `frame.get/set`
**理由**: 单点 dispatch；所有指令统一受益；调用 frame.get 时如发现 Value::Ref，递归走 deref 链路（一层 + 沿 ctx.frame_state_at 到目标 frame.regs[slot] 或 GcRef.borrow()[idx] 或 obj.fields[name]）。性能：仅当 reg 持有 Ref 时一次额外 match，正常路径零开销。

**Sanity guard**: ref-to-ref 嵌套（Value::Ref 指向另一个 Value::Ref）—— z42 当前不会产生（codegen 不允许 ref 实参本身是另一个 ref），但 frame.get/set 应做单层 deref，不递归。如发现嵌套 → bail!("ref-to-ref not supported")。

### Decision R3: out 参数 caller 端 init

**问题**: callee 写 out param 时，caller 的 lvalue 已经是合法 slot 吗？
**决定**: 是。Frame::new 已用 `vec![Value::Null; size]` 初始化所有 reg，且 TypeChecker 阶段已为 `int v` / `out var v` 分配 reg。Codegen emit `LoadLocalAddr slot_of_v` 时 slot 必然存在（Value::Null 占位）。callee 写入 Ref → 覆盖 Null。

### Decision R4: out var x 在 callsite 的处理

**问题**: `if (TryParse(s, out var v)) print(v)` 中 v 怎么落到 caller frame？
**决定**: TypeChecker 阶段（前置 spec）已通过 `BoundOutVarDecl` 注册 v 到 caller scope；Codegen 阶段 v 分到一个 reg。callsite emit `LoadLocalAddr slot_of_v` → 产 Ref 作为 callee arg。callee 写入 → caller 的 v reg 被更新。print(v) 时 frame.get(slot_of_v) 返回 callee 写入的值（不再是 Null）。

### Decision R5: Field RefKind 用 field_name

**问题**: `RefKind::Field` 用 field_name (String) 还是 field_index/offset?
**决定**: field_name (String)，与现有 FieldGet / FieldSet 指令一致。
**理由**: z42 ScriptObject 用 hashmap 存字段（按名访问）。Field RefKind 用名字让 deref 路径与 FieldGet 共享代码。性能：每次 deref 一次 hash lookup，可接受。如未来 z42 引入 field offset 表（性能优化），统一升级 Field RefKind + FieldGet 一起改。

### Decision R6: 嵌套调用 ref 透传

**问题**: `void Outer(ref int x) { Inner(ref x) }` 中 Outer 的 x 是 Ref，传给 Inner 的应该是什么？
**决定**: Codegen 在 callsite 检测：当 BoundModifiedArg.Inner 是 BoundIdent 且该 Ident 已是 ref param（通过 Z42FuncType.ParamModifiers 已知），不再 emit `LoadLocalAddr`，直接传该 reg（已持有 Ref）。
**为什么**：Outer 的 x 在 Outer 的 frame 内是 reg，但其值是 Value::Ref::Stack{caller_frame_idx, caller_slot}。直接把这个 reg 作为 Inner 的 arg 传进去，Inner 收到的就是同一个 Ref，指向最初 caller 的 slot。透传链自然形成。

### Decision R7: GC 协调

- `Value::Ref::Stack` — frame 自然存活，无需特殊处理
- `Value::Ref::Array` / `Value::Ref::Field` — 持 GcRef，在 scan_object_refs 中作为子对象 visit，让 collector 跟随到底层 Vec / Object，保持 keep-alive

## Implementation Notes

### Rust Value::Ref 数据结构

```rust
#[derive(Debug, Clone)]
pub enum Value {
    // ... existing variants ...
    /// Spec impl-ref-out-in-runtime: 函数参数 ref/out/in 修饰符的运行时表达。
    /// 持有该 Value 的寄存器在 frame.get/set 时被透明 deref（单点 dispatch）。
    /// 引用永远不离开调用栈帧（前置 spec design Decision 9），所以 Stack kind
    /// 的 frame_idx 永不 stale。
    Ref { kind: RefKind },
}

#[derive(Debug, Clone)]
pub enum RefKind {
    /// caller frame 第 frame_idx 层（ctx.frame_state_at 索引）的 reg[slot]
    Stack { frame_idx: u32, slot: u32 },
    /// caller 数组对象的第 idx 元素
    Array { gc_ref: GcRef<Vec<Value>>, idx: usize },
    /// caller 对象的命名字段
    Field { gc_ref: GcRef<ScriptObject>, field_name: String },
}
```

### Rust frame.get/set 透明 deref

```rust
impl Frame {
    pub fn get(&self, reg: u32, ctx: &VmContext) -> Result<Value> {
        let raw = self.regs.get(reg as usize)
            .ok_or_else(|| anyhow!("undefined register %{reg}"))?;
        match raw {
            Value::Ref { kind } => deref_ref(kind, ctx),
            other => Ok(other.clone()),
        }
    }

    pub fn set(&mut self, reg: u32, val: Value, ctx: &VmContext) -> Result<()> {
        if reg as usize >= self.regs.len() {
            self.regs.resize(reg as usize + 1, Value::Null);
        }
        match &self.regs[reg as usize] {
            Value::Ref { kind } => store_thru_ref(kind.clone(), val, ctx),
            _ => { self.regs[reg as usize] = val; Ok(()) }
        }
    }
}

fn deref_ref(kind: &RefKind, ctx: &VmContext) -> Result<Value> {
    match kind {
        RefKind::Stack { frame_idx, slot } => {
            let regs_ptr = ctx.frame_state_at(*frame_idx as usize)?;
            // SAFETY: frame_idx 永不 stale（spec Decision 9）
            let regs = unsafe { &*regs_ptr };
            regs.get(*slot as usize).cloned()
                .ok_or_else(|| anyhow!("ref slot %{slot} out of range"))
                .and_then(|v| match v {
                    Value::Ref { .. } => bail!("ref-to-ref not supported"),
                    other => Ok(other),
                })
        }
        RefKind::Array { gc_ref, idx } => gc_ref.borrow().get(*idx).cloned()
            .ok_or_else(|| anyhow!("ref array idx {idx} out of bounds")),
        RefKind::Field { gc_ref, field_name } => {
            let obj = gc_ref.borrow();
            obj.get_field(field_name).cloned()
                .ok_or_else(|| anyhow!("ref field `{field_name}` missing"))
        }
    }
}

fn store_thru_ref(kind: RefKind, val: Value, ctx: &VmContext) -> Result<()> {
    // 镜像 deref，但写入
}
```

> **签名变更**：`Frame::get/set` 现在需要 `&VmContext`。需要更新所有调用点。

### Rust IR opcodes

```rust
pub enum Instruction {
    // ... existing ...
    /// 产生指向当前 frame 的 reg[slot] 的 Ref::Stack。
    /// 由 Codegen 在 ref/out/in callsite 的实参为 BoundIdent 时 emit。
    LoadLocalAddr {
        dst: Reg,
        slot: Reg,
    },
    /// 产生指向 arr[idx] 的 Ref::Array。
    LoadElemAddr {
        dst: Reg,
        arr: Reg,
        idx: Reg,
    },
    /// 产生指向 obj.field_name 的 Ref::Field。
    LoadFieldAddr {
        dst: Reg,
        obj: Reg,
        field_name: String,
    },
}
```

### Rust GC scan_object_refs

```rust
fn scan_object_refs(&self, value: &Value, visitor: &mut dyn FnMut(&Value)) {
    match value {
        // ... existing arms ...
        Value::Ref { kind } => match kind {
            RefKind::Stack { .. } => {} // frame 自然存活
            RefKind::Array { gc_ref, .. } => {
                let arr = gc_ref.borrow();
                for elem in arr.iter() { visitor(elem); }
            }
            RefKind::Field { gc_ref, .. } => {
                let obj = gc_ref.borrow();
                for slot in &obj.slots { visitor(slot); }
            }
        }
        _ => {}
    }
}
```

### C# IrInstr

```csharp
public sealed record LoadLocalAddrInstr(TypedReg Dst, int Slot) : IrInstr;
public sealed record LoadElemAddrInstr(TypedReg Dst, TypedReg Arr, TypedReg Idx) : IrInstr;
public sealed record LoadFieldAddrInstr(TypedReg Dst, TypedReg Obj, string FieldName) : IrInstr;
```

`IrFunction` 添加：
```csharp
List<byte>? ParamModifiers = null  // 0=None, 1=Ref, 2=Out, 3=In
```

### C# Codegen FunctionEmitterCalls.EmitBoundCall

```csharp
private TypedReg EmitBoundCall(BoundCall call) {
    var argRegs = new List<TypedReg>();
    foreach (var argExpr in call.Args) {
        if (argExpr is BoundModifiedArg bma) {
            var addrReg = EmitRefAddr(bma);
            argRegs.Add(addrReg);
        } else {
            argRegs.Add(EmitExpr(argExpr));
        }
    }
    // ... existing dispatch on call.Kind ...
}

private TypedReg EmitRefAddr(BoundModifiedArg bma) {
    return bma.Inner switch {
        BoundIdent id when IsRefParam(id.Name) =>
            // R6 嵌套透传：直接传当前 reg（已持 Ref）
            LookupReg(id.Name),
        BoundIdent id =>
            // 普通 local：emit LoadLocalAddr
            EmitLoadLocalAddr(LookupReg(id.Name)),
        BoundIndex idx =>
            EmitLoadElemAddr(EmitExpr(idx.Target), EmitExpr(idx.Index)),
        BoundMember mem =>
            EmitLoadFieldAddr(EmitExpr(mem.Target), mem.MemberName),
        _ => throw new InvalidOperationException(
            $"unsupported ref arg shape: {bma.Inner.GetType().Name}"),
    };
}
```

## Testing Strategy

### 单元测试（Rust）
- `Value::Ref` PartialEq / Clone
- `frame.get` 透明 deref（Stack / Array / Field 三种）
- `frame.set` 透明 store-through
- ref-to-ref nesting 报错

### Golden tests
位置 `src/runtime/tests/golden/run/21_ref_out_in/`，每个子目录一个场景：
| 子目录 | 场景 |
|---|---|
| 21a_ref_local | 基础 ref 修改原语 local |
| 21b_out_tryparse | out + DA + 调用方读取 |
| 21c_in_readonly | in 参数读取 |
| 21d_array_elem_ref | `Set(ref arr[1], 99)` |
| 21e_field_ref | `Set(ref obj.field, 100)` |
| 21f_ref_string_reseat | 引用类型 reseat |
| 21g_ref_nested | 嵌套调用 ref 透传 |

### 全套验证
- `dotnet build` + `cargo build` 无错
- `dotnet test` 1063+/1063+ 通过（含新 unit tests）
- `./scripts/test-vm.sh` 263/263 通过（256 + 7 新 golden）
