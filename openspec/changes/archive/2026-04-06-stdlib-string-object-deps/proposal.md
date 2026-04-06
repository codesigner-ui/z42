# Proposal: stdlib-string-object-deps

## Why
z42.core 中缺少 String 类型定义，现有 VM 测试直接调用 `__str_*` 内建函数，绕过了标准库抽象层。
同时 `.zbc` 文件的 `imports` 字段目前被忽略，VM 无法通过 zbc 引用自动加载依赖 zpkg。
Object.z42 在当前 zpkg 中的编译产物是错误的 stub（GetHashCode/Equals 返回常量而非调用 native builtin）。

## What Changes
- 编译器：为方法重载添加 arity-based 支持（支持同名不同参数数量）
- z42.core：新增 `String.z42`，所有方法绑定到现有 `__str_*` builtins
- z42.core：修复 `Object.z42` 编译 stub，重新构建 zpkg
- VM 测试：`06_string_builtins`、`14_string_methods`、`44_string_static_methods` 改用 `call z42.core.String.*`
- VM 运行时：`load_zbc` 从 `imports` 字段提取命名空间，返回给 main.rs 加载对应 zpkg
- 测试框架：`test-vm.sh` 支持 `source.zbc` 格式（逐步替换 `.z42ir.json`）
- 新增 VM golden 测试：Object 协议方法（GetHashCode、ReferenceEquals、Equals、GetType）

## Scope（允许改动的文件/模块）
| 文件/模块 | 变更类型 | 说明 |
|-----------|---------|------|
| `src/compiler/z42.Compiler/TypeCheck/Z42Type.cs` | modify | Methods 支持多重载 |
| `src/compiler/z42.Compiler/TypeCheck/TypeChecker.cs` | modify | 注册方法时支持列表 |
| `src/compiler/z42.Compiler/TypeCheck/TypeChecker.Exprs.cs` | modify | 按 arity 选择重载 |
| `src/compiler/z42.Compiler/Codegen/IrGen.cs` | modify | 重载方法加 `$N` 后缀 |
| `src/libraries/z42.core/src/String.z42` | add | String 类定义 |
| `src/libraries/z42.core/src/Object.z42` | modify | 修复 ToString/Equals |
| `src/libraries/z42.core/dist/z42.core.zpkg` | rebuild | 重新编译标准库 |
| `src/runtime/src/metadata/loader.rs` | modify | load_zbc 提取 import namespaces |
| `src/runtime/src/main.rs` | modify | 加载 import_namespaces 对应的 zpkg |
| `src/runtime/tests/golden/run/06_string_builtins/` | modify | 更新 IR 使用 stdlib call |
| `src/runtime/tests/golden/run/14_string_methods/` | modify | 更新 IR 使用 stdlib call |
| `src/runtime/tests/golden/run/44_string_static_methods/` | modify | 更新 IR 使用 stdlib call |
| `src/runtime/tests/golden/run/46_object_protocol/` | add | 新增 Object 测试 |
| `scripts/test-vm.sh` | modify | 支持 source.zbc 格式 |

## Out of Scope
- L2/L3 特性（类型参数化重载、返回值类型重载）
- 二进制 zpkg/zbc 格式
- JIT/AOT 路径
- String 的 Length 属性（已有 `__len` builtin，属性支持另议）

## Open Questions
- [x] test-vm.sh：支持 `.zbc` → 是（新增，与 `.z42ir.json` 并存）
- [x] Substring 重载：arity-based，两个函数在 IR 中为 `z42.core.String.Substring$1` 和 `z42.core.String.Substring$2`
- [x] Object.z42：修复并重新编译
