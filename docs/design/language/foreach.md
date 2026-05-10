# z42 foreach 规范

## 设计参考

| 来源 | 借鉴点 |
|------|--------|
| **C#** | `foreach (var x in collection)` 语法；元素类型推断 |
| **Rust** | `for x in iter` 的 iterator 模式（Phase 2 扩展点） |

---

## Phase 1 范围

只支持**数组迭代**，`IEnumerable` / 自定义迭代器留到 Phase 2。

---

## 语法

```csharp
foreach (var item in arr) {
    // item 类型由数组元素类型推断
}

foreach (int x in intArr) {
    // 显式类型，TypeChecker 检查元素类型是否可赋值给 int
}
```

---

## 语义

- `arr` 求值一次（不会重复求值）
- `item` / `x` 在循环体内只读（不允许赋值；赋值为编译错误）
- 支持 `break`（跳出循环）和 `continue`（跳到下一次迭代），脱糖后与 while 的 break/continue 行为一致

---

## 脱糖到 IR

`foreach (var item in arr) { body }` 等价于：

```
int __len = arr.Length;
int __i   = 0;
while (__i < __len) {
    T item = arr[__i];
    body;
    __i = __i + 1;
}
```

对应 IR 块结构：

```
%len = array_len %arr
%i   = const.i32 0
store "__foreach_i" %i
br foreach_cond

foreach_cond:
  %i_cur = load "__foreach_i"
  %cmp   = lt %i_cur, %len
  br.cond %cmp, foreach_body, foreach_exit

foreach_body:
  %item  = array_get %arr, %i_cur
  store "item" %item
  ... body instructions ...
  %i_next = add %i_cur, const.i32(1)
  store "__foreach_i" %i_next
  br foreach_cond

foreach_exit:
  ...
```

`__foreach_i` 是编译器生成的内部变量名，不与用户变量冲突（双下划线前缀规约）。

---

## TypeChecker

- `arr` 必须是 `T[]` 类型
- `var item`：推断类型为 `T`
- `SomeType item`：检查 `T` 可赋值给 `SomeType`
- 循环体内 `item` 的赋值报错：`error Z0403: foreach iteration variable 'item' is read-only`

---

## 不在此规范范围内

- 字符串字符迭代 `foreach (char c in str)`（Phase 2）
- 自定义 `IEnumerable` / Iterator trait（Phase 2）
- `break` / `continue` 在 foreach 中（若 while 已有，可与本特性同批实现）
