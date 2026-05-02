# Proposal: D1b — 方法组转换 + 调用站点 FuncRef 缓存（I12）

> 这是 `docs/design/delegates-events.md` D1 阶段第二切片，在 D1a 落地后实施。
> 配套子 spec：D1a 关键字 + 命名 delegate；D1c 泛型 delegate + N arity 脚本。

## Why

D1a 之后用户可以写：

```z42
public delegate int IntFn(int x);
int Helper(int x) { return x * 2; }

void Main() {
    IntFn f = Helper;       // ← "方法组转换"：用方法名做 delegate 值
    var r = f(10);
}
```

但当前 Codegen 的实际行为：

- `f = Helper` 走 mono spec 补的 `BoundIdent → LoadFn` 路径 → emit `LoadFnInstr(reg, "Demo.Helper")` → `Value::FuncRef("Demo.Helper")` 装入 `f`
- 每次进入 `Main()` 都重新 LoadFn → 重新构造 `Value::FuncRef(String)` 实例（含 String allocation）
- 高频回调路径（如 `button.Clicked += OnClick;` 在每次 UI 状态切换都进入）会反复分配 String

这正是 `delegates-events.md` D6 / I12 改进项要消灭的：

> | D6 | 方法组转换每次新分配 | call-site static cache（I12） | D1 |

C# CLR 的解法：每个方法组转换 site 编译为"如果 cache slot 为 null 就新建 delegate 否则返回 cache" 的两态指令。z42 应同款。

## What Changes

- **IR**：新增 `LoadFnCachedInstr(Dst, FuncName, CacheSlotId)` —— 行为同 LoadFnInstr，但首次调用时把结果 store 到 module-level static slot；后续命中直接 load slot。CacheSlotId 由 Codegen 分配，每个 (containing-fn, method-name) 对一份
- **VM**：interp 与 JIT 都实现 `LoadFnCached`：检查 slot；slot 非 null 直接返回；否则用 fn name 构造 `Value::FuncRef(name)` 写入 slot 再返回
- **VmContext**：新增 `static_func_refs: Vec<Value>` 持 cache slot 内容（与现有 `static_fields` 类似但专用）
- **zbc**：新增可选 SLOT_COUNT 字段记录模块需要的 cache slot 数；module 加载时预分配 vec
- **Codegen 决策**：当 BoundIdent 解析为顶层函数 / 静态方法（i.e. mono spec 加的 `BoundIdent → LoadFn` 路径）时，emit `LoadFnCached` 而非 `LoadFn`；alias 直接 Call 路径不变（无需缓存）
- **可观测性（可选）**：tasks.md 备注是否加一个 `--print-cache-stats` debug flag

## Scope（允许改动的文件）

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `src/compiler/z42.IR/IrModule.cs` | MODIFY | 新增 `LoadFnCachedInstr` record |
| `src/compiler/z42.IR/BinaryFormat/Opcodes.cs` | MODIFY | 新增 `LoadFnCached = 0x58` |
| `src/compiler/z42.IR/BinaryFormat/ZbcWriter.Instructions.cs` | MODIFY | 序列化 LoadFnCached（FuncName + cache slot id） |
| `src/compiler/z42.IR/BinaryFormat/ZbcReader.Instructions.cs` | MODIFY | 反序列化 |
| `src/compiler/z42.IR/IrModule.cs` (IrModule record) | MODIFY | 新增 `int FuncRefCacheSlotCount` 字段（zbc 编码 1 行） |
| `src/compiler/z42.Semantics/Codegen/FunctionEmitterExprs.cs` | MODIFY | BoundIdent → LoadFn 分支改为 LoadFnCached + 分配 slot id |
| `src/compiler/z42.Semantics/Codegen/IrGen.cs` | MODIFY | 维护全模块 `Dictionary<string fnName, int slotId>`（去重，相同 fn name 共享 slot） |
| `src/runtime/src/metadata/bytecode.rs` | MODIFY | Instruction::LoadFnCached variant；Module 加 `func_ref_cache_slots: usize` |
| `src/runtime/src/metadata/zbc_reader.rs` | MODIFY | 反序列化 |
| `src/runtime/src/interp/exec_instr.rs` | MODIFY | 实现 LoadFnCached 分支 |
| `src/runtime/src/jit/helpers_mem.rs` 或 helpers_closure.rs | MODIFY | 新增 `jit_load_fn_cached` 与 cache slot 访问 |
| `src/runtime/src/jit/translate.rs` | MODIFY | 对应 IR pattern 翻译 |
| `src/runtime/src/vm_context.rs` | MODIFY | 新增 `static_func_refs: RefCell<Vec<Value>>` |
| `src/runtime/src/vm.rs` | MODIFY | Module 加载时按 `func_ref_cache_slots` 调 `vm_ctx.alloc_func_ref_slots(n)` |
| `src/compiler/z42.Tests/MethodGroupConversionTests.cs` | NEW | IR-dump 单元测试 |
| `src/runtime/tests/golden/run/delegate_d1b_method_group/` | NEW | 端到端 golden |

**只读引用**：
- mono spec archive — alias 跟踪与 BoundIdent → LoadFn 路径
- `vm_context.rs::static_fields` — 平行的 static slot 实现风格

## Out of Scope

- ❌ 实例方法的方法组转换（`obj.Method` 装 delegate）—— v1 仅顶层 / 静态方法；实例方法转 closure with target capture 留 follow-up
- ❌ 跨模块缓存（cache slot 仅 module-local；跨 zpkg 边界的方法组每次仍 LoadFn）
- ❌ 移除 `LoadFnInstr`（保留作为 fallback / 未缓存场景）
- ❌ 性能基准报告（属于 follow-up，不阻塞 spec 落地）

## Open Questions

- [ ] **slot 分配粒度**：相同 fn name 跨多 call site 共享一个 slot（去重）vs 每个 site 独立 slot？倾向**共享**（更省 slot 数；缓存效益不变）
- [ ] **slot 全模块预分配 vs 按需分配**：预分配（vec 大小固定）vs lazy HashMap？倾向**预分配 vec + 整数 slot id**（O(1) load，与 static_fields 风格一致）
- [ ] **GC root**：`static_func_refs` 中的 `Value::FuncRef(String)` 持有 String，需要纳入 GC root 扫描？目前 String 不进 GC（栈值），但若未来 String 也走 GC 池则需注册。建议**对齐 static_fields 现有处理**
- [ ] **是否同步加 LoadFnCached 给"capturing closure 的 fn name"**？带捕获走 MkClos 时 fn_name 是 `String` —— 也每次 alloc。但 MkClos 本身就要新建 closure 实例，String 分配相对小头。**推迟**到性能数据指引时
