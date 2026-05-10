# Design: add-native-internalcall

## Architecture

```
z42 源码                编译器 pipeline              VM
─────────────────       ─────────────────────────    ──────────────────────────
[Native("__println")]   Lexer: extern → token        启动:
public static extern    Parser: NativeAttribute       resolve_libs_dir()
  void WriteLine(str);    + IsExtern=true               → load z42.core.zpkg
                         NativeTable: 验证 name        → merge_modules([core])
                           + param count             加载用户 artifact:
                         Codegen: 注入               → read dependencies[]
                           Builtin{name, args}         → load dep zpkgs
                           + Ret                       → merge_modules([core, deps, user])
                                                    执行:
                                                      Builtin{name:"__println"}
                                                      → builtins["__println"](args)
                                                      → fn println(&[Value])
```

## Decisions

### Decision 1: 语法 — `extern` 作为方法修饰符

**问题：** 如何区分 "有 z42 函数体" 和 "实现在 VM 内部" 的方法？

**选项：**
- A: 无 body + 分号（当前 stdlib 文件已有此形式）
- B: `extern` 修饰符 + 无 body（用户选定）

**决定：** 选 B。`extern` 明确表示实现在语言运行时外部，和 `abstract`（等子类实现）语义区分清晰。

规则：
1. `extern` 方法必须携带 `[Native("__name")]` 属性，否则报 Z0092
2. `[Native]` 属性必须出现在 `extern` 方法上，否则报 Z0093
3. `extern` 方法无函数体，分号结尾
4. `extern` 方法隐式 `abstract` 语义禁止（extern ≠ abstract）

### Decision 2: Dispatch — 全局静态 HashMap（`OnceLock`，interp + JIT 共用同一入口）

**问题：** VM 如何从 intrinsic name 找到 Rust 函数？

**关键约束（来自现有代码）：**
JIT 的 `jit_builtin`（`jit/helpers.rs:552`）已经在调用
`crate::interp::builtins::exec_builtin(name, &args)`。
该调用是 `extern "C"` 自由函数，没有 `&self`，不能访问 `Vm` struct 字段。
因此 HashMap **不能放在 `Vm` struct 里**，必须是全局静态，两条路径自动复用。

**选项：**
- A: `match name { "__println" => ... }`（当前，Rust 编译为跳转表）
- B: `OnceLock<HashMap<&'static str, NativeFn>>`，首次调用时初始化，零同步开销后续
- C: 整数 ID + 数组索引（最优，留 JIT 阶段）

**决定：** 选 B。
- `exec_builtin` 的公开签名 `fn(name: &str, args: &[Value]) -> Result<Value>` **不变**
  → interp、JIT `jit_builtin` helper 均无需修改调用点
- HashMap 内部替换 `match`，对外完全透明
- JIT 阶段再迁移为整数 ID，届时同步更新 IR 格式

```rust
// interp/builtins.rs
use std::sync::OnceLock;

pub type NativeFn = fn(&[Value]) -> Result<Value>;

static DISPATCH: OnceLock<HashMap<&'static str, NativeFn>> = OnceLock::new();

fn dispatch_table() -> &'static HashMap<&'static str, NativeFn> {
    DISPATCH.get_or_init(|| {
        let mut m = HashMap::new();
        m.insert("__println", println_fn as NativeFn);
        m.insert("__print",   print_fn   as NativeFn);
        // ...
        m
    })
}

/// 公开 API — interp 和 JIT jit_builtin 均调用此函数，签名不变
pub fn exec_builtin(name: &str, args: &[Value]) -> Result<Value> {
    dispatch_table()
        .get(name)
        .ok_or_else(|| anyhow::anyhow!("unknown builtin `{name}`"))?
        (args)
}
```

**代码复用路径（不需要任何额外修改）：**
```
interp:  exec_instr(Builtin{name}) → exec_builtin(name, args)  ─┐
                                                                  ├─ dispatch_table()[name](args)
JIT:     jit_builtin extern"C"    → exec_builtin(name, args)  ─┘
```

### Decision 3: 参数传递 — `&[Value]` 借用切片

**问题：** native 函数接收什么类型的参数？

**选项：**
- A: `Vec<Value>`（当前，clone 每个 Value）
- B: `&[Value]`（借用 collect 后的 Vec，Value 内部按引用访问）

**决定：** 选 B，配合 `Value::as_str() -> &str` 等借用提取方法，string 参数零拷贝。
`Vec<Value>` 仍在 interpreter 内部用于 collect_args；NativeFn 只看到 `&[Value]` slice。

```rust
// Value 新增借用提取方法
impl Value {
    pub fn as_str(&self)  -> Result<&str>  { ... }
    pub fn as_i64(&self)  -> Result<i64>   { ... }
    pub fn as_f64(&self)  -> Result<f64>   { ... }
    pub fn as_bool(&self) -> Result<bool>  { ... }
}

// native 函数：string 参数不产生堆分配
fn println(args: &[Value]) -> Result<Value> {
    let s = args[0].as_str()?;
    println!("{s}");
    Ok(Value::Null)
}
```

### Decision 3b: `Vm` struct 不持有 builtins 字段

由于 HashMap 已是全局静态，`Vm::new` 签名保持 `new(module: Module, default_mode: ExecMode)`，
不新增 `builtins` 参数。`vm.rs` 无需修改。

### Decision 4: stdlib 加载 — z42.core 无条件加载，其余按 dependencies

**问题：** VM 何时加载哪些 stdlib zpkg？

**方案：**
- z42.core：VM 启动时无条件加载（类比 .NET System.Object，总是存在）
- 其他 stdlib 包（z42.io, z42.math, ...）：读取用户 artifact 的 `dependencies` 字段，按需加载
- 加载顺序：`[z42.core] + [dep1, dep2, ...] + user_module`，调用 `merge_modules`
- libs_dir 已有 `resolve_libs_dir()` 实现（Z42_LIBS → binary-dir/../libs → cwd/artifacts/z42/libs）

### Decision 5: NativeTable 的作用域

NativeTable 放在 `z42.IR` 项目中（和 IrModule 同级），同时服务编译器验证和未来工具（disasm 等）。

```csharp
// z42.IR/NativeTable.cs
public record NativeEntry(string Name, int ParamCount);

public static class NativeTable {
    public static readonly IReadOnlyDictionary<string, NativeEntry> All = new Dictionary<string, NativeEntry> {
        ["__println"]       = new("__println",       1),
        ["__print"]         = new("__print",         1),
        ["__readline"]      = new("__readline",      0),
        ["__str_len"]       = new("__str_len",       1),
        ["__str_substring"] = new("__str_substring", 3),
        // ... 完整列表见 builtins.rs
    };
}
```

## Implementation Notes

### Lexer
`TokenDefs.cs` 的 `Keywords` 字典加一行：`{ "extern", TokenKind.Extern }`

### Parser 改造点

**属性收集**：顶层循环和类 body 循环目前 `SkipAttribute` 后 `continue`，导致属性丢失。
改为：先调用 `TryParseNativeAttribute(ref cursor)` 取出 intrinsic name，存入局部变量，
下一个方法声明时作为参数传入 `ParseFunctionDecl`。

```csharp
// TopLevelParser class body loop（伪代码）
string? pendingNative = null;
while (...) {
    if (cursor.Current.Kind == TokenKind.LBracket) {
        pendingNative = TryParseNativeAttribute(ref cursor);
        continue;
    }
    if (IsFieldDecl(cursor)) { pendingNative = null; /* 属性不用于字段 */ ... }
    else { methods.Add(ParseFunctionDecl(ref cursor, feat, vis, pendingNative)); pendingNative = null; }
}
```

`ParseNonVisibilityModifiers` 返回元组增加 `isExtern`（第 6 项）。

### Codegen

```csharp
// IrGen.cs — EmitFunctionBody
if (func.IsExtern && func.NativeIntrinsic != null) {
    var args = func.Params.Select((_, i) => i).ToList();
    var dst  = ctx.Fresh();
    ctx.Emit(new BuiltinInstr(dst, func.NativeIntrinsic, args));
    ctx.Emit(new RetInstr(func.ReturnType is VoidType ? -1 : dst));
    return;
}
```

### VM startup sequence

```rust
// main.rs
let libs_dir = resolve_libs_dir();
let mut modules = vec![];

// 1. z42.core 无条件加载
if let Some(core_path) = libs_dir.as_ref().map(|d| d.join("z42.core.zpkg")) {
    if core_path.exists() {
        modules.push(load_artifact(core_path.to_str().unwrap())?);
    }
}

// 2. 加载用户 artifact，读取 dependencies
let user = load_artifact(&args.file)?;
for dep in &user.module.dependencies {
    let dep_path = /* resolve relative to libs_dir */;
    modules.push(load_artifact(&dep_path)?);
}

// 3. 合并
modules.push(user);
let merged = merge_modules(modules);
Vm::new(merged, builtins).run(entry)
```

## Testing Strategy

- 单元测试：`TypeCheckerTests` 新增 extern 验证场景（unknown intrinsic, param count mismatch）
- Golden test：`z42.io.Console.WriteLine` 编译产出 `builtin "__println" [%0]` + `ret`
- 集成测试（VM）：`examples/hello_stdlib.z42` → `using z42.io; Console.WriteLine("hello")` 端到端运行
- 回归：`dotnet test`（全部现有测试保持绿）、`./scripts/test-vm.sh`（全部现有 VM 测试保持绿）
