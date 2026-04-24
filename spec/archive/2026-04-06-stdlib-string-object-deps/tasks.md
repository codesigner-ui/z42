# Tasks: stdlib-string-object-deps

> 状态：🟢 已完成 | 创建：2026-04-06 | 完成：2026-04-06

## 进度概览
- [x] 阶段 1: 编译器重载支持
- [x] 阶段 2: String.z42 + Object.z42 修复 + 重新构建
- [x] 阶段 3: VM 运行时依赖加载
- [x] 阶段 4: 测试更新与新增
- [x] 阶段 5: 验证

---

## 阶段 1: 编译器重载支持（arity-based）

- [x] 1.1 `Z42Type.cs` — `IsAssignableTo` 增加 class name 等同判断（修复 pre-pass stub 引用相等问题）
- [x] 1.2 `TypeChecker.cs` — `CollectClasses` 按 arity 注册 `$N` 方法名；`ValidateNativeMethod` 接收 `isInstance` 参数修正 param count
- [x] 1.3 `TypeChecker.Exprs.cs` — 方法调用解析：先精确名，再 arity key（`name$N`）
- [x] 1.4 `IrGen.cs` — 检测同名重载，生成 `ClassName.MethodName$N`；`IrGenExprs.cs` dispatch 同理
- [x] 1.5 编译器单元测试：384 tests passed

## 阶段 2: 标准库 String + Object

- [x] 2.1 创建 `src/libraries/z42.core/src/String.z42`
      - 实例方法：`Contains`、`StartsWith`、`EndsWith`、`Substring$1`、`Substring$2`、`IndexOf`、`Replace`、`ToLower`、`ToUpper`、`Trim`、`TrimStart`、`TrimEnd`、`Split`
      - 静态方法：`IsNullOrEmpty`、`IsNullOrWhiteSpace`、`Join`、`Concat`、`Format`
- [x] 2.2 修复 `Object.z42` — `Equals` / `ToString` 改为 `extern native`；`Type.z42` 属性改为字段
- [x] 2.3 运行 `./scripts/build-stdlib.sh` 重新构建 z42.core.zpkg（5/5 成功）
- [x] 2.4 VM corelib 增加 `__obj_equals`、`__obj_to_str` native builtins

## 阶段 3: VM 运行时依赖加载

- [x] 3.1 `metadata/loader.rs` — `LoadedArtifact` 增加 `import_namespaces: Vec<String>` 字段
- [x] 3.2 `load_zbc` — 从 `ZbcFile.imports` 提取命名空间前缀，去重后填入 `import_namespaces`
- [x] 3.3 `main.rs` — 对 `user_artifact.import_namespaces` 调用 `resolve_namespace`，找到对应 zpkg 并加载（5.1e）
- [x] 3.4 `loader.rs` — 增加单元测试：验证 `extract_import_namespaces`
- [x] 3.5 `scripts/test-vm.sh` — 增加对 `source.zbc` 格式的支持（优先级低于 `source.z42ir.json`）

## 阶段 4: 测试更新与新增

- [x] 4.1 更新 `06_string_builtins/source.z42ir.json` — `builtin __contains/__str_*` 改为 `call z42.core.String.*`
- [x] 4.2 更新 `14_string_methods/source.z42ir.json` — 替换 9 个 string builtin 为 stdlib calls
- [x] 4.3 更新 `44_string_static_methods/source.z42ir.json` — IsNullOrEmpty/Join/Concat 使用 stdlib；variadic Join 改用 array_new_lit；Format 保留 builtin（stdlib 只支持 1 arg）
- [x] 4.4 新增 `46_object_protocol/` — 测试 GetType、Equals（同对象/不同对象）、ToString

## 阶段 5: 验证

- [x] 5.1 `dotnet build src/compiler/z42.slnx` — 无编译错误
- [x] 5.2 `cargo build --manifest-path src/runtime/Cargo.toml` — 无编译错误
- [x] 5.3 `dotnet test src/compiler/z42.Tests/z42.Tests.csproj` — 384/384 通过
- [x] 5.4 `./scripts/test-vm.sh interp` — 43/43 通过（含新增 46_object_protocol）
- [x] 5.5 docs/roadmap.md 更新（见下）

## 备注
- Substring 重载通过 `$N` 命名约定实现，IR 中 `Substring$1(start)` / `Substring$2(start, len)` 均绑定到 `__str_substring` builtin
- Object.ToString() 返回 class 的非限定名（如 "MyClass"）
- Object.Equals() 为引用相等（ref eq）
- test 44 的 `__str_format` 保留 builtin，因为 stdlib `Format` 仅支持 1 个 format arg
