# z42 Roadmap

## 固定决策

- **GC**：z42 始终带 GC，不引入所有权/借用（降低上手成本）
- **IR**：寄存器 SSA 形式
- **执行模式注解**：作用于命名空间级
- **`.zbc` magic**：`ZBC\0`

---

## 阶段总览

| 阶段 | 目标 | 状态 |
|------|------|------|
| **L1** | C# 基础子集，跑通完整 pipeline（源码 → IR → VM 执行） | ✅ 已完成 |
| **L2** | 基础设施完善（编译、工程、测试、VM 质量、标准库） | 🚧 进行中 |
| **L3** | 高级语法扩展（泛型、Lambda、异步 + z42 特有特性） | 📋 待开始 |

> 阶段严格串行：L1 pipeline 全通 → 启动 L2；L2 全完成 → 启动 L3。

---

## L1 — Bootstrap（C# 基础子集）

**目标**：以最小特性集跑通完整 pipeline：词法 → 语法 → 类型检查 → IR Codegen → VM 执行。

### 语言特性范围

| 类别 | 特性 |
|------|------|
| 基本类型 | `int`/`long`/`double`/`float`/`bool`/`char`/`string`/`void` + C# 数值别名（sbyte/ushort/uint…） |
| 运算符 | 算术、比较、逻辑、位运算、复合赋值、三目 `?:`、空合并 `??` |
| 控制流 | `if`/`else`、`while`、`do-while`、`for`、`foreach`、`switch` 表达式/语句、`break`/`continue`/`return` |
| 函数 | 顶层函数、方法、表达式体（`=>`）、默认参数值 |
| 类型定义 | `class`（字段、构造器、方法、属性、`static` 成员）、`struct`、`record`、`enum` |
| 可空类型 | `T?`、`?.` 空条件访问、`??` 空合并 |
| 集合 | `T[]` 数组、`List<T>`、`Dictionary<K,V>`（pseudo-class 策略） |
| 字符串 | 插值 `$"..."`、常用方法（Length/Split/Contains/ToUpper 等） |
| 异常 | `try`/`catch`/`finally`/`throw`、自定义异常类 |
| 内置 | `Console`、`Math`、`Assert`（pseudo-class） |
| z42 扩展 | `[ExecMode]` 执行模式注解、`[HotReload]` 热更新注解（命名空间级） |

### Pipeline 实现进度

| 特性 | Parser | TypeCheck | IrGen | VM | 备注 |
|------|:------:|:---------:|:-----:|:--:|------|
| 基本类型、运算符 | ✅ | ✅ | ✅ | ✅ | |
| `if` / `while` / `for` / `foreach` | ✅ | ✅ | ✅ | ✅ | |
| `do-while` | ✅ | ✅ | ✅ | ✅ | |
| `switch` 表达式 / 语句 | ✅ | ✅ | ✅ | ✅ | |
| 三目 `?:` / `??` / `?.` | ✅ | ✅ | ✅ | ✅ | |
| 字符串插值 `$"..."` | ✅ | ✅ | ✅ | ✅ | |
| 数组 `T[]` | ✅ | ✅ | ✅ | ✅ | |
| `List<T>` | ✅ | ✅ | ✅ | ✅ | pseudo-class |
| `Dictionary<K,V>` | ✅ | ✅ | ✅ | ✅ | pseudo-class，key→string |
| 可空类型 `T?`（隐式包装） | ✅ | ✅ | ✅ | ✅ | |
| 枚举 `enum` | ✅ | ✅ | ✅ | ✅ | 成员值映射为 i64 |
| 类（字段、构造器、方法） | ✅ | ✅ | ✅ | ✅ | ctor 重载（ObjNew 携带 ctor 名）|
| auto-property（class / interface / extern） | ✅ | ✅ | ✅ | ✅ | desugar 到 backing field + getter/setter |
| 异常 `try`/`catch`/`throw` | ✅ | ✅ | ✅ | ✅ | |
| 默认参数值 | ✅ | ✅ | ✅ | ✅ | call site 展开 |
| C# 数值类型别名 | ✅ | ✅ | ✅ | ✅ | |
| Math / Assert / Console | — | ✅ | ✅ | ✅ | pseudo-class |
| `extern` + `[Native]` InternalCall | ✅ | ✅ | ✅ | ✅ | stdlib interop |
| stdlib linking (StdlibCallIndex) | — | — | ✅ | ✅ | user code → CallInstr → stdlib stub → builtin |
| 表达式体方法 `=> expr;` | ✅ | ✅ | ✅ | ✅ | TopLevelParser |
| `struct` / `record` | ✅ | ✅ | ✅ | ✅ | struct 复用 class 路径；record 自动合成ctor |
| 接口 `interface` | ✅ | ✅ | ✅ | ✅ | 通过 VCallInstr 实现运行时分发 |
| 继承 | ✅ | ✅ | ✅ | ✅ | base(...) 构造器链支持 |

---

## L2 — Foundation（基础设施）

**目标**：在 L1 pipeline 基础上，补全编译器覆盖、稳定工程体系、建立测试基线、提升 VM 质量，落地基础标准库。

### 编译器完善
- TypeChecker 完整覆盖 L1 所有特性（struct、record、interface、inheritance）
- IR Codegen 完整覆盖 L1 所有特性
- 错误体系完善：统一错误码（`E####`）、友好错误消息、`explain <CODE>` 命令
- `.zbc` 二进制格式稳定（magic、版本号、section layout 固定）
- `disasm` 反汇编输出可读性

### 工程支持
- `z42.toml` 项目清单：多 binary target、lib target、依赖声明
- **Workspace 模型（C1 ✅ 2026-04-26）**：`z42.workspace.toml` virtual manifest、glob members、`[workspace.project]` 共享元数据、`xxx.workspace = true` 引用语法、4 个内置路径模板变量。
- **Workspace include 机制（C2 ✅ 2026-04-26）**：`include` 字段 + preset 合并语义（标量覆盖 / 表合并 / 数组整体覆盖）、循环检测、嵌套深度上限 8、菱形去重。
- **Workspace policy + 集中产物（C3 ✅ 2026-04-26）**：`[policy]` 字段路径锁定（默认锁定 `build.out_dir` / `build.cache_dir`）、`[workspace.build]` 集中产物（`dist/<member>.zpkg` + cache 按 member 分目录）、`${profile}` 派生路径。
- **z42c workspace 编译运行时（C4a ✅ 2026-04-26）**：CWD 无关 workspace 发现、`-p` / `--workspace` / `--exclude` / `--no-workspace` flags、跨 member 依赖图（DFS 三色环检测）、拓扑串行编译、上游失败 → 下游 blocked、WS001/002/006 错误码。
- **z42c workspace 查询命令（C4b ✅ 2026-04-26）**：`info` / `info --resolved -p` / `info --include-graph -p` / `metadata --format json`（schema_version=1）/ `tree` / `lint-manifest`；CliOutputFormatter 带 ANSI 颜色的友好错误输出（自动 NO_COLOR 检测）。
- **z42c workspace 脚手架 + 清理（C4c ✅ 2026-04-26）**：`new --workspace` 生成完整 monorepo 骨架（z42.workspace.toml + presets/ + libs/ + apps/ + .gitignore）；`new -p <name> --kind lib|exe` 在 workspace 内新增 member；`init` 升级单 manifest 为 workspace；`fmt` 格式化所有 manifest（Tomlyn round-trip）；`clean` workspace 模式集中清理 + `-p` per-member。WS004 已彻底移除（归并入 WS010）。
- **source_hash 增量编译（C5 ✅ 2026-04-27）**：`IncrementalBuild.Probe` 比对 SHA-256 与上次 zpkg 记录、cache zbc 存在性、ExportedModule 可用性，命中则跳过 parse + typecheck + irgen；cached CU 通过 `externalImported` 注入 sharedCollector，让 fresh CU 引用 cached CU 类型。BuildPacked 写 fullMode cache zbc 让单文件自含；UsedDepNamespaces 从上次 zpkg.Dependencies 回填保证 cross-zpkg 引用正确。`--no-incremental` 强制全量。预期：stdlib 第二次 build 命中率 100%。
- `build`/`check`/`run`/`clean` 子命令完整 ✅
- 包格式 `.zpkg` 稳定（indexed/packed 模式、版本信息）

### 测试体系
- ✅ Golden test 覆盖所有 L1 特性（93 例 VM-only + 13 例 stdlib-bound = 106；2026-04-30 按归属分流到 `src/runtime/tests/golden/run/` 与 `src/libraries/<lib>/tests/golden/`）
- ✅ VM interp + JIT 双模式运行同一测试集，结果一致（104/104 × 2）
- ✅ CI 脚本稳定：`dotnet test` (799/799) + `./scripts/test-vm.sh` (104/104 × 2) + `./scripts/test-stdlib.sh` (6 lib) + `./scripts/test-cross-zpkg.sh` 全绿为唯一合并门禁
- ✅ **R 系列测试基础设施（2026-04-29 ~ 04-30）**：
  - **R1**（`add-test-metadata-section` ✅ 2026-04-30）：zbc TIDX section v=2（TestEntry skip_reason / platform / feature 字段）+ 6 个测试 attribute（[Test] [Benchmark] [Setup] [Teardown] [Skip] [Ignore]）解析与收集
  - **R2 minimal**（`add-z42-test-runner` ✅ 2026-04-30）：`Std.Test.Assert` 8 方法（Equal / NotEqual / True / False / Null / NotNull / Contains / Fail / Skip）+ `TestFailure` / `SkipSignal` 异常类
  - **R3 minimal**（`add-z42-test-runner` ✅ 2026-04-30）：z42-test-runner subprocess 模式（fork z42vm with `--entry <name>`，按 stderr 内容分类 Pass/Skip/Fail）
  - **R4.A**（`compiler-validate-test-attributes` ✅ 2026-04-30）：TestAttributeValidator pass + E0911/E0912/E0914/E0915 4 个错误码（[Test]/[Benchmark]/[Setup]/[Teardown] 签名 + 互斥校验；[Skip] 缺 reason 校验）
  - **R4.B**（`add-generic-attribute-syntax` ✅ 2026-04-30）：单类型参数泛型 attribute 语法 `[Name<TypeArg>]`；解锁 `[ShouldThrow<E>]`；E0913 验证（type arg 必填 / 类型存在 / 继承 Exception / 仅 ShouldThrow 接受 type arg）；IrGen 写入 `TestEntry.ExpectedThrowTypeIdx` + `TestFlags.ShouldThrow`
  - **A2**（`extend-runner-shouldthrow` ✅ 2026-04-30）：z42-test-runner 读 TIDX `expected_throw_type` 比对实际抛出（FQ 完整匹配 OR 短名匹配，无 inheritance walk）；dogfood.z42 两个 `[Skip]` 占位替换为 `[ShouldThrow<TestFailure>]` / `[ShouldThrow<SkipSignal>]` —— **z42.test 自检完整闭环**（7 passed / 0 skipped，原 5/2）
  - **A3**（`extend-runner-shouldthrow-inheritance` ✅ 2026-04-30）：inheritance-aware ShouldThrow 编译期展开方案——C# IrGen 把 `[ShouldThrow<E>]` 的 E + 所有可见派生类短名拼成 `;`-delimited 写入 TIDX；runner split 后任一命中即 Pass。零 TIDX 格式 bump、零运行时类型反射；dogfood 加 `[ShouldThrow<Exception>]` 验证（8/0）
  - **R3a**（`runner-formats-and-filter` ✅ 2026-04-30）：runner `--format <pretty|tap|json>` + `--filter <SUBSTR>`；默认按 TTY 自动选 pretty/tap；JSON 自定义 schema（可扩字段）；TAP 13；substring 过滤不引 regex 依赖。CI 集成 unblock
  - **R3c**（`runner-test-changed` ✅ 2026-04-30）：`scripts/test-changed.sh` + `just test-changed` —— git diff 文件 → 目录级映射 → 受影响测试命令集合；支持 `--dry-run` 与 `Z42_TEST_CHANGED_BASE` 环境变量；替换 P2 占位符
  - **R5**（`rewrite-goldens-with-test-mechanism` minimal + ad-hoc 迁移 ✅ 2026-04-30）：6 个 stdlib 库各 1 个原生 `[Test]` 测试文件 + `just test-stdlib` 入口；13 个 stdlib-bound golden 物理迁回所测库目录
- 📋 **后续完整版**（详见 [spec/changes/](../spec/changes/)）：
  - **R3b** （待开 spec）：in-process 执行 + [Setup]/[Teardown] hook 真生效（R3 完整版核心；R3a 已交付 JSON/TAP/filter）
  - **R2 完整版**（`extend-z42-test-library`）：`TestIO.captureStdout` + `Bencher` 完整实现
  - **跨包 inheritance-aware ShouldThrow**（待开 spec）：让 `[ShouldThrow<Base>]` 匹配跨非 import zpkg 依赖的 SubClass（A3 已覆盖 import 范围内；剩下要求 runner 加 LazyLoader 集成，可与 R3 完整版合并）

### VM 质量
- 类型元数据：type info、字段布局、方法表（为 L3 泛型/接口分发做准备）
- 调试符号：行号映射、局部变量名（支持基础调试体验）
- Interpreter 基础优化：指令 dispatch 效率、对象分配路径
- JIT 基础优化：热点函数识别、简单内联、常量折叠
- **MagrGC 子系统（Phase 1 / 1.5 / 3a-3f / 3-OOM ✅ 2026-04-29 主功能完整）**：`trait MagrGC` 接口（MMTk porting contract，10 能力组 ~30 方法）+ `RcMagrGC` 完整 host-side 实现 + `GcRef<T>` / heap registry / Trial-deletion 环回收器（Bacon-Rajan）修复环引用泄漏 + Drop-time finalizer（GcAllocation wrapper）+ 内存压力自动 collect + external root scanner（VmContext static_fields）+ interp/JIT 栈扫描 + strict OOM 真拒绝模式 + `Std.GC.*` 脚本暴露。后续可选迭代（性能 / 嵌入式工具 / MMTk 集成等）规划见 `docs/design/vm-architecture.md` "GC 后续迭代规划" 段。

### Native Interop / 三层 ABI

详见 [docs/design/interop.md](design/interop.md)。

| Spec | 内容 | 状态 |
|------|------|------|
| **C1**（`design-interop-interfaces` ✅ 2026-04-29）| 接口骨架一次锁定：C 头文件 `include/z42_abi.h` + 3 个 Rust crate（`z42-abi` / `z42-rs` / `z42-macros`）+ JSON Schema 2020-12 manifest + 4 个新 IR opcode（`call.native` / `call.native.vt` / `pin` / `unpin`）+ 错误码 Z0905–Z0910 占位。无运行时行为（VM 遇 trap，macro 报 `compile_error!`）。 | ✅ |
| **C2**（`impl-tier1-c-abi` ✅ 2026-04-29）| Tier 1 C ABI 运行时：`VmContext.native_types` registry + libffi cif 缓存 + `Z42Value` marshal（blittable 子集）+ `dlopen` loader + thread-local `CURRENT_VM` + `Instruction::CallNative` interp dispatch（取代 C1 trap）+ Z0905/Z0906/Z0910 抛出点 + `numz42-c` PoC（Counter 类型 alloc/inc/get end-to-end）。`z42_invoke` / reverse-call 留给 C5。 | ✅ |
| **C3**（`impl-tier2-rust-macros` ✅ 2026-04-29）| Tier 2 ergonomic Rust API：`#[z42::methods(module=, name=)]` proc macro 一次 emit descriptor + 方法表 + 所有 extern "C" shim + Z42Type impl；`module!` 生成 `<module>_register()` 入口；signature 解析 + libffi cif 自动构造（复用 C2 路径）+ panic catch_unwind 兜底（`z42_set_panic_message` Z0905 桥）；trybuild 4 个诊断测试。`numz42-rs` 内联 PoC 与 `numz42-c` 端到端等价（alloc → inc×3 → get → 3）。`#[derive(Z42Type)]` / `#[z42::trait_impl]` 仍 stub，与 source generator (C5) 一并设计 | ✅ |
| **C4**（`impl-pinned-block` runtime ✅ 2026-04-29）| Pin/Unpin VM runtime：`Value::PinnedView { ptr, len, kind }` + `PinSourceKind`；`PinPtr` 从 `Value::Str` 抽 ptr+len 构造 view（`Array<u8>` 等字节缓冲类型留给后续 spec）；`UnpinPtr` RC 后端 no-op；`FieldGet` 加 PinnedView.ptr/.len 投影；marshal::value_to_z42 接 PinnedView 投 *const u8 / usize；Z42_VALUE_TAG_PINNED_VIEW=8 钉死。**用户代码 `pinned` 关键字 / 语法** 留给 C5 与 `[Native]` / `import T from "lib"` 一并落地（避免 C# 编译器 churn 两次）。Z0908 已启用 3 条抛出条件。 | ✅ |
| **C5**（`impl-pinned-syntax` ✅ 2026-04-29）| z42 用户代码 `pinned p = s { ... }` syntax：lexer (Pinned keyword) + AST (PinnedStmt) + Parser + TypeChecker（source 类型 / 控制流 / PinnedView 字段）+ IR Codegen（PinPtr/Body/UnpinPtr）+ IrVerifier 接 4 个 native opcode + E0908a/b 错误码。其他 user-facing FFI 语法（`[Native(lib=,entry=)]` extended attribute / `extern class T { ... }` / `import T from "lib"` / `.z42abi` manifest reader / `CallNativeVtable` runtime）**全部留给后续独立 spec**，避免一次改动横跨 C# 编译器太多模块。 | ✅ |
| **C6**（`extend-native-attribute` ✅ 2026-04-29）| 扩展 `[Native]` attribute 接受新形式 `[Native(lib=, type=, entry=)]`；解析为 `Tier1NativeBinding`；TypeChecker 接受新形式（互斥于 legacy `[Native("__name")]`）；IR Codegen 在 stub 中 emit `CallNativeInstr` 而非 `BuiltinInstr`。**z42 用户代码现在能直接调用 C2 注册的 native 函数**——只缺 test harness 在 zbc 启动前预注册 numz42-c（独立 spec）即可端到端运行。E0907 NativeAttributeMalformed 已启用。 | ✅ |
| **C7**（e2e harness ✅ 2026-04-29）| Rust 集成测试加载真实 .z42 编译产物 + 预注册 numz42-c + 跑端到端，闭环 C2→C6；build.rs 自动编译 fixture .z42 → OUT_DIR/.zbc | ✅ |
| **C8**（`marshal-str-to-cstr` ✅ 2026-04-29）| `Value::Str` 直接 marshal 到 `*const c_char`：`marshal::Arena` 承载临时 CString；`(Value::Str, SigType::CStr/Ptr)` 分支构造 NUL-terminated 借出；CallNative dispatch 改造接 arena；interior NUL 报 Z0908；numz42-c 加 strlen + e2e 验证 `strlen("hello world") == 11`。z42 用户代码现在可以直接传 string 到 native 函数无需 pinned 块 | ✅ |
| **C9**（`class-level-native-shorthand` ✅ 2026-04-29）| 类级 `[Native(lib=, type=)]` 默认值：`Tier1NativeBinding` 字段改 nullable 承载部分信息；`ClassDecl` 加 `ClassNativeDefaults` 字段；parser 接受 partial 形式（lib/type/entry 任意子集）；TypeChecker 把方法级 binding 与类级 defaults 拼接，缺 fields 报 E0907；IrGen `EmitNativeStub` 接 stitched binding emit `CallNativeInstr`。**用户写非平凡 native 库不再需要每个方法重复 lib + type**。E0907 抛出条件扩展。 | ✅ |
| **C10**（`byte-buffer-pin` ✅ 2026-04-29）| Array<u8> pin support：`VmContext.pinned_owned_buffers` 副表持有 `Box<[u8]>`；`PinPtr` Array 源扫元素验证 0..=255 → 拷贝到 Box → leak ptr；`UnpinPtr` 释放 Box；snapshot 语义。numz42-c 加 `counter_buflen(*const u8, u64) -> i64`；e2e 测试 z42 byte[] → CallNative buflen → 长度正确。**z42 二进制数据可直接进 native FFI**（文件 IO / 密码学 / 网络协议 unblocked）。Z0908(e) 抛出。 | ✅ |
| **C11a**（`manifest-reader-import` ✅ 2026-04-30）| Phase1 关键字 `import`（`from` contextual） + `import IDENT from "<lib>";` 顶层语法 → AST `NativeTypeImport` 收集到 `CompilationUnit.NativeImports`；`Z42.Project.NativeManifest.Read` 读取 `.z42abi` JSON（System.Text.Json，`abi_version == 1` + 必需字段轻量校验），失败抛 `NativeManifestException`；E0909 ManifestParseError 启用。**编译器消费 manifest 数据通路就位；尚未合成 ClassDecl（留给 C11b）**。 | ✅ |
| **C11b**（`synthesize-native-class` ✅ 2026-04-30，Path B1）| `NativeImportSynthesizer` 编译期 pass：`import T from "lib";` → 找 manifest → 合成 `ClassDecl`（`IsSealed=true`, `Visibility=Internal`, `Fields=[]`, `ClassNativeDefaults` 复用 C9 stitching）注入 `cu.Classes`；`ManifestSignatureParser` 白名单（primitives + `Self` + `*mut/const Self`）；`INativeManifestLocator` 注入式（默认 `<sourceDir>/<lib>.z42abi` + `Z42_NATIVE_LIBS_PATH`）；E0916 启用。**用户 `import T from "lib";` 即得脚本可见类，TypeChecker / IrGen / VM 走既有路径，零新 ABI**。Path B2 (VM-owned 字段)、C (`[Repr(C)]` 脚本端布局) 留 C11c/C11d。 | ✅ |
| **C11+**（后续 spec，未排）| C11c (Path B2: 脚本字段 + VM `z42_obj_*` ABI) / C11d (Path C: 脚本 `[Repr(C)]` 映射) / C11e (扩签名白名单：c_char/Array/Object) / extern class T / CallNativeVtable runtime + IR codegen / JIT emit native opcodes | 📋 |

### 标准库（基础）
- `z42.core`：基础类型协议（ToString、Equals、GetHashCode）
- `z42.io`：文件读写、标准输入输出
- `z42.core/Collections/`：`List<T>`、`Dictionary<K,V>` 纯脚本实现（L3-G4h step3 ✅ 完成；2026-04-25 W1 从 `z42.collections` 上提到 `z42.core` 包子目录，对齐 C# BCL）
- `z42.collections`：次级集合 `Queue<T>` / `Stack<T>` / `LinkedList<T>`（未来 `SortedDictionary` / `PriorityQueue`）
- `z42.core` Wave 2（2026-04-25）：`Exception` 基类 + 9 个标准子类（`ArgumentException` 等）+ `IDisposable` / `IEnumerable<T>` / `IEnumerator<T>` 接口契约；详见 `docs/design/exceptions.md`、`docs/design/iteration.md`
- `z42.core` Wave 3（2026-04-26）：`IComparer<T>` / `IEqualityComparer<T>` / `IFormattable` 接口契约（Script-First 纯定义，无 implementer）
- `z42.string`：字符串操作完整实现

> **后续扩展计划**（time / threading / net / json / crypto 等 P0–P3 分级清单）：
> 见 [docs/design/stdlib-roadmap.md](design/stdlib-roadmap.md)。

### 代码质量 Backlog（按触发条件执行）

> 来源：2026-04-14 代码审查。批次 1–4 已完成，以下为剩余低优先级项。

| 项目 | 触发条件 | 说明 |
|------|---------|------|
| A6: Value `Rc<RefCell>` → `Arc<Mutex>` 或对象池 | L3 async/线程模型设计时 | `Rc` 是 `!Send`，阻塞跨线程传值；需与并发模型一并设计。**注**：MagrGC Phase 1 已收口分配接口（2026-04-29），Phase 3 切换到 `GcRef<T>` 时一并解决 Send 问题 |
| A10: `PackageCompiler` → 可注入 `BuildPipeline` | 需要 mock 文件系统做编译器单元测试时 | 当前 static class 可用，低优先级 |
| `TypeEnv.BuiltinClasses` 动态注入 | L3 泛型设计启动时 | 当前硬编码集合；与泛型一并设计 |
| `IsReferenceType` 中 List/Dict 硬编码 | L3 泛型设计启动时 | List/Dict 应为 `Z42ClassType`，需泛型类型表示 |
| switch 穷举检查（exhaustiveness） | enum switch 场景增多时 | switch on enum 不检查是否覆盖所有成员 |
| 死代码警告 | IDE 集成或用户反馈时 | return 后语句静默丢弃，应发 warning |
| 隐式窄化转换拒绝 | 数值精度 bug 出现时 | `int x = someLong` 应报错要求显式 cast |
| `IrInstr` JsonDerivedType 自动注册 | 指令数超过 60 个时 | 当前 54 个注解，可考虑 Source Generator 方案 |
| `exec_instr.rs` 按类别拆分辅助函数 | 文件超过 450 行时 | 当前 362 行，保持单 match 结构但提取 arm 实现 |
| Golden Test 改用 `test.toml` 声明类别 | 测试目录结构变复杂时 | 当前路径约定 (`/errors/`, `/run/`) 工作正常 |

---

## L3 — Advanced（高级特性）

**目标**：引入 L1 推迟的高级语法，以及 z42 特有的类型系统扩展。L2 全完成后启动。

### L3-G 泛型实现进度

| 子阶段 | 内容 | Parser | TypeCheck | IrGen | VM | 状态 |
|--------|------|:------:|:---------:|:-----:|:--:|:----:|
| **L3-G1** | 泛型函数 + 泛型类（无约束） | ✅ | ✅ | ✅ | ✅ | ✅ |
| **L3-G2** | 接口约束（`where T: I + J`） | ✅ | ✅ | — | — | ✅ |
| **L3-G2.5** | 约束范式补充：基类 ✅ / ctor ✅ / class ✅ / struct ✅ / enum ✅ / notnull 等 | ✅ | 🟡 | ✅ | ✅ | 🟡 |
| **L3-G3a** | zbc 约束元数据 + VM loader + 加载时校验 | — | — | ✅ | ✅ | ✅ |
| **L3-G3c** | 关联类型（`type Output; Output=T`）— **决策：跳过，等真正用例驱动**（迭代器 trait / async Future 等）。当前 `where T: I<T>` 自引用约束已覆盖 90% 数值场景；C# 不带关联类型也很成功 | — | — | — | — | ⏸ 延后 |
| **L3-G3d** | 跨 zpkg TypeChecker 消费约束（TSIG 扩展） | — | ✅ | ✅ | — | ✅ |
| **L3-G4a** | 泛型类实例化类型替换（call-site T → 具体类型） | — | ✅ | — | — | ✅ |
| **L3-G4b** | Primitive-as-struct: stdlib `struct int : IComparable<int>` 驱动；删除 `PrimitiveImplementsInterface` / `primitive_method_builtin` 硬编码 | ✅ | ✅ | — | ✅ | ✅ |
| **L3-Impl1** | extern impl Change 1：`impl Trait for Type { ... }` 块（body 方法；同 CU 合并）| ✅ | ✅ | ✅ | ✅ | ✅ |
| **L3-Impl2** | 跨 zpkg impl 传播：zpkg IMPL section + ImportedSymbolLoader Phase 3 合并（仅脚本 body；impl 块永久禁止 extern，见 generics.md）| — | ✅ | ✅ | ✅ | ✅ |
| **L3-G4c** | User-level 泛型容器源码实现（MyList<T> 端到端 demo） | — | ✅ | — | ✅ | ✅ |
| **L3-G4d** | stdlib 导出泛型类（Std.Collections.Stack / Queue 启用 + 名称冲突裁决 + 懒加载 ctor） | — | ✅ | ✅ | ✅ | ✅ |
| **L3-G4e** | 索引器语法 `T this[int] { get; set; }` — desugar 到 get_Item/set_Item | ✅ | ✅ | — | — | ✅ |
| **L3-G4f** | 源码级 ArrayList<T> ✅；HashMap 放到 G4g | — | 🟡 | — | — | 🟡 |
| **L3-G4g** | 跨命名空间约束解析 ✅ + ArrayList.Contains/IndexOf ✅ + HashMap<K,V> ✅ + TSIG 不重导入 ✅ | — | ✅ | — | — | ✅ |
| **L3-G4h** | step1 `&&`/`||` 短路求值 ✅；step2 foreach 鸭子协议 ✅；step3 pseudo-class List/Dict → 源码 ✅ | — | ✅ | ✅ | ✅ | ✅ |
| **L3-G4** | 泛型标准库（已细拆为 G4a/G4b/G4c/G4d，保留总指标） | — | 🟡 | — | 🟡 | 🟡 |
| **L3-R** | 反射与运行时类型信息 — 见下独立小节（统一批次，延后） | — | — | — | — | 📋 |

> L3-G1 已实现：泛型函数/类定义、显式/推断类型参数、IR 代码共享、SIGS/TYPE section 携带 `type_params`。
> L3-G2 已实现：`where T: I + J` / `where K: I, V: J` 语法、约束方法查找、调用点校验、返回类型按推断替换；启用 `IComparable<T>` / `IEquatable<T>` stdlib 接口。
> L3-G3a 已实现：zbc 版本 0.4 → 0.5，SIGS/TYPE per-tp 约束元数据；Rust VM loader 读取到 `TypeDesc.type_param_constraints` / `Function.type_param_constraints`；加载时 `verify_constraints` pass 校验约束引用的 class/interface 存在（`Std.*` 前缀放行给 lazy loader）。**不**做运行时 Call/ObjNew 校验（留给 L3-G3b 配合反射）。

### L3-G2.5：约束范式扩展（计划）

L3-G2 仅实现 interface 约束。以下范式按优先级排期，每项独立规格。
**设计决策**：约束合取使用 `+`（Rust 风格）而非 C# `,`；**不支持** OR 约束 `T: A | B`
（主流语言都无；见 `docs/design/generics.md` 设计决策小节）。

#### 已完成 / 已规划

| 约束 | 语法 | 语义 | 优先级 / 状态 |
|------|------|------|:------:|
| **基类约束** | `where T: BaseClass` | T 必须继承自指定类；可访问基类字段/方法 | ✅ 已完成（2026-04-22） |
| **构造器约束** | `where T: new()` | T 必须有无参构造器（`new T()` body 实例化待 L3-R） | ✅ 校验已完成（2026-04-23） |
| **引用类型约束** | `where T: class` | T 为引用类型（排除 struct/primitive） | ✅ 已完成（2026-04-22） |
| **值类型约束** | `where T: struct` | T 为值类型 | ✅ 已完成（2026-04-22） |
| **接口继承约束** | `where T: I<U>, U: J` | 跨参数约束链（带 TypeArgs 替换校验） | ✅ 已完成（2026-04-23） |
| **裸类型参数约束** | `where U: T` | U 必须是 T 的子类型（T 为同 decl 其他 type param） | ✅ 已完成（2026-04-22） |
| **枚举约束** | `where T: enum` | T 必须是 enum 类型（校验层完整；反射操作待 L3-R） | ✅ 已完成（2026-04-23） |

#### 后续迭代

| 约束 | 语法 | 语义 | 优先级 | 难度 |
|------|------|------|:-----:|:----:|
| **数值约束** | `where T: INumber<T>` | stdlib 声明 + primitive struct 纯脚本 body 实现；Script-First | ✅ 已完成（2026-04-23） |
| **Operator 重载（C# 风）** | `public static T operator +(T, T)` | desugar 到 `op_Add` 静态调用；支持异构算子；5 个二元算术 | ✅ 已完成（2026-04-24） |
| **静态抽象接口成员（C# 11 对齐）** | `interface INumber<T> where T: INumber<T> { static abstract T op_Add(T, T); ... }` | 三档 `static abstract / virtual / concrete`；实现者 `static override`；泛型 `a + b` on `T: INumber<T>` 通过 VCall 值驱动派发（复用既有 IR，无新指令）。stdlib INumber + int/long/float/double 全迁移 | ✅ 已完成（2026-04-24，iter 1；`T.Zero` 类型级访问延至 iter 2） |
| **非空约束** | `where T: notnull` | T 非空（排除 `T?`） | 🟡 中 | 低（待可空性方案收敛） |
| **无托管约束** | `where T: unmanaged` | T 是无托管引用的值类型（FFI / SIMD / buffer 池） | 🟡 中 | 中（需区分 struct 含 ref 字段） |
| **具象化约束** | `reified T` | body 内可用 `T::class` / `is T`（Kotlin 风格） | 🟡 中 | 高（**依赖 L3-R** runtime type_args） |
| **委托/函数约束** | `where T: Func<...>` | 可调用约束（`Fn/FnMut/FnOnce` 等价） | 🟠 低 | 高（**依赖 lambda**） |
| **关联类型链** | `where T: Iter, T::Item: Clone` | 深度泛型（Rust 迭代器链） | 🟠 低 | 很高（**依赖 L3-G3c**） |
| **变型标注** | `interface IFoo<in T>` / `<out T>` | 协变/逆变 | 延后 L3 后期 | 中 |
| **默认类型参数** | `class Box<T = int>` | 省略时默认值 | 延后 L3 后期 | 低 |

#### 明确不做（与 z42 模型不契合）

| 约束 | 理由 |
|------|------|
| **`T: Copy`** | z42 是 GC 语言；class = ref、struct = value 已自动区分，无须显式 Copy trait |
| **`T: ?Sized`** | z42 所有对象定长（GC 托管）；DST / slice / trait object 概念不适用 |
| **OR 约束 `T: A \| B`** | 主流语言都无；body 只能用 A ∩ B 交集方法，实用性差。替代方案（共同基接口 / 重载 / ADT）更清晰 |

**实现策略**：
- 基类 + 构造器约束复用现有 interface 约束框架（`Z42GenericParamType` 的 Constraints
  扩展为 union: Interface / BaseClass / ConstructorReq / ValueKind）
- `class` / `struct` / `notnull` / `unmanaged` / `enum` / `new()` 作为 flag 附加在
  GenericParam 上，共享 zbc flags 字节（现有已用 bits 0x01–0x10，留 0x20–0x80 给后续）
- `reified` / `T::Output` 类关联功能和 L3-R / L3-G3c 合批
- 每个范式独立 spec change，共享 L3-G3a 的约束元数据 zbc 扩展

### L3-R：反射与运行时类型信息（统一批次，延后）

把原 L3-G3b（反射接口 + 运行时约束校验）与其他反射需求合并成独立轨道，一次性规划
VM 运行时类型系统。多项特性联动，单独做不如合并。

| 子项 | 内容 | 依赖 |
|------|------|------|
| **R-1 核心 Type API** | `typeof(T)` / `t.GetType()` / `Type.Name` / `Type.TypeParams` / `Type.TypeArgs` | L3-G3a（元数据已就绪） |
| **R-2 约束反射** | `Type.Constraints` / `Type.BaseClass` / `Type.Interfaces` | R-1 |
| **R-3 is/as 运行时判断** | `t is IComparable<T>` / `t as SomeBase` 基于 TypeDesc.vtable + constraints | R-1 + R-2 |
| **R-4 运行时 Call/ObjNew 约束校验** | 泛型函数 Call / `new T(...)` 时校验 type_args 满足约束（untrusted zbc 兜底） | R-1 + type_args 传递机制 |
| **R-5 运行时 type_args 传递** | 泛型实例化信息通过隐式参数 / thread-local / TypeDesc 引用传到 callee | 需 VM 架构决策 |
| **R-6 `new T()` 支持** | 依赖 R-5 拿到 T 的 TypeDesc，ObjNew 时用实际类名 | R-5 |
| **R-7 `Activator.CreateInstance<T>(args)`** | 反射式泛型实例化 | R-5 + R-6 |
| **R-8 Module / Assembly 反射** | `Module.GetTypes()` / `Type.GetMethods()` | R-1 |
| **R-9 关联类型反射** | `Type.AssocTypes["Output"]` | L3-G3c |
| **R-10 IDE / 工具 元数据** | 供外部工具（LSP / REPL）读取 TypeDesc 完整结构 | R-1 |

**设计挑战（为什么合并）**：
- R-5 运行时 type_args 传递是最核心的架构决策 — 决定 R-4/R-6/R-7 能否实现
- 单独做 R-1/R-2 意义有限（应用场景少，ROI 低）；和 R-4/R-5 一起才产生价值
- zbc 格式扩展若需要为 R-2/R-9 补字段，与 L3-G3a 的约束字段可一次设计完

**先决条件**：
- L3-G3a 已完成（元数据管道打通）✅
- L3-G3c 关联类型（R-9 依赖）
- VM 架构决策：type_args 如何在代码共享前提下运行时可得

### L3-C 闭包（Closures）实现进度

设计已锁定 — 见 [`docs/design/closure.md`](design/closure.md)（2026-05-01 归档变更 `add-closures`）。
实现拆为两个独立变更，按 L 阶段递进：

| 子阶段 | 内容 | 落地变更 | 状态 |
|--------|------|---------|:----:|
| **L3-C0**（设计） | 闭包 spec + IR 草案 + grammar 文法 + 文档同步 | `add-closures` | ✅ 已完成（2026-05-01）|
| **L2-C1**（无捕获 lambda） | Parser + AST + TypeCheck + Codegen + VM：lambda 字面量、`(T)->R` 函数类型、`Func<>`/`Action<>` desugar、`LoadFn` + `CallIndirect` 间接调用 | `impl-lambda-l2` | ✅ 已完成（2026-05-01）|
| **L2-C1b**（local function） | 嵌套 `Type Name(...)` 函数声明 + L2 无捕获检查（impl-lambda-l2 实施时拆出，理由见 spec） | `impl-local-fn-l2` | 📋 待开始 |
| **L3-C2**（完整闭包） | 捕获分析 + 三档实现（栈/单态化/堆擦除）+ Send 派生 + `--warn-closure-alloc` + Ref<T> 共享；JIT 路径补全 LoadFn/CallIndirect | `impl-closure-l3` | 📋 待开始 |

衍生需求（独立 follow-up）：
- VM 诊断：对象引用链 / captured env dump / allocation site 追踪 — 待 `vm-architecture.md` 立项

### 高级语法（从 L1 推迟）

| 特性 | 说明 |
|------|------|
| 泛型 `<T>` + `where` 约束 | 类型参数、约束推断、代码共享 + 具化（L3-G1 ✅ 基础完成） |
| Lambda + 闭包 | 设计已锁，分 L2-C1 / L3-C2 两批落地（见上 L3-C 表） |
| 接口完整实现 | 多接口、虚方法表、接口继承 |
| 类继承完整实现 | 多态、`override`/`virtual`/`abstract` |
| `async`/`await` | `Task`/`ValueTask`、结构化并发 |
| LINQ 风格 | `Where`/`Select`/`OrderBy`/`ToList` 等 |
| 命名参数 | call site 指定参数名（`Greet(name: "z42")`） |
| 模式匹配扩展 | 属性模式、位置模式、`is` 类型测试 |

### z42 特有扩展

| 特性 | 说明 |
|------|------|
| `Result<T, E>` + `?` 运算符 | 函数式错误处理，`try`/`catch` 的高效替代 |
| `Option<T>` | 替代 `T?`，编译期穷尽检查，消除 null |
| Trait | 接口静态分发（零开销抽象），替代虚方法表 |
| ADT（代数数据类型） | 原生 sum type，替代 `abstract record` 模拟 |
| `match` 穷尽检查 | 强制覆盖所有分支，替代 `switch` |
| 默认不可变变量 | `let` 不可变，`var`/`mut` 显式可变 |
| 单文件脚本模式 | 无需 `z42.toml`，直接执行 `.z42` 文件 |
| 内联 eval | `z42vm -c "..."` 字符串直接执行；嵌入 API（host 传入 source/bytecode） |
| REPL | 交互式求值环境 |

---

## 实现里程碑（pipeline 维度）

| 里程碑 | 内容 | 所属阶段 | 状态 |
|--------|------|:-------:|:----:|
| M1 | Lexer + Parser | L1 | ✅ |
| M2 | TypeChecker（L1 特性全覆盖） | L1 → L2 | ✅ |
| M3 | IR Codegen → `.zbc`（L1 特性全覆盖） | L1 → L2 | ✅ |
| M4 | VM Interpreter（L1 特性全覆盖） | L1 | ✅ |
| M5 | VM JIT（Cranelift，L1 特性） | L1 → L2 | ✅ |
| M6 | 工程支持 + 测试体系 + `.zbc` 格式稳定 | L2 | 📋 |
| M7 | VM 元数据 + 标准库基础（core/io/collections） | L2 | 🚧 |
| M8 | TypeChecker + Codegen 扩展（L3 特性） | L3 | 📋 |
| M9 | VM AOT（LLVM/inkwell） | L3 | 📋 |
| M10 | 自举（Self-hosting） | L3+ | 📋 |

**当前焦点：M6（工程支持 + 测试体系 + 错误码体系）→ M7（VM 元数据 + 标准库）**
