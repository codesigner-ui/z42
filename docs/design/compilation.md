# z42 编译产物规范

本文档定义两种编译输出粒度，以及它们的文件格式、工作流和工具命令。

---

## 总体设计

```
.z42 源文件
   │
   ▼ z42c 编译
   │
   ├─ 粒度 1（文件级）
   │    每个 .z42 → 独立 .zbc（字节码单元）
   │    工程级索引 → .zmod（模块清单）
   │
   └─ 粒度 2（程序集级）
        所有 .zbc 打包 → .zlib（可分发库 / 可执行程序集）
```

---

## 粒度 1 — 文件级：`.zbc` + `.zmod`

### 1.1 `.zbc`（z42 Bytecode — 单文件字节码）

每个 `.z42` 源文件独立编译为一个 `.zbc`。

**类比**：Python `.pyc`、Java `.class`

**Phase 1（JSON 调试格式）**：

```json
{
  "zbc_version": [0, 1],
  "source_file": "src/greet.z42",
  "source_hash": "sha256:e3b0c44298fc1c149...",
  "namespace": "Demo.Greet",
  "exports": ["Greet"],
  "imports": [],
  "module": { /* IrModule — 与 .z42ir.json 格式相同 */ }
}
```

**Phase 2（二进制格式）**：

```
[4 bytes]  magic:        0x5A 0x42 0x43 0x00  ("ZBC\0")
[2 bytes]  version_major
[2 bytes]  version_minor
[32 bytes] source_hash   (SHA-256 of source .z42 file)
[4 bytes]  section_count
[sections...]
  STRINGS  — string pool
  FUNCS    — function bodies (SSA instructions)
  TYPES    — type definitions
  EXPORTS  — public symbol table: [(name, kind, offset)]
  IMPORTS  — external symbols needed: [(namespace, name)]
  META     — optional: source map, debug info
```

**增量编译语义**：

- 加载 `.zbc` 时对比 `source_hash` 与当前 `.z42` 文件的 SHA-256
- 若哈希一致 → 跳过重编译
- 若哈希变化 → 仅重编译该文件，不触碰其他 `.zbc`

---

### 1.2 `.zmod`（z42 Module Manifest — 模块清单 / 索引）

`.zmod` 描述一个**工程**（库或可执行）的所有文件及其依赖，是 `.zbc` 的索引层。

**类比**：Python `__init__.py` + `MANIFEST` 的组合

**格式（始终为 JSON，便于 VCS diff）**：

```json
{
  "zmod_version": [0, 1],
  "name": "Demo.Greet",
  "version": "0.1.0",
  "kind": "lib",
  "files": [
    {
      "source": "src/greet.z42",
      "bytecode": ".cache/greet.zbc",
      "source_hash": "sha256:e3b0c44298fc1c149...",
      "exports": ["Greet"]
    },
    {
      "source": "src/util.z42",
      "bytecode": ".cache/util.zbc",
      "source_hash": "sha256:a665a45920422f9d4...",
      "exports": ["StringUtil"]
    }
  ],
  "dependencies": [
    { "name": "z42.stdlib", "path": "../../stdlib/z42.stdlib.zlib", "version": ">=0.1" }
  ],
  "entry": "Demo.Greet.Main"
}
```

**字段说明**：

| 字段 | 说明 |
|------|------|
| `kind` | `"lib"` = 类库（无入口）；`"exe"` = 可执行（有 `entry`） |
| `files[].bytecode` | 相对于 `.zmod` 文件的路径 |
| `files[].source_hash` | 编译时记录；运行时可验证 |
| `exports` | 该文件对外公开的符号，供 VM 和链接器快速查找 |
| `dependencies` | 引用的外部 `.zlib`；路径可为本地或 registry URL |

**增量更新流程**：

```
z42c --incremental MyLib.zmod
  │
  ├─ 读取 .zmod，遍历 files[]
  ├─ 对比每项 source_hash vs 磁盘文件 SHA-256
  │    ├─ 相同 → 跳过
  │    └─ 不同 → 重编译该 .z42 → 更新 .zbc + source_hash
  └─ 更新 .zmod 中变化项的 source_hash
```

---

## 粒度 2 — 程序集级：`.zlib`

`.zlib` 将一个工程的所有 `.zbc` 打包为**单一可分发文件**，类似 C# `.dll` / Java `.jar`。

**类比**：C# 程序集（`.dll`）

### 2.1 Phase 1（JSON 调试格式）

```json
{
  "zlib_version": [0, 1],
  "name": "Demo.Greet",
  "version": "0.1.0",
  "kind": "lib",
  "exports": [
    { "symbol": "Demo.Greet.Greet", "kind": "func" }
  ],
  "dependencies": [
    { "name": "z42.stdlib", "version": "0.1.0" }
  ],
  "modules": [
    { /* ZbcFile — greet.z42 的完整内容 */ },
    { /* ZbcFile — util.z42 的完整内容 */ }
  ]
}
```

### 2.2 Phase 2（二进制归档格式）

```
[4 bytes]  magic:        0x5A 0x4C 0x42 0x00  ("ZLB\0")
[2 bytes]  version_major
[2 bytes]  version_minor
[4 bytes]  flags         bit0=executable, bit1=debug_info
[4 bytes]  section_count
[sections...]
  MANIFEST  — JSON 元数据（name, version, exports, dependencies）
  ZBC[0]    — 第 0 个 .zbc 文件的完整内容
  ZBC[1]    — 第 1 个 .zbc 文件的完整内容
  ...
  EXPORTS   — 全局导出符号表：[(fqn, zbc_index, func_offset)]
  RELO      — 跨文件调用的重定位表
  META      — 可选：聚合 source map
```

---

## 文件扩展名总览

| 扩展名 | 含义 | 粒度 | 格式 |
|--------|------|------|------|
| `.z42` | 源代码 | — | 文本 |
| `.z42ir.json` | 调试用 IR（Phase 1）| 文件 | JSON |
| `.zbc` | 单文件字节码 | 文件 | 二进制（Phase 2）/ JSON（Phase 1）|
| `.zmod` | 模块清单 / 索引 | 工程 | JSON（始终）|
| `.zlib` | 程序集 / 库包 | 工程 | 二进制（Phase 2）/ JSON（Phase 1）|

---

## 编译器命令

```bash
# 单文件编译 → .zbc（JSON Phase 1）
z42c src/greet.z42 --emit zbc

# 工程编译 → 生成 .zmod + 所有 .zbc
z42c --project MyLib.zmod

# 增量重编译（跳过 hash 未变文件）
z42c --project MyLib.zmod --incremental

# 打包 → .zlib
z42c --project MyLib.zmod --pack

# 全量构建（增量 + 打包）
z42c --project MyLib.zmod --incremental --pack
```

---

## VM 加载语义

| 输入 | VM 行为 |
|------|---------|
| `.z42ir.json` | 直接加载（Phase 1 调试，向前兼容）|
| `.zbc` | 单文件字节码，自包含运行 |
| `.zmod` | 按 `files[]` 顺序加载所有 `.zbc`，合并符号表 |
| `.zlib` | 解包内嵌 ZBC sections，合并后执行 |

VM 入口选择优先级：`entry` 字段 > 名为 `Main` / `main` 的函数。

---

## 符号解析规则

1. 同文件内直接引用（已在单模块 IR 里解析）
2. 同工程跨文件引用：通过 `.zmod` 的 `exports` 表快速查找
3. 跨库引用：通过 `.zlib` 的 `EXPORTS` section + 重定位

---

## 版本兼容性

- `.zbc` / `.zlib` magic bytes 固定，版本字段用于前向兼容
- VM 必须拒绝 `version_major` 高于自身支持版本的文件，并给出明确错误
- `.zmod` 为 JSON，永远向前兼容（新字段旧 VM 忽略）
