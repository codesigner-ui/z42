# z42 Runtime 架构 Review：对照 CoreCLR

> 日期：2026-05-21
> 范围：[`src/runtime/`](../src/runtime/) Rust VM vs CoreCLR (`/Users/d.s.qiu/Documents/codesigner-ui/runtime`) 的目录布局 + 分层模式比对
> 目的：识别可借鉴的架构分层；不是 line-by-line 实现比对

---

## CoreCLR 顶层切分（参考）

| 目录 | 职责 |
|---|---|
| `src/coreclr/vm/` | 运行时引擎：对象模型 (MethodTable / MethodDesc / FieldDesc)、类加载、异常、线程、GC 交互、stub / prestub、方法分发、托管↔原生桥接。子目录 `portable/` + `amd64/`/`arm64/`/`arm/`/`i386/`/`loongarch64/`/`riscv64/`/`wasm/` 分离 portable VM 代码 vs per-arch ASM |
| `src/coreclr/jit/` | JIT 流水线：IL importer → IR (GenTree) → 中端优化（assertionprop / earlyprop / copyprop）→ lowering → regalloc (lsra) → arch-specific emit (emitxarch / emitarm64) |
| `src/coreclr/gc/` | GC 算法：`gc/env/` 抽象层 + `gc/unix/` / `gc/windows/` 平台实现 + `gc/sample/` 参考实现 |
| `src/coreclr/md/` | Metadata 系统：tokens / tables / heaps 压缩存储；类型 / 方法 / 字段索引 |
| `src/coreclr/inc/` | **跨子系统契约头文件**：`corinfo.h` (JIT↔VM 接口) / `corjit.h` / `gcinfo.h` / `contract.h` / `ex.h` |
| `src/coreclr/interpreter/` | 解释器后备路径（启动期 / 复杂代码绕开 JIT） |
| `src/coreclr/debug/` | 调试基础设施 + DAC (Data Access Component) post-mortem |
| `src/coreclr/interop/` | P/Invoke + COM wrappers |
| `src/coreclr/pal/` | 平台抽象层：POSIX/Windows 线程 / 内存 / 信号 / I/O |
| `src/coreclr/binder/` `src/coreclr/dlls/` | Assembly 绑定 + 入口 DLL |
| `src/mono/` | 另一个 VM 实现：解释器 + LLVM/AOT，移动 / WASM 路径 |

---

## z42 现状切分

| 模块 | 职责 |
|---|---|
| `metadata/` | bytecode 格式 ([zbc_reader.rs](../src/runtime/src/metadata/zbc_reader.rs))、类型描述 (`TypeDesc` in [types.rs](../src/runtime/src/metadata/types.rs))、Module/Function/Instruction ([bytecode.rs](../src/runtime/src/metadata/bytecode.rs))、lazy zpkg 加载 ([lazy_loader.rs](../src/runtime/src/metadata/lazy_loader.rs))、merge / resolver / tokens |
| `interp/` | 解释器：`exec_instr.rs` exhaustive 分发 → 7 个 exec_*.rs 类目（value / call / array / object / vcall / address / native） |
| `jit/` | Cranelift JIT：`translate.rs` z42 IR → Cranelift IR；7 个 `helpers/<cat>.rs` 提供 extern "C" runtime；`helpers/registry.rs` 单一注册中心 |
| `gc/` | `trait MagrGC` (10 capability groups) + `ArcMagrGC` 默认实现 + `safepoint.rs` (Phase 3 stub) |
| `exception/` | in-band Value propagation；`VmFrame` 栈帧 + stack trace 捕获 |
| `native/` | Tier 1 C ABI：libffi + dlopen；`exports.rs` 暴露 `z42_register_type` / `z42_invoke` 等 |
| `host/` | Host embedding API：config / entry / module / marshal |
| `corelib/` | builtin 实现（28 文件，按 IO / GC / math / process / platform 等分组） |
| `vm_context.rs` | 单 VM 实例的可变状态容器 |
| `thread/` | 多线程 stub（add-multithreading-foundation, 进行中） |
| `vm.rs` | `Vm::run` 按 ExecMode 路由到 interp / JIT |

---

## 7 个分层模式：可借鉴度评估

### 1. JIT ↔ VM 显式契约（CORINFO 风格） ⭐⭐⭐ 最值得借鉴

**CoreCLR 模式**：
- JIT 不知道 VM 内部表示 — 所有跨界调用走 [`inc/corinfo.h`](https://github.com/dotnet/runtime/blob/main/src/coreclr/inc/corinfo.h) 定义的虚函数接口（`resolveToken / getClassInfo / getMethodInfo / canCast` 等）
- 跨界传值用 opaque handles：`CORINFO_CLASS_HANDLE = void*`、`CORINFO_METHOD_HANDLE = void*`
- 结果：JIT 可单独换（CoreCLR 早期就有 JIT32 / JIT64 / RyuJIT 多代替换）、VM 可重构内部表示而不动 JIT

**z42 现状**：
- [`jit/translate.rs`](../src/runtime/src/jit/translate.rs) 直接 `use crate::metadata::{Function, Instruction, Module}` — JIT 知道 `Function` 字段布局、`Module.functions` 索引、`TypeDesc` 内部结构
- metadata 改字段 → translate.rs 立刻跟着改；JIT 替换成本高

**建议落地**：
- 新建 `jit/vm_interface.rs`，定义 `trait VmInterface` 暴露 JIT 需要的查询：方法解析 / 类布局 / 字段偏移 / 类型检查 / 异常 EH 表查询
- `translate.rs` 只 take `&dyn VmInterface`，不 import metadata 内部类型
- helpers/* 同样改成走 trait
- AOT 路径（未来）也走同一个 trait，统一契约

**ROI**：高。工作量 2-3 天，解锁 JIT 演进 + 后续 AOT。

---

### 2. Lazy / Prestub 模型 ⭐⭐⭐

**CoreCLR 模式**：
- 方法首次调用走 **prestub** 小段代码 → 触发 JIT 编译该单方法 → 原子地把方法表里的 code pointer 从 prestub 改写为编译后真实地址 → 跳过去
- 后续调用直接跳真实地址，无 dispatch 开销
- 冷代码永不编译，启动延迟极低，内存占用与"实际触达"成正比

**z42 现状**：
- [`compile_module`](../src/runtime/src/jit/mod.rs) 在 `Vm::run` 起步时一次性把整个 module JIT 完
- 冷代码也付编译成本；大 module 启动慢

**建议落地**：
- 每个 `Function` 持一个 `Cell<CodePtr>`（或 `AtomicUsize`）初始指向 prestub trampoline
- prestub trampoline 调 JIT 编译该单函数 → 原子写真实指针 → 跳过去
- 与现有 `MethodId` token 系统天然配合（method token → CodePtr 二级表）

**ROI**：高。3-5 天。配合 spec roadmap 中的 hybrid execution 模型（interp + JIT 共存）。

---

### 3. Stub 生成作为一等原语 ⭐⭐

**CoreCLR 模式**：
- write barrier / allocation fast path / virtual call dispatch / P/Invoke marshaller 都是**运行时生成的小段机器码**，不是固定 helper
- 每个对象的写屏障是 inline 的几条指令而非 call 一个 helper
- 见 [`vm/stubgen.cpp`](https://github.com/dotnet/runtime/blob/main/src/coreclr/vm/stubgen.cpp) / `vm/virtualcallstub.cpp`

**z42 现状**：
- 所有 ArrayNew / FieldGet / VCall 都 call extern helper（见 [`registry.rs`](../src/runtime/src/jit/helpers/registry.rs) 注册了 ~50 个 helpers）
- 每条 IR 全走 helper call，丧失 Cranelift inline 优势

**建议落地**：
- 高频路径改 inline Cranelift IR：const_i32 / copy / array_get bounds check 通过路径 / vcall IC hit
- 不 call helper —— Cranelift 完全支持内联实现
- 慢路径 / 异常路径仍走 helper

**ROI**：中。2-3 天。JIT 微基准估测提速 30-50%；不解锁新能力。

---

### 4. Platform 抽象层（PAL） ⭐⭐

**CoreCLR 模式**：
- [`pal/`](https://github.com/dotnet/runtime/tree/main/src/coreclr/pal) 把所有 OS syscall 抽象在一层（线程、文件、内存、信号）
- 上面 VM 代码 0 个 `#[cfg(target_os)]`

**z42 现状**：
- `corelib/` 散落 `fs.rs` / `process.rs` / `platform.rs` 等，OS 特定代码内嵌在业务文件里
- `thread/` 准备做多线程但没 PAL 层支撑

**建议落地**：
- 分出 `runtime/src/pal/{thread,fs,signal,mem}.rs` 做平台 trait + 实现
- corelib 调 PAL 而非直接 std
- L3 多线程 + 移动平台（iOS / Android）支持的前置条件

**ROI**：中。5-7 天。锁住未来移动 / 多线程路径。

---

### 5. VM/EE 与 GC 解耦 ⭐⭐ ✅ 已做对

**CoreCLR 模式**：
- `gc/` 完全独立，`gc/sample/` 是参考实现
- GC ↔ VM 通过 `gcinterface.h` (~30 callbacks)
- 结果：可换 GC 算法不动 VM

**z42 现状**：
- `trait MagrGC` ([heap.rs](../src/runtime/src/gc/heap.rs)) 已经做了这件事 ✅
- 已是项目里抽象最干净的一处

**建议**：保持。新增 GC 实现（如 generational tracing）走 trait 即可。

---

### 6. Two-pass EH + Funclet ⭐ 暂不紧迫

**CoreCLR 模式**：
- 异常分 first-pass（搜索 handler，不展开栈）+ second-pass（展开栈，跳 funclet）
- Funclet = catch / finally 块作为独立代码区，支持 C# `catch (Exception e) when (filter)` filter 表达式
- 见 [`vm/exceptionhandling.cpp`](https://github.com/dotnet/runtime/blob/main/src/coreclr/vm/exceptionhandling.cpp)

**z42 现状**：
- [`exception/mod.rs`](../src/runtime/src/exception/mod.rs) 是 in-band `Value` propagation，简单但够用
- 还不支持 filter expression

**建议**：L3 加 `catch ... when (...)` filter 时再考虑。当前 in-band 模型对 L2 完全够。

---

### 7. 每个 arch 一个子目录 ⭐ 当前不需要

**CoreCLR 模式**：`vm/amd64/` / `vm/arm64/` 因有手写 ASM 转换块、calling conv 适配器、exception prolog。

**z42 现状**：借 Cranelift，arch 差异由 Cranelift 兜底。

**建议**：**这条不抄**。除非未来手写汇编 fast path / inline assembly stub。

---

## 推荐的实施次序

| 优先级 | 改造 | 估时 | 解锁价值 |
|---|---|---|---|
| **P0** | **JIT↔VM 接口抽象** — 新 `jit/vm_interface.rs` 加 trait，translate.rs 重构走 trait | 2-3 天 | metadata 可演进 / JIT 可替换 / AOT 有清晰契约 |
| **P1** | **Prestub / lazy JIT** — 单函数按需编译 | 3-5 天 | 启动延迟 + 内存占用大幅下降；与 hybrid execution 契合 |
| **P2** | **PAL 抽象层** — 多线程 + 多平台前置 | 5-7 天 | 解锁 Phase 3 多线程 + iOS/Android 移植 |
| **P3** | **Hot-path stub inline** — const / copy / array bounds 不走 helper | 2-3 天 | JIT 微基准提速 30-50% |

建议从 **P0（JIT↔VM 接口）** 入手 —— 工作量最小、对后面所有改造都是基础；做完 z42 的 runtime 模块依赖图会清爽很多。

---

## 不抄的部分

- **每 arch 一个目录**：Cranelift 已兜底
- **DAC（Data Access Component）**：post-mortem 调试基础设施过重，等用户呼声出现再考虑
- **md/ heaps / tables 压缩元数据**：zbc 格式当前 footprint 不构成瓶颈，过早优化
- **手写 ASM 转换块**：Rust + Cranelift 不需要

---

## 参考链接

- CoreCLR 仓库（本机镜像）：`/Users/d.s.qiu/Documents/codesigner-ui/runtime`
- JIT↔VM 接口契约：`src/coreclr/inc/corinfo.h`
- GC 接口契约：`src/coreclr/inc/gcinfo.h`
- z42 VM context：[`src/runtime/src/vm_context.rs`](../src/runtime/src/vm_context.rs)
- z42 JIT translate：[`src/runtime/src/jit/translate.rs`](../src/runtime/src/jit/translate.rs)
- z42 JIT helper registry：[`src/runtime/src/jit/helpers/registry.rs`](../src/runtime/src/jit/helpers/registry.rs)

---

# Part 2：代码细节层 Review（2026-05-21 补充）

> 上半部分（顶层切分）找了"目录结构"层面的可借鉴模式。这部分深入**热路径具体实现**，逐项核对代码事实。
>
> **已经做对的部分会显式标出 ✅**，不重复改造建议；问题项标 ❌ 给具体修复方向。

## C1. `Value` enum 大小 + 拷贝成本 ⚠️

**z42 实测**（[`types.rs:198-238`](../src/runtime/src/metadata/types.rs#L198-L238)）：

```rust
#[derive(Debug, Clone)]
pub enum Value {
    I64(i64),                                          // 8B payload
    F64(f64), Bool(bool), Char(char), Null,
    Str(String),                                       // 24B (ptr+len+cap)
    Array(GcRef<Vec<Value>>),                          // 16B (Rc + ptr)
    Object(GcRef<ScriptObject>),                       // 16B
    PinnedView { ptr: u64, len: u64, kind: ... },      // 24B
    FuncRef(String),                                   // 24B
    Closure { env: GcRef<...>, fn_name: String },      // 40B
    StackClosure { env_idx: u32, fn_name: String },    // 32B
    Ref { kind: RefKind },                             // 24-32B
}
```

最大 variant 40B + 8B discriminant alignment → `size_of::<Value>() = 48` 字节。每次 `regs[a].clone()` 都搬 48B + 可能触发 `String::clone()` 走堆分配。

**CoreCLR 对照**：CLR object header 8B（`MethodTable*`）+ 8B sync block；primitives 通过 boxing/unboxing 在栈/寄存器里走 64-bit 通道。

**建议**：
1. **拆 hot / cold variants**：把高频的 `I64 / F64 / Bool / Char / Null / Object(GcRef) / Array(GcRef)` 做一个独立 `enum HotValue` 控制在 16B（NaN-boxing 或 tagged pointer），cold variants（`PinnedView` / `Closure` / `Ref`）走 `Box<ColdValue>` 间接寻址。预期 reg move 从 48B 降到 16B（**3x 节省 cache 占用**）
2. **`Str(String)` → `Str(Rc<str>)`**：immutable string 走 Rc，clone = 1 个 atomic ref-count incr（vs 当前 String clone = 完整堆分配）
3. **`FuncRef(String) / Closure.fn_name`**：用 `MethodId`（u32 token）替代字符串名，对齐已有 token 系统

**ROI**：极高。这是 hot path 上的"基础设施加速"，所有 op 受益。

---

## C2. JIT helper `regs[i].clone()` per op ⚠️ 部分已修

**z42 实测**（[`jit/helpers/arith.rs:16-39`](../src/runtime/src/jit/helpers/arith.rs#L16-L39)）：

```rust
pub unsafe extern "C" fn jit_add(...) -> u8 {
    let regs = &(*frame).regs;
    if let (Value::I64(x), Value::I64(y)) = (&regs[a], &regs[b]) {
        (*frame).regs[dst] = Value::I64(x.wrapping_add(*y));  // ✅ I64 fast path
        return 0;
    }
    let va = regs[a].clone();                                  // ❌ clone fallback
    let vb = regs[b].clone();
    let result = match (&va, &vb) { ... };
    ...
}
```

✅ **已有**：I64 fast path（无 clone，早返回）
⚠️ **遗留**：F64 / Bool / Str 走 clone+match 慢路径。Bool / Char 完全不需要 clone（Copy 类型），是被 enum derive 自动 Clone 拖累。

**对照**：CoreCLR JIT 直接 emit `add r10, r11` 三字节机器码 —— **不走 helper**。我们走 helper 是因为运行时类型未知，但 90% 的情况其实可以由 IR 静态特化（`IrType.I64 + IrType.I64` → 直接 emit Cranelift `iadd`，0 helper call）。

**建议**：
1. **JIT type specialization**：translate.rs 已经知道每个 `TypedReg` 的 `IrType`。当 `lhs.type == rhs.type == IrType.I64`，直接 emit `iadd` Cranelift IR，不 call helper。这能让 stdlib 数值循环（如 `for i in 0..n`）的 hot loop 完全脱离 helper。
2. **Bool / Char copy-not-clone**：检查 derive，避免无意义 Clone。
3. **`Value::Str(String)` 改 `Rc<str>` 后**：Str fast path 也能 inline copy（仅 atomic incr）

预估收益：算术循环 **2-5x** 提速（Cranelift native vs C helper call）。

---

## C3. 字符串实现：`String` 而非 `Rc<str>` ❌

**z42 实测**：
- [`types.rs:205`](../src/runtime/src/metadata/types.rs#L205): `Str(String)` — owned 堆分配
- [`exec_object.rs:134`](../src/runtime/src/interp/exec_object.rs#L134): `"Length" => Value::I64(s.chars().count() as i64)` — **每次访问 `.Length` 都 O(n) UTF-8 解码**
- [`corelib/string.rs:19`](../src/runtime/src/corelib/string.rs#L19): `s.chars().nth(i)` — `str_char_at(i)` 也 **O(n)**
- [`corelib/string.rs:20`](../src/runtime/src/corelib/string.rs#L20): `s.chars().count()` again on error path

**对照** CoreCLR `StringObject` ([`vm/object.h:85`](../../../runtime/src/coreclr/vm/object.h#L85)):
- length 是 fixed field, O(1)
- UTF-16 编码，`str[i]` 是 O(1)（虽然不能跨 surrogate）
- string literals 全局 intern pool 去重

**建议**：
1. **`Value::Str(Rc<str>)`** —— 解决 clone 成本
2. **String literal interning**：编译时 stdlib literal "Length" / "ToString" / "_zeroBytes" 等用 token；运行时 string pool 在 `Module.string_pool` 已经存在 → 让 `Value::Str` 复用 pool slot（`Str(StringId)` 索引）
3. **`Length` 字段加 cache**：要么改 `Value::Str { rc: Rc<str>, char_count: Cell<Option<u32>> }`，要么直接用 byte length（语言层接受 byte semantics，C# 一直用 UTF-16 unit count 也不是 grapheme cluster）

**ROI**：高。`.Length` 在循环里被反复调用，O(n)→O(1) 是数量级差异。

---

## C4. Field offset：HashMap 慢路径 ⚠️ IC 已做、底层未优

**z42 实测**（[`types.rs:105`](../src/runtime/src/metadata/types.rs#L105) + [`exec_object.rs:95-152`](../src/runtime/src/interp/exec_object.rs#L95-L152)）：

```rust
pub struct TypeDesc {
    pub field_index: HashMap<String, usize>,   // ❌ 慢路径
    pub vtable_index: HashMap<String, usize>,  // ❌ 慢路径
    ...
}

pub(super) fn field_get(..., field_ic: Option<&FieldIC>) -> Result<()> {
    if let Some(ic) = field_ic {
        if recv_type == ic.cached_type_id.load(Relaxed) {
            let slot = ic.cached_slot.load(Relaxed);
            return ...obj.slots[slot]...;                      // ✅ IC hit: O(1)
        }
        if let Some(&slot) = type_desc.field_index.get(name) { // ❌ miss: hash(String)
            ic.cached_type_id.store(recv_type, Relaxed);       // ✅ backpatch
            ic.cached_slot.store(slot as u32, Relaxed);
            ...
        }
    }
}
```

✅ **已做**：monomorphic inline cache + backpatch（与 CoreCLR `ResolveCacheElem` 思路一致）
❌ **未做**：
- IC miss 走 `HashMap<String, usize>` lookup —— polymorphic 站点（一个 field_get 反复看到不同类型）会持续付 hash + string compare cost
- 没有 polymorphic IC（PIC）—— CoreCLR 在 `ResolveCacheElem` 上做 chained lookup，缓存最近 N 个类型

**对照** CoreCLR `FieldDesc` ([`vm/field.h:64-68`](../../../runtime/src/coreclr/vm/field.h#L64-L68))：
```c
unsigned m_dwOffset : 27;   // 直接 bake 进 FieldDesc
unsigned m_type     : 5;
```
field access = `obj + fieldDesc[i].m_dwOffset`，纯 2 条机器指令。

**建议**：
1. **`field_index: HashMap<String, usize>` → `Vec<(InternId, u16)>` + perfect hash**：load-time 把字段名 intern 成 u32 token，linear scan ≤8 fields 比 HashMap 快得多
2. **PIC**：`FieldIC` 从 `(type_id, slot)` 扩展为 `[(type_id, slot); 4]` 小数组，megamorphic 才降级 HashMap
3. **`Instruction::FieldGet { field_name: String }` → `field_id: u32`**：从字符串改为 token，跟 method token 系统对齐（compiler 已有 string pool）

---

## C5. 虚调用 dispatch（VCall）：3 层 fallback 链 ⚠️

**z42 实测**（[`exec_vcall.rs:72-281`](../src/runtime/src/interp/exec_vcall.rs#L72-L281)）：

```rust
// Tier 1: IC hit (monomorphic)         — O(1)
// Tier 2: vtable_index HashMap lookup   — O(1) avg, O(string_len) compare
// Tier 3: resolve_virtual               — 线性走父类链 try_lookup_function/level
```

✅ Tier 1 already exists
❌ Tier 2-3 cost 高 + no polymorphic cache

**对照** CoreCLR `VirtualCallStubManager` ([`vm/virtualcallstub.h:59-81`](../../../runtime/src/coreclr/vm/virtualcallstub.h#L59-L81))：
```c
struct ResolveCacheElem {
    void  *pMT;        // MethodTable (type identity)
    size_t token;      // (class_id, slot_id)
    void  *target;     // 直接解析好的 code pointer
    ResolveCacheElem *pNext;  // 冲突链
};
// 全局哈希表 cap=2048，megamorphic 也快
```

**建议**：
1. **`vtable_index: HashMap<String, usize>` → 同 C4**：method name intern + vec lookup
2. **PIC for VCall**：4-slot small array cache
3. **Megamorphic 降级到全局 (TypeId, MethodId) → Slot 表**：而非每个 site 自己 cache

---

## C6. GC 分配 fast path：完全没有 ❌

**z42 实测**（[`gc/arc_heap.rs`](../src/runtime/src/gc/arc_heap.rs)）：每次 `alloc_object` / `alloc_array` 都走 `Rc::new(GcAllocation { inner: RefCell::new(...), header: ... })`：
- atomic refcount 初始化
- RefCell borrow 状态初始化
- 进 heap_registry: `Vec<Weak>` 注册

每次分配 = 2 个 atomic op + 1 个 vec push + 1 个 malloc。

**对照** CoreCLR ([`vm/gchelpers.cpp:44-51`](../../../runtime/src/coreclr/vm/gchelpers.cpp#L44-L51))：
```c
ee_alloc_context {
    BYTE *alloc_ptr;    // 当前写指针
    BYTE *alloc_limit;  // chunk 末尾
};
// 内联 fast path 仅 3 条指令：
//   add rax, size
//   cmp rax, limit
//   jb  slow_path
//   mov [old_ptr], MethodTable*
```

**建议**：
1. **Phase 1**：保留 `ArcMagrGC` 作为默认，先把"分配 + 注册到 registry"做成单独 fast path（thread-local SLAB 池 + pre-allocated GcAllocation pool）
2. **Phase 2（L3 之后）**：考虑替换为真正的 bump allocator + tracing GC。但这是大手术，建议先做 L3 业务再回来
3. **Phase 0**（**马上能做**）：`alloc_object` 内联 fast path —— 把 `RefCell::new` 和 `Rc::new` 合成一次 `Box::leak` + atomic 写。当前的间接性来自 trait `MagrGC` dyn dispatch，可以加 `#[inline(always)]` 在默认实现上

---

## C7. Method token 跨 zpkg lazy 路径 ⚠️

**z42 实测**（[`metadata/resolver.rs`](../src/runtime/src/metadata/resolver.rs) + [`tokens.rs`](../src/runtime/src/metadata/tokens.rs)）：

✅ **已做**：introduce-method-token (Phase 4, 2026-05-08) — 每个 Call site 有 atomic token 缓存槽
✅ **已做**：本模块内 Call 在 resolver 阶段全部预解析
❌ **未解决**：cross-zpkg call 永远 `UNRESOLVED` → 每次都走 `try_lookup_function(&name)` 字符串 HashMap

**对照** CoreCLR：cross-assembly call 也是首次未解析 → 走 prestub → 编译后 patch call site 到真实 code ptr。**第二次调用直接跳，无任何 lookup**。

**建议**：cross-zpkg site 在首次命中后 atomic CAS 写入解析好的 (ModuleId, FunctionIdx) 元组，后续走 array index 而非 HashMap。已有 `AtomicU32` slot 基础设施，扩展为 `AtomicU64` 携带 (module:32, func_idx:32) 即可。

---

## C8. 已经做对的部分 ✅（CoreCLR 同款思路）

| z42 已做 | CoreCLR 对应 | 注 |
|---|---|---|
| `MagrGC` trait 解耦 GC ↔ VM | `gcinterface.h` callbacks | trait 抽象干净，比 C++ vtable 略胜 |
| 每个 Call site 的 `method_tokens: Vec<AtomicU32>` | 同 CoreCLR call site patching | introduce-method-token 已落地 |
| Monomorphic FieldIC + VCallIC | CoreCLR `ResolveCacheElem` 单 slot 形态 | 思路一致，缺 PIC |
| Backpatch CAS-on-miss | CoreCLR same | 已对齐 |
| Exhaustive match dispatch（无 `_` 兜底） | — | CoreCLR 是手写汇编 dispatch；z42 借 Rust 类型系统更安全 |
| zbc strict-pin（无旧版本兼容） | CoreCLR ReadyToRun 也版本严格 | 已对齐 pre-1.0 节奏 |
| Per-site cache slot index resolver | CoreCLR per-call-site IBC profile | 思路对齐 |

---

## C9. 加入新优先级表

把代码层的发现合并到之前的宏观 P0-P3，重新排：

| 优先级 | 改造 | 估时 | 解锁价值 | 类别 |
|---|---|---|---|---|
| **P0** | **JIT type specialization**（C2）— 同 IrType 时直接 emit Cranelift native，不走 helper | 2-3 天 | 数值循环 2-5x；不动 wire format | 代码 |
| **P0** | **JIT↔VM trait 抽象** —— 上半部分 P0 | 2-3 天 | metadata 可演进 | 架构 |
| **P1** | **`Value::Str(String) → Str(Rc<str>)`**（C1+C3）— clone 廉价化 | 1-2 天 | 字符串 clone 0 拷贝 | 代码 |
| **P1** | **Field / Method name → token id**（C4+C5）— `HashMap<String> → Vec<(u32, slot)>` | 3-4 天 | poly site 提速 + 内存省 | 代码 |
| **P2** | **Prestub / lazy JIT** —— 上半部分 P1 | 3-5 天 | 启动延迟 | 架构 |
| **P2** | **Polymorphic IC（PIC）**（C4+C5）— 4-slot cache | 2-3 天 | poly site 不再退到 HashMap | 代码 |
| **P3** | **String literal interning**（C3）— `Value::Str(StringId)` 复用 `Module.string_pool` | 3-4 天 | 内存 + 加速 string == | 代码 |
| **P3** | **PAL 抽象层** —— 上半部分 P2 | 5-7 天 | 多线程 + 移植 | 架构 |
| **P4** | **`Value` 拆 hot/cold variants**（C1）— enum 从 48B → 16B | 5-7 天 | reg move 3x + cache locality | 代码 |
| **P4** | **GC bump allocator**（C6）— 替换 Rc 默认 | 巨大 | L3 性能瓶颈 | 架构 |
| **P5** | **Hot-path stub inline** —— 上半部分 P3 | 2-3 天 | 微优化 | 代码 |
| **P5** | **`String.Length` O(n) → O(1)**（C3）— cache char_count 或改 byte semantics | 1 天 | 字符串循环 | 代码 |

---

## C10. 不抄 / 推迟的部分

- **NaN-boxing 风格 64-bit Value 编码**：太激进，Rust enum 表达力足够；先做 hot/cold 拆分（C1）就行
- **Two-pass EH + funclet**：L3 `catch when` filter 需求出现再做
- **Generational GC / card marking**：先把 bump allocator 做好；分代是后置优化
- **Tiered compilation (Tier 0 / Tier 1)**：等 PGO 框架先有
- **DAC (Data Access Component) 后置调试**：用户呼声出来再做

---

## C11. 立即可上的小项目（每个 ≤1 天，独立 commit）

这些是"看了就能动手"的局部优化，建议穿插在 spec 间隙做：

1. ⚡ **`String.Length` 加 cache** 或改 byte semantics —— [`exec_object.rs:134`](../src/runtime/src/interp/exec_object.rs#L134)、[`corelib/string.rs:10`](../src/runtime/src/corelib/string.rs#L10)
2. ✅ ~~**`__str_char_at` 错误路径减少一次 `chars().count()`**~~ — single-pass impl (2026-05-25), [`corelib/string.rs:21`](../src/runtime/src/corelib/string.rs#L21)
3. ⚡ **JIT bool 类 helper（jit_and / jit_or / jit_not）走 Cranelift native** —— Bool 类型 IR 已知，不需要 helper
4. ⚡ **`jit_const_*` 完全 inline emit** —— Cranelift 原生支持 const，不需要 helper call
5. ✅ ~~**`Frame::regs: Vec<Value>` → 函数入口预分配 `with_capacity(max_reg+1)`**~~ — `Frame::new` pre-sizes via `vec![Value::Null; max_reg]` (no per-set push)

每项独立 commit、独立 spec（fix/refactor 类型，不需要完整 spec 流程）。

---

*文档作者：Claude（z42 项目 review 助手）*
*核对范围：源码 commit `62899729`（2026-05-19 fix-array-default-init）+ 截至 2026-05-21 的 working tree*

---

# Part 3：标准库 Review（2026-05-21 补充）

> 对比对象：CoreCLR [`runtime/src/libraries/`](../../../runtime/src/libraries/) (213+ packages) vs z42 [`src/libraries/`](../src/libraries/) (18 packages, 9986 LOC)
>
> 目的：找 z42 stdlib 的组织缺失，识别 CoreCLR 成熟模式哪些可以借鉴。

## S1. CoreCLR libraries 关键组织模式

| 模式 | 做法 | 关键收益 |
|---|---|---|
| **ref/ vs src/ 分离** | 每个 lib 含 `ref/<Name>.csproj` 只列签名 + `src/<Name>.csproj` 含实现 | 公共 API contract 跟 impl 解耦；breaking change 必须改 ref/，CI 强制 |
| **Common/src/Interop** | `Interop.Brotli.cs` / `Interop.Unix.cs` / `Interop.Windows.cs` partial classes | 多平台 P/Invoke 集中；新平台 = 加一个 partial 文件 |
| **Common/tests trait 合约** | `ICollection.Generic.Tests.cs` 一份，被 List / Queue / Dict tests `Link=` 进各自工程 | 50+ 接口契约测试一次写、所有实现共享 |
| **Tier 0/1/2/3 分层** | CoreLib (0) → Runtime/Collections/Threading (1) → Json/Http (2) → Extensions (3) | 严格 DAG，CI 阻止循环依赖 |
| **Multi-target via `<TargetFrameworks>`** | 一个 .csproj，`net8;net9;netstandard2.0;net462` | 一份源码跨 5 个 framework 出货 |
| **Facade pattern** | `<IsPartialFacadeAssembly>true</IsPartialFacadeAssembly>` 把类型 forward 到 CoreLib | 公开 surface 稳定、impl 可下沉 |
| **Shared helper code via `<Compile Link=>`** | `HexConverter.cs` / `ValueStringBuilder.cs` 物理一份，多个 lib 通过 link 引用 | 杜绝代码重复，比 import 还轻 |

## S2. z42 stdlib 现状与对照

### 已经做对的部分 ✅

| z42 | CoreCLR 对应 | 注 |
|---|---|---|
| Topological ordering 在 [`z42.workspace.toml`](../src/libraries/z42.workspace.toml) 显式 + 注释解释 | CoreCLR build graph 同思路（DAG） | 已避免循环依赖 |
| z42.core 作为 prelude（compiler+VM 自动注入，用户不能显式 dep） | `System.Private.CoreLib` 同地位 | 对齐 |
| 17 个包按职责分（io / text / encoding / collections / json / regex / crypto / test 等） | System.* / Microsoft.Extensions.* 同思路 | 粒度合理 |
| 测试与 src 分目录（`<pkg>/tests/`） | 每个 lib `tests/<Name>.Tests.csproj` | 对齐 |
| Per-member artifacts 隔离（`artifacts/build/libraries/<member>/<profile>/`） | CoreCLR per-lib bin/obj | 对齐 |
| `using Std.<sub>` 导入约定 | `using System.<sub>` | 对齐 |

### 问题清单 ❌

#### S2.1 z42.crypto **没有 README** ❌

所有其他 17 个包都有 README，z42.crypto 缺失。z42.crypto 是最新加的包（2026-05-17 add-z42-crypto），归档时漏写。

**修复**：1 个文件 ≤30 分钟。可以现在补。

#### S2.2 z42.text 和 z42.regex 重复 ❌

[`z42.text/src/Regex.z42`](../src/libraries/z42.text/src/Regex.z42) 是早期 stub，但 z42.regex 是独立包（770 LOC + 6 test files）实际实现。stub 留着是 **dead code**，可能误导新用户。

**修复**：删除 `z42.text/src/Regex.z42`；如果 namespace 有冲突就在 z42.text README 里说明 "Regex moved to z42.regex"。

#### S2.3 **没有共享 helper 代码层** ❌（最值得借鉴）

CoreCLR 把 `HexConverter.cs` / `ValueStringBuilder.cs` / `ArrayPool.cs` 放在 [`Common/src/`](../../../runtime/src/libraries/Common/src/)，多个 lib 通过 `.csproj` 的 `<Compile Include="..." Link="..." />` 物理引用同一份代码 —— 不是 namespace import，是源码级共享。

z42 现状：

- z42.encoding 有 `Hex.z42`（hex encode/decode）
- z42.crypto 也会需要 hex（已经 import 了 z42.encoding ✅）
- 但**比 hex 更小的 helpers**（如 zero-fill buffer pattern、Utf8 byte-count、format 通用 padding）目前可能在多个包里各写各的

**深层问题**：z42 没有"通用工具但又不值得做成 public stdlib API"的存放处。CoreCLR 的 `Common/src/` 解决这个 — 既不污染 public namespace 又能内部共享。

**建议**：
- 短期：识别 stdlib 中的代码重复（grep `(byte)0` patterns、`StringBuilder` 重复使用 patterns）
- 长期：建立 `src/libraries/_internal/` 或在 z42.core 加 `internal namespace Std._Internal`（语言层加 `internal` 访问修饰符是单独 spec）

#### S2.4 **没有 trait-based test commons** ❌

CoreCLR `Common/tests/` 有 `ICollection.Generic.Tests.cs`、`IDictionary.Generic.Tests.cs` 等 ~30 个 trait test suite，每个有 ~50 个测试。所有实现（`List<T>`/`Queue<T>`/`SortedSet<T>`/`ConcurrentDictionary<T>`/...）通过 `.csproj` link 进来自动跑同一套合约测试。

z42 现状：
- z42.collections 的 Queue / Stack / LinkedList / SortedSet 各有 4 个独立测试文件，**互不共享**
- z42.io 的 13 个文件、20 个测试都是 per-file 写的

**建议**：
- z42.test 加 `contracts/` 子目录：`ICollectionContract.z42` / `IEnumerableContract.z42` / `IComparableContract.z42`
- 每个 contract 定义 ~20 个测试函数，take 一个工厂方法 `Func<T>` 创建被测对象
- z42.collections.tests / z42.io.tests 等通过 import 调用 contract 跑测试
- 收益：新增一个 collection 实现，测试代码 = 2 行（call contract 工厂）

**估时**：3-5 天。**这是 stdlib 改造里 ROI 最高的一项**。

#### S2.5 **没有 public API surface 文档** ⚠️

CoreCLR `ref/` 是机器可读的 API contract，CI 阻止意外破坏。z42 没有等价物。最接近的是 README 里的"主要类型"列表，但不强制、不可机检。

**短期建议**：
- 每个包 README 标准化加 `## Public API` 段，列出所有 public class / method 签名
- CI 加 lint：跑一遍 `z42c export <pkg>` 输出公共 API，diff 当前 vs README 段，不匹配则 fail
- 长期：spec 一个 `z42c api-surface <pkg>` 命令，输出 IR 级签名（参考 cargo 的 `cargo public-api`）

#### S2.6 **没有平台抽象 partial 命名约定** ⚠️ 推迟

z42 目前单平台，未涉及 Unix / Windows 路径分支。但 CoreCLR 的 `Interop.Unix.cs` / `Interop.Windows.cs` partial-class 模式是**为未来移植做准备**，不是当前需求。

**建议**：现在记笔记，等 z42.os / z42.io.fs Phase 3 落地时按 `Foo.Unix.z42` / `Foo.Windows.z42` 命名（即使现在只有 Unix 实现）。

#### S2.7 命名一致性：可解释的不一致 ⚠️

- `String.z42` (PascalCase) + `class String`
- `int.z42` / `long.z42` / `bool.z42` (lowercase) + `struct <name>`

这是已有设计决策（primitive-as-struct，String 例外因为是 reference type）。✅ 已文档化，不动。

#### S2.8 README 质量不一 ⚠️

- 高质量（70-108 行）：z42.core / z42.io / z42.uri / z42.cli / z42.diagnostics
- 中等（45-67 行）：z42.collections / z42.text / z42.encoding / z42.regex
- 极简（11-22 行）：z42.math / z42.time / z42.test
- 缺失：z42.crypto

**建议**：建立 [`docs/stdlib/README-template.md`](../docs/stdlib/) 模板，新包必须套。已有包按"用户访问频次"渐进补齐（z42.math / z42.time 用户用得多，应该详细一点）。

## S3. z42 stdlib API 覆盖度对照 CoreCLR

| 领域 | CoreCLR | z42 | 缺口 / 评估 |
|---|---|---|---|
| 基础类型（int / string / array / object） | System | z42.core | ✅ 对齐 |
| 集合（List / Dict / Queue / Stack / ...） | System.Collections.Generic | z42.core (List/Dict) + z42.collections (others) | ✅ 基本覆盖；缺 ConcurrentDictionary（待 threading） |
| 字符串处理 | System.Text | z42.text | ⚠️ z42.text 太单薄（97 LOC）；StringBuilder 在 z42.text，但 Format / Tokenize / Splitter 等都缺 |
| 编码 | System.Text.Encoding / System.Buffers.Text | z42.encoding | ✅ Utf8 / Hex / Base64 都有 |
| 数学 | System.Numerics | z42.math | ⚠️ 50 LOC，缺 BigInteger / Vector / Decimal / Complex |
| 时间 | System.DateTime / System.TimeSpan | z42.time | ⚠️ 177 LOC；缺 timezone、calendar、duration arithmetic 完整 |
| IO / 文件系统 | System.IO.* | z42.io | ✅ 13 文件，覆盖 console / file / process / dir |
| JSON | System.Text.Json (1500+ LOC) | z42.json (736 LOC) | ⚠️ 缺 streaming reader/writer；当前是 DOM-only |
| 加密 | System.Security.Cryptography (8000+ LOC) | z42.crypto (164 LOC, SHA-256 only) | ❌ 缺 HMAC / AES / RSA / ECDSA / CSPRNG / X.509（已有 deferred 列表） |
| 网络 | System.Net.* | — | ❌ 整个 z42.net 不存在；roadmap P1 |
| 反射 | System.Reflection | typeof 内嵌 | ⚠️ 仅 `typeof(T)` + `obj.GetType()`；缺 Type / FieldInfo / MethodInfo / Attribute scan |
| 异步 | System.Threading.Tasks | — | ❌ async/await 是 L3 phase |
| 序列化（XML/二进制） | System.Runtime.Serialization | — | ❌ 不在 roadmap |
| 正则 | System.Text.RegularExpressions | z42.regex (770 LOC) | ✅ 已有 |
| TOML | — | z42.toml (1157 LOC) | ⚠️ CoreCLR 没有；z42 stdlib over-scope（应该是第三方？） |
| URI | System.Uri | z42.uri | ✅ 对齐 |
| 多线程 | System.Threading | z42.threading (398 LOC) | ⚠️ 早期；Channel / Mutex / RwLock 有，但 ThreadPool / Tasks 缺 |
| CLI 参数解析 | System.CommandLine (独立 NuGet) | z42.cli | ✅ 对齐（也是 stdlib 边缘） |
| 诊断 / logging | System.Diagnostics + Microsoft.Extensions.Logging | z42.diagnostics (67 LOC) | ⚠️ 极简 |
| 测试框架 | xUnit (独立) | z42.test | ✅ 自带是 z42 特色 |

**结论**：
- **核心覆盖** 7/9 OK，缺反射 + 异步（已知 L3）
- **第二梯队**（数学 / 时间 / 字符串处理 / JSON）有缺口但 v0 够用
- **第三梯队**（网络 / 高级加密 / 序列化）大缺口；roadmap 已列
- **over-scope 检视**：z42.toml 在 stdlib 里有点重；CoreCLR TOML 是第三方。建议保持现状但加注 "stdlib first-party because z42 build-driver self-hosts on TOML config"

## S4. 立即可做的小项目（每个 ≤1 天）

1. ⚡ **补 z42.crypto README** — 列出 SHA-256 用法 + deferred 列表（HMAC / CSPRNG）
2. ⚡ **删除 z42.text/src/Regex.z42 stub** — 误导性的死代码
3. ⚡ **建立 README-template.md** — 用 z42.core/README.md 为蓝本
4. ⚡ **z42.math / z42.time README 扩充** — 补齐到中等质量（40-50 行）
5. ⚡ **stdlib API audit one-liner** — `z42c export <pkg>` 命令规划（spec → 实施分两阶段）

## S5. 中期项目（每个 3-7 天）

| 优先级 | 项目 | 估时 | 价值 |
|---|---|---|---|
| **S-P0** | **trait-based test commons** — z42.test 加 `ICollectionContract` / `IEnumerableContract`，所有 collections 复用 | 3-5 天 | 测试代码 -50%，新 collection 落地从 1 天 → 2 小时 |
| **S-P1** | **internal shared helpers 层** — 识别 stdlib 中重复 patterns 提到 z42.core/_Internal 或新 internal-only package | 5-7 天 | 杜绝代码重复；维护成本下降 |
| **S-P1** | **public API surface lint** — README `## Public API` 段 + CI diff lint | 2-3 天 | API breaking change 检测；对齐 ref/ 角色 |
| **S-P2** | **z42.text 扩充** — Format / Tokenize / Splitter / Padding helpers | 5-7 天 | 减少业务代码里手写 StringBuilder |
| **S-P2** | **z42.math BigInteger / Decimal** | 7-10 天 | 解锁 z42.crypto 后续工作（RSA 需 BigInteger） |
| **S-P3** | **z42.io streaming JSON reader/writer** | 5-7 天 | 大文件场景；DOM-only 撑不住 |

## S6. 长期项目（spec + ≥2 周）

- **z42.net** — 完整网络栈（TCP / HTTP / TLS）→ 阻塞 web 类应用
- **z42.crypto 完整版** — HMAC / AES / RSA / ECDSA / X.509 → 阻塞安全敏感应用
- **z42.reflection** — Type / FieldInfo / MethodInfo / AttributeScan → 阻塞 ORM / serialization framework
- **async / await** — L3 阶段；scheduler + Task<T>

---

*Part 3 作者：Claude（z42 review）*
*调研日期：2026-05-21*

---

# Part 4：运行时 Ops / DevEx 设施 Review（2026-05-22 补充）

> 对比对象：CoreCLR 的 **运行时配置 / 日志 / 事件流 / profiling / crash 处理 / 指标 / startup 信息** 等"生产 VM 必备"基础设施 vs z42 现状。
>
> 与 Part 2 不同：Part 2 看的是 hot path 性能；这部分看 **operability + observability** —— 一个 VM 要进生产，除了能跑得快，还得能"看得见 + 调得出"。

## D1. 运行时配置系统

### CoreCLR
- 中心配置表 [`inc/clrconfigvalues.h`](../../../runtime/src/coreclr/inc/clrconfigvalues.h) —— 宏定义注册**几百个** runtime knobs（GC 触发阈值 / JIT tier 阈值 / log facility mask / heap 大小上限 / ...）
- 统一访问：`CLRConfig::GetConfigValue(CLRConfig::INTERNAL_*)`
- 支持 env var (`DOTNET_*`) / config 文件 (`runtimeconfig.json`) / registry 三层叠加
- 每个 knob 含 default / type / description / scope (internal vs external)

### z42 现状
- 仅 [`main.rs:55`](../src/runtime/src/main.rs) 的 `Z42_LIBS` + [`main.rs:123`](../src/runtime/src/main.rs#L123) 的 `Z42_PATH` 两个 env var
- 无中心化 `RuntimeConfig` struct；新加 knob 要散落到各处 `std::env::var()`
- 无 CLI ⇄ env var ⇄ config file 三层叠加约定

### 建议 ❌
1. **新建 `runtime/src/config.rs`**，定义：
   ```rust
   pub struct RuntimeConfig {
       // GC
       pub gc_heap_limit_bytes: Option<u64>,        // Z42_GC_HEAP_LIMIT
       pub gc_verbose: bool,                         // Z42_GC_VERBOSE
       pub gc_collect_threshold_bytes: u64,
       // JIT
       pub jit_disable: bool,                        // Z42_JIT_DISABLE
       pub jit_dump_ir: bool,                        // Z42_JIT_DUMP_IR
       // Logging
       pub log_directives: String,                   // Z42_LOG=jit=debug,gc=trace
       // Paths
       pub libs_dir: Option<PathBuf>,                // Z42_LIBS (existing)
       pub module_path: Vec<PathBuf>,                // Z42_PATH (existing)
       // Diag
       pub crash_report_dir: Option<PathBuf>,        // Z42_CRASH_DIR
       pub stack_trace_depth_limit: usize,
   }
   impl RuntimeConfig { pub fn from_env() -> Self {...} }
   ```
2. CLI flag 同步 `--gc-verbose` / `--jit-disable` 等，CLI 覆盖 env

**ROI**：高。1-2 天。后续所有 ops/devex 改造都要这个底层。

## D2. 日志基础设施

### CoreCLR
- [`inc/log.h`](../../../runtime/src/coreclr/inc/log.h) 定义 `LF_*` facility mask（`LF_GC` / `LF_JIT` / `LF_LOADER` / `LF_INTEROP` / ...）× 7 个 level（`LL_ALWAYS` ... `LL_EVERYTHING`）
- 用法：`LOG((LF_GC, LL_INFO, "starting gc cycle %d", cycle))`
- 单独的 [`stresslog.h`](../../../runtime/src/coreclr/inc/stresslog.h)：固定大小 circular buffer，**zero I/O**，崩溃时从 debugger 读取
- 三种 build (CHK / DBG / FRE) 静态裁剪 log level

### z42 现状
- 已用 `tracing` crate ✅ ([main.rs:148-170](../src/runtime/src/main.rs#L148-L170) 等)
- 启动方式：`--verbose` 单一 binary 开关 → `tracing_subscriber::fmt::init()` ([main.rs:251](../src/runtime/src/main.rs#L251))
- 问题：
  - 无 per-module 控制（要么全开要么全关）
  - hot path（`interp/exec_*.rs`）几乎没有 `debug!` / `trace!`
  - 无 stress log 类似物（崩溃前的 ring buffer）

### 建议 ⚠️
1. **`tracing` directives 已经支持 per-module**，只需改启动：
   ```rust
   tracing_subscriber::fmt()
       .with_env_filter(
           tracing_subscriber::EnvFilter::try_from_env("Z42_LOG")
               .or_else(|_| tracing_subscriber::EnvFilter::try_new("z42=warn"))
               .unwrap()
       )
       .init();
   ```
   → 用户可以 `Z42_LOG=z42::jit=debug,z42::gc=trace ./z42vm ...`
2. **在 hot path 加 `trace!` 级别日志**（编译时 zero cost when feature 关闭）：
   - `interp::exec_call::call` — log function entry
   - `gc::arc_heap::collect_cycle` — log GC cycle stats
   - `jit::translate::translate_function` — log JIT compile
3. **stress log 推迟**：等真出现"崩溃前最后几条状态"诊断需求再做

**ROI**：高。半天就能把 EnvFilter 接上 + 加 10 处 trace!。

## D3. 结构化事件流 ✅ Phase 1 落地

### CoreCLR
- **EventPipe** ([`vm/eventpipeinternal.h`](../../../runtime/src/coreclr/vm/eventpipeinternal.h))：统一事件流，多 provider（GC / JIT / Thread / Type / Loader / Method / Assembly / Exception）
- 外部工具（`dotnet-trace` / `dotnet-counters` / Perfview）通过 IPC 订阅
- 内置 circular buffer，可写 `.nettrace` 文件
- ETW 兼容（Windows native）+ EventPipe 协议（cross-platform）

### z42 现状 ✅
- [`gc/types.rs:74`](../src/runtime/src/gc/types.rs#L74) `GcObserver` trait + 5 个 `GcEvent` variants ✅
- **Phase 1 (add-runtime-observer, 2026-05-26)**：[`observer::RuntimeObserver`](../src/runtime/src/observer.rs) trait + `RuntimeEvent` enum (Phase 1: `ModuleLoaded` + `Custom` escape hatch) + `RuntimeObserverRegistry` on `VmCore` + `VmContext::add_runtime_observer` / `fire_runtime_event` accessors。Demo emit site：`main.rs` 每个 boot-time `load_artifact` 成功后 replay-emit `ModuleLoaded`。
- **Phase 2 待补**（独立小 refactor）：JitCompiled in `jit::compile_module` / ExceptionThrown+Caught in `exception::*` / NativeCallEntered in `interp::exec_native` / lazy_loader emit per on-demand zpkg
- **Phase 3 推迟**：外部 IPC 订阅协议（dotnet-trace 等价）、`.nettrace` 兼容 binary format、circular buffer with overrun policy

### 建议 ❌
1. **扩展 observer 到非 GC 域**：
   ```rust
   // runtime/src/observability/events.rs (新文件)
   pub enum RuntimeEvent {
       Gc(GcEvent),                                      // 已有
       JitCompiled { func: String, ir_size: usize, code_size: usize, duration_us: u64 },
       ExceptionThrown { class: String, message: String },
       ExceptionCaught { class: String, frames_unwound: usize },
       NativeCallEntered { lib: String, symbol: String },
       ModuleLoaded { name: String, size: u64 },
   }
   pub trait RuntimeObserver { fn on_event(&self, evt: &RuntimeEvent); }
   ```
2. **`RuntimeStats` snapshot API**（同步轮询用，区别于 push-based event）：
   ```rust
   pub struct RuntimeStats {
       pub heap: HeapStats,
       pub jit_methods_compiled: u64,
       pub jit_total_compile_us: u64,
       pub exceptions_thrown: u64,
       pub exceptions_caught: u64,
       pub instructions_executed: u64,  // optional, gate behind feature
   }
   ```
3. **外部 IPC 协议推迟**：先把 in-process observer 做扎实，再考虑 socket 暴露

**ROI**：中。3-5 天。前置依赖 D1 config。

## D4. Crash 处理 + Signal handler ✅ Phase 1 + Phase 2 已落地

### CoreCLR
- [`debug/createdump/`](../../../runtime/src/coreclr/debug/createdump/) —— SIGSEGV / SIGABRT / 内部 fail-fast 都触发 minidump 生成
- 输出 ELF/PE core dump，可供 `lldb` / `windbg` 离线分析
- Stack walk 在 signal handler 内安全（async-signal-safe primitives）

### z42 现状 ✅
- **Phase 1**（commit `12cf7ef8`, 2026-05-25）：Rust panic hook + `Z42_CRASH_DIR` —— 覆盖 `panic!` / `unwrap` / index OOB / `debug_assert!` failure，捕获 VM 版本 + panic location + Rust backtrace
- **Phase 2**（add-os-signal-handler spec, 2026-05-26）：POSIX OS signal handler —— 覆盖 SIGSEGV / SIGABRT / SIGFPE / SIGILL / SIGBUS，async-signal-safe `sigsafe` 子模块手写写入 primitives，try_lock 拿 z42 call stack（所有 thread / 所有 VmCore），reset to SIG_DFL + raise 保留 kernel coredump
- 还缺：Windows VEH（Phase 2.1）/ stack-pointer 线程归因（Phase 2.2）/ `sigaltstack` for stack overflow（Phase 2.3）/ async-signal-safe Rust backtrace（Phase 2.4）/ 统一 panic+signal 文件复用（Phase 2.5）

### 建议 ❌ **生产前必做**
1. **加 Rust panic hook**（[`main.rs`](../src/runtime/src/main.rs) 入口）：
   ```rust
   std::panic::set_hook(Box::new(|info| {
       eprintln!("=== z42vm internal panic ===");
       eprintln!("location: {:?}", info.location());
       eprintln!("payload: {:?}", info.payload());
       // dump stack trace via backtrace crate
       let bt = std::backtrace::Backtrace::capture();
       eprintln!("rust backtrace:\n{}", bt);
       // dump VM state if available (via thread-local CURRENT_VM)
       if let Some(ctx) = crate::native::exports::CURRENT_VM.with(|c| c.get()) {
           eprintln!("z42 call stack:\n{}", ctx.format_call_stack());
       }
       std::process::abort();
   }));
   ```
2. **OS signal handler**（用 [`signal-hook`](https://crates.io/crates/signal-hook) crate）：
   - SIGSEGV / SIGABRT / SIGFPE → 同上 dump + abort
   - SIGINT → graceful shutdown（让用户 Ctrl+C 时也能看到 partial trace）
3. **`Z42_CRASH_DIR` env var** —— crash 报告输出到该目录而非 stderr
4. **minidump 推迟**：cross-platform 复杂，先做文本 dump

**ROI**：极高。1-2 天。production 前必备。

## D5. 启动 banner + 版本信息

### CoreCLR
- 启动期默认静默；DEBUG build 打印 runtime 版本 + enabled features
- `dotnet --info` 输出完整 runtime / SDK / framework 信息

### z42 现状
- [`main.rs:6`](../src/runtime/src/main.rs#L6) `#[command(name = "z42vm", version)]` —— clap 自动 `--version`
- 启动完全静默（无 banner）
- 没参数运行只输出 clap 的 "error: missing required argument"

### 建议 ⚠️
1. **`z42vm --info` 子命令**输出：
   ```
   z42vm 0.1.0
   target: aarch64-apple-darwin
   features: jit, native-interop
   exec modes: interp, jit
   libs dir: /Users/.../artifacts/build/libs/release
   gc: ArcMagrGC (Rc-backed, Phase 3-OOM)
   build profile: release
   ```
2. **verbose 模式启动 banner**：`--verbose` 时打印简短版（z42vm 0.1.0 (jit) starting）
3. **无参数时**：clap default 已经够，但可以加 `--help-mini` 一行式
4. **首次 JIT 编译 / 首次 GC** 默认 trace! 级别 —— 通过 D2 的 EnvFilter 可以打开

**ROI**：低。0.5 天。诊断价值不高但用户体验好。

## D6. 性能指标 / Counters ✅ Phase 1 落地

### CoreCLR
- EventCounters 模型：runtime 暴露 `gc-heap-size` / `cpu-usage` / `working-set` / `gen-2-gc-count` / `exception-count` 等内置 counter
- 定期采样（默认 60s）+ EventPipe 推送到 monitoring backend
- `dotnet-counters monitor` 直接展示

### z42 现状 ✅
- [`gc/types.rs`](../src/runtime/src/gc/types.rs) `HeapStats`（`allocated_bytes` / `freed_bytes` / `objects_count` / `pause_us` 等）✅
- 暴露给 z42 脚本：`Std.GC.UsedBytes()` / `Std.GC.ForceCollect()` ✅
- **Phase 1 (add-runtime-counters, 2026-05-26)**：[`counters::RuntimeCounters`](../src/runtime/src/counters.rs) 6 个 AtomicU64 字段（builtin_calls / native_calls / jit_methods_compiled / jit_compile_us_total / exceptions_thrown / exceptions_caught）+ Snapshot view + Display impl + `--print-stats-on-exit` CLI flag。`VmCore.counters: Arc<RuntimeCounters>` 单 instance 跨 thread 共享。
- **Phase 1 wiring**：`corelib::exec_builtin` / `exec_builtin_by_id` 接 `builtin_calls.fetch_add` 作为 demo + 验证。
- **Phase 2 待补**（每个独立小 refactor）：JIT 编译数 / 时间 in `jit::compile_module`；native call in `interp::exec_native`；exceptions in `exception::*`
- **Phase 3 推迟**：`Std.Diagnostics.RuntimeStats.Snapshot()` 脚本侧 API / 定时打印 / Prometheus / OTLP exporter

## D7. Profiling 钩子

### CoreCLR
- `ICorProfilerInfo` API：function entry/exit / GC events / alloc / exception / thread lifecycle
- profiler 以 out-of-process 方式 attach
- Evacuation counter 同步避免死锁

### z42 现状
- [`gc/types.rs:99`](../src/runtime/src/gc/types.rs#L99) `AlloceSamplerFn` —— allocation 采样 ✅
- 无 function entry/exit hook
- 无 JIT compile event hook

### 建议 ⚠️ 后置
- 当前阶段 RuntimeObserver（见 D3）已经够；完整 profiling API 等 Phase 4
- function entry/exit 钩子和 JIT inline 冲突，需配合 tiered compilation 设计

**ROI**：低（短期），高（Phase 4+）

## D8. 内部 invariant 检查

### CoreCLR
- [`inc/check.h`](../../../runtime/src/coreclr/inc/check.h) `CONTRACTL` 宏 —— 形式化 pre/post/invariant
- 三种 build (CHK / DBG / FRE)：retail build 完全擦除
- `_ASSERTE` 类似 Rust `debug_assert!` 但 + thread-safe + capture context

### z42 现状
- 596 行 `debug_assert!` / `assert!` / `bail!` 散落在 81 文件 ✅ 覆盖关键路径
- 无 formal contract 系统

### 建议 ✅ 当前够用
- Rust 类型系统已经替代了 CoreCLR 一大半 CONTRACTL 用途（`Option<T>` / `Result<T, E>` / 借用检查器）
- 真正缺的是 **runtime invariants** —— 这块用 `debug_assert!` 即可
- 不需要 formal contract 框架

**结论**：保持现状，标记为已对齐。

## D9. 诊断 IPC

### CoreCLR
- Diagnostic Server：named pipe (Win) / Unix socket，外部工具实时连接
- 协议：dotnet-trace / dotnet-counters / dotnet-dump

### z42 现状 ❌
- 完全没有
- 无 REPL / debug protocol / dump-on-demand

### 建议 ❌ 推迟
- 单线程 VM 实施成本不值；多线程 (Phase 3) 落地后再考虑
- 短期替代：CLI `--stats-interval=5s` 在 stderr 周期输出 stats（D6 配套）

**ROI**：极低（短期）

## D10. README / 运维手册

### CoreCLR
- 完整运维文档（troubleshooting / GC tuning / JIT diagnostics / collecting traces）
- 每个 subsystem 有自己的 design doc

### z42 现状
- [`src/runtime/README.md`](../src/runtime/README.md) 73 行 ✅
- 各 subsystem 有 README（gc / interp / jit）✅
- 缺：**运维手册**（"GC pause 怎么诊断"/"JIT crash 怎么 debug"/"native interop hang 怎么排查"）

### 建议 ⚠️
- 不急。等 production 用户出现报障再写
- 短期：在 [`docs/workflow/debugging/`](../docs/workflow/debugging/) 加 "Runtime triage" 一章

**ROI**：低。0.5-1 天，等出第一个 production bug 再写。

---

## D11. 综合优先级表（合并 Part 1-4）

把所有 Part 的发现重排：

| 优先级 | 改造 | Part | 估时 | 类别 |
|---|---|---|---|---|
| ✅ | ~~Panic hook + signal handler~~ (D4) — Phase 1 `12cf7ef8` + Phase 2 add-os-signal-handler | 4 | done | ops |
| **P0** | **`RuntimeConfig` 中心化** (D1) — 所有 knob 一处声明 | 4 | 1-2 天 | ops |
| **P0** | **JIT type specialization** (C2) — 已知 IrType 不走 helper | 2 | 2-3 天 | perf |
| **P0** | **JIT↔VM trait 抽象** (Part 1) | 1 | 2-3 天 | arch |
| **P1** | **Per-module log filtering** (D2) — `Z42_LOG=z42::jit=debug` | 4 | 0.5 天 | ops |
| **P1** | **`Value::Str(String) → Rc<str>`** (C1+C3) | 2 | 1-2 天 | perf |
| **P1** | **Field/Method name → token id** (C4+C5) | 2 | 3-4 天 | perf |
| **P1** | **trait-based test commons** (S2.4) | 3 | 3-5 天 | stdlib |
| **P1** | **Internal shared helpers 层** (S2.3) | 3 | 5-7 天 | stdlib |
| ✅ | ~~`RuntimeCounters` (D6 Phase 1)~~ — add-runtime-counters a9ba398b (2026-05-26) | 4 | done | ops |
| ✅ | ~~`RuntimeObserver` (D3 Phase 1)~~ — add-runtime-observer (2026-05-26); ModuleLoaded + Custom variants live; JIT/exception/native emit sites are Phase 2 follow-ups | 4 | done | ops |
| **P2** | **Prestub / lazy JIT** (Part 1) | 1 | 3-5 天 | arch |
| **P2** | **Polymorphic IC** (C4+C5) | 2 | 2-3 天 | perf |
| **P2** | **Public API surface lint** (S2.5) | 3 | 2-3 天 | stdlib |
| **P3** | **PAL 抽象层** (Part 1) | 1 | 5-7 天 | arch |
| **P3** | **String literal interning** (C3) | 2 | 3-4 天 | perf |
| ✅ | ~~Startup banner / `--info`~~ (D5) — `--info` build-info dump (2026-05-25) + verbose-mode `tracing::info!` banner (2026-05-26) | 4 | done | ops |
| **P4** | **Value 拆 hot/cold variants** (C1) | 2 | 5-7 天 | perf |
| **P4** | **GC bump allocator** (C6) | 2 | 极大 | arch |
| **P4** | **Hot-path stub inline** (Part 1) | 1 | 2-3 天 | perf |
| **P5** | **Diagnostic IPC** (D9) | 4 | 巨大 | ops |
| **P5** | **z42.text 扩充 / z42.math BigInteger** (S5) | 3 | 7-10 天 | stdlib |

### 立即可上（≤1 天，5 个独立 commit）

1. ✅ ~~**删除 z42.text/src/Regex.z42 stub**~~ (S2.2) — z42.text/src/ only has StringBuilder.z42 now
2. ✅ ~~**z42.crypto 补 README**~~ (S2.1) — README.md present
3. ✅ ~~**`tracing` EnvFilter**~~ (D2) — `Z42_LOG=...` wired in `init_tracing` (2026-05-25)
4. ✅ ~~**Rust panic hook + Z42_CRASH_DIR**~~ (D4 的第一步) — Phase 1 `12cf7ef8` + Phase 2 signal-handler done
5. ⚡ **`String.Length` 加 cache** 或改 byte semantics（C11.1）

## D12. 不做 / 后置的部分

明确不抄的 CoreCLR 设施：
- **createdump minidump** — cross-platform 复杂度 ≫ 价值
- **CONTRACTL 宏系统** — Rust 类型系统已部分替代
- **EventPipe IPC 协议** — 单线程阶段没价值
- **完整 ICorProfilerInfo** — out-of-process profiler 不在 z42 roadmap
- **Locale / globalization 初始化** — z42 当前 ASCII-only，国际化未规划
- **Per-arch ASM stubs** — Cranelift 兜底

---

*Part 4 作者：Claude（z42 review）*
*调研日期：2026-05-22*

---

# Part 5：VM 内部分层 + 数据类型设计深度 Review（2026-05-22）

> 前几部分看了顶层切分、热路径、stdlib、ops/devex。这部分专攻**两个最深的内部问题**：
> 1. VM 内部模块依赖图 + 解耦机制（forward decl / opaque handle / tiering）
> 2. 核心数据类型字节布局 + 大小设计（`Value` / `TypeDesc` / `ScriptObject` / `Instruction` 等）
>
> 这两层是其他所有优化的"地基"：地基不对，上层优化只能 patch；地基对了，性能 / 可维护性 / 演进能力一起涨。

---

## E1. VM 内部分层对照

### CoreCLR 4 层结构 + 解耦机制

```
Tier 0 (Foundation)
    utilcode/                — 内存 / 断言 / 容器；不依赖 VM
Tier 1 (Type System Primitives)
    object.h / field.h       — Object header + FieldDesc (16B bit-packed)
    typehandle.h             — TypeHandle (tagged pointer)
    typedesc.h               — 非 class 类型（数组 / byref / 泛型参数）
Tier 2 (Type Metadata)
    methodtable.h            — MethodTable (热)
    class.h                  — EEClass (冷)
Tier 3 (Method Execution)
    clsload.hpp              — Class loader
    method.hpp               — MethodDesc (32B base + 子类)
    prestub.cpp              — 懒编译入口
    jitinterface.cpp         — JIT↔VM 契约实现
Tier 4 (Runtime Support)
    threads.h                — Thread state
    frames.h                 — Frame hierarchy
    exceptionhandling.cpp    — 异常处理
```

**关键解耦机制**：

1. **Forward declaration walls**：[`methodtable.h`](../../../runtime/src/coreclr/vm/methodtable.h) 第 34-59 行 forward-declare **15 个类**，只 `#include` 必需的 10 个 header。改 `MethodDesc` 字段不会触发 `MethodTable.h` 整链重编译。
2. **Opaque handles**：[`corinfo.h:989`](../../../runtime/src/coreclr/inc/corinfo.h#L989) `CORINFO_CLASS_HANDLE = struct CORINFO_CLASS_STRUCT_*;` —— JIT 拿到的是不透明 void*；MT 内部布局变化与 JIT 完全无关。
3. **`inc/` vs `vm/` boundary**：`inc/` 是跨子系统契约（`corinfo.h` / `clrtypes.h` / `corjit.h`），**不允许 include `vm/*.h`**。这是个 build-time 强制的契约层。
4. **TypeHandle tagged pointer**：[`typehnd.h`](../../../runtime/src/coreclr/vm/typehnd.h) 低 2 bit 区分 `MethodTable*` (0b00) vs `TypeDesc*` (0b10) —— 无需额外 discriminator field。
5. **MethodTable / EEClass 分层**：MT 是热数据（JIT 频繁读：vtable / field offset / interface map），EEClass 是冷数据（class loader / reflection 才用）。

### z42 现状层次

```
metadata/  (root layer, no internal deps)  ✅
    ↑
vm_context.rs                              ⚠️ hub
    ↑
interp/  jit/  gc/  exception/  corelib/   (parallel layer, all depend on metadata + vm_context)
    ↑
host/  native/                             (FFI boundary)
```

**z42 已经对的部分** ✅：

| 模式 | z42 实现 | 对应 CoreCLR 模式 |
|---|---|---|
| metadata 是 root（无回头依赖） | `metadata/mod.rs` 无 `use crate::*` | utilcode 同样无 VM 依赖 |
| `pub(crate)` boundary 严格 | `exec_instr / helpers / frame` 都是 `pub(crate)` 或私有 | `.hpp` vs `.h` 内外有别 |
| Exhaustive match 防 silent breakage | [`exec_instr.rs:53`](../src/runtime/src/interp/exec_instr.rs#L53) 65+ arm 无 `_` 兜底 | CoreCLR forward decl + 严格 include |
| `trait MagrGC` 解耦 GC ↔ Value | [`gc/heap.rs`](../src/runtime/src/gc/heap.rs) trait + visitor 模式 | `IGCHeapInternal` |
| corelib BuiltinId 数组索引 | [`corelib/mod.rs:69`](../src/runtime/src/corelib/mod.rs#L69) `BUILTINS: &[(&str, NativeFn)]` | EEHash + 哈希 dispatch |
| 无 cycle / 无 diamond dep | 调研已确认 | CoreCLR 同 |

**z42 问题** ⚠️：

#### E1.P1 `VmContext` 是 hub（46 public methods, 944 LOC, 19 fields） ⚠️

**现状**：[`vm_context.rs`](../src/runtime/src/vm_context.rs) 集中所有可变状态 —— static fields / pending exception / lazy loader / native types / heap / processes / threads / mutexes / channels / gc_phase 等。
- 不是经典 god object（方法有 boundary，调用者只能通过 `ctx.static_set()` / `ctx.set_exception()` 等访问，看不到 struct field）
- 但 **每个模块都 take `&VmContext`** 作为参数，相当于把"所有 VM 状态"广播给所有调用方
- 加新 concern（observability / monitoring / tracing）必须扩 `VmContext` 字段表

**对照 CoreCLR**：状态分散得多 ——
- Thread-local state 在 `Thread`（[`threads.h:877`](../../../runtime/src/coreclr/vm/threads.h#L877)）
- Process-global state 在 EE singleton（`g_pGCHeap` / `g_pThreadStore` 等）
- 各模块通过自己的 singleton 拿状态，不共用一个 mega-struct

**改造方向**（中长期）：

按 concern 拆 trait，VmContext 实现所有 trait：

```rust
// runtime/src/interfaces.rs (new)
pub trait GcAccess {
    fn heap(&self) -> &dyn MagrGC;
    fn alloc_object(&self, td: Arc<TypeDesc>, slots: Vec<Value>) -> Value;
    fn for_each_root(&self, visitor: &mut dyn FnMut(&Value));
}

pub trait ExceptionAccess {
    fn set_exception(&self, val: Value);
    fn take_exception(&self) -> Option<Value>;
    fn peek_exception(&self) -> Option<Value>;
}

pub trait StaticFieldAccess {
    fn static_get(&self, id: StaticFieldId) -> Option<Value>;
    fn static_set(&self, id: StaticFieldId, val: Value);
}

pub trait CallStackAccess {
    fn push_frame(&self, frame: VmFrame) -> FrameGuard<'_>;
    fn current_frames(&self) -> &[VmFrame];
}

impl GcAccess for VmContext { ... }
impl ExceptionAccess for VmContext { ... }
// ...

// 然后 interp / jit / corelib 只 take 它需要的 trait：
fn array_new(ctx: &impl GcAccess, ...) -> Value { ... }
fn throw(ctx: &impl ExceptionAccess, val: Value) { ... }
```

**收益**：
1. 调用方只看自己需要的能力，依赖收窄
2. 测试可以 mock 单个 trait
3. 多线程（Phase 3）只需让单个 trait 实现 Send/Sync，不是整个 VmContext
4. 加新 concern 只需新 trait，不动 VmContext 字段表

**估时**：5-7 天（含 callsite 修改）。**ROI 中等**：不是性能优化，但解锁 VmContext 演进。

#### E1.P2 JIT↔Metadata 直接 import（重申 Part 1 P0） ⚠️

[`jit/translate.rs:24-100`](../src/runtime/src/jit/translate.rs#L24-L100) 直接 import `crate::metadata::{Function, Instruction, Terminator}`，对 `Instruction` 做 65+ arm 模式匹配。

**对照** CoreCLR：JIT 拿到 `CORINFO_METHOD_HANDLE` (void*)，所有信息通过 callback 查（`getMethodInfo` / `getClassInfo` / `getFieldOffset` 等）。换 metadata 内部格式 JIT 完全无感。

**已在 Part 1 P0 列出**：新建 `jit/vm_interface.rs` 定义 `trait JitVm`，translate.rs 走 trait 不直接 import metadata。

#### E1.P3 没有 `inc/` 等价的契约层 ⚠️

CoreCLR `inc/` 目录强制隔离公共契约 vs 内部实现。z42 没有等价物 —— 公共契约（`Value` / `Instruction` / `Module` 等）混在 metadata 模块里。

**改造方向**：

新建 `runtime/src/interfaces/`（或保留 `lib.rs` 作为聚合点）：
- `interfaces/handles.rs` —— 不透明 handle 类型：`MethodHandle(u32)` / `ClassHandle(u32)` / `FieldHandle(u32)`
- `interfaces/vm.rs` —— 上述 trait（GcAccess / ExceptionAccess 等）
- `interfaces/jit.rs` —— `trait JitVm`（Part 1 P0）

metadata 内部类型保持 `pub` 但实际上仅 host/main.rs 直接用；interp/jit/gc 走 handle + trait。

**ROI**：和 P1 / P2 同等。1-2 周完整改造。

---

## E2. 核心数据类型对照

### z42 数据类型字节布局（实测）

| 类型 | 文件 | 实测大小 | 说明 |
|---|---|---|---|
| `Value` | [types.rs:199](../src/runtime/src/metadata/types.rs#L199) | **~48B** | 12 variant，largest `Closure { env, fn_name: String }` ≈ 40B |
| `TypeDesc` | [types.rs:85](../src/runtime/src/metadata/types.rs#L85) | **~336B** | 13 字段含 6 个 `String` + 4 个 `Vec<...>` + 2 个 `HashMap<String, usize>` |
| `ScriptObject` | [types.rs:165](../src/runtime/src/metadata/types.rs#L165) | **64B 头** + slots | `Arc<TypeDesc>` + `Vec<Value>` + `NativeData` + `Vec<String>` type_args |
| `FieldSlot` | [types.rs:13](../src/runtime/src/metadata/types.rs#L13) | **48B** | `name: String` + `type_tag: String` —— 全 heap String |
| `Function` | [bytecode.rs:191](../src/runtime/src/metadata/bytecode.rs#L191) | **200B+ 头** | name / ret_type / param_types / blocks / exception_table / line_table / local_vars 全 inline |
| `Module` | [bytecode.rs:11](../src/runtime/src/metadata/bytecode.rs#L11) | **~220B** | 双 type_registry（HashMap + Vec），双 func 索引 |
| `Instruction` | [bytecode.rs:291](../src/runtime/src/metadata/bytecode.rs#L291) | **~120B** | 45+ variant，largest `CallNative` 100B+（4 个 String 字段） |
| `Frame` | [interp/mod.rs:161](../src/runtime/src/interp/mod.rs#L161) | **72B 头** | `Vec<Value>` regs + env_arena + ref_writebacks |
| `VmFrame` | [exception/mod.rs:64](../src/runtime/src/exception/mod.rs#L64) | **72B** | 含 raw pointers 到 Frame.regs 和 env_arena |
| `ResolvedTokens` | [resolver.rs:36](../src/runtime/src/metadata/resolver.rs#L36) | **168B 头** | 7 个稀疏缓存 Vec |
| `TypeId`/`MethodId`/`FieldId` | [tokens.rs](../src/runtime/src/metadata/tokens.rs) | **4B 每个** | `u32` newtype；UNRESOLVED = `u32::MAX` |
| `GcRef<T>` | [gc/refs.rs:78](../src/runtime/src/gc/refs.rs#L78) | **16B 手柄** | `Arc<GcAllocation<T>>` |

### CoreCLR 数据类型对照

| z42 类型 | CoreCLR 等价 | CoreCLR 大小 | 设计差异 |
|---|---|---|---|
| `Value` 48B | tagged 64-bit (primitive) / OBJECTREF | **8B** | CoreCLR primitive 内联到 64-bit slot，z42 enum 永远 48B |
| `TypeDesc` 336B | `MethodTable` + `EEClass` 分层 | **MT ~40-50B base** + cold EEClass 出口 | z42 不分热 / 冷 |
| `ScriptObject` 64B 头 | `Object` 8B header (MT*) + 8B sync block | **16B 头** | z42 多了 `Vec<Value>` (24B) 和 `Vec<String>` type_args (24B) inline |
| `FieldSlot` 48B | `FieldDesc` bit-packed | **16B** | z42 全 String，CoreCLR 27-bit offset + 5-bit type + 24-bit token packed |
| `Function` 200B+ 头 | `MethodDesc` 32B base + chunk | **32B** | z42 把 line_table / local_vars / exception_table 全 inline |
| `Instruction` ~120B | IL byte stream | **变长 byte** | z42 用 Rust enum + Vec args；CoreCLR IL 是 byte sequence |
| Token IDs 4B | metadata token 24-bit + table | **4B** | 对齐 |
| `GcRef<T>` 16B | `Object*` | **8B** | z42 多 8B 是 Arc overhead；trade-off：Rust 内存安全 |

### 关键发现 ❌

#### E2.P1 `TypeDesc` 巨型化（336B）—— 没分热 / 冷 ⚠️

**问题**：z42 把所有 metadata 塞进一个 struct：
- 热（vtable / field_index / instance size）
- 冷（type_args / generic_constraints / own_methods 名字列表）
- 冗余（vtable 同时存 `Vec<(String, String)>` + `HashMap<String, usize>`）

每加载一个 class 在堆上躺 336B+，**stdlib ~80 个类 ≈ 27KB**（小但 cache locality 差）。

**对照** CoreCLR：
- `MethodTable` ~40-50B base（指针 + base size + flags） + 出口 vtable / interface map
- `EEClass` 是冷数据，class loader 用，reflection 用，hot path 不读
- `MethodTableAuxiliaryData` 再分一层，把 reflection-exposed class object / hash code cache 进一步分离

**改造方向**（强烈建议）：

```rust
// metadata/types.rs

// 热数据 (~64B)
pub struct TypeDesc {
    pub id: TypeId,                            // 4B
    pub name_id: StringId,                     // 4B (intern, 见 E2.P3)
    pub base_id: Option<TypeId>,               // 4B + 4B padding
    pub instance_size: u32,                    // 4B
    pub field_layout: Box<[FieldSlot]>,        // 16B (fat pointer to slice)
    pub vtable: Box<[MethodId]>,               // 16B
    pub flags: u32,                            // 4B (IsArray / IsValueType / ...)
    pub cold: Box<TypeDescCold>,               // 16B → 间接到冷数据
}

// 冷数据 (~128B, 反射 / 异常 / 调试才用)
pub struct TypeDescCold {
    pub fields_by_name: FxHashMap<StringId, u32>,
    pub vtable_by_name: FxHashMap<StringId, u32>,
    pub own_methods: Vec<MethodId>,
    pub type_args: Vec<TypeId>,
    pub generic_constraints: Vec<ConstraintBundle>,
}
```

**收益**：
- TypeDesc 从 336B → 64B（**5x**）
- hot path（FieldGet IC miss / VCall miss）不 touch cold 数据，cache 友好
- cold 数据延迟分配（首次反射访问时），未触发就不占内存

**估时**：5-7 天（含 callsite 修改 + IC slot 重对齐）。**ROI 极高**。

#### E2.P2 `FieldSlot` 48B → 应该 16B ❌

**现状**：
```rust
pub struct FieldSlot {
    pub name: String,         // 24B heap
    pub type_tag: String,     // 24B heap
}
```

每个 class 几十个字段就是几 KB。

**对照** CoreCLR `FieldDesc` 16B bit-packed：27-bit offset + 5-bit type + 24-bit token + ...

**改造**：
```rust
#[repr(C)]
pub struct FieldSlot {
    pub name_id: StringId,    // 4B (intern)
    pub offset: u32,          // 4B (instance offset，bit 30/31 留 flags)
    pub type_id: TypeId,      // 4B
    pub flags: u32,           // 4B (IsStatic / IsReadOnly / ...)
}                             // 总 16B
```

**估时**：2-3 天。**ROI 高**：FieldSlot 在每个 TypeDesc 里有 N 个，乘数效应大。

#### E2.P3 String 满天飞 → StringId intern ❌

**问题**：z42 数据结构里 String 触目皆是：
- `TypeDesc.name / base_name / vtable.0/1 / type_params / type_args`
- `FieldSlot.name / type_tag`
- `Function.name / ret_type / param_types`
- `Instruction::Call { func: String }` / `CallNative { module/type_name/symbol: String }` / `FieldGet { field_name: String }`

每个 String 24B + heap allocation。clone 触发堆拷贝。

**对照** CoreCLR：metadata tokens (24-bit) 引用 stringheap entry；运行时 cache resolved handle，never store string in hot data。

**改造**：
1. 已有 `Module.string_pool: Vec<String>` ✅
2. 引入 `StringId(u32)` newtype 作为 pool index
3. 所有 metadata struct 的 `String` 字段改 `StringId`
4. 提供 `ctx.string(id) -> &str` 方法做解 reference
5. zbc wire format **不变**（字符串还是写 pool；只是运行时表示改）

**估时**：5-7 天（涉及面广，单独 spec）。**ROI 极高**：内存 -30%+，HashMap 哈希更快（u32 vs String）。

#### E2.P4 `Instruction` enum ~120B → 应该 ≤32B ❌

已在 Part 2 C1 提过。配合 E2.P3 String intern：

```rust
// Before
Call { dst: u32, func: String, args: Vec<u32> }       // 52B+

// After
Call { dst: u32, func: MethodId, args: Box<[u32]> }   // 4+4+16 = 24B
```

**Box<[u32]>** 而非 `Vec<u32>` —— args 创建后不可变，省 8B 长度冗余 + cap field。

**估时**：3-4 天（callsite 多）。**ROI 极高**：bytecode 内存 -60%+；解析 zbc 也快（不分配 String）。

#### E2.P5 `Function` 200B+ → 拆 hot / cold ❌

**现状**：[`bytecode.rs:191`](../src/runtime/src/metadata/bytecode.rs#L191) inline 所有元信息（line_table / local_vars / exception_table / generics constraints）。

**对照** CoreCLR `MethodDesc` 32B base + `MethodDescChunk` 分块 + `MethodDescCodeData` 单独间接。

**改造**：

```rust
// 热数据 (~80B)
pub struct Function {
    pub name_id: StringId,
    pub method_id: MethodId,
    pub param_count: u16,
    pub max_reg: u16,
    pub flags: u32,                       // IsStatic / HasExceptions / ...
    pub ret_type: TypeId,
    pub param_types: Box<[TypeId]>,
    pub blocks: Box<[BasicBlock]>,
    pub resolved: OnceLock<ResolvedTokens>,
    pub cold: Box<FunctionCold>,          // 间接到冷
}

// 冷数据 (debugger / exception trace 才用)
pub struct FunctionCold {
    pub line_table: Vec<LineEntry>,
    pub local_vars: Vec<LocalVar>,
    pub exception_table: Vec<ExceptionEntry>,
    pub generics: GenericConstraintBundle,
}
```

异常发生时才解 cold；正常调用零 cold 访问。

**估时**：3-5 天。**ROI 高**。

#### E2.P6 `ScriptObject` header 64B → 应该 32B ⚠️

**现状**：
```rust
pub struct ScriptObject {
    pub type_desc: Arc<TypeDesc>,      // 16B
    pub slots: Vec<Value>,             // 24B
    pub native: NativeData,            // ~12B
    pub type_args: Vec<String>,        // 24B (大多类不用泛型)
}
```

大多对象 `type_args` 是空 Vec，但占 24B。

**改造**：
```rust
pub struct ScriptObject {
    pub type_desc: Arc<TypeDesc>,            // 16B
    pub slots: Box<[Value]>,                 // 16B (Box<[T]> 不可变长度)
    pub instantiation: Option<Box<TypeArgs>>,// 8B (Box<None> = NULL ptr)
    pub native: NativeData,                  // ~12B (单独打包，见下)
}
// 总 ~52B 头；不带泛型实例化的对象 instantiation = None
```

**估时**：2-3 天。**ROI 中**。

### z42 数据类型 ✅ 已对的部分

| z42 已做 | 对应 CoreCLR | 注 |
|---|---|---|
| `Arc<TypeDesc>` 实例间共享 | `Object` → `MethodTable*` 单例 | 已对齐 |
| `ScriptObject` 含 `Arc<TypeDesc>` ref + slot array | `Object` header + 内联 fields | 思路对齐，细节待优化 |
| Field access canonical = slot index (`obj.slots[slot]`) | CoreCLR `obj + fieldDesc.offset` | 对齐 |
| `Function.resolved: OnceLock<ResolvedTokens>` 热路径缓存 | CoreCLR `MethodDesc.m_codeData` 同思路 | 对齐 |
| `pub use types::{...}` 集中 re-export | CoreCLR `inc/clrtypes.h` 集中类型 | 思路对齐 |
| `GcRef<T>` 透明 newtype 不引入间接 | CoreCLR `OBJECTREF` 透明 alias | 对齐 |
| Token IDs u32 newtype | CoreCLR metadata token 4B | 对齐 |

---

## E3. 合并所有 Part 后的最终 P0-P5 表

| 优先级 | 改造 | Part | 估时 | 类别 |
|---|---|---|---|---|
| ✅ | ~~Panic hook + signal handler~~ (D4) — Phase 1 `12cf7ef8` + Phase 2 add-os-signal-handler | 4 | done | ops |
| ✅ | ~~`RuntimeConfig` 中心化~~ (D1) — refactor-runtime-config `81e1cbba` (2026-05-25); Phase 2 migrating subsystem-local `Z42_*` reads still open | 4 | done | ops |
| 🟡 | **StringId intern**（E2.P3）— Phase A `StringId(u32)` newtype + accessors landed add-string-id-newtype (2026-05-26); Phase B+ migrates individual String fields one at a time | 5 | Phase A done | data |
| **P0** | **JIT type specialization** (C2) | 2 | 2-3 天 | perf |
| **P0** | **JIT↔VM `JitVm` trait 抽象** (Part 1 + E1.P2) | 1 | 2-3 天 | arch |
| **P1** | **TypeDesc 热 / 冷拆分**（E2.P1）— 336B → 64B | 5 | 5-7 天 | data |
| **P1** | **Instruction 瘦身**（E2.P4）— ~120B → ≤32B | 5 | 3-4 天 | data |
| **P1** | **FieldSlot 16B bit-packed**（E2.P2）— 48B → 16B | 5 | 2-3 天 | data |
| ✅ | ~~Per-module log filtering~~ (D2) — EnvFilter `Z42_LOG` wired in `init_tracing` (2026-05-25) | 4 | done | ops |
| **P1** | **`Value::Str(String) → Rc<str>`** (C1+C3) | 2 | 1-2 天 | perf |
| **P1** | **Field/Method name → token id** (C4+C5) | 2 | 3-4 天 | perf |
| **P1** | **trait-based test commons** (S2.4) | 3 | 3-5 天 | stdlib |
| **P1** | **Internal shared helpers 层** (S2.3) | 3 | 5-7 天 | stdlib |
| ✅ | ~~`RuntimeCounters` (D6 Phase 1)~~ — add-runtime-counters a9ba398b (2026-05-26) | 4 | done | ops |
| ✅ | ~~`RuntimeObserver` (D3 Phase 1)~~ — add-runtime-observer (2026-05-26); ModuleLoaded + Custom variants live; JIT/exception/native emit sites are Phase 2 follow-ups | 4 | done | ops |
| **P2** | **VmContext trait 拆分**（E1.P1）— GcAccess / ExceptionAccess 等 | 5 | 5-7 天 | arch |
| **P2** | **`interfaces/` 契约层**（E1.P3）— 对齐 CoreCLR `inc/` | 5 | 7-10 天 | arch |
| **P2** | **Function 热 / 冷拆分**（E2.P5）— 200B → 80B + 间接 cold | 5 | 3-5 天 | data |
| **P2** | **Prestub / lazy JIT** (Part 1) | 1 | 3-5 天 | arch |
| **P2** | **Polymorphic IC** (C4+C5) | 2 | 2-3 天 | perf |
| **P2** | **Public API surface lint** (S2.5) | 3 | 2-3 天 | stdlib |
| **P3** | **ScriptObject header 瘦身**（E2.P6）— 64B → ~52B | 5 | 2-3 天 | data |
| **P3** | **PAL 抽象层** (Part 1) | 1 | 5-7 天 | arch |
| **P3** | **String literal interning** (C3) | 2 | 3-4 天 | perf |
| ✅ | ~~Startup banner / `--info`~~ (D5) — `--info` build-info dump (2026-05-25) + verbose-mode `tracing::info!` banner (2026-05-26) | 4 | done | ops |
| **P4** | **Value 拆 hot/cold variants** (C1) | 2 | 5-7 天 | perf |
| **P4** | **GC bump allocator** (C6) | 2 | 巨大 | arch |
| **P4** | **Hot-path stub inline** (Part 1) | 1 | 2-3 天 | perf |
| **P5** | **Diagnostic IPC** (D9) | 4 | 巨大 | ops |
| **P5** | **z42.text / z42.math 扩充** (S5) | 3 | 7-10 天 | stdlib |

### 建议组织顺序

**第一波 (production-ready 阶段, 2-3 周)**：
1. P0 panic hook + signal handler（生产前 must-have）
2. P0 RuntimeConfig 中心化（解锁后续 knob）
3. P1 per-module log filtering（半天快速 win）
4. P1 Value::Str → Rc<str> + String.Length cache（lowest hanging fruit）

**第二波 (data type 大改, 3-4 周)**：
1. P0 StringId intern —— 前置依赖
2. P1 TypeDesc 热 / 冷拆分
3. P1 Instruction 瘦身
4. P1 FieldSlot 16B
5. P2 Function 热 / 冷拆分

**第三波 (架构演进, 3-4 周)**：
1. P0 JitVm trait + interfaces/
2. P2 VmContext trait 拆分
3. P2 Prestub / lazy JIT

**第四波 (stdlib + ops 完善, 2-3 周)**：
1. P1 trait-based test commons
2. P1 internal shared helpers
3. P1 RuntimeObserver
4. P2 Public API surface lint

**预估总改造时长**：12-14 周 single-developer。可以并发的项目（不同模块）能压缩到 8-10 周。

---

## E4. 不抄的部分（明确放弃）

- **MethodTable 出口 vtable 数组（inline 在 MT 末尾）**：Rust 不易表达 `[u8; flexible]` 紧凑 inline 数组；用 `Box<[MethodId]>` 间接是合理 trade-off
- **EEClass 完全独立 struct**：z42 用 `cold: Box<TypeDescCold>` 间接，效果等价但少一个类型
- **InstantiatedMethodDesc / FCallMethodDesc 子类化**：Rust 无 OOP；用 enum + boxed 子状态即可
- **MethodDescChunk 内存池**：z42 当前规模不需要；几百个 Function 直接 Vec 即可
- **CORINFO_CLASS_HANDLE void* opaque**：Rust 用 `pub struct ClassHandle(u32)` newtype 更安全
- **3 种 build flavor (CHK / DBG / FRE)**：Rust 用 `#[cfg(debug_assertions)]` + features 替代

---

## E5. 立即可做的小项目（≤1 天）

1. ✅ ~~**`Function.line_table` / `local_vars` / `exception_table` 用 `Box<[T]>` 替代 `Vec<T>`**~~ — refactor-function-box-slice (2026-05-26); also includes `param_types` / `type_params` / `type_param_constraints` (6 fields × 8 B ≈ 48 B per Function)
2. ✅ ~~**`Instruction::Call.args: Vec<u32>` → `Box<[u32]>`**~~ — refactor-instruction-box-slice (2026-05-26); covers all 9 reg-list variants (Call / Builtin / CallIndirect / MkClos.captures / ArrayNewLit.elems / ObjNew / VCall / CallNative / CallNativeVtable) + ObjNew.type_args String list; `read_args` decoder now returns `Box<[u32]>` directly
3. ⚡ **TypeDesc cold 字段先用 `Option<Box<...>>` 包起来** —— 不重排字段先减常驻内存
4. ⚡ **`ScriptObject.type_args: Vec<String>` → `Option<Box<[StringId]>>`** —— 非泛型对象省 24B
5. ⚡ **删 `TypeDesc.own_methods` 的 String pair tuple，仅保 `Vec<MethodId>`** —— vtable 名字应该走 vtable_by_name HashMap

每项独立 commit，小步前进，不依赖大改造。

---

*Part 5 作者：Claude（z42 review）*
*调研日期：2026-05-22*
*合并 4 个 explore agent 输出：CoreCLR VM 内部分层 / CoreCLR 数据类型设计 / z42 内部耦合 / z42 数据类型设计*

---

# Part 6：Bootstrap 编译器 vs Roslyn 架构 Review（2026-05-22）

> 对比对象：[`src/compiler/`](../src/compiler/) 的 C# bootstrap 编译器（~24 KLOC across 9 projects）vs Roslyn (.NET 官方 C# 编译器架构)
>
> Roslyn 是迄今最成熟的"编译器即服务"参考实现 —— 不只是 batch compiler，还服务 IDE / Analyzer / Refactoring / Code Generator。z42 当前是 bootstrap 编译器，未来要自举，对应的"编译器即服务"能力必须靠拢 Roslyn 模式。

## F1. Roslyn 核心架构（参考）

| 概念 | 角色 | 关键设计 |
|---|---|---|
| **`Compilation`** | 整个编译单元的不可变快照 | immutable；任何修改 → `WithReferences()` 等产生新 Compilation；多线程共享安全 |
| **GreenNode / RedNode** | 双层 SyntaxTree 表示 | Green = 不可变共享子树（结构相同的代码段去重）；Red = 带 parent 指针的 view，按需创建 |
| **`SyntaxTree`** | 单文件的不可变语法树 | 增量编辑 = 局部 reparse + 子树重用 |
| **Binder hierarchy** | 嵌套作用域的 binder 链 | 每个 scope（method body / loop / lambda / catch block）有自己的 Binder，lookup 沿链向上 |
| **`BoundNode`** | 内部语义树（不暴露公共 API） | 类似 z42 BoundExpr，但 Roslyn 有 30+ lowering pass 转换 |
| **`ISymbol`** | 公共符号身份 | 跨树持久 —— `INamedTypeSymbol` / `IMethodSymbol` / `IFieldSymbol` / `ILocalSymbol` 等；GetHashCode / Equals 稳定 |
| **`SemanticModel`** | 单 SyntaxTree 的语义查询 API | `GetTypeInfo(node)` / `GetSymbolInfo(node)` / `GetDeclaredSymbol(decl)` 按需 bind + cache |
| **`Diagnostic`** | 结构化诊断 | id (CSxxxx) / severity / location / properties (键值对) / 可被 #pragma 抑制 |
| **DiagnosticAnalyzer + CodeFix** | 扩展点 | 用户写 Roslyn 插件加 lint / refactor / analyzer，不动 compiler 核心 |
| **`IOperation`** | 分析用语义树 | 与 BoundNode 独立的稳定 API，供 analyzer 用 |
| **Lowering passes** | Bound → simpler Bound 的多步转换 | async/await 重写 / iterator 重写 / lambda 提升 / using 展开 / switch 表展开 / nullable analysis |
| **Source Generator** | 编译期源码生成扩展 | analyzer + 写文件能力；不修改用户源码，往 Compilation 注入新 SyntaxTree |

## F2. z42 编译器现状概览

### 已经对的 ✅（部分已对齐 Roslyn）

| z42 | Roslyn 对应 | 注 |
|---|---|---|
| AST 全 `sealed record` | Roslyn `SyntaxNode`（GreenNode 不可变） | ✅ 不可变契约对齐 |
| BoundExpr / BoundStmt `sealed record` + Visitor | Roslyn `BoundNode` + `BoundTreeVisitor<...>` | ✅ 同构 |
| `BoundExprVisitor<T>` switch + abstract method 强制覆盖 | Roslyn `BoundTreeWalker` / `BoundTreeRewriter` | ✅ 添加新 BoundNode 编译期失败强制更新 visitors |
| `DiagnosticBag` 收集（不 fail-fast） | Roslyn `DiagnosticBag` 同名同思路 | ✅ 大段对齐 |
| 结构化错误码（E0xxx, 85+ codes） | Roslyn CSxxxx（数千个） | ✅ 模式对齐，规模小 |
| `Span` 带 file/line/column propagate 全程 | Roslyn `Location` + `TextSpan` | ✅ |
| `DiagnosticCatalog` 支持外部注册（WS### / Z###） | Roslyn `DiagnosticDescriptor` registration | ✅ 已有扩展点 |
| Parser 错误恢复（`ErrorExpr` placeholder） | Roslyn `MissingToken` + skipped trivia | ✅ 同思路 |
| TypeChecker pass 分阶段（Symbols → Binding → Flow） | Roslyn (Declaration → Binding → Lowering → Emit) | ✅ 思路对齐 |
| Partial class 拆 TypeChecker (Stmts/Exprs/Calls/Generics) | Roslyn partial class 拆 Binder | ✅ |
| 80+ Test class + 36 error fixture 黄金对比 | Roslyn 巨量测试 | ✅ |

### 重要差距 ❌

#### F2.1 没有 `Compilation` 不可变快照 ⚠️

**Roslyn**：`CSharpCompilation` 是整个编译单元的不可变值。
```csharp
var compilation = CSharpCompilation.Create("MyAssembly")
    .AddReferences(refs)
    .AddSyntaxTrees(trees);

// 后续修改产生新 Compilation：
var c2 = compilation.AddSyntaxTrees(newTree);
// c1 仍然存在，可被其他 thread / IDE feature 用
```

好处：
- 多 thread 同时查询安全
- 增量更新只重建受影响的子树
- IDE feature（hover / find references / refactor）持有 Compilation 引用做长时间分析

**z42 现状**：[`PipelineCore.Compile()`](../src/compiler/z42.Pipeline/PipelineCore.cs) 是 procedural call —— `source → token → AST → Bound → IR`，无中间 cache 对象。每次重编译从头跑。

**建议**：

```csharp
// z42.Pipeline/Compilation.cs (新文件)
public sealed class Compilation {
    public IReadOnlyList<SyntaxTree> SyntaxTrees { get; }
    public IReadOnlyList<Compilation> References { get; }     // 跨 zpkg deps
    public SymbolTable Symbols { get; }                       // 懒解析
    public SemanticModel GetSemanticModel(SyntaxTree tree);   // 按需 bind
    public ImmutableArray<Diagnostic> Diagnostics { get; }

    public Compilation AddSyntaxTrees(params SyntaxTree[] trees)
        => new Compilation(SyntaxTrees.AddRange(trees), References, /* invalidate caches */);

    public Compilation AddReferences(params Compilation[] refs)
        => new Compilation(SyntaxTrees, References.AddRange(refs), /* invalidate caches */);

    public EmitResult Emit(...);     // 出 zbc / zpkg
}
```

收益：
1. **多线程并行编译**：每个 file 一个 Compilation slice 并行 bind
2. **IDE 集成基础**：未来 LSP server 持有 `Compilation`，hover/find-refs 走它
3. **增量重编译**：单 file 改 → 替换该 SyntaxTree，其他 cached symbols 复用

估时：**7-10 天**（涉及 PipelineCore + driver + tests 重构）。**ROI 极高**：解锁 IDE / LSP / 并行编译。

#### F2.2 没有 `ISymbol` 公共抽象 ⚠️

**Roslyn**：所有 Symbol 实现 `ISymbol` 公共接口 —— `INamedTypeSymbol`（类）/ `IMethodSymbol`（方法）/ `IFieldSymbol`（字段）/ `ILocalSymbol`（局部变量）等。Symbol 有稳定身份（GetHashCode/Equals 跨 SemanticModel 一致），可被分析器持有。

**z42 现状**：
- `Z42Type` 体系（`Z42ClassType` / `Z42InterfaceType` / `Z42ArrayType` 等）混合了"类型表示"和"符号身份"
- `IMethodSymbol` / `IFieldSymbol` 有 interface ✅ 但仅在 z42.Semantics 内部用
- 跨 zpkg 解析全靠 string name lookup（`SymbolTable._funcs: Dictionary<string, Z42FuncType>`）—— 没有稳定 SymbolId

**对比 Roslyn**：Roslyn ISymbol 有 `SymbolId` / `OriginalDefinition` / `ContainingSymbol` —— 三个属性几乎解决所有跨文件查询问题。

**建议**：

```csharp
// z42.Semantics/Symbols/ISymbol.cs (新建)
public interface ISymbol {
    SymbolKind Kind { get; }                       // Class / Interface / Method / Field / Local / Param / TypeParam
    string Name { get; }
    ISymbol? ContainingSymbol { get; }             // 父 scope
    INamespaceSymbol? ContainingNamespace { get; }
    Span DeclarationSpan { get; }
    Visibility Visibility { get; }                 // Public / Private / Internal / Protected
    bool IsStatic { get; }
    bool IsAbstract { get; }
    SymbolId Id { get; }                          // 稳定身份，跨 Compilation
    bool Equals(ISymbol? other);
}

public interface INamedTypeSymbol : ISymbol {
    ImmutableArray<IFieldSymbol> Fields { get; }
    ImmutableArray<IMethodSymbol> Methods { get; }
    INamedTypeSymbol? BaseType { get; }
    ImmutableArray<INamedTypeSymbol> Interfaces { get; }
    ImmutableArray<ITypeParameterSymbol> TypeParameters { get; }
    bool IsGenericType { get; }
    INamedTypeSymbol Construct(params ITypeSymbol[] typeArgs);  // 泛型实例化
    // ...
}

public interface IMethodSymbol : ISymbol {
    ITypeSymbol ReturnType { get; }
    ImmutableArray<IParameterSymbol> Parameters { get; }
    bool IsExtensionMethod { get; }
    ImmutableArray<ITypeParameterSymbol> TypeParameters { get; }
    IMethodSymbol Construct(params ITypeSymbol[] typeArgs);
    IMethodSymbol OriginalDefinition { get; }      // 对应 List<int>.Add() → List<T>.Add()
    // ...
}
```

`Z42Type` 和 `ISymbol` 解耦 —— 类型是"形状"，符号是"身份"。Roslyn 同样区分（`ITypeSymbol` ⊂ `ISymbol`）。

估时：**10-14 天**（大改）。**ROI 高**：解锁 future 的 LSP find-references / rename / GoToDefinition。

#### F2.3 没有 `SemanticModel` 按需 binding ⚠️

**Roslyn**：
```csharp
var model = compilation.GetSemanticModel(syntaxTree);
var typeInfo = model.GetTypeInfo(expressionNode);     // 给某 expr 返回 (Type, ConvertedType)
var symbol = model.GetSymbolInfo(invocationNode);     // 给某 invocation 返回 IMethodSymbol
var decl = model.GetDeclaredSymbol(methodDeclNode);   // 给某 method decl 返回 IMethodSymbol
```

这些 query **按需 bind 并缓存** —— 调用前 BoundTree 不必存在；首次调用时 bind 该 node 所在的 method body，结果 cache。

**z42 现状**：[`SemanticModel`](../src/compiler/z42.Semantics/SemanticModel.cs) 类存在 ✅ 但它是 **TypeChecker 跑完后产出的"所有结果集"**，不是按需 binding 接口。

**差距**：z42 必须 eager bind 整个 file 才能拿到任何信息；Roslyn 可以只 bind 你 query 的那一段。

**建议**：

Phase 1（短期）：把现有 SemanticModel 加 query API：

```csharp
public sealed class SemanticModel {
    // 已有：BoundBodies / BoundDefaults / BoundStaticInits / BoundInstanceInits

    // 新增 query API：
    public BoundExpr? GetBoundExpression(Expr astNode);   // O(1) cached
    public Z42Type? GetExpressionType(Expr astNode);
    public ISymbol? GetSymbol(Expr astNode);
    public ISymbol? GetDeclaredSymbol(Item astDecl);
    public IEnumerable<Diagnostic> GetDiagnostics(Span span);
}
```

Phase 2（中期，配合 F2.1 Compilation）：lazy binding —— 首次 query 时才 bind 那段。

估时 Phase 1：**3-5 天**。Phase 2：**5-7 天**。**ROI 高**（IDE 必备）。

#### F2.4 没有 Binder 层级 ⚠️

**Roslyn**：每个 scope 有自己的 `Binder` 对象，嵌套形成链：

```
GlobalScopeBinder
  → UsingsBinder        (using 指令)
  → InNamespaceBinder
  → InContainerBinder    (class)
  → InMethodBinder       (method body)
  → InBlockBinder        (block { } 大括号)
  → InForBinder          (for 循环作用域)
  → InCatchBlockBinder   (catch 变量)
  → ...
```

每个 Binder 知道 "我能 lookup 什么"，未命中传给 parent。Lambda 提取捕获、`using` 别名解析、`goto` label 范围、generic constraint scope —— 全靠 Binder 链。

**z42 现状**：`TypeChecker` 是单 instance，所有作用域信息塞 `TypeEnv` + 几个 mutable stacks（`_lambdaBindingStack` / `_funcConstraints`）。新作用域规则 → 加新字段 + 改 push/pop 逻辑。

**对比**：Roslyn `Binder` 子类有 30+ 个，每个对应一种 scope 语义。z42 把它们全压到 TypeChecker.cs 各方法分支。

**建议**（中期重构）：

```csharp
// z42.Semantics/TypeCheck/Binders/Binder.cs (新建)
public abstract class Binder {
    public Binder? Next { get; }                   // parent scope binder
    public Compilation Compilation { get; }
    public DiagnosticBag Diagnostics { get; }

    // 核心 API：
    public virtual ISymbol? LookupSymbol(string name) {
        return Next?.LookupSymbol(name);          // default = forward to parent
    }

    public virtual BoundExpr BindExpression(Expr expr) {
        return Next!.BindExpression(expr);
    }

    public virtual BoundStmt BindStatement(Stmt stmt) {
        return Next!.BindStatement(stmt);
    }
}

// 各种子类：
public class GlobalScopeBinder : Binder { ... }
public class InNamespaceBinder : Binder {
    string _namespace;
    public override ISymbol? LookupSymbol(string name) {
        // 先查本 namespace
        // 未命中 → base.LookupSymbol(name)
    }
}
public class InMethodBinder : Binder {
    MethodDecl _method;
    Dictionary<string, ILocalSymbol> _locals = new();
    public override ISymbol? LookupSymbol(string name) {
        if (_locals.TryGetValue(name, out var s)) return s;
        // 参数也在这里查
        return base.LookupSymbol(name);
    }
}
public class InBlockBinder : Binder { /* 局部 scope，let / var */ }
public class InLambdaBinder : Binder { /* capture 跟踪 */ }
public class InCatchBinder : Binder { /* catch 变量 */ }
```

收益：
1. 每种 scope 语义独立，加新 scope（如 `using` block / `await using`）= 加一个 Binder 子类
2. 现有 TypeChecker.cs 6 partial files 几千 LOC 可缩到 ~1000 LOC + 10+ Binder 子类各 100-200 LOC
3. 测试单个 Binder 容易（输入：name → 输出：ISymbol?）

估时：**14-21 天**（大架构改动）。**ROI 中长期**：当前 TypeChecker 还能撑，但加 L3 lambda / async / generic constraints 时复杂度爆炸前就该做。

#### F2.5 没有 Lowering Pass 框架 ⚠️

**Roslyn**：Bound → Bound 多级转换 ——
- `LocalRewriter`：async/await → state machine、iterator → state machine、lambda → 闭包类、`using` → try/finally、`foreach` → while + enumerator、switch → if-else 链、interpolated string → 构造、is-pattern → 等价 expr
- 每个 pass 是独立 `BoundTreeRewriter` 子类
- Pass 顺序固定 在 `MethodCompiler.cs`

每个 pass 输入是合法的 BoundNode，输出也是合法的 BoundNode（只是更"低级"）。最终送 IL emitter 的是已经被全部 lower 后的 BoundTree。

**z42 现状**：BoundTree 直接送 [`FunctionEmitter`](../src/compiler/z42.Semantics/Codegen/FunctionEmitter.cs)，所有 lowering 都在 emit 时做（`FunctionEmitterStmts.Loops.cs` 直接展开 foreach；`FunctionEmitterCalls.Interpolation.cs` 直接展开字符串插值）。

**问题**：
- 复杂 lowering（如 L3 的 async / lambda 提升）混在 emit 代码里，调试困难
- 不能在 lowering 后跑 analyzer / optimization
- 不能 `--dump-bound-lowered` 看中间结果

**建议**：

```csharp
// z42.Semantics/Lowering/BoundTreeRewriter.cs (新建)
public abstract class BoundTreeRewriter : BoundExprVisitor<BoundExpr>, BoundStmtVisitor<BoundStmt> {
    // 默认实现：递归 rewrite 所有 children；子类只 override 自己关心的 node
}

public sealed class ForeachLoweringPass : BoundTreeRewriter {
    protected override BoundStmt VisitForeach(BoundForeach f) {
        // foreach (var x in arr) body
        // ↓ rewrite to
        // var __idx = 0; while (__idx < arr.Length) { var x = arr[__idx]; body; __idx++; }
        return new BoundBlock(...);
    }
}

public sealed class InterpolatedStringLoweringPass : BoundTreeRewriter { ... }
public sealed class LambdaCaptureLoweringPass : BoundTreeRewriter { ... }  // L3
public sealed class AsyncStateMachinePass : BoundTreeRewriter { ... }       // L3+
public sealed class SwitchTableLoweringPass : BoundTreeRewriter { ... }

// 主 emitter 只接收 fully lowered BoundTree：
public static class MethodCompiler {
    public static IrFunction Compile(MethodSymbol method, BoundBlock body) {
        var passes = new BoundTreeRewriter[] {
            new ForeachLoweringPass(),
            new InterpolatedStringLoweringPass(),
            new SwitchTableLoweringPass(),
            // L3 时加 lambda / async lowering
        };
        var lowered = passes.Aggregate(body, (b, p) => (BoundBlock)p.Visit(b));
        return FunctionEmitter.Emit(method, lowered);
    }
}
```

收益：
1. 加新 lowering = 加新 Rewriter 子类，不动 emitter
2. 可 `--dump-bound-after=foreach-lowering` 看中间结果
3. L3 async / lambda 复杂语义改写有清晰的 pass 边界，不污染 IR emit
4. 未来 optimization pass（dead code elim / constant folding）走同一框架

估时：**7-10 天**（先建框架 + 迁移 2-3 个现有 lowering）。**ROI 极高**（L3 前置）。

#### F2.6 没有 Analyzer / Source Generator 扩展点 ⚠️

**Roslyn**：用户可以写 `DiagnosticAnalyzer` 子类 + `[DiagnosticAnalyzer]` attribute 注册，分析器跟着每次编译跑，发自定义 warning。配套 `CodeFixProvider` 提供自动修复建议。Source Generator 在 Compilation 阶段往里注入新 SyntaxTree（最早 Roslyn 3.x）。

**z42 现状**：完全没有外部扩展接口。所有 lint / check / 自定义规则都得改 z42.Semantics 源码。

**z42 长期路径**：

Phase 1（短期，未来 spec）：
- `IAnalyzer` interface ── `void Analyze(SemanticModel model, DiagnosticBag bag)`
- DLL plug-in 机制（参考 Roslyn `AnalyzerLoadFailureEventArgs`）
- 编译器在 typecheck 完后调用所有注册的 analyzer

Phase 2（远期）：
- Source Generator ── 用户写代码生成器，编译期注入新 .z42 源码
- Code Fix ── IDE 集成时按 Diagnostic 找匹配的 fix

**ROI**：当前不紧迫；自举完成后 + IDE 集成阶段再做。

#### F2.7 IR Pass Manager 框架空闲 ⚠️

**当前**：[`z42.IR`](../src/compiler/z42.IR/) 有 `IIrPass` interface + `IrPassManager`，但**没有任何 pass 实现**。注释说"infrastructure present but unused"。

**建议**：和 F2.5 lowering 框架配套，把 IR 层 optimization pass 也建起来 ——
- `DeadCodeEliminationPass`
- `ConstantFoldingPass`
- `RegisterCoalescingPass`
- `BasicBlockMergePass`

但 **建议先做 BoundTree lowering（F2.5），IR pass 之后再说**。原因：bytecode 层 optimization 是 micro-optimization；BoundTree lowering 解决可正确性问题（async / lambda），优先级更高。

#### F2.8 Generics: 仅 monomorphization，缺统一表达 ⚠️

**Roslyn**：generic type 有清晰双层 ——
- `INamedTypeSymbol.OriginalDefinition` ── 未实例化的"模板"（`List<T>`）
- `INamedTypeSymbol.Construct(int)` ── 实例化后的具体类型（`List<int>`）
- `IsGenericType` / `IsUnboundGenericType` 区分状态
- 所有泛型 lookup 都通过 `OriginalDefinition` 路径，instantiation 是叶子

**z42 现状**：
- `Z42InstantiatedType(Definition: Z42ClassType, TypeArgs: [...])` 有结构
- `Z42GenericParamType` 表 T 占位符
- 但 [`TypeChecker.Generics.cs`](../src/compiler/z42.Semantics/TypeCheck/TypeChecker.Generics.cs) 各处 ad-hoc 做 substitution，没集中
- IR 层全 monomorphize（每个 `List<int>` / `List<string>` 单独生成 IR）—— 实例多了膨胀

**建议**（中期）：
1. 把 generic substitution 集中到 `TypeSubstitution` 类
2. IR 层考虑 generic sharing（Roslyn / CoreCLR 都做 ── List<int> / List<long> 共享 code，仅 dictionary 不同）—— 但这是 L3 之后的优化

#### F2.9 Parser 错误恢复偏弱 ⚠️

**z42 现状**：spec enhance-expr-recovery 已落地（`ErrorExpr` placeholder + skip-to-boundary），statement 层基本失败立返。

**Roslyn**：
- Token level：`SkippedTokensTrivia` 把无法 parse 的 token 挂在前面 trivia，不丢源码位置
- Production level：panic-mode recovery 通过 first/follow set 计算"何时认为产生式结束"
- 同样一段烂代码，Roslyn 能 fail-soft 报 10 个错而不是 1 个

**建议**：当前对 bootstrap 够用；自举后 IDE 集成时（错误代码需要实时高亮）必须改进。

**ROI**：短期低，长期必要。

## F3. z42 编译器 ✅ Roslyn 模式已对齐的部分

不要重复改造，已经做得不错：

1. **AST 不可变 sealed records** ── ✅
2. **BoundTree 不可变 + Visitor** ── ✅（缺 Rewriter，见 F2.5）
3. **DiagnosticBag 收集模式** ── ✅
4. **结构化错误码** ── ✅（E0xxx vs CSxxxx）
5. **Pretty diagnostic renderer** ── ✅
6. **DiagnosticCatalog + explain 命令** ── ✅
7. **TypeRegistry 数据驱动注册** ── ✅
8. **Partial class 按 concern 拆 TypeChecker** ── ✅
9. **Pratt parser + combinators** ── ✅（Roslyn 是手写递归下降，z42 用 combinators 更模块化）
10. **错误恢复占位符 `ErrorExpr`** ── ✅
11. **Phase 0/1/2 顺序明确** ── ✅
12. **80+ test class + 36 error fixture** ── ✅
13. **Span propagate 全程** ── ✅
14. **Bound 节点编译期 visitor 强制 override** ── ✅ 对齐 Roslyn

## F4. 改进优先级（合并 Part 6 到总表）

新加 6 项 P0-P3：

| 优先级 | 改造 | Part | 估时 | 解锁价值 |
|---|---|---|---|---|
| **P0** | **`Compilation` 不可变快照**（F2.1） | 6 | 7-10 天 | 多线程并行编译 + IDE / LSP 集成基础 |
| **P1** | **`ISymbol` 公共抽象**（F2.2） | 6 | 10-14 天 | 跨文件符号身份；解锁 find-references / rename |
| **P1** | **`SemanticModel` 按需 binding**（F2.3 Phase 1） | 6 | 3-5 天 | query API；IDE / analyzer 前置 |
| **P1** | **BoundTree Lowering Pass 框架**（F2.5） | 6 | 7-10 天 | L3 async / lambda / generics lowering 前置 |
| **P2** | **Binder 层级**（F2.4） | 6 | 14-21 天 | scope 语义清晰，加 L3 特性时不爆炸 |
| **P3** | **Analyzer / SourceGen 扩展点**（F2.6） | 6 | 大 | 自举完成后；IDE 阶段必备 |
| **P4** | **IR Pass Manager 实装**（F2.7） | 6 | 中 | bytecode 微优化；先做 BoundTree lowering |
| **P4** | **Parser 强化错误恢复**（F2.9） | 6 | 中 | 自举后 IDE 阶段 |

合并到总优先级表（**Part 1-6 最终汇总**，省略中间已列项目；仅追加 Part 6 新增）：

**Wave 5（编译器架构演进, 6-8 周）—— 自举前 must-have**：
1. F2.1 Compilation 不可变快照
2. F2.3 SemanticModel 按需 query API (Phase 1)
3. F2.5 BoundTree Lowering Pass 框架
4. F2.2 ISymbol 公共抽象
5. F2.4 Binder 层级

## F5. 立即可做的小项目（≤1 天，独立 commit）

不依赖大改造，今晚就能写：

1. ✅ ~~**把 `Z42Type` 的 well-known singletons 移到 `WellKnownTypes` 类集中**~~ — `WellKnownTypes` (2026-05-25) exposes `ByName` alias map + `AllPrimitives` list; `Z42Type` singletons stay as identity targets
2. ✅ ~~**`DiagnosticCodes` 加 `Category` 字段**~~ — `DiagnosticCategory` enum + `DiagnosticCategories.Of(code)` classifier (2026-05-25)
3. ✅ ~~**`Diagnostic` 加 `Properties: ImmutableDictionary<string, string>` 字段**~~ — `Diagnostic.Properties` + `Props` + `WithProperty` shipped
4. ⚡ **Parser error message 加 `expected: <list>`** ── Roslyn 错误是 "expected `(`, identifier, or `default`"；z42 当前是单 token
5. ⚡ **TypeChecker `_funcConstraints` 等 mutable stack 改 `ImmutableStack<T>`** ── 配合未来 Binder hierarchy 演进；现在改不破任何东西
6. ⚡ **BoundDumper 加 `--dump-bound-with-types` 选项** ── 当前已有 type 注解，再加 symbol id（依赖 F2.2 落地后）方便 LSP 调试
7. ✅ ~~**`IrPassManager` 框架未用，加一个 no-op `IIrPass` 实现作为占位**~~ — `z42.IR/NoOpPass.cs` + IrPassManager pipeline

## F6. 不抄的部分

明确不抄 Roslyn 的部分：

- **GreenNode / RedNode 双层**：Rust 那边 z42 还没 incremental editing 需求；C# bootstrap 是 batch compiler；单层 sealed record 够用
- **`IOperation` 独立分析模型**：Roslyn 为了支持公开的 IDE API 而独立的"语义视图"；z42 Bound 已经够用，不再加一层
- **WPF Workspace API（VS 集成）**：z42 IDE 集成走 LSP 即可，不需要 Roslyn 那套
- **Nullable Reference Type 流分析**：z42 spec 用 `T?` Option type 而非 NRT 注解；语义不同，不抄
- **Source Generator 复杂度**：Phase 1-2 不做
- **30+ lowering pass**：z42 还在 L2，async/await 都没；先做 5-7 个基本 pass（foreach / interpolation / lambda capture / switch / using）

## F7. 总结：z42 编译器目前位置

**类比 Roslyn 时间线**：
- **z42 现状 ≈ Roslyn 0.x 时代**（Microsoft.CodeAnalysis 早期）
  - ✅ 不可变 AST / Bound + Visitor + DiagnosticBag —— **核心 80% 已对齐**
  - ❌ Compilation 不可变快照 / Binder hierarchy / 按需 SemanticModel / lowering pass 框架 —— **未做**
  - ❌ Analyzer extensibility / Source Generator —— **未做**

**距离自举的关键路径**：
1. Wave 5 编译器架构演进（6-8 周）—— **F2.1 + F2.3 + F2.5 是 P0**
2. 性能 / 数据类型优化（Wave 2，3-4 周）
3. 然后才有信心把 compiler 重写到 z42

**最值得抄 Roslyn 的 3 件事**：
1. **Compilation 不可变快照** —— 解锁并行 + IDE
2. **BoundTree Lowering Pass 框架** —— async / lambda / generics 前置
3. **ISymbol 公共抽象** —— 跨文件符号身份；find-references / rename / GoToDef 基础

---

*Part 6 作者：Claude（z42 review）*
*调研日期：2026-05-22*
*Roslyn 部分参考：训练知识 + Roslyn 官方文档（dotnet/roslyn wiki & API docs）*
