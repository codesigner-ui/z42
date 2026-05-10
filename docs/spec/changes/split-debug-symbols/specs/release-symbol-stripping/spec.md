# Spec: Release Symbol Stripping

## ADDED Requirements

### Requirement: TOML `strip` Field Drives Sidecar Behavior

#### Scenario: profile.release 默认 strip=true → 产 sidecar
- **WHEN** 用 `z42c build --profile release` 编译，且 toml 未显式覆盖 `[profile.release].strip`
- **THEN** effective `strip = true`（来自内置默认值，见 [ProjectManifest.cs:329](../../../../src/compiler/z42.Project/ProjectManifest.cs#L329)）
- **AND** writer 走拆分路径（DBUG → sidecar）

#### Scenario: profile.debug 默认 strip=false → 不产 sidecar
- **WHEN** 用 `z42c build` 或 `--profile debug` 编译，toml 未显式覆盖
- **THEN** effective `strip = false`（来自内置默认值，见 [ProjectManifest.cs:328](../../../../src/compiler/z42.Project/ProjectManifest.cs#L328)）
- **AND** writer 走原路径（DBUG 内嵌主 zbc，不产 sidecar）

#### Scenario: toml 显式覆盖
- **WHEN** toml 写 `[profile.debug] strip = true`，用 `--profile debug` 编译
- **THEN** effective `strip = true`，走拆分路径

#### Scenario: CLI override 高于 profile
- **WHEN** 用 `--strip-symbols=false` 编译 release profile（toml `strip=true`）
- **THEN** effective `strip = false`，走非拆分路径
- **AND** CLI flag 优先于 toml profile

### Requirement: Strip Build Emits Stripped zbc + Sidecar

#### Scenario: effective strip=true → main zbc 无 DBUG + sidecar
- **WHEN** effective `strip = true` 时编译一个含至少一个函数（带 LineTable 非空）的模块
- **THEN** 生成 `<out>/<name>.zbc` 不含 DBUG section、含 BLID section（16B），且 `ZbcFlags.SymOnly` 为 0
- **AND** 生成 `<out>/<name>.zsym` magic = `ZBC\0`、版本 1.2、`ZbcFlags.SymOnly = 1`、仅含 DBUG + BLID
- **AND** 主 zbc 与 sidecar 的 BLID 字节相同

#### Scenario: effective strip=false → DBUG 内嵌、不产 sidecar、不含 BLID
- **WHEN** effective `strip = false` 时编译模块
- **THEN** 生成的 `<name>.zbc` 含 DBUG section、**不**含 BLID section
- **AND** 同目录不产生 `<name>.zsym`

#### Scenario: 模块无任何 LineTable 时 strip=true 仍产 sidecar（保持产物一致性）
- **WHEN** effective `strip = true` 编译一个所有函数 LineTable 都为空的模块
- **THEN** 生成的 `<name>.zbc` 含 BLID
- **AND** 同目录产生 `<name>.zsym`，含 BLID（与主 zbc 字节相同）+ 一个空 DBUG section（count=0）
- **AND** 编译器不输出 warning（一致性优先于产物大小）

### Requirement: BLID Computed by Hashing Main zbc with BLID Zeroed

#### Scenario: BLID 算法稳定可重复
- **WHEN** 同一 IrModule 用 `--release` 编译两次（同输入、同 allocator）
- **THEN** 两次产生的 BLID 完全相同（16B 字节级一致）

#### Scenario: 改动一个字节即影响 BLID
- **WHEN** 模块某一函数改一行代码后重新 release 编译
- **THEN** 新 zbc 的 BLID 与旧 zbc 不同
- **AND** 旧 sidecar 不再被 runtime 接受（参见 sidecar mismatch scenario）

#### Scenario: BLID 计算时自身字节置零
- **WHEN** 计算 BLID
- **THEN** 哈希输入 = 完整 zbc 字节流，但 BLID section 的 16B 数据区先置 0
- **AND** 写入的 BLID 取代该 16B 占位
- **AND** 算法 = BLAKE3-128（标准 BLAKE3 输出截首 16B）

### Requirement: Runtime Eager Sidecar Loading and Merge

#### Scenario: sidecar 存在且 build_id 匹配 → 合并 line table
- **WHEN** runtime 加载主 `.zbc`，且同目录存在 `<name>.zsym`，且 sidecar.BLID == main.BLID
- **THEN** 在主模块加载完成的同步路径中读取 sidecar 的 DBUG，合并 line_table 进各 FuncBody
- **AND** 后续 stack trace 显示 `at Module.Func (file:line:column)` 完整形式

#### Scenario: sidecar 缺失 → 静默降级
- **WHEN** runtime 加载主 `.zbc`，同目录不存在 `<name>.zsym`
- **THEN** 加载成功，无 warning
- **AND** stack trace 退化为 `at Module.Func+0x<ip> [build:<8hex>]`（取 BLID 前 4 字节十六进制）

#### Scenario: sidecar 存在但 build_id 不匹配 → 拒绝并 warn
- **WHEN** runtime 加载主 `.zbc`，同目录 `<name>.zsym` 存在但 BLID 与主 zbc 不同
- **THEN** runtime 输出 `warning: <name>.zsym build_id mismatch (got X, expected Y); ignored`
- **AND** 行为等同于 sidecar 缺失（trace 退化为 ip 形式）
- **AND** 加载流程不失败

#### Scenario: sidecar 损坏（非 ZBC magic / 缺 BLID section / SymOnly flag 不为 1）→ 拒绝
- **WHEN** sidecar 文件存在但 magic 错误、或缺 BLID section、或 SymOnly flag 不为 1
- **THEN** runtime 输出对应 warning，忽略 sidecar，加载流程不失败

#### Scenario: sidecar 中 DBUG 为空（count=0）→ 接受但无效果
- **WHEN** sidecar build_id 匹配且 SymOnly flag 正确，但 DBUG section count=0
- **THEN** 加载成功无 warning
- **AND** 该模块所有 frame 的 line_table 仍为空，trace 退化为 ip 形式

### Requirement: Frame Func_name Carries Function Signature

#### Scenario: 任意 frame 的 func_name 含完整签名
- **WHEN** runtime push 任一 frame（interp / jit）
- **THEN** `frame.func_name` 形如 `<FQN>(<t1>,<t2>,...)`，其中：
  - `<FQN>` = namespace + 类 + 方法名（与现行一致）
  - `<ti>` = 第 i 个参数的简化类型名（基本类型用 `i32` / `str` / `bool` 等；类用裸类名不带 namespace；数组用 `T[]`；空参用 `()`）
- **AND** 与是否 strip / 是否加载 sidecar 无关，trace 始终带签名

### Requirement: Stack Trace Format with and without Symbols

#### Scenario: 有调试信息（非 strip 构建 或 strip+加载到 sidecar）
- **WHEN** 抛出异常时 line table 已填充
- **THEN** trace 行 = `  at <FQN>(<sig>) (<file>:<line>:<column>)`

#### Scenario: 无调试信息（strip 构建缺 sidecar）
- **WHEN** 抛出异常时该 frame 的 line==0 / column==0 且模块未加载 sidecar
- **THEN** trace 行 = `  at <FQN>(<sig>)+0x<ip:hex> [build:<8hex>]`
- **AND** `<ip>` = 该 frame 当前 PC 距函数指令流首字节的偏移（u32 hex）
- **AND** `<build>` = 模块 BLID 前 4 字节 hex（小写）

#### Scenario: 模块 BLID 缺失（旧 debug 构建无 BLID）
- **WHEN** 非 strip 构建模块未含 BLID 但仍 line==0
- **THEN** trace 行 = `  at <FQN>(<sig>)+0x<ip:hex>`（省略 `[build:...]` 段）

### Requirement: Offline Symbolicate Tool

#### Scenario: 用 sidecar 把 ip 形式 trace 还原为源位置
- **WHEN** 用户运行 `z42c symbolicate <crash.txt> --syms <name>.zsym`
- **AND** crash.txt 内含 `at MyApp.Greeter.greet(str)+0x1A2 [build:abcd1234]` 形式的 frame
- **AND** sidecar 的 BLID 前 4 字节 hex == `abcd1234`
- **THEN** 输出对应 frame = `at MyApp.Greeter.greet(str) (path/to/file.z42:42:7)`
- **AND** 其余非 ip-form 行原样保留

#### Scenario: build_id 不匹配 → 报错退出
- **WHEN** crash.txt 中的 `[build:xxxx]` 与 sidecar BLID 前 4B hex 不符
- **THEN** 工具退出码非 0，stderr 输出 `error: build_id mismatch: trace says X, sidecar is Y`

#### Scenario: trace 中有未带 build_id 的 ip 形式（旧产物）
- **WHEN** crash.txt 中 frame = `at MyApp.Greeter.greet(str)+0x1A2`（无 [build:...]）
- **AND** 用户提供 `--syms` 但不能验证匹配
- **THEN** 工具按 sidecar 解析；若该 ip 落在 sidecar line table 范围内则替换；否则原样输出 + stderr `warning: cannot verify build_id`

### Requirement: SymOnly File Misuse Detection

#### Scenario: 把 .zsym 当主模块加载 → 拒绝
- **WHEN** runtime 或 driver 把一个 SymOnly 文件作为主 zbc 加载（即试图 dispatch）
- **THEN** 报错 `error: <path> is a debug-symbol sidecar, not a module; cannot be loaded as main`
- **AND** 不进入指令分派

## IR Mapping

新增 / 修改的二进制要素：

| 名称 | 位置 | 大小 | 说明 |
|------|------|------|------|
| `ZbcFlags.SymOnly` | header.flags bit 2 | 1 bit | 标识本文件是 sidecar |
| `SectionTags.Blid` | section directory tag | 4 字节 ASCII = `BLID` | NEW section tag |
| `BLID` section payload | section data | 16 字节 | BLAKE3-128(zbc with BLID zeroed) |
| `DBUG` section | section data | 重组（每函数 LineTable + LocalVarTable）| 1.2 重组：LineTable 从 FUNC 内联迁入 DBUG；DBUG 在 strip=false 时内嵌主 zbc，strip=true 时整体迁到 sidecar；count=0 表示空 |
| `FUNC` section | section data | 移除 LineTable（lineCount + LineEntry[]）| 1.2 简化：FUNC 仅承载执行信息（reg_count、blocks、instr、exc），无任何 debug 字段 |
| zbc version | header.major.minor | 2 字节 | 1.1 → 1.2（pre-1.0 不留兼容） |

## Pipeline Steps

受影响的 pipeline 阶段：

- [ ] Lexer — 不涉及
- [ ] Parser / AST — 不涉及
- [ ] TypeChecker — 不涉及
- [ ] IR Codegen — 不涉及（LineTable 已由现有 emitter 填充）
- [x] zbc Writer — 拆分主/sidecar；BLID 计算与回填
- [x] zbc Reader — 识别 SymOnly + BLID
- [x] runtime metadata loader — sidecar 同目录探测 + 校验 + 合并 line_table
- [x] runtime exception formatter — line==0 退化格式
- [x] driver CLI — `--release` / `--strip-symbols`
- [x] driver subcommand — `symbolicate`

## MODIFIED Requirements

### Requirement: zbc 文件格式（docs/design/zbc.md）
**Before:** META section 描述为"调试信息（可选）"含 source_file_str_idx + sha256 + ip→line/column 表；实际代码 LineTable 内联在 FUNC body、DBUG 仅含局部变量名，文档与代码漂移。
**After:** META section 仅承载模块名/版本/entry（与代码一致）；DBUG section 重组为 z42 调试信息的唯一容器（**LineTable + LocalVarTable** 两表合一），FUNC body 移除 LineTable 字段；sidecar 形态由新增 BLID section + `ZbcFlags.SymOnly` 标识。文档与代码对齐到 1.2。
