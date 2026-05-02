# Design: D1b — 方法组转换 + I12 调用站点缓存

## Architecture

```
┌── 编译期（C#）────────────────────────────┐
│ IrGen 维护：                                │
│  Dictionary<string fnName, int slotId> _funcRefSlots
│  int _nextFuncRefSlotId = 0                 │
│                                             │
│ FunctionEmitterExprs::EmitExpr(BoundIdent)  │
│  if id.Type is FuncType                     │
│    && (TopLevelFn or StaticMethod):         │
│    var slotId = _ctx.GetOrAllocFuncRefSlot(qualName)
│    Emit(new LoadFnCachedInstr(dst, qualName, slotId))
│                                             │
│ Final IrModule.FuncRefCacheSlotCount        │
│   = _funcRefSlots.Count                     │
└──────────────────────────────────────────────┘

zbc 编码：module header 加 1 字段 (u32 slot_count)；
LoadFnCached opcode = LoadFn + slot_id (u32)

┌── 运行期（Rust）──────────────────────────┐
│ VmContext:                                  │
│   func_ref_slots: RefCell<Vec<Value>>      │
│                                             │
│ Module load: vm_ctx.alloc_func_ref_slots(n) │
│   → resize vec 到 n, 全部 Value::Null      │
│                                             │
│ exec_instr LoadFnCached { dst, fn_name, slot_id }:
│   match &vm_ctx.func_ref_slots[slot_id]    │
│     Value::Null →                           │
│       let v = Value::FuncRef(fn_name.clone())
│       vm_ctx.func_ref_slots[slot_id] = v.clone()
│       frame.set(dst, v)                     │
│     hit →                                   │
│       frame.set(dst, hit.clone())           │
│                                             │
│ JIT mirrors via jit_load_fn_cached helper   │
└──────────────────────────────────────────────┘
```

## Decisions

### Decision 1: cache 粒度 — 全模块去重 vs 每 call site 独立

**问题**：相同 fn name 多 site 是否共享 slot？

**选项**：
- A: **全模块去重**（同 fn name 一个 slot，跨多 site 共享）
- B: 每 call site 独立 slot

**决定**：**选 A**。理由：

1. cache 命中率：同模块的 fn name 重复出现概率高（GUI 事件回调）；共享 = 一次写多次读
2. slot 数减少 → memory footprint 小 → vec resize 一次性
3. 实现简单 — IrGen 用 `Dictionary<string, int>` 即可
4. 选 B 的"per-site slot"在 fn name 重复时没有额外好处（值就是 FuncRef(name)，恒等）

### Decision 2: slot 存储位置 — VmContext vs Module-bound

**问题**：cache slot 放 VmContext（per-VM）还是 Module 内部（per-module）？

**决定**：**VmContext + 偏移分配**。

理由：
1. 与现有 `static_fields: HashMap<String, Value>` / `static_func_refs: Vec<Value>` 一致 — 都是 VM 级状态
2. 多 module 加载时按 module 偏移分配 slot id；module unload 时回收（未来）
3. 编码层把 slot id 视为 module-local index；module loader 负责 base offset 计算（与现有 string pool 重映射机制平行）

### Decision 3: 用 Vec<Value> 而非 HashMap<String, Value>

**问题**：cache slot 用整数 id 还是 string key？

**决定**：**整数 id + Vec**。理由：

1. O(1) load（vec index 访问），HashMap 是 O(string-len) 哈希计算
2. cache slot 是稳定 ID（同 fn name 在编译时 fixed），无需运行时 lookup
3. 编码紧凑：u32 slot id 4 bytes vs string pool 索引 4 bytes（持平），但解码后 vec 访问比 HashMap 快

### Decision 4: 首次写入用 Value::Null sentinel

**问题**：怎么判断"是否首次"？

**决定**：cache slot 默认初始化为 `Value::Null`；首次访问时写入 FuncRef，后续读取命中非 Null 即可。

理由：FuncRef 永不为 Null（构造时必有 name），用 Null 作"未初始化"哨兵清晰；与 static_fields 现有约定一致。

> 边缘情形：如果某 fn name 真的需要在 slot 中持有 Null（不可能，FuncRef 不可能 Null），不会冲突。

### Decision 5: alias direct-call 路径不需缓存

mono spec 已经把 `var f = Helper; f();` 折叠为 `Call("Demo.Helper", ...)` —— 根本不 emit LoadFn。所以 direct-call 路径**已经零分配**，cache 在这里没用。

只有"赋给 var 但稍后用"这种留 BoundIdent → LoadFn 路径的场景才需 cache。

### Decision 6: lambda lifted name 不需缓存

`var sq = (int x) => x * x;` 走 EmitLambdaLiteral → LoadFn（lambda lifted name）。这个 LoadFn 也是分配 String。

**决定**：**不在 D1b 缓存这条路径**。理由：

1. lambda 字面量在源代码中是"创建一个新值"语义；用户预期每次 hit 都是新 Value
2. 实际 z42 里 LoadFn 装的是 `Value::FuncRef(liftedName)` —— 这是 immutable 值，但语义上是"lambda 实例"
3. 缓存它会让"每次 hit 都是同一 Value"，与字面量语义有微妙不一致；且收益有限（lambda 通常在创建后立即调用）

留作 follow-up（性能数据驱动）。

## Implementation Notes

### IR / zbc 编码

```csharp
// IrModule.cs
public sealed record LoadFnCachedInstr(
    TypedReg Dst, string FuncName, uint SlotId) : IrInstr;

// IrModule record itself
public sealed record IrModule(
    string Name,
    /* ... existing ... */,
    int FuncRefCacheSlotCount);   // NEW

// Opcodes.cs
public const byte LoadFnCached = 0x58;

// ZbcWriter
case LoadFnCachedInstr i:
    w.Write(Opcodes.LoadFnCached); w.Write(TypeTagFromIrType(i.Dst.Type)); WriteReg(w, i.Dst);
    w.Write((uint)pool.Idx(i.FuncName));
    w.Write(i.SlotId);
    break;

// ZbcReader 对应解析
```

Module-level cache slot count 加在 Module header 末尾（与 string pool 之后），1 个 u32。zbc reader 调 `Module { func_ref_cache_slots: count }`。

### Codegen 侧

```csharp
// IrGen.cs
private readonly Dictionary<string, int> _funcRefSlots = new(StringComparer.Ordinal);
private int _nextFuncRefSlotId = 0;

internal int GetOrAllocFuncRefSlot(string fqName)
{
    if (_funcRefSlots.TryGetValue(fqName, out var id)) return id;
    var allocated = _nextFuncRefSlotId++;
    _funcRefSlots[fqName] = allocated;
    return allocated;
}
```

```csharp
// FunctionEmitterExprs.cs::BoundIdent case (mono spec 已加的路径)
if (id.Type is Z42FuncType
    && _ctx.TopLevelFunctionNames.Contains(id.Name))
{
    var dst = Alloc(IrType.Ref);
    var fqName = _ctx.QualifyName(id.Name);
    var slotId = _ctx.GetOrAllocFuncRefSlot(fqName);
    Emit(new LoadFnCachedInstr(dst, fqName, (uint)slotId));   // ← 替代 LoadFnInstr
    return dst;
}
// 静态方法分支同样替换
```

`IEmitterContext` 加 `int GetOrAllocFuncRefSlot(string fqName);`（IrGen 实现）。

### VM 侧

```rust
// vm_context.rs
pub struct VmContext {
    // ... existing ...
    pub(crate) func_ref_slots: RefCell<Vec<Value>>,  // NEW
}

impl VmContext {
    pub(crate) fn alloc_func_ref_slots(&self, n: usize) {
        let mut s = self.func_ref_slots.borrow_mut();
        if s.len() < n { s.resize(n, Value::Null); }
    }
    pub(crate) fn func_ref_slot(&self, idx: usize) -> Value {
        self.func_ref_slots.borrow().get(idx).cloned().unwrap_or(Value::Null)
    }
    pub(crate) fn set_func_ref_slot(&self, idx: usize, v: Value) {
        let mut s = self.func_ref_slots.borrow_mut();
        if idx >= s.len() { s.resize(idx + 1, Value::Null); }
        s[idx] = v;
    }
}

// vm.rs::run / Module 加载
ctx.alloc_func_ref_slots(module.func_ref_cache_slots);
```

```rust
// exec_instr.rs
Instruction::LoadFnCached { dst, fn_name, slot_id } => {
    let cached = ctx.func_ref_slot(*slot_id as usize);
    if matches!(cached, Value::Null) {
        let v = Value::FuncRef(fn_name.clone());
        ctx.set_func_ref_slot(*slot_id as usize, v.clone());
        frame.set(*dst, v);
    } else {
        frame.set(*dst, cached);
    }
}
```

JIT helper：

```rust
// jit/helpers_closure.rs
#[no_mangle]
pub unsafe extern "C" fn jit_load_fn_cached(
    frame: *mut JitFrame, ctx: *const JitModuleCtx,
    dst: u32,
    name_ptr: *const u8, name_len: usize,
    slot_id: u32,
) -> u8 {
    let vm_ctx = vm_ctx_ref(ctx);
    let cached = vm_ctx.func_ref_slot(slot_id as usize);
    let v = if matches!(cached, Value::Null) {
        let name = std::str::from_utf8(std::slice::from_raw_parts(name_ptr, name_len))
            .unwrap_or("<invalid>")
            .to_string();
        let v = Value::FuncRef(name);
        vm_ctx.set_func_ref_slot(slot_id as usize, v.clone());
        v
    } else {
        cached
    };
    (*frame).regs[dst as usize] = v;
    0
}
```

translate.rs 加对应 IR 翻译。

### GC root

`VmContext.func_ref_slots` 中的 `Value::FuncRef(String)` 当前不持 GcRef，所以 GC 不需特殊处理。但若未来 String 进 GC 池则需注册。**记入 follow-up**，本期不做。

## Testing Strategy

### 单元测试

`src/compiler/z42.Tests/MethodGroupConversionTests.cs`（NEW）：

1. `Method_Group_Emits_LoadFnCached` — IR dump 命中 `LoadFnCachedInstr`
2. `Same_Method_Multiple_Sites_Share_Slot` — 多 site 共享 slot id
3. `Different_Methods_Distinct_Slots` — slot id 单调递增 / 不冲突
4. `Module_Records_FuncRef_CacheSlot_Count` — IrModule.FuncRefCacheSlotCount 与实际 slot 用量一致
5. `Lambda_Literal_Still_Emits_LoadFn` — `var sq = (int x) => x*x;` 还是 LoadFn（不是 cached）
6. `Alias_Direct_Call_Skips_LoadFn_Entirely` — `var f = Helper; f();` 仍只 emit Call（无 LoadFnCached）

### Golden test

`src/runtime/tests/golden/run/delegate_d1b_method_group/source.z42`：

```z42
namespace Demo;
using Std.IO;

public delegate int IntFn0();
public delegate int IntFn1(int x);

int Helper() { return 42; }
int Square(int x) { return x * x; }

void DoCall(IntFn0 f) { Console.WriteLine(f()); }

void Main() {
    IntFn0 a = Helper;
    Console.WriteLine(a());        // 42
    Console.WriteLine(a());        // 42 — slot hit

    IntFn1 sq = Square;
    Console.WriteLine(sq(5));      // 25
    Console.WriteLine(sq(6));      // 36

    // 跨函数边界使用 cache
    DoCall(Helper);                // 42
    DoCall(Helper);                // 42 — same slot
}
```

期望输出：
```
42
42
25
36
42
42
```

### 验证命令

```bash
dotnet build src/compiler/z42.slnx
cargo build --manifest-path src/runtime/Cargo.toml
dotnet test src/compiler/z42.Tests/z42.Tests.csproj   # +6
./scripts/regen-golden-tests.sh                        # 全 zbc regen（编码变化）
./scripts/test-vm.sh                                   # +1×2 modes
```

## Risks & Mitigations

| 风险 | 缓解 |
|------|------|
| zbc 编码变更导致历史 zbc 无法解析 | pre-1.0 不留兼容；regen-golden-tests 一次处理 |
| slot id 与 module load 顺序耦合（多模块 base offset） | 阶段 1 仅 single-module；多模块 base offset 在 D1c / 后续 spec 处理；现 module loader 已经处理 string pool 重映射，模式同 |
| FuncRef String 在 cache 长持，造成 String 池增长 | 当前 String 不池化（直接 String）；cache 持有引用与 fn name 一一映射，不增加新 String 副本 |
| BoundIdent → LoadFn 路径在 D1a 用户场景中可能未触发 | D1a 测试源里 `IntFn0 f = Helper;` 走该路径；D1b 实施时验证 IR dump 命中 |
| 缓存 slot 在 lambda lifted name 错误命中 | EmitLambdaLiteral 不调用 GetOrAllocFuncRefSlot，明确分流 |
