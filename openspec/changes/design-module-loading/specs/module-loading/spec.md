# Spec: Module Loading & Dependency Resolution

## ADDED Requirements

---

### Requirement: Z42_PATH module search path（zbc only）

#### Scenario: Z42_PATH 环境变量存在
- **WHEN** `Z42_PATH=/home/user/scripts` 且该目录下有 `utils.zbc`（namespace header = `Utils`）
- **THEN** `using Utils` 能成功解析到该 zbc 文件

#### Scenario: cwd/ 自动包含
- **WHEN** `./utils.zbc` 存在（namespace = `Utils`），未设置 `Z42_PATH`
- **THEN** `using Utils` 在当前目录找到 `utils.zbc`，解析成功

#### Scenario: cwd/modules/ 自动包含
- **WHEN** `./modules/utils.zbc` 存在（namespace = `Utils`）
- **THEN** `using Utils` 在 `modules/` 子目录找到，解析成功

#### Scenario: verbose 输出 module path 搜索结果
- **WHEN** 以 `--verbose` 运行
- **THEN** 输出 `module path: ["/home/user/scripts", "<cwd>/", "<cwd>/modules/"]`
- **AND** 列出找到的 `.zbc` 文件名（仅 log，不自动加载）

---

### Requirement: 解析优先级（zbc > zpkg）

#### Scenario: zbc 覆盖 zpkg（合法）
- **WHEN** `modules/z42.io.zbc`（namespace = `z42.io`）存在，`libs/z42-io.zpkg`（namespaces 含 `z42.io`）也存在
- **THEN** `using z42.io` 解析到 `modules/z42.io.zbc`，**不报错**，不警告（覆盖是合法行为）

#### Scenario: 同层 zbc 冲突
- **WHEN** `Z42_PATH` 下两个不同目录都有提供 `Utils` namespace 的 `.zbc` 文件
- **THEN** 编译错误：`ambiguous namespace "Utils": found in a.zbc and b.zbc, remove one from module path`

#### Scenario: 同层 zpkg 冲突
- **WHEN** `libs/` 下两个不同 `.zpkg` 的 `namespaces` 都包含 `z42.io`
- **THEN** 编译错误：`ambiguous namespace "z42.io": found in x.zpkg and y.zpkg, remove one from libs`

#### Scenario: 完全未找到
- **WHEN** 两条路径均无法找到 namespace `Foo`
- **THEN** 编译错误：`unresolved namespace "Foo"`

---

### Requirement: zpkg 顶层 namespaces 字段

#### Scenario: lib zpkg 包含 namespaces
- **WHEN** `z42c build` 编译 `kind=lib` 项目，源文件声明 `namespace z42.io`
- **THEN** 输出 zpkg 顶层 `namespaces: ["z42.io"]`

#### Scenario: packed zpkg 也有 namespaces
- **WHEN** `pack=true` 打包，`files[]` 为空
- **THEN** 顶层 `namespaces` 仍包含所有导出命名空间（不依赖 `files[].exports` 推导）

#### Scenario: exe zpkg 的 namespaces
- **WHEN** `kind=exe` 项目编译，源文件声明 `namespace Hello`
- **THEN** 输出 zpkg 的 `namespaces: ["Hello"]`

#### Scenario: 编译器扫描 zpkg 时不匹配 zpkg 文件名
- **WHEN** `libs/my-custom-io-lib.zpkg` 的 `namespaces: ["z42.io"]`
- **THEN** `using z42.io` 成功解析到该 zpkg（映射基于 namespaces 字段，而非文件名）

---

### Requirement: zpkg dependencies 为编译期解析结果

#### Scenario: 依赖 zpkg 时记录文件名
- **WHEN** 编译时 `using z42.io` 解析到 `libs/z42-io.zpkg`
- **THEN** 输出 zpkg 的 `dependencies` 包含 `{ "file": "z42-io.zpkg", "namespaces": ["z42.io"] }`

#### Scenario: 依赖 zbc 时记录文件名
- **WHEN** 编译时 `using Utils` 解析到 `modules/utils.zbc`
- **THEN** 输出 zpkg 的 `dependencies` 包含 `{ "file": "utils.zbc", "namespaces": ["Utils"] }`

#### Scenario: 未使用的 libs/ 文件不进入 dependencies
- **WHEN** `libs/` 下有 `z42-text.zpkg` 但源码没有 `using z42.text`
- **THEN** 输出 zpkg 的 `dependencies` 中**不包含** `z42-text.zpkg`

#### Scenario: VM 按 dependencies 加载
- **WHEN** VM 加载 `app.zpkg`，其 `dependencies: [{ file: "z42-io.zpkg", ... }]`
- **THEN** VM 在 libs/ 中查找 `z42-io.zpkg` 并加载，不需要重新扫描命名空间

---

### Requirement: zbc namespace header

#### Scenario: 编译产出含 namespace header
- **WHEN** `z42c hello.z42 --emit zbc`，源文件声明 `namespace Hello`
- **THEN** 产出的 `hello.zbc` 文件头含 `NAMESPACE = "Hello"`，位于 magic + version 之后

#### Scenario: 快速扫描无需解析指令
- **WHEN** 编译器扫描 `modules/` 下的 `.zbc` 文件
- **THEN** 仅读取 namespace header（固定偏移量），不解析指令 section

#### Scenario: 无命名空间的 zbc（顶层脚本）
- **WHEN** 源文件无 `namespace` 声明（仅顶层语句）
- **THEN** namespace header = `""` 或 `"<anonymous>"`，不参与 `using` 解析

---

## MODIFIED Requirements

### z42.toml [dependencies] 语义变更

**Before:** `z42.toml` L5 通过 `[dependencies]` 声明依赖库，字段为 `{ path, version }`，依赖名直接映射到包路径或注册中心。

**After:** `[dependencies]` 保留，字段改为包名 + 可选版本约束。编译器按包名在 libs/ 中查找 `[project] name` 匹配的 zpkg，从其 `namespaces` 字段获取可用命名空间。

```toml
# Before（旧设计）
[dependencies]
z42-std = { path = "../std" }

# After（新设计）
[dependencies]
"z42.io" = "*"    # 包名，编译器去 libs/ 找 name="z42.io" 的 zpkg，读其 namespaces
"my-lib" = "*"
```

**有 `[dependencies]` 时：** 只从声明的包中解析命名空间，未声明的 libs/ 包不参与 `using` 解析。

**无 `[dependencies]` 时（脚本模式）：** 自动扫描 libs/ 全部 zpkg 和 Z42_PATH 全部 zbc。

**Scenario: 声明依赖后 using 解析**
- **WHEN** `[dependencies]` 含 `"my-lib" = "*"`，`libs/` 有 `name="my-lib"` 的 zpkg，其 `namespaces: ["MyLib"]`
- **THEN** `using MyLib` 解析成功，与 zpkg 文件名无关

**Scenario: 声明依赖但 libs/ 找不到**
- **WHEN** `[dependencies]` 含 `"z42.text" = "*"`，libs/ 中无 `name="z42.text"` 的 zpkg
- **THEN** 编译错误：`dependency "z42.text" not found in libs path`

**Scenario: 无 [dependencies] 的脚本模式**
- **WHEN** `z42.toml` 无 `[dependencies]`，`libs/` 有多个 zpkg
- **THEN** 编译器扫描全部 zpkg，`using` 可解析任意其中的命名空间

### package.sh 产物目录

**Before:** `artifacts/z42/libs/` 下生成 5 个 `.zbc` + 5 个 `.zpkg` 占位文件。

**After:** `artifacts/z42/libs/` 下只生成 `.zpkg` 占位文件（5 个），移除 `.zbc` 占位。

---

## Pipeline Steps

受影响的 pipeline 阶段：

- [ ] Lexer — 无变化
- [ ] Parser / AST — 无变化
- [ ] TypeChecker — 扫描 libs/Z42_PATH 建立 namespace→file 映射表
- [ ] IR Codegen — 无变化
- [ ] Build Driver（BuildCommand.cs）— 依赖解析 + 写入 dependencies
- [x] VM startup（main.rs）— 新增 Z42_PATH 探测
- [ ] VM Loader（loader.rs）— 按 dependencies.file 加载依赖；zbc namespace header 读取
- [ ] zbc 格式（ZbcFormat.cs / formats.rs）— 新增 namespace header section
