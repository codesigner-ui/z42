# Spec: PackageCompiler 重构

## MODIFIED Requirements

### Requirement: ParseException 错误码正确（H2）

**Before:** ParseException catch 块直接写 `Console.Error.WriteLine($"error[E0001]: ...")`，错误码为 E0001，绕过 DiagnosticBag。

**After:** ParseException 写入 DiagnosticBag，使用 `DiagnosticCodes.UnexpectedToken`（E0201）。诊断输出通过 `diags.PrintAll()` 统一输出，与 TypeChecker 错误走同一路径。

#### Scenario: ParseException 产生正确错误码
- **WHEN** 源文件存在语法错误（如意外 token）
- **THEN** 输出 `error[E0201]: ...`，不再出现 `error[E0001]`

### Requirement: CompileFile/CheckFile 共享解析逻辑（H4）

**Before:** 两个方法各自实现"读文件 → Lex → Parse → TypeCheck"，逻辑 ~95% 重复。

**After:** 提取 `TryParseAndCheck(sourceFile, out source, out cu, out diags)` 私有方法，`CompileFile` 和 `CheckFile` 均调用此方法。

### Requirement: BuildTarget 拆分（L6）

**Before:** `BuildTarget` 约 139 行，混合了扫描/编译/打包多阶段逻辑。

**After:** 拆分为四个私有方法：
- `ScanLibsForNamespaces(libsDirs)` → `Dictionary<string, string>`
- `ScanZbcForNamespaces(dirs, nsMap)` → void（更新传入的 nsMap）
- `TryCompileSourceFiles(sourceFiles, stdlibIndex)` → `List<CompiledUnit>?`
- `BuildDependencyMap(units, nsMap)` → `List<ZpkgDep>`

`BuildTarget` 本身保留流程编排逻辑（调用上述方法 + 打包写出），行数降至约 40 行。
