# Design: catch-by-generic-type

## Architecture

```
Source                Parser              TypeChecker             IrGen
─────────             ─────────           ───────────             ─────
catch (T e) {       → CatchClause       → BoundCatchClause     → IrExceptionEntry
   ...                ExceptionType      ExceptionTypeName        CatchType =
}                    : "T"               : "FQ.T" (resolved)      "FQ.T"

                     ┌── E0420 if T 不存在 / 不是 Exception 子类
                     └── 写 exception_table
                            ↓ zbc 序列化（已有 path）
                            ↓
Rust VM (interp/JIT)
─────────────────────
exception_table: Vec<ExceptionEntry { catch_type: Option<String>, ... }>

throw flow:
  thrown_val (Value::Object)
    ↓
  find_handler(func, block_idx, type_registry, thrown_val)
    ↓ scan exception_table in source order
    ↓ for each entry covering block_idx:
    ↓   match entry.catch_type:
    ↓     None      → match (wildcard / untyped catch)
    ↓     Some("*") → match (synthetic finally fallthrough)
    ↓     Some(T)   → is_subclass_or_eq_td(thrown.class, T)
    ↓ first match wins
```

## Decisions

### Decision 1: BoundCatchClause 字段类型 — string FQ name vs Z42Type 引用

**问题**：BoundCatchClause 应该存"已解析的 z42 类型对象"还是"FQ 类型字符串"？

**选项**：
- A. `Z42Type? ExceptionType` — 类型对象（与其他 Bound 节点一致）
- B. `string? ExceptionTypeName` — FQ 字符串（直接对应 IrExceptionEntry.CatchType）

**决定**：选 **B**（FQ 字符串）。理由：
1. IrExceptionEntry.CatchType 早就是 string 字段，IR/zbc 序列化无需任何改动
2. VM 端 `is_subclass_or_eq_td` 接受字符串，零间接层
3. catch type 在编译期 instance-of 检查的语义是"class 名称匹配 + 沿继承链上溯"，不需要 Z42Type 的结构化表示（不需要泛型 substitution、约束求解等）
4. 与 IrExceptionEntry 同构降低心智负担（类似 IsInstance / AsCast 指令也存 class_name 字符串）

E0420 校验仍在 TypeChecker 期完成（用 SymbolTable + ImportedSymbols 查 Exception 链），校验通过后**只把 FQ 名存进 BoundCatchClause**。

### Decision 2: 类型校验位置 — TypeChecker.Stmts.cs 还是独立 visitor

**问题**：哪里做"catch type 必须是 Exception 子类"校验？

**决定**：在 TypeChecker.Stmts.cs `case TryCatchStmt` 分支内联完成。理由：
- 此处已经有访问 catch clause 的代码路径
- catch type 校验逻辑短（~20 行），单独 visitor 增加文件数无收益
- 与现有 `_catchVarStack.Push(catchVar)` 逻辑紧邻、共享 scope

实现：
```csharp
string? exTypeFq = null;
if (clause.ExceptionType is { } exTypeName)
{
    if (!TryResolveCatchType(exTypeName, out var resolvedFq, out var err))
        _diags.Error(DiagnosticCodes.InvalidCatchType, err, clause.Span);
    else
        exTypeFq = resolvedFq;
}
catches.Add(new BoundCatchClause(clause.VarName, exTypeFq, body, clause.Span));
```

`TryResolveCatchType` 流程：
1. 查 `_symbols.Classes` + `_imported?.Classes` 找 short name
2. 若找到 → 沿 BaseClassName 上溯到 `Exception`
3. 上溯成功 → 返回 FQ 名（imported 用 `ImportedClassNamespaces[name]`，本包用 `cu.Namespace`）
4. 上溯到顶仍非 Exception → 返回 false + 错误信息
5. 类不存在 → 返回 false + 错误信息

### Decision 3: VM `find_handler` 是否破坏二进制 ABI

**问题**：扩展 find_handler 签名（多接 type_registry + thrown）是否影响 zbc 格式或外部 ABI？

**决定**：纯 Rust 内部签名变更，**不**影响 zbc 二进制格式（IR 层 CatchType 字段已存在）、不影响 stdlib 二进制兼容（VM internals）、不影响 cross-zpkg 路径。

JIT 路径（[translate.rs:378](src/runtime/src/jit/translate.rs#L378)）当前用与 interp 类似的 find-handler-by-block-idx 逻辑，需要同步注入 thrown 值过滤。看实现细节：JIT throw helper 通过 helper trampoline 把控制权交还 interp，那时再调 find_handler 即可。

### Decision 4: 多 catch 子句的源序保留

**问题**：z42 编译器是否保证 IrExceptionEntry 的写入顺序 = catch 源序？

**决定**：是。当前 [FunctionEmitterStmts.cs:434](src/compiler/z42.Semantics/Codegen/FunctionEmitterStmts.cs#L434) `foreach (var clause in tc.Catches)` 遍历是 BoundTryCatch.Catches 的迭代顺序，BoundTryCatch.Catches 是 List 保留 parser 给的源序。VM 端 `for (i, entry) in func.exception_table.iter().enumerate()` 也按 Vec 顺序扫，第一个匹配返回，自然 first-source-match-wins。

无需额外排序或元数据。spec 场景"Multiple catches — first source-order match wins"自动满足。

### Decision 5: 现有 finally fallthrough sentinel "*"

**问题**：[FunctionEmitterStmts.cs:460](src/compiler/z42.Semantics/Codegen/FunctionEmitterStmts.cs#L460) 已有 `catch all -> finally` 的 sentinel `"*"`，与本变更新增的 typed catch 是否冲突？

**决定**：保留 `"*"` 作为 synthetic finally catchall 标记，与 user-typed catch 区分：
- `null` → user 写 `catch { }` / `catch (e)`（无类型）
- `"*"` → 编译器合成的 finally-only fallback（用户没写 catch）
- `"FQ.T"` → user 写 `catch (T e)`

VM `find_handler` 三分支处理（None / "*" / 其他）。语义上 `null` 和 `"*"` 都是 wildcard，但来源不同；保留区分便于将来诊断。

### Decision 6: Generic catch type（`catch (MulticastException<int> e)`）当前形态

**问题**：本变更是否处理泛型 catch 类型？

**决定**：**透明支持，按字符串路径走**。
- TypeChecker 把 `MulticastException<int>` 解析为字符串（z42 现有 generic instantiated type 已能 round-trip 字符串名 `MulticastException$1<int>` 之类）
- IrExceptionEntry.CatchType 写完整 FQ 名（含 type-args）
- VM 比对 thrown.class.name vs catch_type 字符串

当前 z42 generic class 实例化已经实际使用 mangled name，`is_subclass_or_eq_td` 走的是 type_registry 的字符串 lookup。所以**自动 piggyback** generic case，**无需等 D-8b-0**。

例外：D-8b-0 没解决前，同名 generic / non-generic 共存（`MulticastException` + `MulticastException<R>`）会在 class registry 阶段就冲突，那一步在 catch 之前就被挡掉，不影响本变更。

## Implementation Notes

### TypeChecker 校验 Exception 子类的算法

```csharp
private bool TryResolveCatchType(string typeName, out string fqName, out string error)
{
    // 1. resolve short → FQ
    string? fq = null;
    if (_symbols.Classes.TryGetValue(typeName, out _))
        fq = _currentNamespace is null ? typeName : $"{_currentNamespace}.{typeName}";
    else if (_symbols.ImportedClassNamespaces.TryGetValue(typeName, out var ns))
        fq = $"{ns}.{typeName}";
    else
    {
        error = $"catch type '{typeName}' not found";
        fqName = "";
        return false;
    }

    // 2. walk BaseClass chain to Exception
    string current = typeName;
    int depth = 0;
    while (depth < 32)  // sanity bound
    {
        if (current == "Exception") { error = ""; fqName = fq!; return true; }
        var baseName = LookupBaseClass(current);
        if (baseName is null) break;
        current = baseName;
        depth++;
    }

    error = $"catch type '{typeName}' must derive from Exception";
    fqName = "";
    return false;
}
```

`LookupBaseClass` 优先查 `_symbols.Classes`，再查 `_imported?.Classes`。

### VM `find_handler` 签名升级

```rust
fn find_handler(
    func: &Function,
    block_idx: usize,
    block_map: &HashMap<String, usize>,
    type_registry: &TypeRegistry,
    thrown: &Value,
) -> Option<usize> {
    let thrown_class = match thrown {
        Value::Object(rc) => rc.borrow().type_desc.name.clone(),
        _ => return None, // z42 throw 必为 Object
    };
    for (i, entry) in func.exception_table.iter().enumerate() {
        let start_idx = *block_map.get(&entry.try_start)?;
        let end_idx   = *block_map.get(&entry.try_end)?;
        if !(block_idx >= start_idx && block_idx < end_idx) { continue; }

        match entry.catch_type.as_deref() {
            None        => return Some(i),                                          // user untyped
            Some("*")   => return Some(i),                                          // synthetic finally
            Some(t) if is_subclass_or_eq_td(type_registry, &thrown_class, t)
                        => return Some(i),
            _           => continue, // type mismatch — try next entry
        }
    }
    None
}
```

旧调用点（interp/mod.rs:380, 432; jit/translate.rs:378 路径）全部传入 `&module.type_registry` + `&thrown_val`。

### 错误码 E0420

```csharp
public const string InvalidCatchType = "E0420";

DiagnosticCatalog 条目：
- Title: "Catch type must be a known Exception-derived class"
- Message: "A `catch (T e)` clause requires `T` to be a known class that derives from `Exception`."
- Examples:
    catch (NoSuchType e) {} // E0420: catch type 'NoSuchType' not found
    catch (Foo e) {}        // E0420: catch type 'Foo' must derive from Exception
```

数字 0420 与现有 E0414 (event field), E0413 (...) 等异常 / class 错误码相邻。

## Testing Strategy

- **Golden run tests**（[src/tests/exceptions/](src/tests/exceptions/)）:
  - `catch_by_type/` — 单 catch 类型过滤（通过 IOException + 不通过 NullRef）
  - `catch_by_type_subclass/` — 抛 derived，catch base，触发
  - `catch_by_type_order/` — 多 catch 源序选择
  - `catch_wildcard_compat/` — 无类型 catch 仍 wildcard（确保现有 12_exceptions / 41_try_finally / 57_nested_exceptions / 60_finally_propagation / 67_stack_trace / 91_exception_base / 92_exception_subclass 全 PASS）

- **Golden error tests**（[src/tests/errors/](src/tests/errors/)）:
  - `420_invalid_catch_type/` — `catch (NotExc e)` 触发 E0420

- **C# 单元测试**（[src/compiler/z42.Tests/CatchByTypeTests.cs](src/compiler/z42.Tests/CatchByTypeTests.cs)）:
  - BoundCatchClause.ExceptionTypeName 在 typed catch / untyped catch 上正确填充
  - IrExceptionEntry.CatchType 序列化 round-trip
  - E0420 在 invalid catch type 上触发，正确 catch 不报错
  - Multi-level inheritance walk 可达 Exception

- **Rust 单元测试**（[src/runtime/src/interp/mod.rs](src/runtime/src/interp/mod.rs) 同模块 `_tests.rs`）:
  - `find_handler` 在 catch_type=None / "*" / typed-match / typed-mismatch 四个分支正确选择
  - 多 entry 时返回首个匹配
- **回归验证**：现有 7 个异常 goldens 必须全 PASS（无类型 catch 的兼容性）；现有 88+ 异常 unit tests 必须全过
