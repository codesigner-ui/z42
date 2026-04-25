# z42 异常机制

> **状态**：L1 throw/catch 语法 ✅；Wave 2（2026-04-25）补齐 stdlib `Exception` 类层次。
> **使用者视角**；实现原理见 `docs/design/vm-architecture.md`。

---

## 抛出与捕获

```z42
try {
    if (x < 0)
        throw new ArgumentException("x must be non-negative");
    DoWork(x);
} catch (Exception e) {
    Console.WriteLine(e.Message);
}
```

- `throw <expr>` 把任意值（Phase 1 兼容）或 `Exception` 子类实例传给最近的
  匹配 `catch` 块
- `catch (Exception e)` 当前为"通用 catch"，绑定抛出的值（无类型过滤）
- 未来 L2/L3 可能加入按类型过滤（`catch (ArgumentException e) { ... }`）

## Exception 基类

```z42
public class Exception {
    public string Message;          // 异常消息
    public string StackTrace;       // 调用栈快照（Phase 1 占位，恒为 null）
    public Exception InnerException; // 包裹的原始异常（无则 null）

    public Exception(string message) { ... }

    override string ToString();  // "Exception: <Message>"
}
```

**Phase 1 限制**（Wave 2 实施时记录的 backlog）：

1. **字段不支持 `?` 可空标注** — Parser 限制；当前 ref-type 字段默认
   nullable（运行时正确，但类型层面无 explicit null tracking）
2. **构造器不支持重载** — VM ObjNew 按 `Class.SimpleName` 查找 ctor，
   stdlib 编译生成 `$1`/`$2` arity suffix 时无法命中。当前 Exception
   仅一个 ctor；wrapping pattern 需用 setter（见下）
3. **同类型字段 self-reference assign 报 E0402** — TypeChecker 限制；
   用户代码 `outer.InnerException = inner;` 当前无法编译。Wave 2 不
   尝试在用户代码层使用 InnerException；stdlib 内部抛出可以维持空 inner

## 标准子类

| 子类 | 继承自 | 何时用 |
|------|--------|--------|
| `ArgumentException` | Exception | 参数非法（值 / 组合不符合契约）|
| `ArgumentNullException` | ArgumentException | 参数为 null（更细分）|
| `InvalidOperationException` | Exception | 对象状态不允许（如空 Queue Dequeue）|
| `NullReferenceException` | Exception | 解引用 null |
| `IndexOutOfRangeException` | Exception | 索引越界 |
| `KeyNotFoundException` | Exception | 字典 / Map 找不到键 |
| `FormatException` | Exception | 字符串解析失败（`int.Parse` 等）|
| `NotImplementedException` | Exception | 方法未实现 |
| `NotSupportedException` | Exception | 方法不支持当前场景 |

每个子类只有 ctor 转发 + override ToString 返回 `"<ClassName>: <Message>"`
（硬编码类名字符串，不依赖反射；L3-R 后可重构为统一 `GetType().Name`）。

## Phase 1 兼容性

- 仍支持 `throw "string"` / `throw 42` 等任意值抛出（保持 L1 行为）
- stdlib 自身 / 新代码**推荐**用 `throw new <ExceptionType>(...)`
- catch 子句无视类型过滤，`catch (Exception e)` 捕获一切（pre-Wave 2 行为）

## 未来计划（按需推进）

| 项 | 说明 |
|---|----|
| StackTrace 自动填充 | 需 VM 在 throw 时收集帧链；零 source 改动可上线 |
| catch 按类型过滤 | `catch (T e)` 仅匹配 T 子类；需要 TypeChecker + IR 配合 |
| Exception ctor 重载 | 等 VM ObjNew 支持 arity suffix 查找后补 `Exception(msg, inner)` |
| 字段 `?` 可空标注 | 等 Parser / TypeChecker 扩展字段 nullable |
| 同类型字段 self-assign | E0402 修复；解锁用户代码 InnerException 链使用 |
| 派生子类扩展 | `OverflowException` / `DivideByZeroException` / `IOException` 按需加 |
