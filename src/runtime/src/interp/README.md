# interp — Tree-walking bytecode interpreter

## 职责
执行 IR 指令的解释器后端。逐块遍历、逐指令 dispatch，支持异常处理和虚方法分发。

## 核心文件
| 文件 | 职责 |
|------|------|
| `mod.rs` | 公开 API（`run`/`run_with_static_init`）、`Frame`、执行循环、异常表查找 |
| `exec_instr.rs` | 单指令 dispatch（一个大 match）：常量、算术、比较、逻辑、数组、对象、调用 |
| `dispatch.rs` | 对象分发辅助：vtable 解析、ToString 协议、子类检查、静态字段、fallback TypeDesc |
| `ops.rs` | 寄存器级辅助：`int_binop`、`numeric_lt`、`collect_args`、`bool_val`、`str_val` |

## 入口点
- `interp::run(module, func, args)` — 执行单个函数
- `interp::run_with_static_init(module, func)` — 初始化静态字段后执行

## 依赖关系
- 依赖 `corelib` 模块的 `exec_builtin` 和 `value_to_str`
- 依赖 `metadata` 模块的 `Module`、`Function`、`Instruction`、`Value` 等类型
