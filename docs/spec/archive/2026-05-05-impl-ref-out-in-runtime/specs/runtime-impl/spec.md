# Spec: ref-out-in-runtime

## ADDED Requirements

### Requirement: 运行时 ref 语义（caller 看见修改）

#### Scenario: ref 修改原语 local
- **WHEN** `void Increment(ref int x) { x = x + 1 }`；`var c = 0; Increment(ref c); print(c)`
- **THEN** 运行时输出 `1`

#### Scenario: ref 多次调用累积
- **WHEN** `var c = 0; Increment(ref c); Increment(ref c); print(c)`
- **THEN** 输出 `2`

#### Scenario: ref 数组元素
- **WHEN** `void Set(ref int x, int v) { x = v }`；`var a = new int[]{10,20,30}; Set(ref a[1], 99); print(a[1])`
- **THEN** 输出 `99`

#### Scenario: ref 对象字段
- **WHEN** `class Box { public int Val; }`；`var b = new Box(); b.Val = 5; Set(ref b.Val, 100); print(b.Val)`
- **THEN** 输出 `100`

#### Scenario: ref 引用类型（reseat）
- **WHEN** `void Reseat(ref string s) { s = "new" }`；`var name = "old"; Reseat(ref name); print(name)`
- **THEN** 输出 `new`

---

### Requirement: 运行时 out 语义

#### Scenario: out 写入 caller var
- **WHEN** `bool TryParse(string s, out int v) { v = 42; return true }`；`int n; TryParse("x", out n); print(n)`
- **THEN** 输出 `42`

#### Scenario: out var x 内联声明
- **WHEN** `if (TryParse("x", out var n)) print(n)`
- **THEN** 输出 `42`

---

### Requirement: 运行时 in 语义

#### Scenario: in 参数读取
- **WHEN** `int DoubleIt(in int x) { return x * 2 }`；`var v = 21; print(DoubleIt(in v))`
- **THEN** 输出 `42`

#### Scenario: in 参数零拷贝
- **WHEN** 大值类型（未来 user struct）通过 in 传递
- **THEN** callee 不拷贝（性能要求；本 spec 内仅原语类型 in，行为正确即可，性能不强测）

---

### Requirement: 嵌套调用 ref 透传

#### Scenario: 嵌套两层 ref 透传
- **WHEN** `void Outer(ref int x) { Inner(ref x) }`，`void Inner(ref int x) { x += 100 }`；`var c = 1; Outer(ref c); print(c)`
- **THEN** 输出 `101`（Outer 把自己的 ref param 作为 ref arg 传给 Inner，Inner 写入最终落到 caller 的 c）

---

### Requirement: GC 协调

#### Scenario: ref to array element 在 GC 周期间存活
- **WHEN** caller 将数组传给 callee（ref 元素），callee 内部触发 GC
- **THEN** 数组对象不被回收（Value::Ref::Array 内的 GcRef 加入 GC 根）

#### Scenario: ref to object field 在 GC 周期间存活
- **WHEN** caller 将对象字段以 ref 传，callee 内部触发 GC
- **THEN** 对象不被回收

---

## IR Mapping

| z42 形态 | IR 指令序列 |
|---|---|
| `f(ref c)` 调用 local | `%addr = LoadLocalAddr slot_of_c` → `Call f, [%addr]` |
| `f(ref a[i])` 调用 array elem | `%addr = LoadElemAddr a, i` → `Call f, [%addr]` |
| `f(ref obj.fld)` 调用 object field | `%addr = LoadFieldAddr obj, "fld"` → `Call f, [%addr]` |
| `f(out var n)` callsite 内联 | TypeChecker 已为 n 分配 reg；同 ref 走 `LoadLocalAddr` |
| Callee 读 ref param | 普通 `Copy` / 算术指令；`frame.get` 透明 deref |
| Callee 写 ref param | 普通 `Copy` / 算术指令；`frame.set` 透明 store-through |

---

## Pipeline Steps

- [x] **TypeChecker**：已在前置 spec 完成（无新工作）
- [ ] **IR Codegen**：FunctionEmitterCalls 检测 BoundModifiedArg → emit LoadXxxAddr
- [ ] **VM interp**：`Value::Ref` + `RefKind` + 3 新 opcode + 透明 deref via `frame.get/set`
- [ ] **GC**：scan_object_refs 处理 Value::Ref
- [ ] **JIT / AOT**：占位 fallback，本 spec 不实施
