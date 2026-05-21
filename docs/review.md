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
2. ⚡ **`__str_char_at` 错误路径减少一次 `chars().count()`** —— [`corelib/string.rs:20`](../src/runtime/src/corelib/string.rs#L20)
3. ⚡ **JIT bool 类 helper（jit_and / jit_or / jit_not）走 Cranelift native** —— Bool 类型 IR 已知，不需要 helper
4. ⚡ **`jit_const_*` 完全 inline emit** —— Cranelift 原生支持 const，不需要 helper call
5. ⚡ **`Frame::regs: Vec<Value>` → 函数入口预分配 `with_capacity(max_reg+1)`** —— 现有按需 push 会触发多次 realloc

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

## D3. 结构化事件流

### CoreCLR
- **EventPipe** ([`vm/eventpipeinternal.h`](../../../runtime/src/coreclr/vm/eventpipeinternal.h))：统一事件流，多 provider（GC / JIT / Thread / Type / Loader / Method / Assembly / Exception）
- 外部工具（`dotnet-trace` / `dotnet-counters` / Perfview）通过 IPC 订阅
- 内置 circular buffer，可写 `.nettrace` 文件
- ETW 兼容（Windows native）+ EventPipe 协议（cross-platform）

### z42 现状
- 仅 [`gc/types.rs:74`](../src/runtime/src/gc/types.rs#L74) `GcObserver` trait + 5 个 `GcEvent` variants（`BeforeCollect` / `AfterCollect` / `AllocationPressure` / `NearHeapLimit` / `OutOfMemory`）
- 仅 GC 域有事件流 —— JIT / interp / exception / type-load 都无
- 无外部订阅协议

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

## D4. Crash 处理 + Signal handler

### CoreCLR
- [`debug/createdump/`](../../../runtime/src/coreclr/debug/createdump/) —— SIGSEGV / SIGABRT / 内部 fail-fast 都触发 minidump 生成
- 输出 ELF/PE core dump，可供 `lldb` / `windbg` 离线分析
- Stack walk 在 signal handler 内安全（async-signal-safe primitives）

### z42 现状 ❌
- 顶层 `main() -> Result<()>`，`anyhow!` / `bail!` 错误以 `Display` 形式输出到 stderr 后退出码 1
- **没有 signal handler**：SIGSEGV / SIGABRT 直接 abort，丢失全部状态
- 无 Rust panic hook override
- 无 crash report 文件输出

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

## D6. 性能指标 / Counters

### CoreCLR
- EventCounters 模型：runtime 暴露 `gc-heap-size` / `cpu-usage` / `working-set` / `gen-2-gc-count` / `exception-count` 等内置 counter
- 定期采样（默认 60s）+ EventPipe 推送到 monitoring backend
- `dotnet-counters monitor` 直接展示

### z42 现状
- [`gc/types.rs`](../src/runtime/src/gc/types.rs) `HeapStats`（`allocated_bytes` / `freed_bytes` / `objects_count` / `pause_us` 等）✅
- 暴露给 z42 脚本：`Std.GC.UsedBytes()` / `Std.GC.ForceCollect()` ✅
- 缺：JIT 编译数 / 异常 throw 数 / Builtin call 频率 / native call 数

### 建议 ⚠️
1. 在 `VmContext` 加 atomic counters：
   ```rust
   pub struct RuntimeCounters {
       pub jit_methods_compiled: AtomicU64,
       pub jit_compile_us_total: AtomicU64,
       pub exceptions_thrown: AtomicU64,
       pub exceptions_caught: AtomicU64,
       pub native_calls: AtomicU64,
       pub builtin_calls: AtomicU64,
   }
   ```
2. corelib 加 `Std.Diagnostics.RuntimeStats.Snapshot()` 暴露给脚本
3. CLI `--print-stats-on-exit` 打印 summary
4. Prometheus / OTLP exporter 推迟

**ROI**：中。1-2 天 (counters) + 后续脚本 API 单独 spec。

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
| **P0** | **Panic hook + signal handler** (D4) — 生产前 must-have | 4 | 1-2 天 | ops |
| **P0** | **`RuntimeConfig` 中心化** (D1) — 所有 knob 一处声明 | 4 | 1-2 天 | ops |
| **P0** | **JIT type specialization** (C2) — 已知 IrType 不走 helper | 2 | 2-3 天 | perf |
| **P0** | **JIT↔VM trait 抽象** (Part 1) | 1 | 2-3 天 | arch |
| **P1** | **Per-module log filtering** (D2) — `Z42_LOG=z42::jit=debug` | 4 | 0.5 天 | ops |
| **P1** | **`Value::Str(String) → Rc<str>`** (C1+C3) | 2 | 1-2 天 | perf |
| **P1** | **Field/Method name → token id** (C4+C5) | 2 | 3-4 天 | perf |
| **P1** | **trait-based test commons** (S2.4) | 3 | 3-5 天 | stdlib |
| **P1** | **Internal shared helpers 层** (S2.3) | 3 | 5-7 天 | stdlib |
| **P1** | **`RuntimeObserver` + `RuntimeStats`** (D3+D6) | 4 | 3-5 天 | ops |
| **P2** | **Prestub / lazy JIT** (Part 1) | 1 | 3-5 天 | arch |
| **P2** | **Polymorphic IC** (C4+C5) | 2 | 2-3 天 | perf |
| **P2** | **Public API surface lint** (S2.5) | 3 | 2-3 天 | stdlib |
| **P3** | **PAL 抽象层** (Part 1) | 1 | 5-7 天 | arch |
| **P3** | **String literal interning** (C3) | 2 | 3-4 天 | perf |
| **P3** | **Startup banner / --info** (D5) | 4 | 0.5 天 | ops |
| **P4** | **Value 拆 hot/cold variants** (C1) | 2 | 5-7 天 | perf |
| **P4** | **GC bump allocator** (C6) | 2 | 极大 | arch |
| **P4** | **Hot-path stub inline** (Part 1) | 1 | 2-3 天 | perf |
| **P5** | **Diagnostic IPC** (D9) | 4 | 巨大 | ops |
| **P5** | **z42.text 扩充 / z42.math BigInteger** (S5) | 3 | 7-10 天 | stdlib |

### 立即可上（≤1 天，5 个独立 commit）

1. ⚡ **删除 z42.text/src/Regex.z42 stub** (S2.2)
2. ⚡ **z42.crypto 补 README** (S2.1)
3. ⚡ **`tracing` EnvFilter**（D2）—— `Z42_LOG=...` 即时生效
4. ⚡ **Rust panic hook + Z42_CRASH_DIR**（D4 的第一步）
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
