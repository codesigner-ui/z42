# Parameter Modifiers — `ref` / `out` / `in`

> 状态：**编译期 + 运行时全部已落地**（spec/archive/2026-05-05-define-ref-out-in-parameters-typecheck/ + spec/archive/2026-05-05-impl-ref-out-in-runtime/）。

z42 提供三个参数修饰符：

| 修饰符 | 语义 | 调用方语法 |
|---|---|---|
| `ref T x` | 双向引用，调用方必须已初始化；callee 可读可写；caller 可见修改 | `f(ref x)` |
| `out T x` | 单向输出，调用方可未初始化；callee 必须在 normal-return 路径赋值 | `f(out v)` 或 `f(out var v)` |
| `in T x` | 只读引用（callee 端契约），零拷贝传大值；callee 不可写 | `f(in y)` |

## 设计哲学

**单一形态 + 结构性约束**——避免 C# 在这块演化出的 7+ 修饰符（`ref` / `in` / `ref readonly` / `scoped` / `ref struct` / `ref readonly` 等）互相弥补的复杂度链。z42 选择保留 `ref` / `out` / `in` 三个对称的参数修饰符；其他位置（local / return / field / type）的 ref 全部延后（见 "Deferred / Future Work"）。

**关键洞察**：C# `ref` 体系真正的复杂度 80% 来自让 ref 离开调用栈帧的扩展（`ref struct` / `ref` field / `ref` return）。z42 的简化路线是 **"ref 永远不离开调用栈帧"** —— 不需要 lifetime 系统、不需要 `scoped`、不需要 `ref struct`，全部塌缩。

## 语法

### 函数声明

```z42
// ref：双向，调用方必须初始化
void Increment(ref int x) {
    x = x + 1;
}

// out：单向输出 + DefiniteAssignment
bool TryParse(string s, out int v) {
    v = 0;
    // ... 解析逻辑 ...
    return true;
}

// in：只读引用，零拷贝
double Norm(in BigVec v) {
    return Sqrt(v.x*v.x + v.y*v.y);
}
```

### 调用方语法

```z42
// ref：必须显式写
var c = 0;
Increment(ref c);    // c == 1

// out：支持 `out var x` 内联声明（作用域延伸到包含的 if/while-after）
if (TryParse("42", out var n)) {
    Console.WriteLine(n);    // n 在此可见
}

// in：必须显式写（z42 修正 C# 可省略的不一致）
var v = new BigVec(1.0, 2.0);
var d = Norm(in v);
```

### 类型严格匹配（修饰符边界）

跨修饰符边界禁止隐式转换——避免 caller 看到修改时类型不一致：

```z42
void Foo(ref int x) { x = 1; }
long n = 0;
Foo(ref n);    // 编译错误：long 不能匹配 ref int（即使 long → int 一般可通过 cast）
```

### Lvalue 限制

`ref` / `out` / `in` 实参必须是 lvalue：

```z42
Increment(ref c);              // ✓ 变量
Increment(ref arr[i]);         // ✓ 数组元素（运行时 follow-up spec 实施）
Increment(ref obj.field);      // ✓ 对象字段（同上）
Increment(ref f());            // ✗ 函数返回值
Increment(ref 42);             // ✗ 字面量
```

## 多返回值替代

对于"返回多个值"场景，**优先使用 tuple 解构**：

```z42
// 推荐：tuple 解构
(bool ok, int v) TryParse(string s) {
    return (true, 42);
}

if (TryParse("42") is var (ok, v); ok) {
    print(v);
}

// 等价但 out 形式更接近 C# 风格
bool TryParse(string s, out int v) {
    v = 42;
    return true;
}
```

## 修饰符参与重载

`Foo(int)` 与 `Foo(ref int)` 视为不同重载（modifier-tagged key）：

```z42
void Foo(int x) { ... }            // by-value
void Foo(ref int x) { x = 0; }     // by-ref

var c = 1;
Foo(c);          // 选 by-value 重载
Foo(ref c);      // 选 by-ref 重载
```

**当前限制**：modifier-based overload 仅在同名同 arity 不同 modifier 序列时启用 modifier-tagged key。同名同 arity **同 modifier** 序列仍是 z42 既有的"second wins"限制（与 type-based overload 一样，等待统一改造）。

## 4 条交互限制（编译期）

1. **Lambda 捕获禁止**：`var f = () => x + 1;` 当 `x` 是 ref/out/in 参数时编译错误（ref 不可逃逸调用栈帧）
2. **async 方法**：当前 z42 未支持 async；async 引入时单独 spec 决定如何放开 ref 参数
3. **iterator 方法**：同上
4. **Generic `T`**：T 不能绑定为 ref/out/in 形态（modifier 不是类型）

## DefiniteAssignment 规则（`out` 专属）

### Caller 端

调用前 `out` 实参的 lvalue **可未初始化**；调用后视为已赋值：

```z42
int v;             // 未初始化
TryParse("x", out v);
print(v);          // 不报"v 未初始化"
```

### Callee 端

callee 必须在所有 normal-return 路径上对 `out` 参数赋值：

```z42
void Foo(out int x) {
    x = 1;        // ✓ 赋值
}                  // normal-return：x 已赋

void Bar(out int x) {
    return;        // ✗ 编译错误：normal-return 路径未赋 x
}

void Baz(out int x) {
    if (cond) {
        x = 1;
    } else {
        throw new Err();    // throw 路径不要求赋值
    }
}                  // ✓ 编译通过：唯一 normal-return 路径已赋
```

## `in` 写保护

callee 函数体内对 `in` 参数赋值编译错误：

```z42
void Foo(in int x) {
    x = 5;    // ✗ 编译错误：cannot assign to `in` parameter `x` (read-only contract)
}
```

注：`in` 仅约束"slot 不可重赋"，不约束"指向对象的内部状态"。`in obj` 时 callee 可调用 `obj.SomeMethod()` 修改对象内部，这与 C# 一致。

---

## Runtime Implementation

运行时通过 `Value::Ref` + 入口 copy-in / 出口 copy-out 实现，由 spec [`impl-ref-out-in-runtime`](../../spec/archive/2026-05-05-impl-ref-out-in-runtime/) 落地（2026-05-05）。

**关键架构（design Decision R2 sidecar 方案）**：

1. **3 个 IR opcodes** 由 Codegen 在 ref/out/in callsite 实参展开时 emit：
   - `LoadLocalAddr { dst, slot }` → `Value::Ref::Stack { frame_idx, slot }`
   - `LoadElemAddr { dst, arr, idx }` → `Value::Ref::Array { gc_ref, idx }`
   - `LoadFieldAddr { dst, obj, field_name }` → `Value::Ref::Field { gc_ref, field_name }`

2. **入口 copy-in**：`exec_function` 在 callee Frame 创建后，扫描 params；持 `Value::Ref` 的 reg 被 deref'd 替换为底层值，原 `RefKind` 存入 `frame.ref_writebacks` sidecar。callee 体内所有指令读到的都是普通 `Value`，**无需修改 80+ 指令 handler**。

3. **出口 copy-out**：`exec_function` 在每个 return / throw 路径前调用 `run_ref_writebacks`，把 callee 最终 reg 值通过原 `RefKind` 写回 caller 的 lvalue（Stack → caller frame slot；Array → 数组元素；Field → 对象字段）。

4. **嵌套调用透传**：`Outer(ref x) { Inner(ref x) }` 中 Outer 的 x 已被入口 copy-in 解为底层值；Codegen 对 `Inner(ref x)` emit `LoadLocalAddr` 指向 Outer 的 slot；Inner 写入触发 Outer slot 更新；Outer 出口 writeback 把 Outer slot 写回原 caller。两步 indirection 自然形成。

5. **GC 协调**：`Value::Ref::Array` / `Field` 持 `GcRef`，参与 GC mark 让底层数组 / 对象在调用期间存活。Stack ref 不持 GcRef（frame 在调用栈自然存活）。

**Decision R2 选择 sidecar 方案而非"frame.get/set 内部 deref"**的理由：
- frame.get/set 内部 deref 需要修改 80+ 指令 handler 调用点（每个都要传 ctx）
- sidecar 仅在 frame 入口/出口工作，所有指令 handler 不变
- 语义等价：用户无法观察到调用中途状态（与 C# 一致）
- 性能：仅 1 次 Vec 分配 + n 次 deref/store-through（n = ref params 数量），远小于 80+ handler 改造的运行时开销

---

## Deferred / Future Work（设计期延后）

下列特性在本 spec 设计期主动决定不引入；记录形态、延后理由、重启评估触发条件。

### D1：`ref` 局部变量 `ref int x = ref expr`

- **形态**：`ref int slot = ref arr[idx]; slot = 42; slot += 1;`
- **延后理由**：z42 还没有 user-defined value type（struct）；ref local 主要服务于值类型原地修改，原语数组场景收益薄
- **重启触发**：user struct 提案落地（独立 spec）；性能 profiling 显示原语数组热路径需要 ref local
- **简化预案**：永远块作用域（不可 return / 不可存字段 / 不可被 lambda 捕获），单一规则收掉 C# 全部 `scoped` 默认规则

### D2：`ref` 返回类型 `ref T M()`

- **形态**：`ref int FindSlot(int[] arr, int key) { ... return ref arr[i]; }`
- **延后理由**：单独引入 ref return 价值减半（caller 没法接收，需 D1 配套）；escape analysis 实现成本高（需 ref-safe-context 分析）
- **重启触发**：D1 落地后；用户实际场景需要 in-place 修改 + 返回引用
- **简化预案**：仅允许 return 三种 lvalue（参数 / 引用类型字段 / 数组元素），结构性 escape check 不需要 lifetime annotation

### D3：`ref` 字段 `ref T field`

- **形态**：仅在 `ref struct` 内部允许（与 D4 同进退）
- **延后理由**：绑定 D4，独立无意义
- **重启触发**：D4 落地（实际上 D4 推荐永不引入，所以 D3 也是永不）

### D4：`ref struct` 类型

- **形态**：`ref struct MySpan { ref T _ptr; int _length; }`
- **延后理由**：C# `ref struct` 的传染性约束（不能装箱 / 不能进 generic 容器 / 不能跨 async-yield-closure）会让 z42 类型系统裂成两半；GC 语言中 slice 用 GC 对象表达性能完全可接受
- **重启触发**：极端性能场景（高频零分配 buffer 视图传递）profiling 证明 GC slice 不可接受
- **当前替代**：`pinned p = string_or_array { p.ptr ... }` （buffer 零拷贝）+ GC slice 类型（一般场景）

### D5：`scoped` 修饰符

- **形态**：`void Foo(scoped ref int x)` / `scoped Span<int> s`
- **延后理由**：`scoped` 是 C# 在缺乏 lifetime 系统下补救 `ref struct` / `ref return` 不安全性的妥协；z42 砍掉 ref local/return/field/struct 后，`scoped` 自然不需要（ref 永远不离开调用栈帧）
- **重启触发**：D1 / D2 / D4 任一落地（与 lifetime 系统一同评估）—— 实际上推荐永不

### D6：`ref readonly`（任何位置）

- **形态**：`ref readonly T` 参数 / 返回 / 局部 / 字段
- **延后理由**：参数位由 `in` 取代（修正 C# `in` / `ref readonly` 双形态冗余）；其他位置（return / 局部）在没有 `mut` 体系下 holder-side "我不写" 承诺意义弱
- **重启触发**：D1 / D2 落地后用户实际场景需要 holder-side 只读契约 —— 推荐永不

---

## 与其他特性的关系

- **`mut` 修饰符**：永不引入（feedback_no_mut_modifier）。`in` 是 callee 端 API 契约，不是 caller 端可变性标注，与 mut 体系正交
- **`unsafe` 关键字**：暂留至后续讨论（feedback_no_unsafe_keyword）；ref/out/in 的安全性由结构性约束（lvalue + blittable + 4 交互规则）保证，不依赖 unsafe 包裹
- **生命周期标注**：永不引入（feedback_leak_via_diagnostics）。z42 通过砍掉 ref 在栈帧外的扩展位置（local / return / field / struct）天然避免需要 lifetime 系统
- **interop / FFI**：`ref T ↔ *mut T` / `out T ↔ *mut T` / `in T ↔ *const T` ABI 映射见 [interop.md](interop.md) §6

## 与 C# 的对照

| C# 特性 | z42 | 处理 |
|---|---|---|
| `ref T` 参数 | ✓ 保留 | 一致语义 |
| `out T` 参数 | ✓ 保留 | 一致语义（含 `out var x` 内联声明）|
| `in T` 参数 | ✓ 保留，**callsite 强制写** | 修正 C# `in` 可省的不一致 |
| `ref readonly T` 参数 | ✗ 砍 | 由 `in` 取代 |
| `scoped` | ✗ 砍 | ref 永远不离开栈帧，不需要 |
| `ref T` 局部变量 | ✗ 延后（D1）| 等 user struct 落地 |
| `ref T` 返回 | ✗ 延后（D2）| 等 D1 配套 |
| `ref T` 字段 | ✗ 砍 | 跟随 ref struct |
| `ref struct` 类型 | ✗ 砍 | GC 语言不需要 |
| `where T : allows ref struct` | ✗ 不需要 | 自动消失 |
| callsite `in` 可省 | ✗ 改为强制写 | 语法统一 |

## 错误码（占位）

当前所有 ref/out/in 相关 TypeChecker 诊断使用 `DiagnosticCodes.TypeMismatch` 占位。在 follow-up spec `impl-ref-out-in-runtime` 落地时统一分配 E04xx 段位（避免占位 → 段位的二次迁移）。具体映射在 follow-up spec 的 `error-codes.md` 同步条目中给出。

---

## 历史

- **2026-05-04**：spec `define-ref-out-in-parameters` 起草（Phase 1-8 含 IR + VM）
- **2026-05-05**：拆分为 `define-ref-out-in-parameters-typecheck`（编译期验证，本 spec 来源）+ `impl-ref-out-in-runtime`（运行时实施，后续 spec）
