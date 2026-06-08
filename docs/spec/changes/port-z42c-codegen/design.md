# Design: z42c codegen — Bound 树 → IR lowering

> 状态：DRAFT（待 User 审批）｜归属：port-z42c-codegen
> 前置：z42c.semantics 类型检查半完整（Bound 树 + Z42Type 注解）；z42c.ir 当前仅骨架。
> 来源：会话内 2 个 Explore agent 全量 map C# `z42.IR/IrModule.cs`（IR 模型）+ `z42.Semantics/Codegen/`（lowering）。

## 范围

z42c.semantics 的 **codegen 半**：Bound 树（已类型检查）→ **寄存器式 SSA IR 内存模型**。
- **输入**：`SemanticModel`（每方法体 BoundBlock + 每 Expr 携 Z42Type）。
- **输出**：`IrModule`（IrFunction[] + IrClassDesc[] + StringPool）；文本 dump 供 [Test]。
- C# 对应：`IrGen.Generate(cu)` + `FunctionEmitter`（17 文件 ~2783 行）。
- **不含**：byte-identical .zbc emit（ZbcWriter）+ token 分配 —— 独立后续 design。

---

## Architecture

```
SemanticModel (Bound 树 + Z42Type)   ← z42c.semantics 类型检查半 ✅
        │
        ▼  IrGen.Generate            ← 模块级：StringPool / IrClassDesc / 驱动逐函数
        │
        ▼  FunctionEmitter (每函数)   ← 递归树遍历，集中 if-is 调度
        │     EmitExpr(BoundExpr) → TypedReg     （子表达式分配寄存器 + 内联 emit 指令）
        │     EmitStmt(BoundStmt) → void          （if/while → StartBlock/EndBlock + 基本块）
        ▼
   IrModule (z42c.ir)                ← IrFunction → IrBlock → IrInstr / IrTerminator
        │
        ▼  （独立后续 design）ZbcWriter → .zbc（byte-identical）
```

---

## 三个关键决策（已与 User 裁决 2026-06-09）

### 决策 1：dispatch = 集中式 if-is 链（沿用 semantics D1）

- C# codegen 用 `BoundExprVisitor<TypedReg>` / `BoundStmtVisitor<Unit>`（abstract base + VisitX + 一处 `Visit()` switch）。
- z42 无 abstract / 无 switch-on-type → **`EmitExpr(BoundExpr)` / `EmitStmt(BoundStmt)` 各一条 `if (e is BoundLitInt x){...} else if ... else throw ICE` 链**，1:1 镜像 C# 集中 switch。与 TypeChecker 的 `_bindExpr`/`_bindStmt` 完全同构（已验证可行）。

### 决策 2：IR 指令表示 = class-per-instruction（虚 Dump）（User 裁决）

- C# 用 ~40 个 `sealed record : IrInstr`。z42 无 record/abstract → **每条指令一个 `class : IrInstr` + `virtual int Op()` + `virtual string Dump()`**，同 `Bound.z42` 既有模式（class 继承 + 虚 Dump 出文本）。
- 按增量逐条加（CG-1A 只需 ConstI64/ConstStr/.../Copy/Add..Rem/Ret 等几条），最终 ~40 条。
- `IrInstr.z42` 超 500 行硬限时按类别拆 `IrInstr.<cat>.z42`（独立 refactor commit）。

### 决策 3：IR 数据结构 = 忠实镜像 IrModule.cs，受限写法替换

| C# | z42c |
|----|------|
| `enum IrType : byte` | `static class IrType` + int 常量（同 BinaryTypeTable.OperandKind 模式）|
| `readonly record struct TypedReg(int Id, IrType Type)` | `sealed class TypedReg`（public Id/Type 字段）|
| `record IrModule(... List<IrFunction> ...)` | `sealed class IrModule`，集合用 **typed array + count**（无泛型字段）|
| `abstract record IrInstr` + sealed 子类 | `class IrInstr` 基类 + 子类（虚 Dump）|
| `List<IrInstr> Instructions` | `IrInstr[] + int count`（块内增长数组，同 syntax/AST 模式）|

---

## IR 数据模型（z42c.ir，从零建）

### IrType（int 常量）
```
static class IrType {
    public static int Unknown = 0;
    public static int I8 = 1; ... I64 = 4; U8 = 5; ... U64 = 8; F32 = 9; F64 = 10;
    public static int Bool = 11; Char = 12; Str = 13; Ref = 14; Void = 15;
    public static int FromZ42Type(Z42Type t) { ... }   // prim→具体 tag / class·array→Ref / void→Void
    public static string Name(int tag) { ... }          // dump 用："i64"/"bool"/"ref"/...
}
```
- **所有堆对象（class/instantiated/array）→ Ref**（镜像 C# `Z42ClassType/ArrayType → IrType.Ref`）。
- prim 名 → tag：`int→I64`（z42 int 是 64 位）/ `long→I64` / `double→F64` / `float→F32` / `bool→Bool` / `char→Char` / `string→Str` / `byte→U8` …（沿用 C# TypeRegistry 映射，实施时逐一核对 ir.md 类型映射表）。

### TypedReg
```
sealed class TypedReg { public int Id; public int Type; TypedReg(id, type){...} string Dump(){ return "%" + Id; } }
```

### 容器（class，集合 = typed array + count）
```
sealed class IrModule    { string Name; string[] StringPool; int StringCount;
                           IrClassDesc[] Classes; int ClassCount; IrFunction[] Functions; int FuncCount; }
sealed class IrClassDesc { string Name; bool HasBase; string BaseName; IrFieldDesc[] Fields; int FieldCount; }
sealed class IrFieldDesc { string Name; string Type; }
sealed class IrFunction  { string Name; int ParamCount; string RetType; bool IsStatic;
                           IrBlock[] Blocks; int BlockCount; int MaxReg; }
sealed class IrBlock     { string Label; IrInstr[] Instrs; int InstrCount; IrTerminator Term; bool HasTerm; }
```
- **StringPool**：去重字符串字面量，1-based（0=无）。IrGen 维护 `Intern(s)→int`。CG-1A 起即建（const.str 用）。
- 暂不携：ExceptionTable / LineTable / LocalVarTable / TypeParams / ParamModifiers（延后增量；byte-identical 才需）。

### IrInstr 层次（基类 + 虚 Dump，按增量加子类）
```
class IrInstr { public virtual int Op(); public virtual string Dump(); }   // Op = opcode int（dump/后续 zbc）
// CG-1A：ConstI64Instr(Dst,Val) / ConstStrInstr(Dst,Idx) / ConstBoolInstr / ConstF64Instr / ConstNullInstr
//        / CopyInstr(Dst,Src) / AddInstr(Dst,A,B) / Sub/Mul/Div/Rem
// CG-1B+：比较 Eq/Ne/Lt/Le/Gt/Ge / 逻辑 And/Or/Not/Neg / 位 BitAnd.. / StrConcat/ToStr
// CG-1C+：CallInstr(Dst,Func,Args[]) / VCallInstr(Dst,Obj,Method,Args[]) / FieldGet/FieldSet / StaticGet/Set / BuiltinInstr
// CG-1D+：ObjNewInstr(Dst,Class,Ctor,Args[]) / ArrayNew/Get/Set/Len / IsInstance / AsCast / Convert
```
Dump 形如 ir.md：`%2 = add i64 %0, %1` / `%3 = const.i64 5` / `%4 = call @Foo(%2, %3)`。

### IrTerminator
```
class IrTerminator { public virtual string Dump(); }
RetTerm(bool HasReg, TypedReg Reg) → "ret %r" / "ret"
BrTerm(string Label)               → "br <label>"
BrCondTerm(Cond, TrueLabel, FalseLabel) → "br.cond %c, <t>, <f>"
ThrowTerm(Reg)                     → "throw %r"（延后到异常增量）
```

---

## Lowering 算法（FunctionEmitter）

每函数一个 FunctionEmitter 实例，状态：
```
sealed class FunctionEmitter {
    SymbolTable _symbols;
    StrMap      _locals;        // name → TypedReg（param/local；value=TypedReg via object）
    StrMap      _fields;        // 实例字段名集合（→ FieldGet/Set on reg0=this）
    IrBlock[]   _blocks; int _blockCount;
    IrInstr[]   _cur;    int _curCount;     // 当前块指令累积
    string      _curLabel;
    bool        _ended;        // 当前块是否已终结
    int         _nextReg;
    int         _labelSeq;
    IrGen       _gen;          // 回模块级（Intern 字符串 / 函数签名查询）
}
```

### 入口
```
EmitFunction(MethodDecl/free func, BoundBlock body, bool isStatic, Z42ClassType owner?):
  paramOffset = isStatic ? 0 : 1
  if (!isStatic) _locals["this"] = TypedReg(0, Ref); _fields = owner.Fields keys
  for i in params: _locals[name] = TypedReg(i+paramOffset, IrType.FromZ42Type(ptype))
  _nextReg = paramCount + paramOffset
  StartBlock("entry")
  EmitStmt(body)
  if (!_ended) EndBlock(RetTerm(void))     // 隐式 ret
  return IrFunction(name, paramCount+paramOffset, retType, isStatic, _blocks, _maxReg)
```

### 表达式（集中 if-is，返回 TypedReg）
```
EmitExpr(BoundExpr e) → TypedReg:
  if (e is BoundLitInt n)  { r = Alloc(I64); Emit(ConstI64Instr(r, n.Val)); return r; }
  else if (e is BoundIdent id) { return _lookupIdent(id); }       // _locals 命中 → reg；字段 → FieldGet(reg0)
  else if (e is BoundBinary b) { a=EmitExpr(b.L); c=EmitExpr(b.R); d=Alloc(...); Emit(<op>Instr(d,a,c)); return d; }
  else if (e is BoundAssign a) { ... WriteBack ... }
  else if (e is BoundCall c)   { ... CallInstr/VCallInstr ... }    // CG-1C
  ... else { throw ICE }                                          // 兜底（同 _bindExpr）
```
- **无表达式临时栈**：每子表达式 Alloc 一个新寄存器并立即 Emit；返回该寄存器。
- `Alloc(tag) = TypedReg(_nextReg++, tag)`，`_maxReg = max`.

### 语句（集中 if-is）
```
EmitStmt(BoundStmt s):
  if (s is BoundBlockStmt b) { for st in b: EmitStmt(st); }
  else if (s is BoundVarDeclStmt v) { r=EmitExpr(v.Init); _bindLocal(v.Name, r); }
  else if (s is BoundReturn ret) { if val: r=EmitExpr(ret.Val); EndBlock(RetTerm(r)); else EndBlock(RetTerm(void)); }
  else if (s is BoundExprStmt e) { EmitExpr(e.Expr); }            // 结果弃
  else if (s is BoundIf i) { ... 见控制流 ... }                   // CG-1B
  else if (s is BoundWhile w) { ... }
  ... else { throw ICE }
```

### 控制流 → 基本块（CG-1B）
```
StartBlock(label): _curLabel=label; _cur=[]; _ended=false
EndBlock(term):    if (!_ended) { _blocks += IrBlock(_curLabel, _cur, term); _ended=true }
FreshLabel(hint):  return hint + "_" + (_labelSeq++)

EmitIf(BoundIf i):
  c = EmitExpr(i.Cond)
  thenL=Fresh("then"); endL=Fresh("end"); elseL = i.HasElse ? Fresh("else") : endL
  EndBlock(BrCondTerm(c, thenL, elseL))
  StartBlock(thenL); EmitStmt(i.Then); if(!_ended) EndBlock(BrTerm(endL))
  if (i.HasElse) { StartBlock(elseL); EmitStmt(i.Else); if(!_ended) EndBlock(BrTerm(endL)) }
  StartBlock(endL)
```
- break/continue：`_loopLabels` 栈（每 while 入栈 (continueLabel, breakLabel)）→ `EndBlock(BrTerm(target))`。

### 局部变量 / 字段写回（WriteBack，镜像 C#）
- 写 local：`_locals` 命中则 CopyInstr 到既有寄存器（SSA-lite，保持变量↔单寄存器）；否则新分配。
- 写实例字段：`FieldSetInstr(reg0, name, val)`。

---

## IR 文本 dump（IrDump 工具，[Test] 断言用）

格式镜像 ir.md 的 .zasm 文本形（非 byte-identical，仅可读断言）：
```
fn @Foo(2) -> int {
entry:
  %2 = add i64 %0, %1
  ret %2
}
```
- `IrFunction.Dump()` → 多行；块以 `<label>:` 起，指令缩进 2 空格，末终结符。
- `IrModule.Dump()` → 各函数拼接。
- `IrDump.DumpFunc(src, funcKey)` / `IrDump.DumpModule(src)`：源 → parse → SymbolCollector + TypeChecker → IrGen → dump（纯函数，内部建临时 DiagnosticBag）。
- 测试断言用多行字符串（IR 块结构天然多行，区别于 Bound 的单行 s-expr）。

---

## Pass / 入口

```
IrGen.Generate(cu, semanticModel) → IrModule
  · 收集 IrClassDesc（类名/基类/字段，从 SymbolTable）
  · 逐 class 方法 + 顶层 func：FunctionEmitter.EmitFunction（用 semanticModel 的 BoundBlock）
  · StringPool 汇总
  · return IrModule
```
- driver `--dump-ir`（类比 `--dump-bound`）后续增量加。

---

## 增量计划（每增量走 `xtask test compiler-z42`，IR 文本断言 + 错误用例沿用 typecheck）

| # | 内容 | 关键节点 |
|---|------|---------|
| **CG-1A** | 最小：非泛型函数/方法，int/double/bool/string 字面量 + 局部/参数 + 二元算术 + var-decl/赋值/return → 单 entry 块 | IrType/TypedReg/IrModule/IrFunction/IrBlock + Const*/Copy/Add..Rem/Ret + FunctionEmitter/IrGen + IrDump |
| **CG-1B** | 控制流 if/while/break/continue → 多基本块 + Br/BrCond | StartBlock/EndBlock/FreshLabel + _loopLabels |
| **CG-1C** | 调用（free/static/instance/virtual）+ field get/set + this + 继承 | CallInstr/VCallInstr/FieldGet·Set/StaticGet·Set + 调用分派 |
| **CG-1D** | new / cast / is / 数组 | ObjNewInstr/ArrayNew·Get·Set·Len/IsInstance/AsCast |
| **CG-1E** | 比较 / 一元 / 逻辑短路(&&‖) / 字符串拼接 / 三目 / ?? | Eq..Ge/Not/Neg/BitAnd../StrConcat/ToStr/Convert + 短路降块 |
| **CG-2** | 泛型 type-args（obj_new 携 resolved args）+ default_of | ObjNew.TypeArgs / DefaultOfInstr |
| **defer** | 闭包·lambda / 异常 try-catch / native interop / static_init / 插值 / foreach 迭代器协议 | — |
| **后续独立 design** | ZbcWriter（byte-identical .zbc）+ TokenAllocator | — |

---

## Testing Strategy

- 每增量：源 → IrDump → IR 文本断言（多行）+ 错误用例（沿用 typecheck 的诊断断言，codegen 不引入新错误码）。
- 起步 `z42c.semantics/tests/codegen/`（与 typecheck/bound 并列单元）。
- byte-identical 不在本 change（无二进制产物）。

---

## Deferred / 不在本设计内

- **byte-identical .zbc（ZbcWriter）+ token 分配** —— 依赖本 change 的独立后续 design（需先 map z42.IR/BinaryFormat/ZbcWriter.cs + Tokens.cs）。
- 闭包 L3 / lambda / 异常表 / native interop / static_init / 字符串插值 / foreach 迭代器协议 / LineTable·LocalVarTable（调试元数据）。
- z42c.pipeline build 命令串联。

---

## 决策点（待 User 审批）

- **D1（dispatch）= 集中 if-is 链**（沿用 semantics D1，已验证）。
- **D2（IR 指令表示）= class-per-instruction + 虚 Dump**（User 2026-06-09 裁决）。
- **D3（增量起点）= CG-1A 最小集**（非泛型函数 + 字面量 + 局部 + 二元算术 + var-decl/赋值/return → 单 entry 块 + IrDump）。
- **D4（IR dump 格式）= .zasm-like 多行文本**（镜像 ir.md，可读断言；非 byte-identical）。
- **D5（int→IrType 映射）= z42 `int` 映射 I64**（z42 int 64 位；实施时逐一核对 ir.md 类型映射表，prim 名双拼写沿用 ResolveType 不规范化的处理）。
