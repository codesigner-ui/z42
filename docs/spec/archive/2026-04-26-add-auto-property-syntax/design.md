# Design: Auto-property 语法实现

## Architecture

```
源码 `public int X { get; set; }`
  │
  ↓ Parser (TopLevelParser)
  │   IsAutoPropDecl(cursor) → true
  │   ParseAutoPropDecl 返回:
  │     - FieldDecl  __prop_X: int   (private)
  │     - FunctionDecl get_X() : int { return this.__prop_X; }
  │     - FunctionDecl set_X(int value) : void { this.__prop_X = value; }    [仅 set; 时]
  │
  ↓ ClassDecl
  │   fields:  [..., __prop_X]
  │   methods: [..., get_X, set_X]
  │
  ↓ TypeChecker / SymbolCollector
  │   ClassType.Fields[__prop_X] = int
  │   ClassType.Methods[get_X] = () -> int
  │   ClassType.Methods[set_X] = (int) -> void
  │
用户代码 `box.X = 42;`
  │
  ↓ TypeChecker.BindAssign
  │   target.Member = "X"
  │   class 有 set_X method → BoundCall(box, set_X, [42])
  │   （类似 indexer 的 set_Item dispatch）
  │
用户代码 `var v = box.X;`
  │
  ↓ TypeChecker.BindMember
  │   class 有 get_X method → BoundCall(box, get_X, [])
  │
  ↓ Codegen
VCall get_X / set_X (现有 IR 路径)
```

## Decisions

### Decision 1: backing field 命名 `__prop_<Name>`

**问题**：auto-property 合成字段如何命名避免与用户字段冲突？

**决定**：双下划线前缀 `__prop_<Name>`。

**理由**：
- 与现有编译器内部命名约定一致（如 `__foreach_i`）
- z42 当前没有"identifier 不允许双下划线开头"的约束（但作为 convention 保留）
- 用户代码即使写 `__prop_Width` 也可被 visibility=private 阻挡

### Decision 2: property 在 binding 阶段 desugar，不在 codegen

**问题**：在哪个阶段把 `obj.X` 转换成 `obj.get_X()`？

**决定**：**TypeChecker binding 阶段**。BoundMember binding 时检测 method
`get_<Name>` 是否存在 → 转 BoundCall。

**理由**：
- 类型检查阶段可以正确推断 property 类型（getter return type）
- Codegen 不需要新逻辑（已有 BoundCall → VCall 路径）
- 与 indexer 的 desugar 在 codegen 阶段不同；indexer 因为索引语法 `[i]`
  独立，property 因为复用成员访问语法 `.Name` 必须在 binding 阶段做（否则
  Codegen 拿到 BoundMember 不知道是字段还是 property）

### Decision 3: 字段 vs property 优先级 — property 优先

**问题**：若类同时有 `__prop_X` 字段和 `get_X` 方法（auto-property 的合成
结果），用户写 `obj.X` 怎么解析？

**决定**：**property 优先**。TypeChecker 先查 method `get_<Name>`，命中
即 desugar；否则 fallback 到字段查找。

**理由**：
- 不允许"既有 X 字段又有 X auto-property"（用户错误，两者命名冲突）。
  实际上 X auto-property 合成 `__prop_X` field（不同名），不会冲突
- 用户字段 X（无 auto-property）走原 BoundMember 路径，不受影响

### Decision 4: parser 识别 auto-property 的 lookahead

**问题**：parser 如何区分 field、auto-property、method？

**当前 lookahead**（line 254-258 / 401-402）：
```csharp
// `T Name { get; ...` — peek `{` then peek 1 后是 `get`
if (cursor.Current.Kind == TokenKind.LBrace
    && cursor.Peek(1).Text == "get")
```

**决定**：**保留这个 lookahead 模式**，仅替换 `SkipAutoPropBody + continue`
为 `ParseAutoPropDecl(...)` + 把返回的 FieldDecl + FunctionDecl 加入对应集合。

**理由**：lookahead 已可靠区分（auto-property 必有 `{ get;` token 序列）；
不需重新设计 grammar。

### Decision 5: ParseAutoPropDecl 内部结构

**问题**：3 处调用点（class body / interface body / extern）需要不同输出 —
class 要 backing field + body methods；interface 仅 method signatures（无 body）；
extern 仅 method signatures + extern modifier。如何统一？

**决定**：分两个 helper：

```csharp
// Class body 用：返回 FieldDecl + 至少一个 FunctionDecl（带 synthesized body）
internal static (FieldDecl, IEnumerable<FunctionDecl>) ParseClassAutoProp(
    ref TokenCursor cursor, LanguageFeatures feat,
    Visibility classDefaultVis);

// Interface / extern 用：返回纯方法签名 list（无 body 或 extern body）
internal static IEnumerable<MethodSignature> ParseSignatureProp(
    ref TokenCursor cursor, LanguageFeatures feat,
    bool isExtern, Visibility memberVis);
```

理由：class auto-property 才合成 backing field，interface/extern 不需要。
分两个 helper 比单 helper 带 mode flag 清晰。

### Decision 6: Setter `value` 参数类型

**问题**：setter 的 `value` 参数类型与 property 类型一致。

**决定**：直接用 property 声明的 type expr 作为 `value` 参数 type。L3-G4e
indexer setter 已是同样做法（line 340-342）。

### Decision 7: accessor visibility 处理

**问题**：`public int X { get; private set; }` 的 accessor 各自 visibility？

**决定**：**解析但忽略**（accessor 一律继承 property 的 visibility）。同
indexer 现状（[Helpers.cs:318](src/compiler/z42.Syntax/Parser/TopLevelParser.Helpers.cs#L318) 注释 "accessor visibility ignored in L3-G4e"）。

**理由**：accessor 各自 visibility 是 C# 8+ 特性，本 scope 不上；统一继承
property visibility 已能满足 stdlib + 用户大部分场景。

### Decision 8: BoundMember 处理 property 的具体实现

**问题**：TypeChecker 怎么把 `obj.X` 转成 BoundCall？

**决定**：在 `BindMemberAccess`（具体函数名 grep TypeChecker）路径里：

```csharp
// 检查 obj 类型 cls 的 Methods 含 get_<Name>
var getterName = $"get_{m.Member}";
if (clsType.Methods.TryGetValue(getterName, out var getter))
{
    // 转为 BoundCall(obj, getterName, [], getter.Ret, m.Span)
    return new BoundCall(obj, classQualName, getterName, [], getter.Ret, m.Span);
}
// fallback：原有 field lookup → BoundMember
```

赋值路径 `obj.X = v` 在 `BindAssignment` 类似处理：

```csharp
if (assign.Target is MemberAccessExpr m)
{
    var setterName = $"set_{m.Member}";
    if (clsType.Methods.ContainsKey(setterName))
    {
        // 转为 BoundCall(obj, setterName, [v]) 当作 statement
        return new BoundExprStmt(new BoundCall(...));
    }
    // fallback：BoundFieldAssign（原路径）
}
```

具体实现位置阶段 1 调研后确定。

### Decision 9: stdlib 升级时机

**决定**：本 change 同 commit 内升级 `IEnumerator.Current` 回 property 形式
作为端到端验证（与 #2 同 commit 升级 Exception 双 ctor 同思路）。

## Implementation Notes

### Parser: ParseClassAutoProp 骨架

```csharp
internal static (FieldDecl, IEnumerable<FunctionDecl>) ParseClassAutoProp(
    ref TokenCursor cursor, LanguageFeatures feat,
    Visibility vis, TypeExpr type, string name, Span start)
{
    // 已经吃了 `<vis>? <type> <name>`，cursor 在 `{` 处
    ExpectKind(ref cursor, TokenKind.LBrace);
    bool hasGet = false, hasSet = false;
    while (cursor.Current.Kind != TokenKind.RBrace)
    {
        ParseVisibility(ref cursor, vis); // 忽略 accessor visibility
        var kw = cursor.Current;
        if (kw.Text == "get") { hasGet = true; }
        else if (kw.Text == "set") { hasSet = true; }
        else throw new ParseException(...);
        cursor = cursor.Advance();
        ExpectKind(ref cursor, TokenKind.Semicolon);
    }
    ExpectKind(ref cursor, TokenKind.RBrace);
    if (!hasGet) throw new ParseException("auto-property must have at least `get`", start);

    // Backing field: private __prop_<name>: type
    var bfName = $"__prop_{name}";
    var field = new FieldDecl(bfName, type, null, Visibility.Private, isStatic: false, start);

    // Synthesize getter body: return this.__prop_<name>;
    var thisExpr  = new IdentExpr("this", start);
    var fieldRead = new MemberAccessExpr(thisExpr, bfName, start);
    var getBody   = new BlockStmt([new ReturnStmt(fieldRead, start)], start);
    var getter    = new FunctionDecl($"get_{name}", [], type, getBody,
        vis, FunctionModifiers.None, null, start);

    var methods = new List<FunctionDecl> { getter };
    if (hasSet)
    {
        // Synthesize setter body: this.__prop_<name> = value;
        var valueExpr = new IdentExpr("value", start);
        var fieldWrite = new AssignExpr(
            new MemberAccessExpr(thisExpr, bfName, start), valueExpr, start);
        var setBody = new BlockStmt([new ExprStmt(fieldWrite, start)], start);
        var setter  = new FunctionDecl($"set_{name}",
            [new Param("value", type, null, start)],
            new VoidType(start), setBody,
            vis, FunctionModifiers.None, null, start);
        methods.Add(setter);
    }

    return (field, methods);
}
```

### Parser: ParseSignaturePropForInterface 骨架

接口 / extern 仅声明方法签名（无 body）：

```csharp
internal static IEnumerable<MethodSignature> ParseInterfaceProp(
    ref TokenCursor cursor, LanguageFeatures feat,
    Visibility vis, TypeExpr type, string name, Span start,
    bool isStatic)
{
    // 解析 { get; [set;] }
    ExpectKind(ref cursor, TokenKind.LBrace);
    bool hasGet = false, hasSet = false;
    while (...) { /* 同 class 版 */ }

    var methods = new List<MethodSignature>();
    if (hasGet)
        methods.Add(new MethodSignature($"get_{name}", [], type, start, isStatic, false, null));
    if (hasSet)
    {
        var setParms = new List<Param> { new Param("value", type, null, start) };
        methods.Add(new MethodSignature($"set_{name}", setParms, new VoidType(start), start, isStatic, false, null));
    }
    return methods;
}
```

### TypeChecker: BindMemberAccess

```csharp
case MemberAccessExpr m:
{
    var target = BindExpr(m.Target, env);
    // 现有字段查找之前先尝试 property getter
    if (target.Type is Z42ClassType ct
        && ct.Methods.TryGetValue($"get_{m.Member}", out var getter)
        && getter.Params.Count == 0)
    {
        var fqClass = QualifyClassNameForMember(target, ct);
        return new BoundCall(BoundCallKind.Virtual, target, fqClass, $"get_{m.Member}",
            [], getter.Ret, m.Span);
    }
    // 现有字段访问逻辑保持
    // ...
}
```

### Stdlib 升级 (IEnumerator.z42)

```z42
namespace Std;

public interface IEnumerator<T> : IDisposable {
    bool MoveNext();
    T Current { get; }   // 升级回 property 形式
}
```

`docs/design/iteration.md` 同步说明：parser 完整支持后 IEnumerator.Current
形式恢复 C# 标准。

## Testing Strategy

### Parser unit tests（ParserTests.cs）

- `Parser_ClassAutoProperty_DesugarsToFieldAndAccessors`
- `Parser_ClassReadonlyAutoProperty_OnlyGetter`
- `Parser_InterfaceProperty_DesugarsToMethodSignatures`
- `Parser_ExternProperty_DesugarsToExternMethod`
- `Parser_AutoPropertyMustHaveGet`（错误用例：`{ set; }` only）

### TypeChecker tests

- `TypeChecker_PropertyRead_BindsToGetterCall`
- `TypeChecker_PropertyWrite_BindsToSetterCall`
- `TypeChecker_ReadonlyPropertyAssign_ReportsError`

### Golden tests

- `run/97_auto_property_class`：用户类 with `{ get; set; }` + 读写 +
  multiple property
- `run/98_auto_property_readonly`：readonly + 试图赋值报错（diagnostic test）
- `run/99_interface_property`：interface 含 property，user class implement，
  调用走 VCall

### 端到端

- IEnumerator.Current 升级回 property 后所有现有测试保持绿
- stdlib 重新 build；regen 全部 source.zbc

## 兼容性风险

| 风险 | 评估 | 缓解 |
|------|------|------|
| `__prop_X` field 名意外被用户代码命中 | 低 | visibility=private 阻挡；测试覆盖外部访问报错场景 |
| BoundMember 转 BoundCall 的范围过广（误把字段也当 property） | 中 | 只在 method `get_<Name>` 存在时转；阶段 1 测试覆盖含字段无 property 的 case |
| accessor visibility 误生效 | 低 | 与 indexer 现状一致，`accessor visibility ignored` |
| 现有 SkipAutoPropBody 调用点漏改 | 中 | 全 grep 三处全替换；构建后 grep `SkipAutoPropBody` 应仅 declaration 一处 |
| MethodSignature 数据结构是否容纳"无 body 的 property accessor" | 低 | 同 abstract method 走相同路径；现有 interface 已支持无 body method |
| 现有测试中字段 `Count` 与 stdlib auto-property `Count { get; }` 冲突 | 低 | 检查 stdlib 是否被升级（List.Count 仍是字段而非 property，不影响） |
