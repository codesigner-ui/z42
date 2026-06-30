# Spec: params 变长参数

## ADDED Requirements

### Requirement: `params` 形参声明

#### Scenario: typed params 形参
- **WHEN** 声明 `int Sum(params int[] values)`
- **THEN** 解析成功，末参 `values` 标记 `IsParams=true`，签名 `ParamsFrom` = 该形参索引

#### Scenario: object params 形参
- **WHEN** 声明 `void Write(string fmt, params object[] args)`
- **THEN** 解析成功，`fmt` 为普通必填形参，`args` 标记 params

#### Scenario: params 非末参（错误）
- **WHEN** 声明 `void F(params int[] xs, int y)`
- **THEN** 报错 `E_PARAMS_NOT_LAST`，不生成签名

#### Scenario: params 类型非数组（错误）
- **WHEN** 声明 `void F(params int x)`
- **THEN** 报错 `E_PARAMS_NOT_ARRAY`

#### Scenario: params 与 ref/out/默认值冲突（错误）
- **WHEN** 声明 `void F(params ref int[] xs)` 或 `void F(params int[] xs = null)`
- **THEN** 报错 `E_PARAMS_MODIFIER_CONFLICT`

### Requirement: expanded form 调用（散列实参打包）

#### Scenario: typed expanded form
- **WHEN** 调用 `Sum(1, 2, 3)`（`Sum(params int[])`）
- **THEN** 编译器在调用点 emit `new int[]{1,2,3}` 并作为单数组实参传入；运行得 `6`

#### Scenario: 零实参 expanded form
- **WHEN** 调用 `Sum()`
- **THEN** emit `new int[0]` 空数组传入；运行得 `0`

#### Scenario: 前缀必填 + params
- **WHEN** 调用 `Write("{0}{1}", 1, "x")`（`Write(string, params object[])`）
- **THEN** `"{0}{1}"` 绑到 `fmt`；`1`、`"x"` 装箱打包成 `object[]{1,"x"}` 传入 `args`

#### Scenario: object[] 混类型装箱
- **WHEN** expanded form 打包 `object[]` 且实参含 int/string 等原始/引用混合
- **THEN** 各实参按 boxing 方案 A 上转 object（codegen no-op，object 槽持 tagged Value），
  运行期各元素保留具体 tag

### Requirement: normal form 调用（数组直传）

#### Scenario: 直传匹配数组
- **WHEN** 已有 `int[] arr = {1,2,3}`，调用 `Sum(arr)`
- **THEN** 选 normal form，`arr` 直绑 params 形参，**不**额外打包；运行得 `6`

#### Scenario: normal 优先 expanded
- **WHEN** 单个 `T[]` 实参既可 normal 直传又可被当 expanded 单元素包裹
- **THEN** 选 normal form（不打包）

### Requirement: 重载决议

#### Scenario: 非 params 重载优先
- **WHEN** 同时存在 `F(int)` 与 `F(params int[])`，调用 `F(5)`
- **THEN** 选精确 arity 的 `F(int)`（params 重载为兜底）

#### Scenario: 更具体 element type 胜
- **WHEN** 同时存在 `F(params string[])` 与 `F(params object[])`，调用 `F("a", "b")`
- **THEN** 选 `params string[]`（精确）而非 `params object[]`（装箱加宽）

#### Scenario: 混类型 fallthrough 到 object[]
- **WHEN** 同上两重载，调用 `F(1, "x")`
- **THEN** `string[]` 不适配（int 不可绑 string）→ 选 `params object[]`

### Requirement: 跨 zpkg params 调用

#### Scenario: 跨包 expanded form
- **WHEN** zpkg A 暴露 `Path.Join(params string[])`，zpkg B 调用 `Path.Join(a, b, c)`
- **THEN** A 的 TSIG 记录携带 `paramsFrom` 标记；B 编译时从 imported TSIG 读得该标记 →
  在 B 的调用点打包 `string[]{a,b,c}`；端到端运行正确

#### Scenario: 旧 zpkg 不可读（strict-pin）
- **WHEN** z42vm 读到 minor 版本不匹配的旧 zpkg
- **THEN** 按既有 strict-pin 报版本不符（不提供兼容路径）

## IR Mapping

- **无新 IR 指令 / 无新 zbc opcode**。expanded form 复用既有 `ArrayNewLitInstr`（数组字面量）+
  既有 `CallInstr`/`VCallInstr`/`CallIndirectInstr`。
- **zpkg TSIG 格式**：每 free-function / method 记录新增 1 字节 `paramsFrom`（0xFF=无）→ zpkg minor +1。

## Pipeline Steps

受影响阶段（按顺序）：
- [x] Lexer（`params` 关键字 token）
- [x] Parser / AST（`Param.IsParams`；末参/数组/互斥约束）
- [x] TypeChecker（约束校验 + expanded-form 绑定 → `BoundArrayLit` + 重载决议 + 跨包标记读写）
- [ ] IR Codegen（无新增——复用 `BoundArrayLit`→`ArrayNewLitInstr`）
- [ ] VM interp（无改动——VM 不感知 params）
