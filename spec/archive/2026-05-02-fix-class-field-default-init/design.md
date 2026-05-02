# Design: 修复 class field 默认初始化

## Architecture

```
┌──────────── 编译期（C#） ─────────────┐  ┌─── 运行期（Rust） ───┐
│                                       │  │                       │
│  Parser ──► FieldDecl.Initializer     │  │  Loader ──► FieldSlot │
│                │                      │  │              .type_tag │
│                ▼                      │  │              ▼         │
│       TypeChecker (P1)                │  │  ObjNew (P3)           │
│       BindFieldInits                  │  │  slots = field.iter()  │
│       → BoundInstanceInits            │  │    .map(default_for)   │
│                │                      │  │              │         │
│                ▼                      │  │              ▼         │
│       Codegen (P2)                    │  │  ctor() ──► FieldSet   │
│       1) inject field-init in every   │  │   注入位置 (P2)        │
│          explicit ctor                │  │                       │
│       2) synthesize implicit ctor     │  │                       │
│          when needed                  │  │                       │
│                │                      │  │                       │
│                ▼                      │  │                       │
│         IR (FieldSet × N)             │  │                       │
└───────────────────────────────────────┘  └───────────────────────┘
```

P2（注入 init）与 P3（slot type defaults）互补：
- P3 兜底"字段无 init"的默认值（一次性、不依赖 ctor 是否存在）
- P2 处理"字段有 init"时把表达式求值后写入字段
- 两者职责清晰、不重叠

## Decisions

### Decision 1: 字段类型默认值在 VM 层（P3）vs Codegen 层（在 ctor 中显式发射所有字段的 type-default init）

**问题**：`int n;`（无显式 init）应该变 `0`。两种实现路径。

**选项 A — VM 层**：`ObjNew` 按 `FieldSlot.type_tag` 选默认值；Codegen 仅处理"显式 init"字段。

- 优点：每个 class 只为有 init 的字段发射代码（IR 量小）；implicit-ctor-skip 路径仍可工作（无 init 字段直接拿到默认值）；与 CLR/JVM 的"分配即默认"模型一致。
- 缺点：FieldSlot 多一个 `type_tag` 字段（已在 zbc，仅 plumb）；interp + JIT 两处同步。

**选项 B — Codegen 层**：每个 ctor 入口为**所有**字段（含无 init 的）发射 `field_set this <name> <type-default>`；VM ObjNew 不变。

- 优点：VM 不需要类型感知；FieldSlot 不变。
- 缺点：每个 ctor 多 N 条 FieldSet 指令（典型 class 5–10 字段）；字段无 init 的 class 也必须合成 ctor（implicit-ctor-skip 路径失效）；运行时浪费指令。

**决定**：**选 A**。z42 zbc 已经有 `FieldDesc.type_tag`，plumb 一下即可；运行时一次性默认值是最经济的；implicit-ctor-skip 路径继续工作。

### Decision 2: 字段 init 在 ctor 中的注入位置

**问题**：相对 base ctor call 与用户 ctor body，字段 init 应该在哪里执行？

**选项**：
- A（C# 语义）：字段 init → base ctor → 用户 body
- B（z42 简化版）：base ctor → 字段 init → 用户 body
- C：base ctor → 用户 body → 字段 init

**决定**：**选 B**。理由：

1. C# 语义（A）需要在 base ctor 调用前读取/写入 `this.<field>`，但此时父类还没初始化、`this` 处于"半初始化"状态 —— z42 没有 C# 那种 IL 级别的"未初始化对象"概念，强行模仿会让 P2 的代码生成大幅复杂化。
2. B 的语义清晰：base ctor 完成 → 子类字段 init → 子类用户 body 可以覆写。这与"父类先就位、子类再叠加"的心智模型一致。
3. C 的问题是用户在 body 里读字段会读到 Null/默认值，违反直觉。

**取舍**：失去 C# 严格兼容性。但 z42 不是 C# 的字节兼容子集；本决策记入 `docs/design/class.md`，作为 z42 显式的语义偏离点。

### Decision 3: 隐式 ctor 合成时机

**问题**：什么情况下编译器要合成 `<ClassName>.<ClassName>` 隐式 ctor？

**规则**：

| 类有显式 ctor | 类有字段（含 init） | 任一字段有显式 init | 行为 |
|---------------|---------------------|---------------------|------|
| ✓ | – | – | 不合成；每个显式 ctor 各自注入字段 init |
| ✗ | ✗ | – | 不合成（与现状一致）|
| ✗ | ✓ | ✗ | 不合成（VM ObjNew 的 P3 默认值即可）|
| ✗ | ✓ | ✓ | **合成隐式 ctor**：base call（如有）+ 字段 init |

合成的 IR 函数命名为 `<QualifiedClassName>.<SimpleName>`，与显式无参 ctor 同名。`ResolveCtorName` 行为不变（已在"无显式 ctor"路径返回 className），合成后 VM 通过 `module.func_index` 能正常找到。

### Decision 4: 隐式 ctor 与无零参父类 ctor 的冲突

**问题**：`class P { P(int x) {} } class C : P { int n = 5; }`，C 需要合成隐式 ctor 但 P 没有零参 ctor。

**决定**：**TypeChecker 报错**，要求用户为 C 显式声明 ctor 并显式 `: base(...)`。诊断码新增 `Z0922 NeedsExplicitConstructor`（待最终落 `DiagnosticCodes`），消息：

> `class 'C' has field initializers but its base class 'P' has no parameterless constructor; declare an explicit constructor with `: base(...)`.`

与 C# 同行为。

### Decision 5: type_tag → default Value 的统一函数位置

**问题**：interp `exec_instr.rs` 和 JIT `helpers_object.rs` 都需要这个函数。

**决定**：放在 `src/runtime/src/metadata/types.rs`，作为 `pub fn default_value_for(type_tag: &str) -> Value`，紧挨 `FieldSlot` 定义。两端都 `use crate::metadata::default_value_for;`。

实现要点：
```rust
pub fn default_value_for(type_tag: &str) -> Value {
    match type_tag {
        "i8" | "i16" | "i32" | "i64" | "u8" | "u16" | "u32" | "u64" | "isize" | "usize" | "char" => Value::I64(0),
        "f32" | "f64" => Value::F64(0.0),
        "bool" => Value::Bool(false),
        // str / class types / array / option / unknown → Null
        _ => Value::Null,
    }
}
```

`char` 走 `I64(0)` 与现有 char-as-i64 表示一致（runtime/`types.rs` 没有专门 `Value::Char`）。

### Decision 6: SemanticModel 字段命名

**问题**：当前是 `BoundStaticInits`。新增什么名字？

**决定**：保留 `BoundStaticInits`，新增 `BoundInstanceInits`。两者并列、命名对称、含义清晰。`Bound*` 前缀保持现有约定。

## Implementation Notes

### P1（TypeChecker）

`TypeChecker.cs:404` 的 `BindStaticFieldInits` 重构为：

```csharp
private void BindFieldInits(CompilationUnit cu) {
    foreach (var cls in cu.Classes) {
        var fields = cls.Fields.Where(f => f.Initializer != null).ToList();
        if (fields.Count == 0) continue;
        var env = new TypeEnv(_symbols.Functions, _symbols.Classes, _symbols.ImportedClassNames);
        var scope = env.WithClass(cls.Name);
        foreach (var field in fields) {
            // expected type = field type
            var expected = ResolveType(field.Type);
            var bound = BindExpr(field.Initializer!, scope, expected);
            RequireAssignable(expected, bound.Type, field.Span, $"field initializer of `{field.Name}`");
            if (field.IsStatic) _boundStaticInits[field] = bound;
            else                _boundInstanceInits[field] = bound;
        }
    }
}
```

`SemanticModel` 增加 `IReadOnlyDictionary<FieldDecl, BoundExpr> BoundInstanceInits` 字段 + 构造参数 + `BoundInstanceInits = boundInstanceInits;`。

> **注意**：instance scope 内的 init 表达式可以引用其他静态成员（与 C# 一致）但不能引用 `this`。当前 `WithClass` 的 scope 默认允许 `this` —— 需要确认 instance field init 阶段的 scope 处理（可能需要新增 `WithoutThis()` 或在 BindIdent 检测）。如果当前实现已经允许（C# 也允许字段 init 写 `this.foo` 但语义上不推荐），先按现有 scope 行为推进，后续观察单元测试。

### P2（Codegen）

`FunctionEmitter.EmitMethod` 在已有 ctor 分支（`if (isCtor && method.BaseCtorArgs ...)`）之后增加：

```csharp
if (isCtor) {
    // 注入实例字段 init（base ctor call 之后、用户 body 之前）
    EmitInstanceFieldInits(className);
}
```

`EmitInstanceFieldInits` 遍历 `cls.Fields.Where(f => !f.IsStatic && f.Initializer != null)` 按声明顺序：

```csharp
foreach (var field in instanceFieldsWithInit) {
    if (!_ctx.SemanticModel.BoundInstanceInits.TryGetValue(field, out var initExpr)) continue;
    var valReg = EmitExpr(initExpr);
    Emit(new FieldSetInstr(new TypedReg(0, IrType.Ref), field.Name, valReg));
}
```

**隐式 ctor 合成**入口在 `IrGen.cs`（class 遍历层）：

```csharp
// 在每个 class 的方法发射后：
bool hasExplicitCtor = cls.Methods.Any(m => m.Name == cls.Name);
bool hasInstanceInit = cls.Fields.Any(f => !f.IsStatic && f.Initializer != null);
bool needSynthCtor = !hasExplicitCtor && hasInstanceInit;
if (needSynthCtor) {
    // 验证 Decision 4：base 类必须有零参 ctor 或无 base
    EnsureBaseHasParameterlessCtorOrFail(cls);
    var synthCtor = MakeSynthImplicitCtor(cls);
    irFunctions.Add(emitter.EmitMethod(cls.Name, synthCtor, ...));
}
```

`MakeSynthImplicitCtor` 构造一个空 body 的 `FunctionDecl`，name = `cls.Name`，无参，IsStatic=false。`EmitMethod` 遇到这个空 body 会跑：base ctor call（如有）→ `EmitInstanceFieldInits` → 空用户 body → ret null。

### P3（VM）

**`metadata/types.rs`**：

```rust
#[derive(Debug, Clone)]
pub struct FieldSlot {
    pub name: String,
    pub type_tag: String,   // ← NEW
}

pub fn default_value_for(type_tag: &str) -> Value { /* Decision 5 */ }
```

**`metadata/loader.rs:322`**：

```rust
fields.push(FieldSlot {
    name: f.name.clone(),
    type_tag: f.type_tag.clone(),
});
```

**`corelib/object.rs`**：

```rust
FieldSlot { name: "__name".to_string(), type_tag: "str".to_string() },
FieldSlot { name: "__fullName".to_string(), type_tag: "str".to_string() },
```

**`interp/exec_instr.rs:305`** 与 **`jit/helpers_object.rs:200`**：

```rust
let slots: Vec<Value> = type_desc.fields.iter()
    .map(|f| crate::metadata::default_value_for(&f.type_tag))
    .collect();
```

## Testing Strategy

### 单元测试（C#）

`src/compiler/z42.Tests/ClassFieldInitTypeCheckTests.cs`（NEW）：

1. `instance_field_init_binds_correct_type` — `class C { int n = 5; }` → `BoundInstanceInits` 含 `n`，类型为 int
2. `instance_field_init_type_mismatch_reports_error` — `class C { int n = "x"; }` → 报 TypeMismatch
3. `static_and_instance_init_coexist` — `class C { static int s = 1; int n = 2; }` → 两个 dict 各 1 条
4. `field_with_no_init_not_in_dict` — `class C { int n; }` → 都不在
5. `parent_no_zero_arg_ctor_blocks_implicit_ctor_synthesis` — Decision 4 报错

### Golden test（端到端）

`src/runtime/tests/golden/run/class_field_default_init/source.z42`（NEW）：

```z42
namespace Demo;
using Std.IO;

class A { int x = 1; string s = "hello"; bool b = true; }

class B { int n; string s; bool flag; }

class C { int n = 5; C(int x) { this.n = x; } }

class P { int p = 100; }
class Q : P { int q = 200; }

void Main() {
    var a = new A();
    Console.WriteLine($"A: x={a.x} s={a.s} b={a.b}");      // A: x=1 s=hello b=true

    var b = new B();
    Console.WriteLine($"B: n={b.n} s={b.s} flag={b.flag}"); // B: n=0 s= flag=false
    // 注意：s 是 Null，$"" 插值需要是空串或 "null" — 看现有插值行为决定 expected

    var c1 = new C(99);
    Console.WriteLine($"C(99): n={c1.n}");                  // C(99): n=99（用户 body 覆写 init）

    var q = new Q();
    Console.WriteLine($"Q: p={q.p} q={q.q}");                // Q: p=100 q=200
}
```

`expected_output.txt` 按上述注释填写（先确认 `s=null` 时的插值行为再定）。

### 验证命令

```bash
dotnet build src/compiler/z42.slnx
cargo build --manifest-path src/runtime/Cargo.toml
dotnet test src/compiler/z42.Tests/z42.Tests.csproj   # 应增加 5 个测试
./scripts/regen-golden-tests.sh                        # 重新生成 source.zbc（含新 golden）
./scripts/test-vm.sh                                   # interp + jit 全绿
```

### 回归覆盖

- 既有 class goldens（07/51/110/111/112 等）必须继续通过 —— 它们都是显式 ctor + 无字段 init，不会被新行为影响
- corelib `Object` 的 `__name` / `__fullName` 之前是 Null，现在变成 Null（type_tag = "str" 走 fallback Null）—— 行为不变 ✓

## Risks & Mitigations

| 风险 | 缓解 |
|------|------|
| `s = null` 字段被 `$""` 插值时输出 `"null"` 字面量影响 expected_output 编写 | 先在 spec scenario 里只测 `b.s == null` 的等价性，不放进 println |
| FieldSlot 加字段后所有构造点必须更新（编译器静态检查会兜底） | 用 Rust 的非 default 字段，编译器报错时按 grep 顺序补齐 |
| 隐式 ctor 名字和某个用户函数同名冲突 | 用户的 `Box.Box` 已经被 `ResolveCtorName` 占用；冲突场景与现有显式 ctor 同名等价，不引入新冲突 |
| 现有 golden 因 `int n;` 行为变化（Null → 0）出回归 | 既有 goldens 全部使用显式 ctor + `this.n = ...`，不依赖默认值；扫一遍确认无问题 |
| JIT helper 与 interp 行为漂移 | 共用 `default_value_for` 函数，单一来源 |
