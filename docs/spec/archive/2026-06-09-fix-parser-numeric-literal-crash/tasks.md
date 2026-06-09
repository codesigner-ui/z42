# Tasks: fix-parser-numeric-literal-crash

> 状态：🟢 已完成并归档（2026-06-09）

**变更说明：** `ExprParser.ParseIntLit` / `ParseFloatLit` 对 token 文本做无保护的 `long.Parse` / `Convert.ToInt64` / `double.Parse`。词法器可能产出宽松/畸形数字 token（如 `8F`）或溢出 Int64 的字面量 → 抛**非结构化** `FormatException`/`OverflowException`，逃出 parser 崩溃。

**触发：** `Z42.Tests.PropertyTests.Parser_NeverCrashes_Random`（FsCheck，unseeded → flaky）fuzz 到 `8F`（shrunk from `a(){8F`）→ `long.Parse("8F")` FormatException → CI build-and-test 间歇红（ubuntu-arm / windows 视随机种子）。pre-existing parser robustness gap，非近期 commit 引入。

**原因 + 修复：** `ParseIntLit` / `ParseFloatLit` 用 try-catch 捕获 `FormatException`/`OverflowException`/`ArgumentException` → 抛结构化 `ParseException`（E0103 `InvalidNumericLit`）。parser 顶层有 error-recovery，会把它收进 DiagnosticBag（`8F` 现得干净 `error E0103: invalid integer literal '8F'`，不再崩溃）。

**文档影响：** 无（parser 健壮性 fix，不改合法程序行为 / wire format；只把崩溃转成结构化诊断）。

**子系统：** `compiler`（空闲；fix-fqn-class-resolution 已归档释放）。fix 型，minimal mode。

- [x] 1.1 `src/compiler/z42.Syntax/Parser/ExprParser.Atoms.cs`：`ParseIntLit` + `ParseFloatLit` 加 try-catch → `ParseException`(E0103)
- [x] 1.2 `src/compiler/z42.Tests/PropertyTests.cs`：确定性回归 `Parser_MalformedNumericLiteral_NoUnstructuredCrash`（`8F` / 表达式位 `8F` / Int64 溢出 → 非非结构化崩溃）
- [x] 1.3 验证：z42c on `a(){8F` → 干净 E0103（非 unhandled crash）；PropertyTests 46/46 绿

## 备注
- 这修复 `Parser_NeverCrashes_Random` 在数字字面量这一类输入上的 flaky 崩溃 —— CI build-and-test 间歇红的根因之一（连同已修的 Windows E0063 f2c93971）。
- FsCheck 是通用 fuzzer，理论上仍可能在**其他**未保护路径发现新崩溃；本 fix 只覆盖数字字面量这一已确认类。后续若再现按同法逐个加结构化保护。
