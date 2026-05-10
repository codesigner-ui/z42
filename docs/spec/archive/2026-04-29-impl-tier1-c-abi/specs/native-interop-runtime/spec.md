# Spec: Native Interop Runtime (Tier 1 C ABI)

## ADDED Requirements

### Requirement: TypeRegistry Lifecycle

`VmContext` 持有 `native_types: Rc<RefCell<HashMap<(String, String), Arc<RegisteredType>>>>`，每 VM 独立。

#### Scenario: register + resolve 同 VM 内
- **WHEN** 在某 `VmContext` 上 register 一个 `Counter` 类型，再用 `resolve_native_type("numz42", "Counter")` 查找
- **THEN** 返回与注册时相同的 `TypeRef`（指针稳定）

#### Scenario: 不同 VmContext 隔离
- **WHEN** vm1 注册 `Counter`，vm2 用同 module/name 查找
- **THEN** vm2 返回 None；vm1 返回正常 TypeRef

#### Scenario: 重复注册同 (module, type) 拒绝
- **WHEN** 同一 VM 上 register 两次相同 `(module="numz42", type="Counter")`
- **THEN** 第二次返回 NULL TypeRef，`z42_last_error()` 返回 Z0905（重复注册）

---

### Requirement: ABI Version Validation

`z42_register_type` 必须校验 `desc->abi_version == Z42_ABI_VERSION`。

#### Scenario: 版本匹配通过
- **WHEN** descriptor 的 `abi_version = 1`
- **THEN** 注册成功，返回非 NULL TypeRef

#### Scenario: 版本不匹配拒绝
- **WHEN** descriptor 的 `abi_version = 2`（虚构未来版本）
- **THEN** 返回 NULL TypeRef，`z42_last_error()` 返回 Z0906，message 含 "ABI version mismatch: expected 1, got 2"

#### Scenario: descriptor null 拒绝
- **WHEN** `z42_register_type(NULL)` 调用
- **THEN** 返回 NULL TypeRef，Z0905，message 含 "null descriptor"

---

### Requirement: Z42Value Marshal

`Z42Value` ↔ z42 `Value` 双向转换无损（blittable 子集）。

#### Scenario: I64 往返
- **WHEN** `value_to_z42(Value::I64(42))` 然后 `z42_to_value(&result, "i64")`
- **THEN** 得到 `Value::I64(42)`

#### Scenario: F64 NaN 保留
- **WHEN** `value_to_z42(Value::F64(f64::NAN))` 往返
- **THEN** 得到 NaN（不 panic）

#### Scenario: Bool / Null / Str 各往返不丢精度
- **WHEN** 测试 Value::Bool(true)、Value::Null、Value::Str(Rc::new("hi".to_string()))
- **THEN** 每个值往返后等于原值（结构等价）

#### Scenario: 不支持类型 marshal 失败
- **WHEN** `value_to_z42(Value::Object(...))` 但目标 ABI 类型为 `i64`
- **THEN** 返回 Err，message 含 "blittable type mismatch"

---

### Requirement: libffi Dispatch

`MethodEntry.cif` 在注册时构建；`dispatch::call(cif, fn_ptr, args)` 调用 libffi。

#### Scenario: 进程内函数指针调用
- **WHEN** 注册一个 Rust `extern "C" fn(i64, i64) -> i64`，构建 cif，调用并传 `[Z42Value::I64(3), Z42Value::I64(4)]`
- **THEN** 返回 `Z42Value::I64(7)`

#### Scenario: 单参数 self-pointer 调用
- **WHEN** 注册 `extern "C" fn(*mut Counter) -> i64`，分配 Counter，调用 inc 三次
- **THEN** 第三次返回 3

#### Scenario: 签名解析失败
- **WHEN** signature 字符串包含 unsupported 类型如 `"(&[i64]) -> i64"`
- **THEN** 注册返回 Err，错误信息指向 unsupported 部分（pinned 块在 C4 提供）

---

### Requirement: dlopen + Native Library Loading

`VmContext::load_native_library(path)` 加载 native 库并触发其 register entry。

#### Scenario: 加载已编译的 numz42-c
- **WHEN** 调用 `vm.load_native_library(path_to_libnumz42_c.dylib)`
- **THEN** 成功；之后 `vm.resolve_native_type("numz42", "Counter")` 返回 Some

#### Scenario: 路径不存在
- **WHEN** `load_native_library("/nonexistent/foo.so")`
- **THEN** 返回 Err，错误码 Z0910；message 含底层 libloading 信息

#### Scenario: register entry 不存在
- **WHEN** 加载一个 .dylib 但里面没有 `<libname>_register` 符号
- **THEN** 返回 Err Z0910，message 提示 "missing register entry symbol"

#### Scenario: VM drop 释放库
- **WHEN** `VmContext` drop
- **THEN** 持有的 `libloading::Library` 全部释放（无 leak）

---

### Requirement: CallNative IR Dispatch

`Instruction::CallNative` 替换 C1 trap：marshal → libffi → unmarshal。

#### Scenario: 端到端 Counter inc
- **WHEN** 加载 numz42-c → 注册 Counter → 手工构造 zbc：
  1. 调用 alloc 直接得到 `*mut Counter`（C2 测试约定，C5 才有真正 ctor 路径）
  2. `CallNative numz42::Counter::inc(self_ptr)` 三次
  3. `CallNative numz42::Counter::get(self_ptr)`
- **THEN** 最终结果 `Value::I64(3)`

#### Scenario: 未知 type 报错
- **WHEN** zbc 含 `CallNative "ghost"::"Phantom"::"foo"` 且未注册
- **THEN** VM 返回 Err，message 含 "unknown native type ghost::Phantom (Z0905)"

#### Scenario: 未知 method 报错
- **WHEN** Counter 已注册但 zbc 调用 `inc_lol` 这个不存在的 method
- **THEN** VM 返回 Err，message 含 "unknown method numz42::Counter::inc_lol (Z0905)"

---

### Requirement: thread_local Last Error

`z42_last_error()` 报告**线程本地**最后一次错误，多次查询同结果。

#### Scenario: 成功操作清除上次错误
- **WHEN** 第一次 `z42_register_type` 失败（abi 不匹配）→ Z0906；第二次 register 成功 → 再调 `z42_last_error()`
- **THEN** 第二次成功 register **不清除** Z0906；但下一次任何 z42_* 入口（包括成功 register 那一次）会先 reset；所以第二次 register 成功后 last_error.code 为 0 / Null

#### Scenario: Last error 跨线程隔离
- **WHEN** 主线程 register 失败设置 Z0906；新建辅线程查询 `z42_last_error()`
- **THEN** 辅线程返回 `Z42Error { code: 0, message: NULL }`

---

### Requirement: Other C1 Opcodes Still Trap

`CallNativeVtable` / `PinPtr` / `UnpinPtr` 在 C2 仍触发清晰 trap（C4/C5 才接入）。

#### Scenario: CallNativeVtable 仍 trap
- **WHEN** 执行 `CallNativeVtable` 字节码
- **THEN** VM 返回 Err 含 "spec C5"

#### Scenario: PinPtr/UnpinPtr 仍 trap
- **WHEN** 执行 `PinPtr` 或 `UnpinPtr`
- **THEN** VM 返回 Err 含 "spec C4"

## IR Mapping

不新增 opcode；`Instruction::CallNative`（C1 已声明，0x53）的运行时语义被钉死：

```
%dst = CallNative <module>, <type>, <symbol>, [args...]

Runtime:
  ty := vm.native_types.get((module, type)) or fail Z0905
  m  := ty.methods.get(symbol)             or fail Z0905
  z_args := args.map(value_to_z42)
  z_ret  := libffi.call(m.cif, m.fn_ptr, z_args)
  dst    := z42_to_value(z_ret, m.return_type)
```

## Pipeline Steps

- [ ] Lexer — 不涉及
- [ ] Parser / AST — 不涉及（用户面向 syntax 在 C5）
- [ ] TypeChecker — 不涉及
- [ ] IR Codegen — 不涉及（hand-crafted bytecode 测试）
- [x] VM interp — `CallNative` dispatch 接通；其余 3 个 opcode 保持 trap
- [ ] JIT / AOT — 不涉及（保持 bail!，L3.M16）

## Out-of-band Verification

新增**端到端**集成测试 `tests/native_interop_e2e.rs`：

```
build numz42-c → libnumz42_c.dylib via build.rs
↓
VmContext::new()
  ↓ load_native_library(libnumz42_c.dylib)
  ↓ ↓ dlopen ✓
  ↓ ↓ symbol lookup `numz42_register` ✓
  ↓ ↓ call register → z42_register_type(&COUNTER_DESC) ✓
↓
hand-crafted Module with Function containing CallNative bytecode:
  let counter = call_native numz42::Counter::__alloc__(...)
  call_native numz42::Counter::inc(counter)  -- 3 times
  result = call_native numz42::Counter::get(counter)
  return result
↓
interp::run → assert returned value == I64(3)
```

`__alloc__` 是 C2 测试期约定的 alloc-only 方法名（绕过 ctor，等 C5 真实接 alloc → ctor pipeline）。

## Documentation Sync (mandatory before archive)

- `docs/design/interop.md` §10 Roadmap：C2 行 → ✅ + 完成日期
- `docs/design/error-codes.md`：Z0905 / Z0906 / Z0910 从"占位"改为"已启用"+ 抛出条件
- `docs/design/vm-architecture.md`：VM 状态段补 `native_types` + `native_libs`
- `docs/roadmap.md` Native Interop 表 C2 → ✅
- `src/runtime/src/native/README.md` NEW
