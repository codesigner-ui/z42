# Spec: stdlib call resolution

## ADDED Requirements

### Requirement: 静态伪类调用编译为 CallInstr

#### Scenario: Console.WriteLine 编译输出
- **WHEN** 用户脚本调用 `Console.WriteLine("hello")`
- **THEN** 生成 `CallInstr(dst, "z42.io.Console.WriteLine", [argReg])`，而非 `BuiltinInstr("__println", [argReg])`

#### Scenario: Assert.Equal 编译输出
- **WHEN** 用户脚本调用 `Assert.Equal(a, b)`
- **THEN** 生成 `CallInstr(dst, "z42.core.Assert.Equal", [aReg, bReg])`

#### Scenario: Math.Abs 编译输出
- **WHEN** 用户脚本调用 `Math.Abs(x)`
- **THEN** 生成 `CallInstr(dst, "z42.math.Math.Abs", [xReg])`

### Requirement: stdlib 实例方法编译为 CallInstr

#### Scenario: str.Substring 编译输出
- **WHEN** 用户脚本调用 `s.Substring(1)`（receiver 为 string 变量）
- **THEN** 生成 `CallInstr(dst, "z42.core.String.Substring$1", [sReg, 1Reg])`

#### Scenario: str.ToLower 编译输出
- **WHEN** 用户脚本调用 `s.ToLower()`
- **THEN** 生成 `CallInstr(dst, "z42.core.String.ToLower", [sReg])`

### Requirement: 输出 zpkg 包含 stdlib 依赖

#### Scenario: 使用了 Console.WriteLine 的脚本
- **WHEN** 脚本调用 `Console.WriteLine(...)`，未声明 `using z42.io`
- **THEN** 输出 zpkg 的 `dependencies` 包含 `z42.io`，且指向 `z42.io.zpkg`

#### Scenario: 未使用任何 z42.io 的脚本
- **WHEN** 脚本不调用任何 z42.io 中的函数
- **THEN** 输出 zpkg 的 `dependencies` 不包含 `z42.io`

### Requirement: 删除 NativeTable 后 stdlib 源文件仍可编译

#### Scenario: 编译 stdlib 自身（Assert.z42 等）
- **WHEN** 运行 `./scripts/build-stdlib.sh`
- **THEN** 编译成功，不报 unknown intrinsic 错误（ValidateNativeMethod 只检查 extern+[Native] 共存）

## MODIFIED Requirements

### `ValidateNativeMethod` 行为变更
**Before:** 同时验证 intrinsic 名称存在于 `NativeTable.All` + 参数数量匹配  
**After:** 只验证 `[Native]` 和 `extern` 必须同时出现或同时不出现；不再验证 intrinsic 名称和参数数量

## IR Mapping
```
Before:  Console.WriteLine("hi") → BuiltinInstr(dst, "__println", [argReg])
After:   Console.WriteLine("hi") → CallInstr(dst, "z42.io.Console.WriteLine", [argReg])
                                    └─ stdlib: z42.io.Console.WriteLine body = BuiltinInstr("__println", ...)
```

## Pipeline Steps
- [ ] Lexer — 无变更
- [ ] Parser / AST — 无变更
- [x] TypeChecker — 移除 BuiltinTable 引用（回退 Unknown）；简化 ValidateNativeMethod
- [x] IR Codegen — 替换伪类调用路径，发出 CallInstr
- [ ] VM interp — 无变更（已能执行 CallInstr + BuiltinInstr 链）
