# Spec: Exception hierarchy + IEnumerable/IEnumerator contracts

## ADDED Requirements

### Requirement: Exception 基类

`z42.core` 提供 `class Exception`，作为所有异常类型的推荐基类。构造与字段
访问走普通 class 路径（Script-First，零 VM extern）。

#### Scenario: 构造 + 访问 Message
- **WHEN** 用户写 `var e = new Exception("oops");`
- **AND** 读取 `e.Message`
- **THEN** 得到字符串 `"oops"`

#### Scenario: 构造 + InnerException 链
- **WHEN** `var inner = new Exception("cause"); var outer = new Exception("wrap", inner);`
- **THEN** `outer.InnerException.Message == "cause"`

#### Scenario: StackTrace 字段存在但默认为 null
- **WHEN** `var e = new Exception("x");`
- **THEN** `e.StackTrace` 返回 `null`
- **AND** 类型能读写（未来实现填充时无需修改接口）

#### Scenario: ToString 默认格式
- **WHEN** `var e = new Exception("msg"); Console.WriteLine(e.ToString());`
- **THEN** 输出形如 `"Exception: msg"`（`<ClassName>: <Message>`）
- **AND** 子类覆盖 ToString 时行为一致（自动使用子类名）

### Requirement: Exception 子类（9 个）

所有子类仅继承 `Exception` + 构造器转发（`: base(msg)` / `: base(msg, inner)`），
不加字段。方法按需覆盖 `ToString()` 取子类名（通过 `GetType()` 或硬编码皆可）。

子类清单（对齐 C# BCL 常用子集）：

| 子类 | 语义 |
|------|------|
| `ArgumentException` | 参数非法（值或组合不符合要求）|
| `ArgumentNullException` : `ArgumentException` | 参数为 null（`ArgumentException` 子类）|
| `InvalidOperationException` | 对象当前状态不允许此操作（如空队列 Dequeue）|
| `NullReferenceException` | 解引用 null（字段访问 / 方法调用）|
| `IndexOutOfRangeException` | 索引越界 |
| `KeyNotFoundException` | 字典查找键不存在 |
| `FormatException` | 字符串解析失败（Parse / Format）|
| `NotImplementedException` | 方法已声明但未实现 |
| `NotSupportedException` | 方法不支持当前场景 |

#### Scenario: 子类构造 + 捕获
- **WHEN** `try { throw new ArgumentException("bad"); } catch (Exception e) { Console.WriteLine(e.Message); }`
- **THEN** 输出 `"bad"`
- **AND** 运行时类型为 `ArgumentException`（可通过 `e is ArgumentException` 判断）

#### Scenario: ArgumentNullException 是 ArgumentException 的子类
- **WHEN** 捕获块写 `catch (ArgumentException e)`，且 throw 的是 `new ArgumentNullException("x")`
- **THEN** 捕获命中（Phase 1 如无 catch 类型过滤则按用户代码 is-check 进行）

### Requirement: IEnumerable<T> 接口

```z42
public interface IEnumerable<T> {
    IEnumerator<T> GetEnumerator();
}
```

#### Scenario: 泛型约束可用
- **WHEN** 定义 `T Sum<T>(IEnumerable<T> src) where T: INumber<T> { ... }`
- **THEN** 编译通过；约束 `IEnumerable<T>` 被 TypeChecker 正确解析
- **AND** 传入实现 `IEnumerable<int>` 的类时类型检查放行

#### Scenario: 显式实现
- **WHEN** `class MyList<T> : IEnumerable<T> { public IEnumerator<T> GetEnumerator() { ... } }`
- **THEN** 编译通过
- **AND** `MyList<int>` 可赋值给 `IEnumerable<int>` 变量

### Requirement: IEnumerator<T> 接口

```z42
public interface IEnumerator<T> : IDisposable {
    bool MoveNext();
    T Current { get; }
}
```

- 继承 `IDisposable`（L1 已有），使得 foreach/using 场景未来可统一清理
- `Current` 定义为 getter property（L3-G4e 索引器语法已支持 property；
  接口 property 需确认 parser/TypeCheck 支持，否则退化为 `T Current()` 方法）

#### Scenario: 自定义 iterator 实现
- **WHEN** `class RangeIterator : IEnumerator<int> { public bool MoveNext() {...} public int Current { get {...} } public void Dispose() {} }`
- **THEN** 编译通过，可通过 `IEnumerator<int>` 引用调用

### Requirement: foreach 语义不变

foreach codegen **不修改**。现有鸭子协议（`int Count()` + `T get_Item(int)`）
对 `IEnumerable` 实现者不会自动生效 —— 用户若想 foreach 一个 IEnumerable
但没有 Count/get_Item 的类型，本 Wave 不支持（保留到未来升级）。

#### Scenario: 现有 foreach 行为保持
- **WHEN** 现有 golden `run/80_stdlib_arraylist` / `83_foreach_user_class` 等
- **THEN** 全部继续通过，无行为变化

### Requirement: Script-First 实现

所有新增类型 / 接口用 `.z42` 脚本实现。不允许新增 VM extern / intrinsic。

#### Scenario: stdlib 构建
- **WHEN** `./scripts/build-stdlib.sh`
- **THEN** `z42.core.zpkg` 构建成功，含新增 Exception + 9 子类 +
  IEnumerable + IEnumerator 符号
- **AND** 其他 zpkg 无影响；VM 无需重新编译

## IR Mapping

本 Wave 不新增 IR 指令；`throw` / `try` / `catch` 指令语义不变。
新增的类和接口走普通 `ClassDesc` / `InterfaceDef` + VCall 路径。

## Pipeline Steps

- [ ] Lexer — 不涉及
- [ ] Parser / AST — 不涉及（需确认接口 property 语法已支持；若否，退化为方法）
- [x] TypeChecker — 消费新接口（通过 stdlib TSIG 自动可见）
- [ ] IR Codegen — 不涉及（Exception 子类 / 接口经普通 class 路径）
- [ ] VM interp — 不涉及（零 extern / 零新指令）
- [x] stdlib build — 编译新增 .z42 文件进 z42.core.zpkg
