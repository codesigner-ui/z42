# z42 Auto-Property 语法

> **状态**：2026-04-26 add-auto-property-syntax 落地。class / interface / extern
> 三种位置完整支持 auto-property 形式 `{ get; [set;] }`。
> **使用者视角**；实现原理见 `docs/design/compiler-architecture.md`。

---

## 语法形式

### Class auto-property

```z42
class Box {
    public int Width { get; set; }    // 可读可写
    public int Height { get; }        // 只读（仅 ctor 可写 backing field）
}
```

desugar 为：

- 隐藏字段 `private int __prop_Width;`
- getter 方法 `public int get_Width() { return this.__prop_Width; }`
- 仅当 `set;` 出现时生成 setter `public void set_Width(int value) { this.__prop_Width = value; }`

### Interface property

```z42
interface IShape {
    int Area { get; }
    string Name { get; set; }
}
```

desugar 为方法签名（无 body，无 backing field）：

- `int get_Area();`
- `string get_Name();` + `void set_Name(string value);`

实现类**必须**提供这些方法（方法名约定：`get_<Name>` / `set_<Name>`）。

### Extern property（仅 readonly）

```z42
public class String {
    [Native("__str_length")]
    public extern int Length { get; }
}
```

desugar 为 `extern int get_Length();`，绑定 VM intrinsic。

> 当前 extern property **仅支持 `{ get; }` 形式**；带 setter 的 extern property
> 留待未来需求。

## 用户代码访问

```z42
var b = new Box();
b.Width = 42;             // → b.set_Width(42)
Console.WriteLine(b.Width); // → b.get_Width()
```

TypeChecker 在 binding 阶段把 `obj.Name` 读 / `obj.Name = v` 写 desugar
为 method call（VCall 路径），实现者只看到调用 — codegen 不需要新逻辑。

### 优先级（避免破坏现有字段访问）

成员访问 `obj.X` 解析顺序：

1. 类有字段 `X` → 字段访问（保持原 BoundMember / FieldGet 路径）
2. 否则若类有方法 `get_X()`（参数为 0）→ 视为 property，转 BoundCall
3. 否则若类有方法 `X` → 普通方法引用
4. 否则报错

赋值 `obj.X = v` 解析顺序：

1. 类有方法 `set_X(value)`（1 参数）→ property 写，转 BoundCall
2. 否则按字段写处理（若 X 是字段）
3. 否则报错

> Auto-property 自动生成的字段名 `__prop_<X>` **不与** property 名 `X` 冲突，
> 所以双向访问都正确路由。

## 限制

| 项 | 状态 |
|---|---|
| `{ get; [set;] }` auto-property | ✅ 已支持 |
| accessor 各自可见性 `public T X { get; private set; }` | ⚠️ 解析但**忽略**（统一继承 property visibility） |
| 自定义 body property `int X { get { return _x*2; } }` | ❌ 不支持（推迟） |
| init-only `int X { get; init; }` | ❌ 不支持 |
| Expression-bodied `int X => _x;` | ❌ 不支持 |
| extern property setter | ❌ 不支持（无 stdlib use case） |
| Static property `static int X { get; set; }` | ✅ 支持（会生成 static getter/setter） |

未实现的 property 形式（自定义 body / init / expression-bodied）按需独立
变更补齐。

## 命名约定

- backing field：`__prop_<Name>`（双下划线前缀，与编译器内部命名 `__foreach_i`
  一致；用户代码不应主动使用此前缀）
- getter 方法：`get_<Name>()`
- setter 方法：`set_<Name>(value)`

实现类若手写 `get_X` / `set_X` 方法（即"为接口 property 提供 implementation"）
其语义等价于 auto-property 的合成方法。

## 与 indexer 的关系

L3-G4e indexer `T this[int i] { get; set; }` 同样 desugar 到 `get_Item` /
`set_Item` 方法。auto-property 是同一 desugar pattern 的扩展（成员访问替代
索引访问）。两者在 codegen 中完全统一为 VCall 调用。
