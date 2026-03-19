# Namespace 与 Using — Phase 1 规范

## 概述

Phase 1 完全对齐 C# 9–12 的 file-scoped namespace 语法，目标是：
- 支持 `namespace Foo.Bar;` 声明文件所属命名空间
- 支持 `using Foo.Bar;` 导入命名空间
- 支持 `using Alias = Foo.Bar.MyClass;` 类型别名
- IrGen 按命名空间限定函数/类名称（qualified name）
- Driver 按命名空间解析入口函数

---

## 语法

```
namespace_decl ::= "namespace" dotted_name ";"
using_decl     ::= "using" ( alias "=" )? dotted_name ";"
dotted_name    ::= IDENT ( "." IDENT )*
alias          ::= IDENT
```

- 每个文件至多一条 `namespace` 声明，必须出现在所有 `using` 之前、所有顶层声明之前
- `using` 声明紧跟在 `namespace`（或文件开头）之后，所有顶层声明之前
- Phase 1 不支持 block-scoped namespace（`namespace Foo { ... }`）

---

## 单文件语义

### 命名空间声明

```z42
namespace Demo;

class Point { ... }
void Helper() { ... }
```

- `Point` 的 qualified name = `Demo.Point`
- `Helper` 的 qualified name = `Demo.Helper`
- IrModule.Namespace = `"Demo"`

### Using 导入（单文件中不影响解析，仅记录）

```z42
using System;
using MyLib.Utils;
```

Phase 1 中 `using` 仅被 parser 记录到 `CompilationUnit.Usings`，TypeChecker 和 IrGen 不做跨文件解析（单文件模式下无其他文件可导入）。

### Using 别名

```z42
using MyPoint = Demo.Point;
```

Phase 1 中别名同样仅被 parser 记录，不做别名替换（单文件模式下别名等同于原始类型）。

---

## IR 名称限定规则

| 声明 | 无 namespace | 有 `namespace Foo` |
|------|-------------|-------------------|
| 顶层函数 `void Bar()` | `Bar` | `Foo.Bar` |
| 类 `class Baz` | `Baz` | `Foo.Baz` |
| 类方法 `class Baz { void M() }` | `Baz.M` | `Foo.Baz.M` |
| 构造函数 `class Baz { Baz() }` | `Baz.Baz` | `Foo.Baz.Baz` |

IrModule.Namespace 存储 `"Foo"`（或 `"main"` 当无 namespace 时）。

---

## 入口函数解析

Driver 按以下顺序查找入口函数：

1. `{Namespace}.Main`（有 namespace 时优先）
2. `Main`（无限定名回退）
3. `{Namespace}.main`（小写 main）
4. `main`（无限定名小写回退）

若找不到以上任一函数，报错退出。

---

## 多文件编译（Phase 1 预留，暂未实现）

- Driver 接受多个 `.z42` 文件或一个目录
- 每个文件独立 parse，得到各自的 `CompilationUnit`
- Linker 合并 `CompilationUnit` 列表：
  - 将所有 `Classes`、`Functions`、`Enums` 合并到一个 `CompilationUnit`
  - 保留各文件的 `Namespace`，用于 qualified name 生成
- 多文件场景下 `using Foo.Bar;` 将解析为：把命名空间 `Foo.Bar` 下的所有顶层符号导入当前文件作用域

---

## 错误处理

| 错误情形 | 错误消息 |
|----------|----------|
| `namespace` 出现在顶层声明之后 | `namespace declaration must appear before any top-level declarations` |
| 同一文件出现两条 `namespace` | `duplicate namespace declaration` |
| `using` 出现在顶层声明之后 | `using directive must appear before any top-level declarations` |

---

## 示例

### 单文件带命名空间

```z42
namespace Demo;

class Point {
    int X;
    int Y;
    Point(int x, int y) {
        this.X = x;
        this.Y = y;
    }
    string ToString() {
        return $"({this.X}, {this.Y})";
    }
}

void Main() {
    var p = new Point(3, 4);
    Console.WriteLine(p.ToString());
}
```

生成的 IR 函数名：`Demo.Point.Point`、`Demo.Point.ToString`、`Demo.Main`

入口函数：`Demo.Main`

### Using 别名（记录但暂不解析）

```z42
namespace App;
using MyUtils;
using Pt = Demo.Point;

void Main() {
    Console.WriteLine("hello");
}
```

生成入口：`App.Main`
