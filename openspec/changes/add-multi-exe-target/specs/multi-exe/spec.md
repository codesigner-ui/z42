# Spec: multi-exe-target

## ADDED Requirements

---

### Requirement: [[exe]] 数组表语法

#### Scenario: 最小合法多 exe 工程
- **WHEN** `<name>.z42.toml` 含 `[project]`（无 `kind`）+ 两个 `[[exe]]` 块，每块有 `entry`
- **THEN** 解析成功，得到两个 `ExeTarget`，name 分别从各自 `name` 字段读取

#### Scenario: [[exe]] 的 name 字段必填
- **WHEN** 某个 `[[exe]]` 缺少 `name` 字段
- **THEN** 报错：`error: [[exe]] entry is missing required field 'name'`

#### Scenario: [[exe]] 的 entry 字段必填
- **WHEN** 某个 `[[exe]]` 缺少 `entry` 字段
- **THEN** 报错：`error: [[exe]] '<name>' is missing required field 'entry'`

#### Scenario: [[exe]] 继承 [sources]
- **WHEN** `[[exe]]` 未指定 `src` 字段
- **THEN** 该 exe 使用 `[sources].include / exclude` 的 glob 展开结果

#### Scenario: [[exe]] 独立 src 覆盖 [sources]
- **WHEN** `[[exe]]` 指定 `src = ["src/tool/**/*.z42"]`
- **THEN** 该 exe 仅使用自身 `src` glob，忽略 `[sources]`

#### Scenario: [[exe]] 与 kind="exe" 共存时报错
- **WHEN** `[project] kind = "exe"` 且同时存在 `[[exe]]`
- **THEN** 报错：`error: cannot use [[exe]] together with [project] kind = "exe"; use one or the other`

#### Scenario: 单个 [[exe]] 等价于 kind="exe"
- **WHEN** 只有一个 `[[exe]]` 块
- **THEN** 解析成功，行为与 `kind="exe"` 等价（产物为 `dist/<exe-name>.zbc`）

---

### Requirement: z42c build 多目标构建

#### Scenario: 默认构建所有 [[exe]]
- **WHEN** 执行 `z42c build`，工程有 2 个 `[[exe]]`
- **THEN** 依次编译两个 exe，各自输出到 `dist/<name>.zbc`

#### Scenario: --exe 只构建指定目标
- **WHEN** 执行 `z42c build --exe hello`
- **THEN** 仅编译名为 `hello` 的 `[[exe]]`，其他跳过

#### Scenario: --exe 指定不存在的目标
- **WHEN** 执行 `z42c build --exe nonexistent`
- **THEN** 报错：`error: no [[exe]] named 'nonexistent'`

#### Scenario: 构建进度输出
- **WHEN** 构建多个 exe
- **THEN** 每个 exe 开始时输出：`  Compiling <name> (<entry>)`

---

### Requirement: 向后兼容

#### Scenario: 现有 kind="exe" 工程不受影响
- **WHEN** `[project] kind = "exe"` + `entry = "Hello.main"`，无 `[[exe]]`
- **THEN** 行为与之前完全一致，产物输出到 `dist/<project-name>.zbc`

#### Scenario: kind="lib" 工程不受影响
- **WHEN** `[project] kind = "lib"`，无 `[[exe]]`
- **THEN** 行为与之前完全一致
