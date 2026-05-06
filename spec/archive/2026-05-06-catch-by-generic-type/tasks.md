# Tasks: catch-by-generic-type

> 状态：🟢 已完成 | 创建：2026-05-06 | 完成：2026-05-06 | 类型：lang/vm（完整流程）

## 进度概览

- [x] 阶段 1: BoundCatchClause + 错误码（数据结构基础）
- [x] 阶段 2: TypeChecker 解析 + 校验
- [x] 阶段 3: IrGen emit catch_type
- [x] 阶段 4: VM interp `find_handler` 类型过滤
- [x] 阶段 5: VM JIT throw 路径同步
- [x] 阶段 6: Golden tests + C# 单测 + Rust 单测
- [x] 阶段 7: 文档同步 + 验证 + 归档

---

## 阶段 1: 数据结构基础

- [x] 1.1 [BoundCatchClause](src/compiler/z42.Semantics/Bound/BoundStmt.cs) 增 `string? ExceptionTypeName` 字段
- [x] 1.2 [DiagnosticCodes.InvalidCatchType](src/compiler/z42.Core/Diagnostics/Diagnostic.cs) 新增 `E0420`
- [x] 1.3 [DiagnosticCatalog](src/compiler/z42.Core/Diagnostics/DiagnosticCatalog.cs) 增 E0420 条目（title + message + 2 个示例）

## 阶段 2: TypeChecker 解析 + 校验

- [x] 2.1 [TypeChecker.Stmts.cs](src/compiler/z42.Semantics/TypeCheck/TypeChecker.Stmts.cs) `case TryCatchStmt` 内联调 `TryResolveCatchType`
- [x] 2.2 实现 `TryResolveCatchType(typeName, out fqName, out error)`：
  - 查 `_symbols.Classes` + `_imported?.Classes`
  - 沿 BaseClassName 上溯到 `Exception`（depth 上限 32 防环）
  - 返回 FQ name 或 error
- [x] 2.3 失败时 `_diags.Error(InvalidCatchType, ..., clause.Span)`，BoundCatchClause.ExceptionTypeName=null
- [x] 2.4 成功时把 FQ name 存入 BoundCatchClause

## 阶段 3: IrGen emit catch_type

- [x] 3.1 [FunctionEmitterStmts.cs:439](src/compiler/z42.Semantics/Codegen/FunctionEmitterStmts.cs#L439) 删除 `null` 写入注释 + 改用 `clause.ExceptionTypeName`
- [x] 3.2 验证 IrExceptionEntry.CatchType 在 ZasmWriter / ZbcWriter / ZbcReader round-trip 已支持（无需改 IR 元数据格式）

## 阶段 4: VM interp `find_handler` 类型过滤

- [x] 4.1 [interp/mod.rs::find_handler](src/runtime/src/interp/mod.rs) 签名升级：接 `type_registry` + `thrown: &Value`
- [x] 4.2 实现 catch_type 过滤分支（None / "*" / typed match via `is_subclass_or_eq_td`）
- [x] 4.3 更新 2 个调用点（line 380 callee-throw 路径 + line 432 user-throw Terminator）
- [x] 4.4 同模块 `_tests.rs` 加单测：覆盖 None / "*" / typed-match / typed-mismatch 四分支

## 阶段 5: VM JIT throw 路径同步

- [x] 5.1 [jit/translate.rs:378](src/runtime/src/jit/translate.rs#L378) JIT throw helper 路径补 catch_type 过滤（trampoline 回 interp 即可复用 find_handler）
- [x] 5.2 现有 110_gc_jit_transitive / closure_l3_* 等 JIT golden 全 PASS（无类型 catch wildcard 兼容性）

## 阶段 6: Tests

- [x] 6.1 `src/tests/exceptions/catch_by_type/` golden（IOException + IOException 匹配）
- [x] 6.2 `src/tests/exceptions/catch_by_type_subclass/` golden（抛 derived，catch base）
- [x] 6.3 `src/tests/exceptions/catch_by_type_order/` golden（多 catch 源序）
- [x] 6.4 `src/tests/exceptions/catch_wildcard_compat/` golden（catch{} / catch(e) 兼容）
- [x] 6.5 `src/tests/errors/420_invalid_catch_type/` 错误用例
- [x] 6.6 [src/compiler/z42.Tests/CatchByTypeTests.cs](src/compiler/z42.Tests/CatchByTypeTests.cs) 新建：
  - BoundCatchClause.ExceptionTypeName 填充测试（typed / untyped）
  - IrExceptionEntry.CatchType emission 测试
  - E0420 触发条件测试（unknown type / non-Exception 子类）
  - Inheritance chain 解析测试
- [x] 6.7 `regen-golden-tests.sh` 重生新 case 的 .zbc

## 阶段 7: 文档同步 + 验证 + 归档

- [x] 7.1 [docs/design/exceptions.md](docs/design/exceptions.md) 加"catch 类型过滤"章节（语义 + 子类匹配 + E0420）
- [x] 7.2 [docs/deferred.md](docs/deferred.md) 移除 D-8b-2 active 条目，登记到"已落地"
- [x] 7.3 验证全套：
  - `dotnet build src/compiler/z42.slnx` 无错
  - `cargo build --manifest-path src/runtime/Cargo.toml` 无错
  - `dotnet test` 全过（含新 CatchByTypeTests）
  - `./scripts/test-vm.sh interp` 全过（4 个新 golden + 现有所有 PASS）
  - `./scripts/test-vm.sh jit` 全过
  - `./scripts/test-cross-zpkg.sh` 全过
  - `cargo test --manifest-path src/runtime/Cargo.toml` 全过（含新 find_handler 单测）
- [x] 7.4 commit + push（`fix(typecheck+vm): catch clauses filter by declared exception type`）
- [x] 7.5 归档 `spec/changes/catch-by-generic-type/` → `spec/archive/2026-05-06-catch-by-generic-type/`

---

## 备注

### 验证场景与 spec.md scenarios 的映射

| spec scenario | 验证位置 |
|--------------|---------|
| Catch matches exact type | golden `catch_by_type/` |
| Catch matches subclass via base type | golden `catch_by_type_subclass/` |
| Catch does NOT match unrelated type | golden `catch_by_type/`（IOException 抛 + 仅 NullRef catch）|
| Multiple catches — first source-order match wins | golden `catch_by_type_order/` |
| Catch (Exception e) is universal | 现有 91_exception_base 已覆盖（typed Exception catch）|
| Untyped catch retains wildcard | golden `catch_wildcard_compat/` + 现有 12_exceptions / 41_try_finally / 57_nested_exceptions / 60_finally_propagation 回归 |
| Catch type must be known class | golden errors `420_invalid_catch_type/` + CatchByTypeTests E0420 单测 |
| Catch type must be Exception subclass | CatchByTypeTests E0420 单测（class Foo {} + catch (Foo e)）|
| Catch (Exception e) is allowed | CatchByTypeTests positive case |
| Multi-level inheritance match | golden `catch_by_type_subclass/`（L1→L2→L3 链）+ Rust find_handler 单测 |
| Mismatched derived branch doesn't match parent's sibling | Rust find_handler 单测（type_registry 设置 + 验证 None） |
