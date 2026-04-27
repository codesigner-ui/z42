# Tasks: fix-static-field-access

> 状态：🟢 已完成 | 类型：fix | 创建：2026-04-27 | 完成：2026-04-27

**变更说明：** 修复静态字段访问 `Class.Field`（如 `Math.PI` / `Math.E` / 计划中的 `Path.Separator`）在用户代码中调用返回 `null` 的问题。

**根因（两处）：**

1. **编译器命名空间错误** ([IrGen.cs:303-309](src/compiler/z42.Semantics/Codegen/IrGen.cs#L303))：
   `TryGetStaticFieldKey` 返回 `${QualifyName(className)}.{fieldName}`，而 `QualifyName` 用的是**当前模块**的命名空间，不是导入类的命名空间。`Math.PI` 在用户 `main` 模块下编译为 `static.get @Math.PI`（无 `Std.Math` 前缀），但 zpkg 把字段注册为 `Std.Math.Math.PI`。Key 不匹配，VM HashMap 找不到 → 返回 `Value::Null`。

2. **VM 只跑主模块的 __static_init__** ([interp/mod.rs:76-89](src/runtime/src/interp/mod.rs#L76))：
   `run_with_static_init` 只查 `{module.name}.__static_init__`。但 `module.name` 是用户主程序的命名空间，导入的 stdlib zpkg（如 `z42.math.zpkg`）虽然把自己的 `Std.Math.__static_init__` 函数 link 进来，却从不被调用。即使编译器命名空间对了，PI 字段也从未被赋值过。

**对照表：**

| 方面 | 修前 | 修后 |
|---|---|---|
| 编译器 emit | `static.get @Math.PI` | `static.get @Std.Math.Math.PI` |
| VM 启动序列 | run `__static_init__` (main only) → entry | run **all** `*.__static_init__` (sorted) → entry |
| `Math.PI` 运行结果 | `null` | `3.141592653589793` |

## Tasks

- [x] 1.1 `IrGen.TryGetStaticFieldKey`：使用 `QualifyClassName(className)` 而非 `QualifyName(className)`，自动选 imported namespace
- [x] 2.1 `interp/mod.rs::run_with_static_init`：扫描 module.functions 找所有 `.__static_init__` 后缀，按名字排序逐一调用（不仅主模块）
- [x] 2.2 `jit/mod.rs::run_with_static_init`：同样的修复（与 interp 对称）
- [x] 3.1 新增 golden test `src/runtime/tests/golden/run/17_math_constants/`（source.z42 + expected_output.txt）覆盖 `Math.PI` / `Math.E` / `Math.Tau`
- [x] 4.1 `dotnet build` / `cargo build` / `dotnet test` / `test-vm.sh` 全绿
- [x] 5.1 commit + push + 归档

## 备注

- **Path.Separator 不在本变更范围内**：等本 fix 落地后，独立 spec 给 z42.io.Path 加常量字段
- **不动 static.set**：当前 stdlib 没有用户级别的 `Class.Field = value` 赋值（只有内部 init 用 `static.set`，路径不经过 TryGetStaticFieldKey）
- **`__static_init__` 顺序**：现实场景中 init 函数互不依赖（设常量），按命名空间字母序遍历足够。如果未来出现依赖关系（不太可能 —— 都是常量），再加显式拓扑排序
- **实施中发现第 3 个 sub-bug**：`extract_import_namespaces_from_module` (`metadata/loader.rs`) 只扫 Call/Builtin，不扫 StaticGet/StaticSet → user code 只用 `Math.PI` 时 zbc 里 import_namespaces 不含 Std.Math → lazy_loader 不知道有依赖 → 即使我修了 1+2 还是 null。第三处一并修
- **副作用**：interp 模式下，所有声明的 stdlib zpkg 现在会被 eagerly 加载（不再纯 lazy）以确保 `__static_init__` 跑到。当前 stdlib 规模 (~50KB / 5 包) 下成本可忽略
- **`name` 字段从 JitModule 删除**：之前用于 `format!("{name}.__static_init__")` 找主模块 init，现在改为扫所有 `*.__static_init__`，字段不再需要
