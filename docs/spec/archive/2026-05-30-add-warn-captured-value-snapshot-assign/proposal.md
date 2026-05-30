# Proposal: warn on assignment to captured value-snapshot variable (W0604)

## Why

`docs/design/language/closure.md` §4.1 明确规定：值类型按快照捕获——
闭包内部写 `seenBoth = true` 不传回外部作用域。§4.4 推荐用 class（或
等价的 array cell）共享可变状态。

但当前编译器对"闭包内对值类型 captured var 的赋值"**静默放行**，
代码看起来一切正常运行，跨线程 / 跨闭包结果丢失却查不出来。本 session
内的 `add-z42-net-http-brotli` 实施期就栽过：

```z42
bool seenBoth = false;
Thread srv = Thread.Start(() => {
    ...
    seenBoth = true;          // ← 写入 closure 局部副本，丢
});
srv.Join();
Assert.True(seenBoth);         // ← 永远 false，测试静默挂死/假阳/假阴
```

这正是 spec §4.1 描述的 ValueSnapshot 行为，但调用方期望与 C# `bool`
共享语义。编译器有信息（capture kind 在 TypeCheck 时已决定，
`BoundCaptureKind.ValueSnapshot`），加一行 if 即可发出 warning。

不做：每次写 z42 闭包都得记得"原语值捕获是快照"，新人 / Claude session
反复踩同一个坑，silent bug 漫延。

## What Changes

- 新增 diagnostic code `W0604 CapturedValueSnapshotAssign`
- TypeChecker.BindAssign 末尾：若 `target` 是 `BoundCapturedIdent` 且
  `target.Type` 是 value type（`!Z42Type.IsReferenceType(target.Type)`），
  emit W0604 warning at the assignment span
- Warning message 引导用户改用 class wrap 或 array-cell pattern
- 新 xUnit 测试（`WarnCapturedValueSnapshotAssignTests.cs`）覆盖：
  - 写值类型 captured var → W0604
  - 写引用类型 captured var (Counter class) → 无 warning
  - 写 captured array element (`cell[0] = true`) → 无 warning（array 是引用类型）
  - 多重闭包嵌套 → 内层闭包写外层 captured → W0604（按内层 frame 的 capture 判断）
  - lambda 内部声明的 var 自己写自己 → 无 warning（不是 capture）
  - +=/-= 等复合赋值同样触发
- `docs/design/language/closure.md` 加一节 §4.5 "编译期提示" 链接 W0604
- `docs/design/runtime/diagnostics-catalog.md`（如有）加 W0604 条目

## Scope（允许改动的文件）

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `src/compiler/z42.Core/Diagnostics/Diagnostic.cs` | MODIFY | 加 `CapturedValueSnapshotAssign = "W0604"` 常量 |
| `src/compiler/z42.Core/Diagnostics/DiagnosticBag.cs` | MODIFY（如需）| 检查 Warning 入口可用，可能无需改 |
| `src/compiler/z42.Semantics/TypeCheck/TypeChecker.Exprs.Operators.cs` | MODIFY | BindAssign 末尾加 check |
| `src/compiler/z42.Tests/WarnCapturedValueSnapshotAssignTests.cs` | NEW | xUnit 覆盖 6 场景 |
| `docs/design/language/closure.md` | MODIFY | §4.5 编译期提示 + 链接 W0604 |

**只读引用**：

- `src/compiler/z42.Semantics/Bound/BoundExpr.cs`（BoundCapturedIdent / BoundCapture 形态）
- `src/compiler/z42.Semantics/TypeCheck/Z42Type.cs`（IsReferenceType 判据）
- 之前的 unused-import 警告（aa64cf87）的 W0603 模式做参考

## Out of Scope

- Capture 数据流分析（如检测"闭包内读但从未写"以提示"可以省去 capture"）
- 反向方向（外部写但闭包未读）— 与本 spec 关注的"silent miss"无关
- 强制 error（W → E）— 现 spec 显式 ValueSnapshot 是合法语义，warning 即可
- 同一 lambda body 内既读 captured var 又写其同名 lambda-local var
  的别名冲突分析
- 跨线程语义检查（z42.threading 的 fork-join 边界）— 走专门 concurrency
  分析 spec

## Open Questions

- [ ] W0604 还是 W0610？— 现有 W06xx 是 strict-using 系列；W0604 是
      compositionally next free，但语义不在同一族。**建议 W0604** —
      6xx 段保留给"semantic-edge warning"统一族；命名空间 / unused-import
      与 closure-capture 都属 semantic semantics。
