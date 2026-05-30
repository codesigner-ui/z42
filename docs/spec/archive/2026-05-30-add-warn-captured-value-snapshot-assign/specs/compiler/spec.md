# Spec: W0604 — captured value-snapshot assign warning

## ADDED Requirements

### Requirement: 写 value-type captured var → W0604

Lambda / local-function 体内对外部捕获的 value-type 变量（int / bool /
double / char / struct / enum value）的赋值，emit `W0604` warning at
the assignment span。

#### Scenario: bool capture write 触发 W0604

- **GIVEN**
  ```z42
  void F() {
      bool x = false;
      Action a = () => { x = true; };
      a();
  }
  ```
- **WHEN** TypeCheck 完成
- **THEN** 报 `W0604` warning at `x = true;` 的 span，message 含
  `captured value-type variable 'x'` 与 `wrap in a class` 指引

#### Scenario: int capture compound assign 同样触发

- **GIVEN**
  ```z42
  void F() {
      int n = 0;
      Action a = () => { n = n + 1; };
      a();
  }
  ```
- **WHEN** TypeCheck 完成
- **THEN** 报 `W0604`

### Requirement: 写 reference-type captured var → 无 warning

引用类型按对象身份共享（closure.md §4.2），赋值改变的是 closure-local
slot，但通过对象身份的字段修改是合法且常用模式。

#### Scenario: 写 captured class field 无 warning

- **GIVEN**
  ```z42
  class Counter { public int n = 0; }
  void F() {
      var c = new Counter();
      Action a = () => { c.n = c.n + 1; };  // 写 c 的 FIELD，不是 c 本身
      a();
  }
  ```
- **WHEN** TypeCheck 完成
- **THEN** 无 W0604（field assign，target 是 BoundMember，不是 BoundCapturedIdent）

#### Scenario: 写 captured array element 无 warning

- **GIVEN**
  ```z42
  void F() {
      bool[] cell = new bool[1];
      Action a = () => { cell[0] = true; };  // 写 cell 元素，array 是 reference type
      a();
  }
  ```
- **WHEN** TypeCheck 完成
- **THEN** 无 W0604（target 是 BoundIndex，array 类型 IsReferenceType true 但
  这里 target 不是 BoundCapturedIdent；同时 `cell` 自身重新赋值会触发 W0604）

### Requirement: lambda-local var 不触发

#### Scenario: lambda 内声明的局部 var

- **GIVEN**
  ```z42
  void F() {
      Action a = () => {
          int local = 5;
          local = 10;          // local 不是 captured
      };
  }
  ```
- **WHEN** TypeCheck 完成
- **THEN** 无 W0604（`local` 在 lambda env 内声明，BoundAssign.Target 是
  普通 BoundIdent，不是 BoundCapturedIdent）

### Requirement: 嵌套 lambda 按最内层 frame 的 capture 判断

#### Scenario: 内层写外层捕获

- **GIVEN**
  ```z42
  void F() {
      int x = 0;
      Action outer = () => {
          Action inner = () => { x = 1; };  // x 被 inner 捕获（也被 outer 捕获）
          inner();
      };
  }
  ```
- **WHEN** TypeCheck 完成
- **THEN** 在 `x = 1` 处报 1 个 W0604（inner frame 看到的 BoundCapturedIdent）

### Requirement: 非 lambda 上下文 — 无 W0604（正交）

#### Scenario: 顶层函数赋值不报

- **GIVEN**
  ```z42
  bool g = false;
  void F() { g = true; }     // 全局 var；不是 capture
  ```
- **WHEN** TypeCheck 完成
- **THEN** 无 W0604

## MODIFIED Requirements

无 — 纯增。

## IR Mapping

无 — 只在 Bind / TypeCheck phase 加 diagnostic，IR codegen 行为不变。
ValueSnapshot capture 仍按 closure.md §4.1 lift；warning 不阻塞 codegen。

## Pipeline Steps

- [ ] Lexer — N/A
- [ ] Parser / AST — N/A
- [x] TypeChecker — BindAssign 末尾 + 新 W0604 code
- [ ] IR Codegen — N/A
- [ ] VM interp — N/A
