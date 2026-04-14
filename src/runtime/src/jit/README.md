# jit — Cranelift JIT backend

## 职责
将 z42 SSA 字节码编译为原生机器码执行。所有值操作通过 `extern "C"` helper 函数实现，Cranelift 只生成控制流（分支、跳转、函数入口/出口）。

## 核心文件
| 文件 | 职责 |
|------|------|
| `mod.rs` | 公开 API（`compile_module`、`JitModule::run`）、helper 符号注册 |
| `frame.rs` | `JitFrame`（寄存器文件 + 变量槽）、`JitModuleCtx`（编译后函数表 + 字符串池） |
| `translate.rs` | `HelperIds` 声明 + `translate_function`（z42 指令 → Cranelift IR） |
| `helpers.rs` | 共享状态（异常槽、静态字段）、通用数值 helper、类型别名 |
| `helpers_arith.rs` | 算术、比较、逻辑、一元、位运算 helper |
| `helpers_mem.rs` | 常量加载、拷贝、变量槽、字符串操作、控制流辅助 |
| `helpers_object.rs` | 函数调用、数组操作、对象操作、类型检查、静态字段访问 |

## 入口点
- `jit::compile_module(module)` → `JitModule`
- `JitModule::run(entry_name)` → 执行入口函数

## 依赖关系
- 依赖 `corelib` 的 `exec_builtin` 和 `value_to_str`
- 依赖 `metadata` 的 `Module`、`Function`、`Instruction`、`Value` 等类型
- 外部依赖：`cranelift-codegen`、`cranelift-frontend`、`cranelift-jit`、`cranelift-module`
