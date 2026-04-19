# Design: 调试变量名 + 调用栈

## Architecture

```
FunctionEmitter._locals (Dict<string, TypedReg>)
    ↓ snapshot at emit end
IrFunction.LocalVarTable (List<IrLocalVarEntry>)
    ↓ ZbcWriter
DBUG section in .zbc (per-function: var_count + name_idx + reg_id)
    ↓ zbc_reader.rs
Function.local_vars (Vec<LocalVar>)
    ↓ interp error path
"at Demo.Main (line 42)" + call stack chain
```

## Decisions

### Decision 1: 变量名表存储位置
**问题：** 变量名表是放在 DBUG section 还是 FUNC section？
**决定：** DBUG section。理由：DBUG 是可选 section，stripped 模式不含调试信息；变量名属于调试元数据，不影响执行。

### Decision 2: DBUG section 编码格式
**问题：** DBUG section 内部如何组织？
**决定：** 按函数顺序排列，每个函数先写 line table 再写 var table：
```
DBUG section:
  func_count: u32
  Per function (顺序与 FUNC section 一致):
    line_entry_count: u16
    Per line entry:
      block_idx: u16
      instr_idx: u16
      line: u32
      file_idx: u32  (0xFFFFFFFF = no file)
    var_count: u16
    Per var:
      name_idx: u32  (STRS pool index)
      reg_id: u16
```
这与当前 line table 写入方式兼容，只是追加了 var 部分。

### Decision 3: 调用栈实现方式
**问题：** 如何构建调用栈链？
**决定：** 利用 anyhow 的 `.context()` 链。每次函数调用返回 Err 时，追加当前函数的位置信息。异常最终打印时 anyhow 自动展开 context 链，形成完整调用栈。

当前已有的模式（interp/mod.rs line 179）：
```rust
return Err(e.context(format!("  at {} (line {})", func.name, loc)));
```
只需确保 **每层** 函数调用都追加 context 即可自然形成调用栈。

### Decision 4: 变量名在 disasm 中的展示
**决定：** 在函数头部添加 `.locals` section：
```
.func @Demo.Main  params:0  ret:void  mode:Interp
  .locals
    %0 = x
    %1 = name
  .linetable
    ...
```

## Implementation Notes

- FunctionEmitter 在 `EmitMethod()` / `EmitFunction()` 末尾 snapshot `_locals` 字典
- `_locals` 包含参数（含 this）和局部变量，无需额外收集
- ZbcWriter 当前未写 DBUG section（flags 中 HasDebug 始终为 0），需新增
- ZbcReader (C#) 已有 DBUG 读取骨架，需补全 var 部分
- zbc_reader.rs (Rust) 需新增 DBUG section 解析

## Testing Strategy

- 单元测试：ZbcRoundTripTests 新增 LocalVarTable round-trip 验证
- Golden test：新增一个包含多函数调用的 run test，验证调用栈输出
- 手动验证：`z42c disasm` 输出 `.locals` section
