# Design: Module Loading & Dependency Resolution

## Architecture

```
using z42.io;  ← 源码中的命名空间导入

编译器解析阶段：
  │
  ├─ 扫描 Z42_PATH / cwd / modules/  → .zbc 文件 → 读 namespace header
  │    ↓ 未找到
  └─ 扫描 Z42_LIBS / adjacent libs/  → .zpkg 文件 → 读 namespaces 字段
       ↓ 未找到
       编译错误: unresolved namespace "z42.io"

输出 zpkg 记录解析结果：
  dependencies: [{ file: "z42-io.zpkg", namespaces: ["z42.io"] }]

VM 加载阶段：
  加载 app.zpkg
  └─ 读 dependencies → 按 file 字段去 libs/ / Z42_PATH 中找对应文件加载
```

---

## Decisions

### Decision 1: 双搜索路径，职责分离

**问题：** 单一 libs/ 路径无法同时服务"正式打包依赖"和"Python-like 脚本模块"两种场景。

**选项：**
- A: 单路径，扩展名区分（libs/ 支持 zpkg + zbc）— 格式混杂，namespace 扫描逻辑复杂
- B: 双路径，类型专用（libs/=zpkg，path/=zbc）— 职责清晰，扫描策略统一

**决定：** 选 B。

| 路径 | 格式 | 环境变量 | 默认目录 |
|------|------|---------|---------|
| module path | `.zbc` only | `Z42_PATH` | `<cwd>/`、`<cwd>/modules/` |
| libs path | `.zpkg` only | `Z42_LIBS` | `<binary-dir>/../libs/`、`<cwd>/artifacts/z42/libs/` |

### Decision 2: 解析优先级

**问题：** zbc 和 zpkg 路径都能提供同一命名空间时谁优先？

**决定：** Z42_PATH（zbc）优先于 Z42_LIBS（zpkg）。理由：本地模块覆盖安装包，与 Python 行为一致，便于开发期测试和 stdlib override。

同一层内多个文件提供相同命名空间 → **报错**，要求用户清理路径。跨层（zbc 覆盖 zpkg）是合法行为，不报错。

```
解析顺序（高 → 低优先级）：
  1. Z42_PATH 环境变量指定的目录（zbc）
  2. <cwd>/                          （zbc，自动包含）
  3. <cwd>/modules/                  （zbc，自动包含）
  4. Z42_LIBS 环境变量指定的目录     （zpkg）
  5. <binary-dir>/../libs/           （zpkg）
  6. <cwd>/artifacts/z42/libs/       （zpkg）
```

### Decision 3: C#-style 命名空间映射，编译器自动发现

**问题：** `using z42.io` 如何找到对应的 zpkg？

**选项：**
- A: 包名 = 命名空间根（Rust 风格）— 约定有冲突风险（同名包）
- B: 包名与命名空间无关，编译器扫描 zpkg exports（C# 风格）— 职责分离，更灵活
- C: 需要 `z42.toml [dependencies]` 声明（Cargo 风格）— 繁琐，用户负担重

**决定：** 选 B。编译器扫描 libs/ 中所有 zpkg 的 `namespaces` 字段，建立 `namespace → zpkg file` 映射表，解析 `using` 时查表。无需 `z42.toml [dependencies]`。

### Decision 4: zpkg 新增顶层 namespaces 字段

**问题：** packed zpkg 的 `files[]` 为空，无法从 file-level exports 推导命名空间；扫描所有 zpkg 时需要快速读取命名空间列表。

**决定：** 在 zpkg manifest 顶层新增 `namespaces: string[]`，编译打包时由所有源文件的 namespace 声明汇总生成。

```json
{
  "name": "z42-io",
  "version": "0.1.0",
  "kind": "lib",
  "namespaces": ["z42.io", "z42.io.internal"],
  ...
}
```

### Decision 5: zpkg dependencies 改为解析结果

**问题：** `dependencies` 字段目前是声明式（类 Cargo），当前始终为空。

**决定：** 改为编译期**解析结果**，记录实际依赖的 zpkg/zbc 文件名和命名空间。VM 加载时直接按文件名查找，不需要重新扫描命名空间。

```json
"dependencies": [
  { "file": "z42-io.zpkg",   "namespaces": ["z42.io"] },
  { "file": "z42-core.zpkg", "namespaces": ["z42.core"] }
]
```

旧 `ZpkgDep(Name, Version?, Path?)` → 新 `ZpkgDep(File, Namespaces)`。

### Decision 6: zbc 轻量 namespace header

**问题：** 扫描 Z42_PATH 下的 zbc 文件获取命名空间，需要解析完整字节码，开销大。

**决定：** zbc 文件新增固定位置的 namespace header section，仅包含命名空间字符串，可在不解析指令的情况下读取。

```
zbc 文件结构（新增 NAMESPACE section）：
  [MAGIC: 4 bytes]  Z42\0
  [VERSION: 2 bytes]
  [NAMESPACE_LEN: 2 bytes]
  [NAMESPACE: N bytes]  ← 新增，如 "z42.io"
  [... 现有 sections ...]
```

### Decision 7: z42.toml [dependencies] 保留，语义改为"声明 + 扫描"

**问题：** 不声明依赖时，编译器需要扫描 libs/ 中所有 zpkg，可能意外引入无关包；同时用户也需要一个地方记录项目依赖哪些包。

**决定：** 保留 `[dependencies]`，但语义从"声明式解析"改为"范围约束 + 验证"：

- `[dependencies]` 中列出的是**包名**（对应 zpkg manifest 的 `[project] name` 字段），版本约束可选
- 编译器按包名在 libs/ 中找对应 zpkg，读其 `namespaces` 字段，注册可用命名空间
- `using X` 只从已声明依赖的包中解析，防止意外引入 libs/ 里的无关包
- **没有 `[dependencies]` 时（脚本模式）**：自动发现模式，扫描 libs/ 全部 zpkg 和 Z42_PATH 全部 zbc

```toml
[dependencies]
"z42.io"  = "*"      # 包名 = "z42.io"，版本约束 * = 不限（由 libs/ 内容决定）
"my-lib"  = "*"      # my-lib.zpkg 可能导出 "MyLib" 命名空间，编译器从 namespaces 字段读取
```

编译器解析流程（有 `[dependencies]` 时）：
```
1. 遍历 [dependencies] 每条记录
2. 在 libs/ 中找 name 字段匹配的 zpkg（不是文件名匹配）
3. 读该 zpkg 的 namespaces 字段 → 注册到 namespace→file 映射表
4. 同时扫描 Z42_PATH（不受 [dependencies] 限制，zbc 始终全量扫描）
5. using X 查映射表；未找到 → 编译错误
6. 依赖声明但在 libs/ 找不到对应包 → 编译错误
```

版本约束目前只做**存在性校验**（能找到包即可），不做 semver 比较，也不生成 lockfile。用户通过控制 libs/ 中的文件版本来锁定依赖。

**stdlib 不在 `[dependencies]` 范围内：** `z42.core` 由 VM 在启动时自动注入，无需声明。其他 stdlib 模块（`z42.io`、`z42.collections` 等）由 VM 从 libs/ 自动加载，也无需在 `[dependencies]` 中声明。`[dependencies]` 只用于声明用户自己的第三方 zpkg 依赖。

---

## Implementation Notes

**编译器扫描流程（BuildCommand.cs）：**
```
1. 启动时扫描所有 libs/ 下 .zpkg → 读 namespaces → 建立 Map<namespace, zpkg_file>
2. 扫描所有 Z42_PATH 下 .zbc → 读 namespace header → 建立 Map<namespace, zbc_file>
3. 合并两个 Map，zbc 优先（同 namespace 存在 zbc 则覆盖 zpkg entry）
4. TypeChecker 遇到 `using X` → 查 Map → 加载对应文件的符号表
5. 编译完成 → 输出 zpkg，dependencies 字段填入实际用到的条目
```

**VM 加载流程（loader.rs）：**
```
load_zpkg(path):
  1. 读 manifest
  2. 读 dependencies[] → 对每条 dep：
     a. 在 Z42_PATH 下找 dep.file（若为 .zbc）
     b. 在 Z42_LIBS 下找 dep.file（若为 .zpkg）
  3. 递归加载所有依赖，合并符号表
  4. 加载自身 modules/files
```

### Decision 8: zbc 自描述模式（full vs stripped）

**问题：** zbc 有两种使用场景：① Z42_PATH 独立模块（需完整元数据）；② indexed zpkg 的 `.cache/` 缓存（元数据已在 zpkg，zbc 只需函数体）。如何区分这两种 zbc？

**选项：**
- A: 两种不同的文件扩展名（`.zbc` vs `.zbcs`）— 工具链需要感知扩展名，不够优雅
- B: 依赖外部上下文决定如何解析 — 读取方需要额外信息，脆弱
- C: Header flags 自描述 — 读取方只看文件本身即可判断模式

**决定：** 选 C。在 zbc header 的 `flags` 字段中用 bit 0（`STRIPPED`）区分：

```
flags = 0x00  →  full zbc（独立可用，含 EXPT + SIGS + IMPT + STRS）
flags = 0x01  →  stripped zbc（需 zpkg 索引，只含 NSPC + BSTR + FBOF + FBDY）
```

**zpkg build 流程（indexed 模式）：**
1. 编译器输出规范 zbc（full mode，含完整元数据）
2. zpkg builder 从规范 zbc 读取 `EXPT` + `SIGS` → 写入 zpkg `SYIX` + `SIGS`
3. 剥离规范 zbc → 精简 zbc（`STRIPPED=1`），`STRS` 缩减为 `BSTR`
4. 精简 zbc 写入 `.cache/`，`source_hash` 写入 zpkg `FILE_TABLE`（不在 zbc 内）

**content-stable 保证：** 精简 zbc 的所有 section 内容完全由源文件决定，不受其他文件或 zpkg 结构变化影响。同一源文件 + 同一编译器版本 → 逐字节相同的精简 zbc。

**错误防护：** 读取方检查 `flags & STRIPPED`，若 stripped zbc 出现在 Z42_PATH 中，立即报错，不静默降级。

---

## Testing Strategy

- 单元测试：`ModuleResolutionTests` — 验证 namespace → file 映射逻辑及优先级
- 单元测试：`ZbcHeaderTests` — 验证 namespace header 读写
- Golden test：新增带依赖的 zpkg 编译场景，验证 dependencies 字段内容
- VM 验证：`./scripts/test-vm.sh` 包含跨 zpkg 依赖的加载测试
