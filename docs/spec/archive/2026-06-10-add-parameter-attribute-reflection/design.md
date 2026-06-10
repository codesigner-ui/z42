# Design: 参数级用户 attribute 反射

## Architecture

复用 field/method attribute 反射的同构管线（add-field-attribute-reflection / C3b）。唯一新增维度：attribute 的**宿主从「字段/方法」变为「方法的某个参数」**，故 key 多一个 `paramIdx` 维度，wire 数据挂在 SIGS section 的每函数记录里（参数本就住在 SIGS）。

```
Parser            Param.Attributes（现丢弃 → 捕获）
  │
Synthesizer       每带 attr 参数 → factory thunk  __attr$param$<func>$<idx>$<n>()
  │
IrGen             IrFunction.ParamAttributes[paramIdx] = [IrAttributeRef...]
  │
ZbcWriter SIGS    每函数尾部：for each param → WriteAttrRefs(paramAttrs[i])   (zbc 1.15)
  │
ZbcReader(Rust)   read_attr_refs × paramCount → FuncDesc param attr 索引
  │
loader            (qualified-func, paramIdx) → Box<[AttributeRef]>
  │
reflection.rs     ParameterInfo{__qualified,__position}; __param_custom_attributes
  │
ParameterInfo.z42 GetCustomAttributes() / GetAttribute(Type)（调 native → call factories）
```

## Decisions

### Decision 1: z42c 锁冲突的处置（**核心，待 User 裁决**）

**问题**：bump zbc minor（1.14→1.15）改变**每个** .zbc 的版本字节（含 empty module），按 version-bumping.md step 5 必须同 commit 同步 z42c 的 `ZbcFormat.z42`（版本常量）+ `zbc_tests.z42`（`test_zbc_empty_byte_identical` 的 247B golden hex）。但 `z42c` 子系统正被 **port-z42c-tsig** 占用（它正做 zpkg 全文件 byte-identical）。不同步 → z42c byte-identical 单测**红** → 违反 GREEN 铁律，不能 commit。

**选项**：
- **A（推荐）— 等 port-z42c-tsig 归档后再实施**：现在只落 spec（本组文档，零锁冲突）；port 归档释放 z42c 后，本变更同时占 compiler+runtime+stdlib+**z42c** 四锁，一次性同 commit 做 C#+Rust+stdlib bump **与** z42c writer 同步。port-z42c-tsig 是自举主线最后收口步，预计很快。**代价**：实施延后到 port 落地（小时级，非天级）。
- **B — User 授权本变更立即共占 z42c**：要求 port-z42c-tsig 先暂停/让出 z42c 锁（跨 session 协调，本 session 无法单方面强制）。**代价**：打断 port 的 byte-identical 收口，可能让其返工。
- **C — 拆 z42c 同步为 follow-up，本变更接受 z42c 单测临时红**：违反 GREEN 铁律，**不推荐**（feedback_fix_validation_gap 的 interim-stopgap 仅适用「fix 不可验证」，不适用「主动改格式留红门」）。

**决定**：**待 6.5 gate User 裁决**。Claude 推荐 A——它让「full item 2 now」的代码实施紧接 port 归档（最小延后），且全程不破任何锁/门禁。User 已表态「accepting port must re-converge」，A 正是「port 先收口、本变更随后带 z42c 同步落地」的最干净兑现。

### Decision 2: factory key 与命名空间

**问题**：param factory key 不能与 class（`__attr$<Class>$<n>`）/ field（`__attr$fld$<Class>$<Field>$<n>`）/ method（`__attr$<func>$<n>`）碰撞。
**决定**：key = `__attr$param$<qualifiedFunc>$<paramIdx>$<n>`。`param$` 前缀与 `fld$` 对称，`<qualifiedFunc>` 含命名空间天然隔离，`<paramIdx>` 再分参数。runtime 索引键 = `(qualifiedFunc, paramIdx)`，与 method-level `qualifiedFunc` 索引正交（method attr 无 paramIdx 维度）。

### Decision 3: wire 布局 —— SIGS 每参数块位置

**决定**：在每函数记录**最末**（C3b 方法级 attr 块之后）追加 `for i in 0..ParamCount { WriteAttrRefs(paramAttrs[i]) }`。每参数恒写 `attr_count: u16`（0=无），保持 uniform per-param 布局，reader 按 ParamCount 循环读。位置选最末 → 不扰动现有字段偏移，diff 最小。

### Decision 4: IR 表达 —— 平行 list vs ParamDesc

**问题**：`IrFunction` 现以平行 list 表达参数（`ParamTypes: List<string>?`），无 `ParamDesc` 聚合类型。
**决定**：沿用平行 list 风格，加 `ParamAttributes: List<List<IrAttributeRef>>?`（外层长度=ParamCount，内层每参数 0+ ref；null=全无）。不引入 ParamDesc（避免为单特性重构既有平行表达，符合「最小破坏面」——若未来参数元数据维度变多再统一重构）。

## Implementation Notes

- **参数解析**：`TopLevelParser.Members.cs` 参数解析处现有 leading-attr 丢弃点，改为收集进 `Param.Attributes`（镜像 `TopLevelParser.Types.cs:349-391` 的 field 路径）。
- **instance method 的 `this`**：reflection.rs 现按 `start = is_static ? 0 : 1` 跳过 param0（this）。param attr 的 paramIdx 必须用**源码参数下标**（不含 this），与 `ParameterInfo.Position` 一致 → wire 写入也用源码下标（IrFunction.ParamAttributes 已是源码参数维度）。需校验 ParamCount 是否含 this（IrFunction.ParamCount 现含 this？—— 实施时插桩确认，写 fixture 前对齐）。
- **空函数/无参**：ParamCount=0 → 不写任何 per-param 块（empty module SIGS 函数数=0，连函数记录都没有）→ empty fixture 仅版本字节变。
- **strict-pin**：bump 后所有 stdlib/test zbc 失效，跑 `z42 xtask.zpkg regen`。

## Testing Strategy

- **单元**：C# — ZbcWriter/Reader round-trip 含 param attr 的 SIGS（z42.Tests）。Rust — zbc_reader 读 param attr（cargo test，含 version-pin 测试更新）。
- **Golden e2e**：`src/tests/types/param_attributes.z42` — 形参标 `[Foo("x")]`，反射 `m.GetParameters()[0].GetCustomAttributes()` 取回实例并断言字段。
- **stdlib [Test]**：`reflection.z42` 追加参数 attribute 断言（lib stage）。
- **格式 invariant**：`generate-fixtures.sh` ×2 regen；`dotnet test --filter Zbc|Zpkg` 绿。
- **byte-identical**：z42c zbc 单测（待 Decision 1 排程后随 z42c 同步一起绿）。
