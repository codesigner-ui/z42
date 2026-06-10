# Spec: z42c-dbug — span 链与 DBUG 字节对账

## ADDED Requirements

### Requirement: AST 节点携 source span

#### Scenario: parser 填入语句起始位置
- **WHEN** 解析 `int x = 5;`（位于第 3 行第 5 列）
- **THEN** 该 `VarDeclStmt.Span` 的 line=3、col=5、file=解析入口传入的文件名

#### Scenario: 既有 Dump 断言不受影响
- **WHEN** 任何既有 `--dump-ast` / Dump() 断言用例运行
- **THEN** 输出与加 span 前逐字符一致（Dump 不打印 span）

### Requirement: 诊断携真实位置

#### Scenario: 类型错误指向表达式行列
- **WHEN** 类型检查 `int Bad() { return "hi"; }`（"hi" 在第 1 行）
- **THEN** 诊断的 span 行=1、列=该表达式起始列（非 `<sem>` 占位）

### Requirement: DBUG section byte-identical

#### Scenario: 有语句体函数全文件逐字节
- **WHEN** z42c 对 `int F() { return 5; }` 执行 `--emit-zbc`
- **THEN** 输出与 C# z42c 同源产物**逐字节一致**（含 DBUG section + flags.HasDebug）

#### Scenario: 同行多语句去重
- **WHEN** 函数体两条语句位于同一行
- **THEN** LineTable 仅 1 条该行记录（_lastLine 去重，镜像 C#）

#### Scenario: e2e byte-compare 三源全对
- **WHEN** xtask e2e 把 selfcheck/callcheck/typecheck 三个源分别经 z42c 与 C# driver 编译
- **THEN** 三对 .zbc 逐字节 diff 均为空

## IR Mapping
无新 IR 指令；IrFunction 增 LineTable（IrLineEntry[]）/ LocalVarTable；.zbc 增 DBUG section（既有格式，z42c 首次产出）。

## Pipeline Steps
- [x] Lexer（line/col 已有，不改）
- [ ] Parser / AST（全节点携 Span）
- [ ] TypeChecker（Bound 携 Span + 真实诊断位置）
- [ ] IR Codegen（TrackLine → LineTable/LocalVarTable）
- [ ] ZbcWriter（DBUG section + HasDebug flag）
- [ ] VM interp（不改——DBUG 是既有格式，VM 已消费）
