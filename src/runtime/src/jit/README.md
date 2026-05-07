# jit — Cranelift JIT backend

## 职责
将 z42 SSA 字节码编译为原生机器码执行。所有值操作通过 `extern "C"` helper 函数实现，Cranelift 只生成控制流（分支、跳转、函数入口/出口）。

## 核心文件
| 文件 | 职责 |
|------|------|
| `mod.rs` | 公开 API（`compile_module`、`JitModule::run`）；委托 helper 注册到 `helpers::registry` |
| `frame.rs` | `JitFrame`（寄存器文件 + 变量槽）、`JitModuleCtx`（编译后函数表 + 字符串池） |
| `translate.rs` | `translate_function`（z42 指令 → Cranelift IR）；`HelperIds` 重导出自 `helpers` |
| `helpers/` | `extern "C"` helper 集合（按指令类别拆分；与 `interp/exec_*.rs` 命名对称） |

### `helpers/` 子目录
| 文件 | 职责 |
|------|------|
| `mod.rs` | 共享工具（`vm_ctx_ref` / `set_exception` / 数值 helper / `JitFn`）+ `VM_JIT_INTERFACE_VERSION` |
| `registry.rs` | **中央 helper 注册表**：`HelperIds` 结构、`register_symbols`（→ JITBuilder）、`declare_imports`（→ JITModule） |
| `value.rs` | 常量加载、Copy、字符串、`get_bool` / `set_ret` |
| `arith.rs` | 算术、比较、逻辑、一元、位运算 |
| `control.rs` | `throw` / `install_catch` / `match_catch_type` |
| `call.rs` | `jit_call`、`jit_builtin` |
| `array.rs` | 数组分配、元素访问、长度 |
| `object.rs` | 对象分配、字段访问、类型检查、静态字段、`default(T)` |
| `vcall.rs` | 虚调用（独立文件，含 primitive-as-struct + 懒加载 fallback） |
| `closure.rs` | L3 闭包：`load_fn` / `mk_clos` / `call_indirect` / `load_fn_cached` |

## 入口点
- `jit::compile_module(module)` → `JitModule`
- `JitModule::run(entry_name)` → 执行入口函数

## Helper 边界（formalize-jit-vm-interface, 2026-05-07）

加新 helper 改 **2 处**:
1. 对应 `helpers/<category>.rs` 添加函数定义
2. `helpers/registry.rs` 添加 `register_symbols` 中的 `reg!()` 行 + `HelperIds` 字段 + `declare_imports` 中的 `decl!()` 行

详见 [docs/design/vm-architecture.md](../../../../docs/design/vm-architecture.md) "JIT/EE helper 边界"。

## 依赖关系
- 依赖 `corelib` 的 `exec_builtin` 和 `value_to_str`
- 依赖 `metadata` 的 `Module`、`Function`、`Instruction`、`Value` 等类型
- 依赖 `interp::primitive_class_name`（vcall 共享判定）+ `interp::dispatch::is_subclass_or_eq_td`（control 共享）
- 外部依赖：`cranelift-codegen`、`cranelift-frontend`、`cranelift-jit`、`cranelift-module`
