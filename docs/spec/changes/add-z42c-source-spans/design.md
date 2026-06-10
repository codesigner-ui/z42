# Design: add-z42c-source-spans — span 链（AST → Bound → LineTable → DBUG）

> 状态：DRAFT（待 User 审批）｜归属：add-z42c-source-spans
> 来源：port-z42c-zbc-writer 实施发现（DBUG 是全面 byte-identical 唯一阻塞）；C# 权威 = `FunctionEmitter.Helpers.TrackLine` + `ZbcWriter.BuildDbugSection`。

## Architecture

```
Token(line,col 已有)
  → Parser 构造点填 Span(start,len,line,col,file)        [syntax：42 节点 + 构造点]
  → TypeChecker 透传 AST.Span → Bound.Span                [semantics：~20 Bound 节点]
  → FunctionEmitter.EmitStmt 入口 TrackLine(stmt.Span)     [codegen：行表收集]
      同行去重（_lastLine）；记 (blockIdx=当前块, instrIdx=当前块内指令数, line, file-basename, col)
  → IrFunction.LineTable / LocalVarTable                   [ir：模型字段]
  → ZbcWriter：hasDebug → flags|=HasDebug + DBUG 第 9 section [ir：字节]
```

DBUG 字节布局（镜像 C# BuildDbugSection，权威勿改）：
```
u32 fnCount；每函数：
  u16 lineCount + lineCount × (block_idx u16, instr_idx u16, line u32, file_str_idx u32[无=0xFFFFFFFF], column u32)
  u16 varCount  + varCount × (name_str_idx u32, reg_id u16)
```

## Decisions

### D1：Span 在 AST 节点的形态 = 尾参（镜像 C# 约定）
**选项**：A 尾参 ctor（C# 规范"每节点末位参数必须是 Span Span"的 z42 镜像）；B 构造后 `SetSpan()`（少改 ctor，但可漏设 → 静默 0 行号）。
**决定**：A。漏设在 A 下是编译错（ctor 参数缺失），在 B 下是运行期静默错行号——byte-identical 场景必须编译期强制。Dump 不打印 span → 既有断言全保。

### D2：Bound 层逐节点携带（镜像 C#）
**选项**：A 全 Bound 节点携 Span；B 仅 BoundStmt（DBUG 只消费语句行）。
**决定**：A。TypeChecker 诊断要表达式级 span（`return "hi"` 错在表达式不在语句头）；C# BoundExpr/BoundStmt 均携带；B 是省一半功夫的临时形态，后续诊断必返工。

### D3：TrackLine 时机 = EmitStmt 入口（1:1 C#）
C# 在 `FunctionEmitterStmts.EmitStmt` 入口 `TrackLine(stmt.Span)`，**仅语句级**（表达式不再记行）；同行去重靠 `_lastLine` 单调记忆。z42c 在 `FunctionEmitter._emitStmt` 入口同位置调用。块化表达式（&&/三目/??）中途分块不记行——与 C# 一致（它们也不在 C# 记）。

### D4：file = basename（镜像 fix-dbug-file-basename）
绝对路径机器相关 → DBUG 字节不可复现。C# `BaseName` 同时切 `/` 与 `\`。z42c 同实现（CharAt 反向扫）。`IrDump` 测试源 file="<input>" → basename 原样；e2e 编真实文件名。

### D5：验证 = byte-identical 升级为主轨
- zbc_tests：`int F(){return 5;}` 完整 hex 对 C# 同源 golden（306 字节级别；用 `dotnet z42c.dll --emit zbc` 重截，随版本 bump 由 checklist 第 5 步刷新）。
- xtask e2e 加 **byte-compare 步**：同一 e2e 源分别经 z42c 与 C# driver 产 .zbc → 逐字节 diff（这是 self-hosting 对账 gate 的 .zbc 段首次上线）。

## Implementation Notes

- **z42c.core.Span 已有**（`Span(start,len,line,col,file)`），不改；syntax 引 z42c.core 已是依赖。
- Parser 填 span：每个 `_parseXxx` 开头取 `Token first = this._peek();`，构造时 `new Span(first.Start?, …, first.Line, first.Col, this._file)` —— Token 字段名以实际为准；**只需 start 位置精确**（DBUG 用 line/col；len 可先 0，对账不消费）。
- 受限写法注意：42 个 AST ctor 全加尾参 = 全部调用点（Parser + 各测试直构节点）机械更新；z42 无默认参数 → 测试直构节点也要补（工作量大头，纯机械）。
- TypeChecker `_noSpan()` 删除；`Infer` 各诊断点改用所在节点 span。诊断测试只断言 ErrorCount → 全保。
- LocalVarTable：EmitContext.Locals（name→TypedReg）在函数收尾时导出 (name_idx, reg_id)；**顺序须对 C#**（C# 按声明序 List）→ z42c Locals 是 hashed StrMap 无序！需并行 append-list 记录声明序（StrMap 仅查询）。
- e2e byte-compare 需要 gate 内可用的 C# driver——`_ensureZ42cTooling` 已 dotnet build driver ✓。

## Testing Strategy

- 单元：parser span 断言（节点行/列）；TrackLine 去重；DBUG 字节（spec-derived）。
- Golden：`int F(){return 5;}` 全文件 hex 对 C#。
- e2e：xtask byte-compare 步（z42c vs C# 同源 diff，selfcheck/callcheck/typecheck 三源全对）。

## Deferred

- 表达式级行跟踪（C# 也不做）；len 精确终点（对账不消费）；插值串/lambda span（前端未接）；LocalVarTable 的作用域分段（C# 平铺）。
