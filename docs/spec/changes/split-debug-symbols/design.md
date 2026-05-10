# Design: Split Debug Symbols to Sidecar (.zsym)

## Architecture

```
┌─────────────────────────────────────────────────────────────────┐
│  Compile-time                                                   │
│                                                                 │
│   z42.toml:                                                     │
│     [profile.debug]   strip = false  (default)                  │
│     [profile.release] strip = true   (default)                  │
│                                                                 │
│   driver:                                                       │
│     effective_strip = CLI override                              │
│                       ?? toml profile.strip                     │
│                       ?? built-in default                       │
│                                                                 │
│   IrModule ──► ZbcWriter.Write(module, strip=effective_strip)   │
│                  │                                              │
│                  ├─ strip=true ─►                               │
│                  │     main.zbc : NSPC STRS TYPE SIGS IMPT EXPT │
│                  │                FUNC BLID(=h) [TIDX FRCS...]  │
│                  │     name.zsym: DBUG BLID(=h)                 │
│                  │                flags = SymOnly               │
│                  │                                              │
│                  └─ strip=false ─►                              │
│                        main.zbc : ...FUNC DBUG [TIDX FRCS...]   │
│                        no sidecar                               │
│                                                                 │
│   h = BLAKE3-128(main.zbc with BLID bytes zeroed) [16B]         │
└─────────────────────────────────────────────────────────────────┘

┌─────────────────────────────────────────────────────────────────┐
│  Run-time (eager sync load)                                     │
│                                                                 │
│   load(path/name.zbc):                                          │
│     1. parse main → Module { funcs[].line_table = [] if no DBUG │
│                              else populated from inline DBUG }  │
│     2. probe path/name.zsym                                     │
│        ├─ exists & magic ok & SymOnly=1                         │
│        │  └─ parse sidecar; if BLID matches:                    │
│        │       merge DBUG → funcs[i].line_table                 │
│        │     else: warn + ignore                                │
│        └─ absent: silent (degraded trace path)                  │
│                                                                 │
│   format_stack_trace(frame):                                    │
│     line>0  → "  at FQN(sig) (file:line:col)"                   │
│     line==0 → "  at FQN(sig)+0x<ip> [build:<8hex>]"             │
└─────────────────────────────────────────────────────────────────┘

┌─────────────────────────────────────────────────────────────────┐
│  Offline tool                                                   │
│                                                                 │
│   z42c symbolicate crash.txt --syms name.zsym                   │
│     parse sidecar → ip→(file,line,col) lookup table             │
│     for each "at FQN(sig)+0x<ip> [build:<id>]" line:            │
│       verify build_id; lookup ip; rewrite                       │
└─────────────────────────────────────────────────────────────────┘
```

## Decisions

### Decision 1: BLAKE3-128 作为 build_id 哈希

**问题：** 哈希算法选型。

**选项：**
- A. **BLAKE3-128**（截 BLAKE3-256 前 16B）— 极快（单核 ~3 GB/s）；C# 有 `Blake3.NET`、Rust 有 `blake3` crate
- B. SHA-256 截 16B — 标准库自带、慢（~500 MB/s）
- C. xxh3-128 — 极快但工业生态偏少

**决定：选 A**。速度无感（典型 zbc 几 MB，单次哈希 < 1ms）；16B 截断后碰撞 2^-64，对"构建标识"足够；Rust/.NET 各一个 crate / NuGet 包，生态成熟。

### Decision 2: BLID 写入位置 = 主 zbc 的最后一个 section

**问题：** 怎么"哈希自身字节流但置零自身"？需确定 BLID 在文件中的字节范围。

**决定：BLID 总是最后一个 section**（writer 强制顺序）。
- writer 在所有其他 section 写完后追加 BLID（含 16 个 0 字节占位） + 计算 BLAKE3-128(整流) + `Seek(end-16)` 回填
- reader 读到 BLID 直接拿 16B 即可，无需位置感知
- 实现 50 行内可完成

### Decision 3: BLID section tag 字节 = `BLID`（4 ASCII 字符）

现有 SectionTags 全部 4 字符 ASCII（`FUNC` / `DBUG` / `TIDX` / `FRCS`）。新增项遵循同惯例：
```csharp
public static readonly byte[] Blid = "BLID"u8.ToArray();  // 16-byte BLAKE3-128 build_id
```
Rust 端对应 `b"BLID"`。"BLID" = "Build ID"，比 `BLDI` 更易读（User 决策）。

### Decision 4: 拆分逻辑统一，行为由 toml `[profile.*].strip` 控制

**问题：** debug / release 是否走不同代码路径？

**决定：统一拆分代码路径，行为由配置驱动**（User 决策，2026-05-10）。

- ZbcWriter 仅一条路径，接受 `bool strip` 参数
- effective `strip` 解析顺序（高 → 低）：
  1. CLI flag `--strip-symbols=true|false`
  2. toml `[profile.<active>].strip`（`active` ∈ {debug, release}）
  3. 内置默认值：debug = `false`，release = `true`
- toml `strip` 字段已存在（[ProjectManifest.cs:246, 328-329](../../../src/compiler/z42.Project/ProjectManifest.cs#L246)），但当前**仅解析未消费**；本期接到 driver / writer
- effective `strip = true` 时产 sidecar；`= false` 时 DBUG 内嵌、不产 sidecar

**为什么不区分 build mode**：拆分机制本身与 release/debug 语义无关（仅是产物布局选择）。User 可在 debug 下显式开 strip（验证产物 / CI 稳定性），或在 release 下关 strip（便携调试）。

### Decision 5: 同步加载（eager），不做 lazy mmap

**问题：** sidecar 加载时机。

**决定：eager**（User 决策，2026-05-10）。
- runtime 加载主 zbc 完成后立刻探测 + 解析 sidecar，合并到 FuncBody.line_table
- 异常路径零额外 IO（trace 已有 line/column）
- 简单 + 可预测；启动一次开销（典型 sidecar < 100KB，毫秒级）

留作未来优化（不在本 spec）：
- mmap sidecar + 按 ip 查询表的 lazy 路径，对启动延迟敏感场景独立 spec 引入

### Decision 6: Sidecar 路径 = `<name>.zsym`（同目录）

**决定：`<name>.zsym`**（User 决策，2026-05-10）。
- 不含 `.zbc` / `.zpkg` 中缀（短）
- 同目录探测，不引入搜索路径概念
- 跨目录搜索（环境变量、debuginfod 风格）属未来扩展

### Decision 7: 退化格式带函数签名 `at <FQN>(<sig>)+0x<ip> [build:<8hex>]`

**问题：** trace 行格式中函数名形态。

**决定：带签名，trace 全模式统一**（User 决策，2026-05-10）。

- `<FQN>` = namespace + 类 + 方法名（与现行一致，如 `MyApp.Greeter.greet`）
- `<sig>` = 简化参数类型，逗号分隔无空格（如 `str,i32`）
  - 基本类型用 zbc 类型标签名：`i32` / `i64` / `f32` / `f64` / `bool` / `str` / `void`
  - 类用裸类名（如 `Greeter`，不含 namespace）
  - 数组用 `T[]`（如 `i32[]`）
  - 空参用 `()`
- `<ip>` = u32 hex 偏移（无前导零）
- `<build>` = BLID 前 4B hex（小写）；模块无 BLID 时省略 `[build:...]`

**实现位置**：interp / jit 各 push frame 的站点把 `func_name` 改写为 `<FQN>(<sig>)`，由 module metadata 中的 sig 信息合成。trace 现有格式（line>0 时）也获益于此变更。

### Decision 8: 不为旧版本提供兼容（zbc 1.1 → 1.2）

按 [feedback_no_legacy_compat](../../../.claude/projects/-Users-d-s-qiu-Documents-codesigner-ui-z42/memory/MEMORY.md) 与 workflow.md "不为旧版本提供兼容" 规则：版本 bump 后 reader 直接拒绝 1.1 zbc。残留旧产物用 `regen-golden-tests.sh` 重生。

### Decision 9: DBUG 重组为 z42 调试信息唯一容器（LineTable + LocalVarTable）

**问题**（实施期发现，2026-05-10）：原 spec 假设 DBUG 已含 LineTable，实际 LineTable 内联在 FUNC body、DBUG 仅含 LocalVarTable。这种割裂使"DBUG → sidecar"无法真正剥离行号信息。

**决定**：作为 1.2 wire format 重组的一部分，把 LineTable 从 FUNC 抽出并入 DBUG。User 确认（2026-05-10）。

新 DBUG 每函数布局：
```
u16 lineCount
LineEntry[lineCount]: u16 block_idx, u16 instr_idx, u32 line, u32 file_str_idx, u32 column
u16 varCount
VarEntry[varCount]:   u32 name_str_idx, u16 reg_id
```

新 FUNC 每函数布局（移除 LineTable，简化）：
```
u16 reg_count, u16 block_count, u32 instr_byte_count, u16 exc_count
u32[block_count] block_offsets
ExcEntry[exc_count]
instr_bytes[instr_byte_count]
```

策略：
- strip=false：DBUG 内嵌主 zbc（与现行 1.1 行为等价，仅位置变化）
- strip=true：DBUG 整体外迁到 sidecar，主 zbc 不含 DBUG
- Stripped 模式（`.cache/*.zbc`）：仍写 DBUG（保持 dev workflow 异常 trace 显示 file:line:col）
- 触发条件：模块任何函数有 LineTable 或 LocalVarTable → 写 DBUG（无论模式）

迁移影响：ZbcWriter.BuildFuncSection 移除 LineTable / ZbcWriter.BuildDbugSection 重写 / ZbcReader 同步 / runtime zbc_reader.rs 同步 / **ZpkgWriter+Reader 增 DBUG body 字段保 packed 模式 trace feature parity** / golden 全部 regen。

## Implementation Notes

### C# 端

**依赖**：`Blake3.NET` NuGet 包（首次引入；选定后同步到 `.claude/libraries.md`）

**Writer API**：
```csharp
public static (byte[] main, byte[]? sidecar) WriteWithSidecar(
    IrModule        module,
    bool            stripSymbols,
    ZbcFlags        flags     = ZbcFlags.None,
    IEnumerable<string>? exports = null,
    TokenAllocator? allocator = null);
```
`stripSymbols=true` 时 sidecar 非空（即使 LineTable 全空也产空 DBUG sidecar，per Decision 4 一致性）；`=false` 时 sidecar = null，main 内嵌 DBUG。

**BLID 写入**：`AssembleFile` 末尾固定 reserve 16B（追加 BLID section 含 16 字节 0 占位） + 计算 BLAKE3 + `Seek` 回填。

**WriteHeader**：sidecar 文件设 `SymOnly` 标志位（bit 2，0x04）。

**Driver 流程**：
```csharp
bool effectiveStrip =
    cliStripOverride
    ?? manifest.SelectProfile(release).Strip
    ?? releaseProfile;  // built-in default
var (main, sidecar) = ZbcWriter.WriteWithSidecar(module, effectiveStrip, ...);
File.WriteAllBytes(outDir / $"{name}.zbc", main);
if (sidecar != null)
    File.WriteAllBytes(outDir / $"{name}.zsym", sidecar);
```

### Rust 端

**依赖**：`blake3` crate（生态主流）

**Loader 流程**：
```rust
fn load_module(path: &Path) -> Result<Module> {
    let main_bytes = fs::read(path)?;
    let main = parse_zbc(&main_bytes)?;
    if main.flags.contains(ZbcFlags::SymOnly) {
        bail!("{}: is a debug-symbol sidecar, not a module", path.display());
    }
    let sidecar_path = path.with_extension("zsym");
    if let Ok(sym_bytes) = fs::read(&sidecar_path) {
        match parse_sidecar(&sym_bytes) {
            Ok(sym) if sym.blid == main.blid => merge_dbug(&mut main, sym),
            Ok(sym) => warn_mismatch(&sidecar_path, sym.blid, main.blid),
            Err(e)  => warn_corrupt(&sidecar_path, e),
        }
    }
    Ok(main)
}
```

**`merge_dbug`**：按 func_idx 写回 `FuncBody.line_table`。当前 `FuncBody.line_table: Vec<LineEntry>`（[zbc_reader.rs:354](../../../src/runtime/src/metadata/zbc_reader.rs#L354)）在加载完成后需允许后置写入；评估改为 `RefCell<Vec<LineEntry>>` 或在加载早期保留可变引用直到 sidecar merge 完成。最终选型留 IMPL 阶段（影响 < 5 行）。

**`format_stack_trace` 退化**：
```rust
if frame.line == 0 {
    let ip_hex = format!("{:x}", frame.ip);
    let build_suffix = frame.module_blid
        .map(|b| format!(" [build:{:08x}]", u32::from_be_bytes([b[0], b[1], b[2], b[3]])))
        .unwrap_or_default();
    write!(out, "  at {}+0x{}{}", frame.func_name, ip_hex, build_suffix);
}
```
其中 `frame.func_name` 已含签名（Decision 7）。

**Frame.func_name 签名合成**（interp / jit 各 push frame 的入口）：
```rust
fn make_func_name(fqn: &str, sig: &Signature) -> String {
    let params = sig.params.iter().map(simplify_type_name).collect::<Vec<_>>().join(",");
    format!("{}({})", fqn, params)
}
```

### Driver 端

- `--strip-symbols=true|false` CLI flag（高优先级 override）
- `z42c symbolicate <crash.txt> --syms <path.zsym> [--out <file>]` 子命令：
  - 解析 trace 行正则：`^\s*at (.+?\(.*?\))\+0x([0-9a-f]+)( \[build:([0-9a-f]{8})\])?$`
  - 加载 sidecar；按 ip 查 line table；重写匹配行；其余行原样

### `zpkg` 集成

- packed `<name>.zpkg` 由 packer 把 `<modules>.zbc` 打成单文件；strip 模式下 packer 同时把 `<modules>.zsym` 打成 `<name>.zpkg.zsym`（zpkg 内部格式不变，只是容器名）
- 本 spec **不动 packer 逻辑** — 留 packer 后续 spec 接入；本期 sidecar 仅在 indexed 模式 `.cache/` / `dist/` 旁

## Testing Strategy

### 单元测试

- **C# (`SidecarSymbolsTests.cs`)**：
  - `WriteWithSidecar(strip=true)` 含/不含 LineTable 两 case，断言 main 不含 DBUG / sidecar 仅含 DBUG+BLID
  - `WriteWithSidecar(strip=false)` 维持现行（DBUG 内嵌、no sidecar、no BLID）
  - BLID 稳定性（同输入两次哈希一致）
  - BLID 敏感性（改一字节哈希变）
  - BLID 整流置零正确性（手算覆盖）
- **C# (`ProjectStripWiringTests.cs`)**：
  - effective strip 解析（CLI > toml > 默认）覆盖
  - debug 默认 false / release 默认 true 验证
- **C# (`SymbolicateCommandTests.cs`)**：
  - 完整 trace 还原（含签名）
  - build_id 不匹配退出码 + stderr
  - 旧 trace（无 [build:...]）warn + best-effort
- **Rust (`metadata/sidecar_tests.rs`)**：
  - sidecar 探测 + 匹配 → line_table 合并成功
  - sidecar 缺失 → 静默
  - sidecar mismatch → warn + 忽略
  - sidecar 损坏（magic / SymOnly flag / BLID 缺失）→ warn + 忽略
  - SymOnly 文件作为主模块加载 → 拒绝

### Golden 测试

- `src/tests/exception/sidecar_symbols/`（新）：
  - 一个抛异常的 z42 source
  - 编译 strip=true → 产 zbc + zsym
  - 运行 → trace 含 file:line:col 且函数名带签名
  - 删除 sidecar → 重新运行 → trace 是 `FQN(sig)+0x<ip> [build:...]`

### 验证命令

```bash
dotnet build src/compiler/z42.slnx
cargo build --manifest-path src/runtime/Cargo.toml
dotnet test src/compiler/z42.Tests/z42.Tests.csproj
./scripts/test-vm.sh
```

## Implementation Order

`unify-frame-chain` 已归档（`fd5deb2`，VmFrame 单一栈布局已就绪），本变更可直接单阶段实施：

1. C# 端：Opcodes / ZbcWriter / ZbcReader / BuildId / driver / SymbolicateCommand / Pipeline + 测试
2. Rust 端：zbc_reader / build_id / loader / sidecar 解析 + 测试
3. Rust 端 frame.func_name 增签名（interp + jit push 站点；VmFrame.func_name 已是 String 字段，仅改 push 处合成逻辑）
4. Rust 端 format_stack_trace 退化格式
5. Golden test
6. 文档同步（zbc.md / exceptions.md / project.md / 各 README）

## Deferred / Future Work

记入 `docs/design/language/exceptions.md` 的 Deferred 段（设计期延后）：

- **Lazy / mmap 加载** — 启动延迟敏感场景的优化路径
- **跨目录 sidecar 搜索** — debuginfod 风格、环境变量配置
- **packer 集成** — packed `<name>.zpkg` 的配套 `<name>.zpkg.zsym`
- **stdlib 公开 API** — `Std.Reflection.Symbolicate` 类
- **跨模块 sidecar bundle** — multi-module pack 形态
- **更紧凑的 DBUG wire format**（delta + dedup 优化）
