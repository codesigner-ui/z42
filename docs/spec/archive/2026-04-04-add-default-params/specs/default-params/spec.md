# Spec: Default Parameter Values

## ADDED Requirements

### Requirement: 声明带默认值的参数

#### Scenario: 函数参数带字面量默认值
- **WHEN** 函数声明参数为 `type name = expr`
- **THEN** 解析成功，`Param.Default` 为该 expr

#### Scenario: 接口方法参数带默认值
- **WHEN** 接口方法声明 `void Draw(string color = "black");`
- **THEN** 解析成功，不报错

#### Scenario: 类方法参数带默认值
- **WHEN** 类方法声明 `public virtual void Draw(string color = "black") { ... }`
- **THEN** 解析成功，方法可正常调用

---

### Requirement: 调用时省略有默认值的参数

#### Scenario: 省略单个尾部默认参数
- **WHEN** `f(x)` 调用了签名为 `f(int a, string b = "hi")` 的函数
- **THEN** 编译成功，IR 中 call 指令参数列表 = `[a, "hi"]`（默认值已展开）

#### Scenario: 省略多个尾部默认参数
- **WHEN** `f()` 调用了签名为 `f(int a = 0, string b = "")` 的函数
- **THEN** 编译成功，IR call 参数 = `[0, ""]`

#### Scenario: 显式覆盖默认参数
- **WHEN** `f(42, "custom")` 调用了 `f(int a = 0, string b = "")`
- **THEN** 编译成功，IR call 参数 = `[42, "custom"]`（不使用默认值）

---

### Requirement: 默认值类型检查

#### Scenario: 默认值类型与参数类型兼容
- **WHEN** `void f(int n = 0)` — int 字面量赋给 int 参数
- **THEN** 无诊断

#### Scenario: 默认值类型与参数类型不兼容
- **WHEN** `void f(int n = "bad")` — string 赋给 int 参数
- **THEN** 报类型不匹配诊断（E0402）

#### Scenario: 必填参数不可省略
- **WHEN** `f()` 调用了签名为 `f(int a, string b = "hi")` 的函数（省略了 `a`）
- **THEN** 报参数数量不足诊断

---

## IR Mapping

默认值在 **call site** 展开，不改变 IR 函数签名。

```
// 源码: Draw()  (省略了 color 参数，默认 "black")
// 生成 IR:
  %s = ConstStr "black"
  call Draw [%s]
```

函数定义 IR 不变（参数数量与显式声明相同）。

## Pipeline Steps
受影响的 pipeline 阶段（按顺序）：
- [x] Parser / AST — Param 增加 Default 字段，ParseParamList 解析 `= expr`
- [ ] TypeChecker — 签名收集存默认值，调用点允许省略参数
- [ ] IR Codegen — call site 补全省略参数的默认值
- [ ] VM interp — 无变更（IR 已展开）
