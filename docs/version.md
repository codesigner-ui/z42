# z42 版本路线图（0.1.0 → 1.0.0）

> **目的**：把语言设计（[features.md](features.md)）与实现阶段（[roadmap.md](roadmap.md)）映射到具体的 SemVer 版本号，让贡献者和使用者能看到清晰的发布节奏。
>
> **与 [roadmap.md](roadmap.md) 的关系**：
> - `roadmap.md` 按 **phase（L1/L2/L3）** 组织实现进度，回答"现在做到哪一步"
> - 本文档按 **SemVer 版本** 组织发布节奏，回答"什么特性在哪个版本发布"
> - 两份文档互补、不重复内容；版本与 phase 的对应关系在每个大版本的 charter 中标注
>
> **状态**：草案（2026-04-28）。版本号与子版本切分已确定；具体子版本顺序与工作量估算在实施时可调整。

---

## 设计原则

1. **每个 minor 是独立可发布单位** —— 升级到 `0.X.0` 应该有用户可感知的能力跃迁
2. **每个 patch 是独立 spec** —— `0.X.Y` 对应一个 `spec/changes/` 变更单元，单独实施 + 验证 + 归档 + commit + push
3. **横向工作流贯穿所有版本** —— benchmark、perf CI、GREEN 标准随版本演进，与功能特性独立
4. **1.0.0 之前不承诺向后兼容** —— 与 [workflow.md "不为旧版本提供兼容"](../.claude/rules/workflow.md) 对齐
5. **1.0.0 启用 SemVer + deprecation 周期** —— 必须先通过自举 + AOT + perf 三道关

---

## 总览

| 版本 | 主题 | Phase | 子版本数 | 预计工作量 |
|------|------|:----:|:----:|:----:|
| **0.1.x** | L1 GA + 收尾 | L1 | 4 | 2–3 周 |
| **0.2.x** | 工程化 & 包系统 + perf CI + 多平台 CI | L2-M6 | 7 | 6–8 周 |
| **0.3.x** | 测试体系 & VM 质量 + GC v1 | L2-M7 | 5 | 6–8 周 |
| **0.4.x** | 标准库 v1 + test/bench/docgen 工具链 | L2-M7 | 9 | 9–11 周 |
| **0.5.x** | 泛型 & Trait & 反射 + LSP + Interop 2a | L3 | 8 | 10–14 周 |
| **0.6.x** | 函数式 & unmanaged + GC v2 + linter | L3 | 8 | 9–11 周 |
| **0.7.x** | Result + ADT + match | L3 | 5 | 6–8 周 |
| **0.8.x** | 并发 & 多线程 + GC v3 + DAP debugger | L3 | 8 | 12–16 周 |
| **0.9.x** | 脚本 & 嵌入 & 可裁剪 + WASM + Interop 2b | L3 | 9 | 10–14 周 |
| **0.10.x** | 性能强化（philosophy §9 全指标达标） | L3 | 9 | 8–12 周 |
| **1.0.x** | 自举 + 跨架构 AOT + 稳定 + 工具链 GA | L3+ | 6 (rc) | 14–18 周 |

**累计**：78 个子版本 / 约 16–20 个月（按全职 1 人节奏）。

---

## 横向工作流（贯穿全部版本）

下列项目不绑定单一版本，在指定版本启用后持续生效，不再消失。

| 工作流 | 启用版本 | 内容 |
|------|:------:|------|
| **Benchmark 套件** | 0.2.2 | `cargo bench`（VM）+ BenchmarkDotNet（编译器）骨架与基线 |
| **Perf CI** | 0.2.3 | 关键 benchmark > 10% 退化阻塞 commit |
| **多平台 CI matrix** | 0.2.5 | macOS（x86_64/arm64）+ Linux（x86_64/arm64）+ Windows（x86_64）build & test 全绿 |
| **项目级 CI 模板** | 0.2.5 | `z42c new` 生成的项目自带 GitHub Actions / GitLab CI 模板 |
| **Release 自动化** | 0.2.6 | git tag → 跨平台 z42c/z42vm 二进制 + zpkg 制品自动产出 |
| **跨 mode 一致性 CI** | 0.3.0 | interp / JIT 同测试集结果一致 |
| **`z42c test` GREEN 门禁** | 0.4.6 | stdlib 与用户代码的 z42 测试纳入 GREEN 标准 |
| **`z42c bench --diff`** | 0.4.7 | z42 代码 bench 进入 perf CI 链路 |
| **`z42-doc` API 文档自动发布** | 0.4.8 | 标准库 doc comment → 静态站点，每次发布自动更新 |
| **LSP 集成测试** | 0.5.7 | LSP server 协议级 conformance test 进 CI |
| **DAP debugger conformance** | 0.8.7 | DAP 协议适配层进 CI，支持 VS Code/JetBrains 调试 |
| **WASM target CI** | 0.9.7 | VM 编译为 WASM 并在 headless 浏览器跑标准库测试集 |
| **跨 mode bench 对比** | 0.10.x | interp / JIT / AOT 三模 bench 报告对比 |
| **跨架构 perf 矩阵** | 1.0-rc1 | x86_64 / arm64 / wasm32 三架构 perf 数字进 release notes |

### 多平台支持矩阵

| 平台 | 编译器（dotnet） | VM（Rust） | NativeAOT 目标 | 起始版本 |
|------|:---:|:---:|:---:|:----:|
| macOS x86_64 | ✅ | ✅ | ✅ | 0.2.5 |
| macOS arm64 | ✅ | ✅ | ✅ | 0.2.5 |
| Linux x86_64 | ✅ | ✅ | ✅ | 0.2.5 |
| Linux arm64 | ✅ | ✅ | ✅ | 0.2.5 |
| Windows x86_64 | ✅ | ✅ | ✅ | 0.2.5 |
| Windows arm64 | ✅ | ✅ | ⚠️ rc | 1.0-rc2 |
| WASM (wasm32-wasi) | — | ✅ VM only | — | 0.9.7 |
| iOS / Android | — | 🔬 实验 | 🔬 实验 | 1.x+ |
| 嵌入式（no_std） | — | 🔬 实验 | — | 1.x+ |

### Toolchain 矩阵

| 工具 | 用途 | 起始版本 | GA 版本 |
|------|----|:----:|:----:|
| `z42c` | 编译器驱动（build/check/run/test/bench/fmt/clean/disasm/explain/new/init/doc） | 当前 | 0.4.x |
| `z42vm` | VM 运行时（执行 .zbc / .zpkg / 源文件 / -c） | 当前 | 0.9.x |
| `z42-fmt` | 代码格式化（独立 binary，亦集成在 z42c fmt） | 当前 | 0.2.4 |
| `z42-doc` | API 文档生成（doc comment → HTML / markdown） | 0.4.8 | 0.4.8 |
| `z42-lsp` | Language Server Protocol 实现 | 0.5.7 | 0.6.7 |
| `z42-lint` | 静态检查（基于 LSP 诊断扩展） | 0.6.7 | 0.7.x |
| `z42-dap` | Debug Adapter Protocol 适配层 | 0.8.7 | 0.9.x |
| `z42up` | 版本管理工具（rustup 等价物） | 1.0-rc6 | 1.0 |
| `z42-pkg` | 包注册表客户端（远程依赖发布与下载） | 1.x+ | 1.x+ |

---

## 跨版本关键依赖

```
0.1 ─► 0.2 ─► 0.3 ──┬──► 0.4 ──► 0.5 ──► 0.6 ──► 0.7 ──► 0.8 ──► 0.9 ──► 0.10 ──► 1.0
       │       │   │           │                                            │
       │       │   │           └─── reflection ───► 0.5.6 (test 增强)        │
       │       │   │                                                        │
       │       │   └─── GC v1 ─────► GC v2 (0.6) ─► GC v3 (0.8) ───────────► │
       │       │                                                            │
       │       └─── benchmark 套件 ─► perf CI (0.2.3) ──持续生效──────────► │
       │                                                                    │
       └─── .zbc/.zpkg 格式冻结 ─────► 1.0 SemVer 启用 ────────────────────► │

强依赖关系：
  • 0.5 反射 ◄── 0.10.x 性能数据自查（type metadata access）
  • 0.6 unmanaged ◄── 0.9.6 C ABI 头文件（C-compatible struct 跨边界）
  • 0.7 Result ◄── 0.8 async（async fn 通常返回 Task<Result<T,E>>）
  • 0.8 GC v3 ◄── 0.9.5 VM 组件化（gc 组件可替换）
  • 0.10 性能基线 ◄── 1.0 稳定承诺（无基线不能锁 API）
  • 1.0 自举 ◄── 0.5+ 全部 L3 特性（编译器自身需要 lambda / generic / async）

工具链 / CI 依赖链：
  • 0.2.5 多平台 CI matrix ◄── 1.0-rc2 跨架构 NativeAOT
  • 0.3.1 调试符号 ◄── 0.8.7 DAP debugger
  • 0.5.1 反射 R-1 ◄── 0.5.7 LSP（hover 类型信息）
  • 0.5.7 LSP ◄── 0.6.7 z42-lint（诊断扩展）
  • 0.4.6 z42.test ◄── 0.4.8 z42-doc（doctest 集成）
  • 0.9.5 VM 组件化 ◄── 0.9.7 WASM target（wasm 子集需 gc/exceptions 可替换）
  • 0.2.6 Release 自动化 ◄── 1.0-rc6 z42up（依赖跨平台二进制持续产出）
```

---

## 0.1.x — L1 GA + 收尾

**Charter**：把 L1 子集所有 pipeline 阶段稳定到"对外可用"水准，作为后续版本的工作基线。

**入口标准**：当前已达成（L1 全特性 ✅、Interp + JIT 双模 ✅、stdlib W1–W3 ✅）

**退出标准**：
- 所有 L1 特性 golden test 全绿
- 错误码 `E####` 体系雏形可用，所有 lexer/parser/typecheck 错误经过统一编码
- `examples/` 至少覆盖每个 L1 特性 1 个示例
- `docs/design/language-overview.md` 与代码行为完全一致

| 版本 | 内容 | 工作量 |
|------|------|:----:|
| 0.1.0 | L1 全特性 GA + disasm 可读性收尾 | 1 周 |
| 0.1.1 | 错误码 `E####` 雏形（lexer/parser/typecheck 三阶段全部接入） | 1 周 |
| 0.1.2 | `z42c explain <CODE>` 命令 + 错误消息友好化（带源码片段、提示） | 3–5 天 |
| 0.1.3 | `examples/` 全特性示例补齐 + `docs/design/` 文档与实现对齐 sweep | 3–5 天 |

**关键风险**：
- 错误码体系对所有现有错误点 retrofit 工作量可能超出预估，必要时拆分到 0.2.x

---

## 0.2.x — 工程化 & 包系统 + perf CI 立项

**Charter**：固化 `.zbc` / `.zpkg` 格式与 `z42c` 工程命令；建立 perf CI 基础设施作为后续所有版本的守门员。

**入口标准**：0.1.x 完成（错误码体系可用）

**退出标准**：
- `.zbc` v0.x 头部布局冻结（magic、版本号、section 顺序固定）
- `.zpkg` indexed/packed 二种模式格式冻结
- Benchmark 套件运行 + perf CI 上线
- `z42c new/init/fmt/clean/build/check/run` 命令完整

| 版本 | 内容 | 工作量 |
|------|------|:----:|
| 0.2.0 | `.zbc` v0.x 格式冻结（magic + section layout 锁定） | 1 周 |
| 0.2.1 | `.zpkg` indexed/packed 格式冻结 + `z42c disasm` 完整 | 1 周 |
| 0.2.2 | Benchmark 套件骨架（`cargo bench` + BenchmarkDotNet）+ 初始基线 | 1.5 周 |
| 0.2.3 | Perf CI + 性能预算工作流（≥10% 退化阻塞 commit） | 1 周 |
| 0.2.4 | `z42c new/init/fmt/clean` 命令收尾 + `z42-fmt` 独立 binary + `lint-manifest` | 1 周 |
| **0.2.5** | **多平台 CI matrix**：5 平台 × build/test 全绿 + `z42c new` 生成 GitHub Actions / GitLab CI 模板 | 1.5 周 |
| **0.2.6** | **Release 自动化**：git tag → 跨平台 z42c/z42vm 二进制 + zpkg 自动产出（GitHub Releases） | 1 周 |

**关键风险**：
- 格式冻结后任何不兼容修改成本变高，需要 review 现有字段是否够用
- perf CI 的"退化阈值"调参可能需要多轮迭代
- Windows 平台路径分隔符 / 行结束符在 stdlib 中可能露出隐性 bug，需要 0.2.5 集中暴露

**对后续依赖**：
- 0.4.6/0.4.7 的 `z42c test` / `z42c bench` 复用本阶段 CI 基础设施
- 0.10.x 全部依赖本阶段建立的基线数字
- 1.0-rc2 跨架构 NativeAOT 复用 0.2.5 的多平台 build matrix

---

## 0.3.x — 测试体系 & VM 质量 + GC v1

**Charter**：把 VM 从"功能跑通"升级到"工程级稳定"——真 GC、调试符号、热重载完整、跨 mode 一致性。

**入口标准**：0.2.x 完成（perf CI 已生效）

**退出标准**：
- Golden test 覆盖所有 L1 特性（每特性正常 + 边界 + 错误用例）
- interp 与 JIT 跑同一测试集结果一致
- `Rc<RefCell>` 已替换为真 GC（mark-and-sweep）
- 行号映射、局部变量名可被外部调试器读取
- 热重载在 interp 模式下端到端可用

| 版本 | 内容 | 工作量 |
|------|------|:----:|
| 0.3.0 | Golden 全 L1 特性覆盖 + interp/JIT 一致性 CI | 2 周 |
| 0.3.1 | 调试符号：行号映射稳定 + 局部变量名 + 栈回溯优化 | 2 周 |
| 0.3.2 | 热重载 VM 端完整实现（interp 模式，签名变化报错保留旧版本） | 2 周 |
| 0.3.3 | **GC v1**：抽象 GC 接口 + mark-and-sweep（替换 `Rc<RefCell>`） | 2–3 周 |
| 0.3.4 | Profiler hooks：函数 entry/exit、allocation、GC 事件可被外部读取 | 1 周 |

**关键风险**：
- **GC v1 是单点最大改动**：heap 布局、root tracing、对象 header、所有 IR 指令的对象访问点都需要改造，回归测试链路必须完备
- 热重载的"签名变化保留旧版本"语义需要 spec 明确

---

## 0.4.x — 标准库 v1 + test/bench 框架

**Charter**：用户写 z42 业务代码所需的"电池"——标准库稳定 + 官方测试 / benchmark 框架。

**入口标准**：0.3.x 完成（真 GC 可用）

**退出标准**：
- `z42.core` / `z42.io` / `z42.math` / `z42.collections` / `z42.text` 五个模块 API 稳定
- pseudo-class List/Dict 完全替换为 z42.collections 纯脚本实现
- `z42c test` / `z42c bench` 子命令可用
- GREEN 标准扩展：`z42c test` 全绿成为门禁

| 版本 | 内容 | 工作量 |
|------|------|:----:|
| 0.4.0 | `z42.core` 完整：Object/Convert/Assert/IEquatable/IComparable/IDisposable | 1.5 周 |
| 0.4.1 | Exception 体系完整 + 9 个标准子类 + IEnumerable<T> 完整 | 1 周 |
| 0.4.2 | `z42.io`：文件读写 + stdin/stdout + Path 操作 | 1.5 周 |
| 0.4.3 | `z42.math`：libm 绑定完整 + 常量 | 1 周 |
| 0.4.4 | `z42.collections`：List/Dict 纯脚本替换 pseudo-class + Queue/Stack | 2 周 |
| 0.4.5 | `z42.text`：字符串操作完整 + StringBuilder | 1 周 |
| 0.4.6 | **`z42.test` v1 + `z42c test`**：注解发现 + Assert 扩展 + golden 集成 | 2 周 |
| 0.4.7 | **`z42.bench` v1 + `z42c bench`**：warmup + 多次迭代 + JSON + baseline diff | 2 周 |
| **0.4.8** | **`z42-doc` 文档生成器**：doc comment 解析 + 静态站点输出（HTML + markdown）+ stdlib 文档自动发布 | 1.5 周 |

**关键风险**：
- pseudo-class → 纯脚本 List/Dict 替换涉及编译器多处特化代码删除，必须 perf CI 守门
- test 框架的"编译期生成入口表"机制需要新建编译器 pass

**对后续依赖**：
- 0.5.6 的高级测试增强（Theory、参数化）依赖 0.5 反射 + 本阶段 test 基础

---

## 0.5.x — 泛型 & Trait & 反射 + Interop 2a

**Charter**：把 L3 类型系统的核心打通——泛型完整、Trait 静态分发、反射元数据 API；同时稳定 Rust 嵌入 API。

**入口标准**：0.4.x 完成（标准库可作为泛型测试床）

**退出标准**：
- L3-G2.5 约束矩阵 + L3-R 反射 R-1~R-7 全部上线
- Trait 取代 interface 成为主要抽象（interface 保留兼容）
- Rust host 可通过稳定 API 嵌入 z42 VM

| 版本 | 内容 | 工作量 |
|------|------|:----:|
| 0.5.0 | L3-G2.5 收尾：`notnull` 约束 + 已规划但未完成的约束范式 | 1.5 周 |
| 0.5.1 | L3-R-1/R-2：`typeof(T)` / `Type` API + 约束反射 | 2 周 |
| 0.5.2 | L3-R-3/R-4：`is/as` 运行时判断 + 约束运行时校验 | 1.5 周 |
| 0.5.3 | L3-R-5/R-6/R-7：type_args 传递 + `new T()` + `Activator.CreateInstance<T>` | 2–3 周 |
| 0.5.4 | Trait 静态分发 v1（取代 interface 主路径） | 2 周 |
| 0.5.5 | Interop Layer 2a：Rust embedding API 稳定（`VM::new` / `load_module` / `call`） | 1.5 周 |
| 0.5.6 | `z42.test` 增强：`[Theory]` 数据驱动 + 动态 `[TestCase]`（依赖反射） | 1 周 |
| **0.5.7** | **`z42-lsp` Language Server v1**：语法高亮 + goto definition + hover + 自动补全（基础）+ VS Code 扩展 | 2.5 周 |

**关键风险**：
- **R-5 type_args 运行时传递机制**是 VM 架构最大决策，需要单独 spec 与 design 评审
- Trait 与 interface 的共存策略必须明确（同一类型同时实现两者？vtable 复用？）

---

## 0.6.x — 函数式 & unmanaged + GC v2

**Charter**：补齐用户写函数式风格代码的基础（lambda、命名参数、模式匹配扩展、不可变默认）+ FFI 友好的 unmanaged 约束 + GC 升级到 generational。

**入口标准**：0.5.x 完成（反射可用，lambda 委托元数据可表达）

**退出标准**：
- Lambda + 闭包可在生产环境使用（`Func<>` / `Action<>` 委托）
- `let` 不可变绑定可用，`var/mut` 显式可变语义稳定
- `unmanaged` 约束 + `[StructLayout]` 可让 struct 跨 FFI 边界
- GC 升级为 generational，young-gen 可见性能提升

| 版本 | 内容 | 工作量 |
|------|------|:----:|
| 0.6.0 | Lambda + 闭包（捕获变量、`Func<>` / `Action<>`） | 2.5 周 |
| 0.6.1 | 命名参数（call site `Greet(name: "z42")`） | 1 周 |
| 0.6.2 | 模式匹配扩展（属性模式、位置模式、`is` 类型测试） | 1.5 周 |
| 0.6.3 | `let` 不可变 + `var/mut` 显式可变 | 1.5 周 |
| 0.6.4 | LINQ 风格迭代器链（Where/Select/OrderBy/ToList，基于 lambda） | 2 周 |
| 0.6.5 | `unmanaged` 约束 + `[StructLayout(Sequential)]` C-compatible struct | 1.5 周 |
| 0.6.6 | **GC v2**：generational + write barrier 雏形 | 2–3 周 |
| **0.6.7** | **`z42-lint` v1**：静态检查（未使用变量、可空性、shadowing、stdlib 误用）+ LSP 诊断扩展 | 1.5 周 |

**关键风险**：
- 闭包变量捕获语义（值捕获 vs 引用捕获）需要 spec 明确
- `let` 引入会破坏现有用户代码（pre-1.0 不留兼容），需要 migration 工具或 codemod 指南

---

## 0.7.x — 错误处理 & ADT

**Charter**：函数式错误处理（Result + ?）+ 原生 sum type + 强制穷尽 match——把 z42 从 C# 风格升级为 Rust 风格的类型安全。

**入口标准**：0.6.x 完成（lambda 与模式匹配是 Result/ADT 的基础设施）

**退出标准**：
- `Result<T, E>` + `?` 传播运算符可用
- `Option<T>` 与 `T?` 共存且互操作规则明确
- 原生 ADT（sum type）取代 abstract record 模拟
- `match` 穷尽检查在 ADT 上强制启用

| 版本 | 内容 | 工作量 |
|------|------|:----:|
| 0.7.0 | `Result<T, E>` 类型 + `?` 传播运算符 | 2 周 |
| 0.7.1 | `Option<T>` 类型 + 与 `T?` 互操作规则 | 1.5 周 |
| 0.7.2 | 原生 ADT（sum type）语法与代码生成 | 2.5 周 |
| 0.7.3 | `match` 表达式 + 穷尽检查 | 2 周 |
| 0.7.4 | switch 在 enum/ADT 上的穷尽检查迁移 | 1 周 |

**关键风险**：
- `Option<T>` 与 `T?` 是否冲突，是否可以互转，需要 spec 明确
- ADT 在反射 API 中的元数据形状（`Type.Variants`）需要扩展

---

## 0.8.x — 并发 & 多线程 + GC v3

**Charter**：philosophy §7 的"无数据竞争 + 结构化并发"完整落地——async/await + 真 OS 线程 + lock + 类型层的同步约束 + concurrent GC。

**入口标准**：0.7.x 完成（Result 用于 async 函数返回类型）

**退出标准**：
- `async`/`await` 单线程 task scheduler 可用
- 真 OS thread API 可用，跨线程数据访问受类型系统保护
- GC 升级为 concurrent marking + write barrier
- `Rc<RefCell>` 完全清理（A6 backlog 闭环）

| 版本 | 内容 | 工作量 |
|------|------|:----:|
| 0.8.0 | `async`/`await` + 单线程 task scheduler | 2 周 |
| 0.8.1 | `Task<T>` / `ValueTask<T>` 标准类型 | 1.5 周 |
| 0.8.2 | `Task.WhenAll` / `WhenAny` 结构化并发 | 1 周 |
| 0.8.3 | OS thread API + Arc 替换 Rc（A6） | 2 周 |
| 0.8.4 | `lock` 关键字 + `Monitor` | 1.5 周 |
| 0.8.5 | 类型层数据竞争预防（shared mutable 必须同步） | 2 周 |
| 0.8.6 | **GC v3**：concurrent marking + write barrier 完整 | 3 周 |
| **0.8.7** | **`z42-dap` Debugger 适配层**：DAP 协议适配 + 断点 / step / inspect 局部变量 + VS Code 调试器扩展（依赖 0.3.1 调试符号 + 0.3.2 热重载） | 2 周 |

**关键风险**：
- "类型层数据竞争预防"是 z42 是否兑现 philosophy §7 的关键，但实现复杂度高，需独立设计文档
- GC v3 与多线程必须同时上线，单独发布无意义；最大风险是回归
- DAP 在多线程环境下的暂停语义需要单独 spec（暂停一个 thread vs 全部 thread）

---

## 0.9.x — 脚本 & 嵌入 & 可裁剪

**Charter**：philosophy §17–18 的兑现——script-friendly 三模式（直执行 / eval / REPL）+ 语言子集开关 + tree-shaking + VM 组件化 + C ABI 嵌入。

**入口标准**：0.8.x 完成（多线程稳定，host 嵌入可信）

**退出标准**：
- `z42vm script.z42` / `z42vm -c "..."` / 交互式 REPL 三模式可用
- `[language]` 特性开关在编译器/解析器层强制
- stdlib 与 VM 组件可裁剪到嵌入式子集
- C ABI 头文件 `z42_vm.h` 可被 C/Go/Python 嵌入

| 版本 | 内容 | 工作量 |
|------|------|:----:|
| 0.9.0 | 源文件直执行 `z42vm script.z42` | 1.5 周 |
| 0.9.1 | `z42vm -c "..."` 内联 eval + `VM.Eval()` 嵌入 API | 1 周 |
| 0.9.2 | REPL（交互式求值，多行输入、历史） | 2 周 |
| 0.9.3 | `[language]` 特性开关全部接入（philosophy §18 列出的全部 flag） | 1.5 周 |
| 0.9.4 | stdlib tree-shaking（模块级 + 函数级） | 1.5 周 |
| 0.9.5 | VM 组件化构建（core/interp/jit/aot/gc/exceptions/corelib/debug 可裁剪） | 2 周 |
| 0.9.6 | Interop Layer 2b：C ABI `z42_vm.h` + C/Go/Python 嵌入示例 | 2 周 |
| **0.9.7** | **WASM target**：VM 编译为 `wasm32-wasi` + 浏览器示例（z42 在浏览器执行） | 2.5 周 |
| **0.9.8** | **嵌入式子集验证**：philosophy §18 ~200KB 最小镜像在裁剪后达成 + IoT demo | 1.5 周 |

**关键风险**：
- REPL 增量类型环境的语义（重定义、shadowing）需要 spec
- 组件化裁剪后的最小镜像目标（philosophy 写 ~200KB）需要量化验证
- WASM 下 GC 选型有限（不能用 OS thread / mmap），可能需要单独的 wasm-gc 路径

---

## 0.10.x — 性能强化（philosophy §9 全指标达标）

**Charter**：把 philosophy §9 的 5 个量化目标全部打到目标内，作为 1.0 的最后一道关。每个子版本以"测量 → 优化 → 验证"循环组织。

**入口标准**：0.9.x 完成（功能集冻结，可专注优化）

**退出标准**（必须全部达标）：

| 指标 | 目标 |
|------|------|
| Interp dispatch | ≤ 5 cycles/instr |
| JIT numeric | 与 C# 同档（≥ 90%） |
| Native call | ≤ 1 indirect jump |
| GC pause | < 10ms / 100MB heap |
| Bytecode 压缩 | 源码 40–60% |
| 冷启动 | interp ~10ms |

| 版本 | 内容 | 工作量 |
|------|------|:----:|
| 0.10.0 | 解释器 dispatch（threaded code / computed goto） | 2 周 |
| 0.10.1 | JIT inline cache（多态调用点 90% 命中） | 1.5 周 |
| 0.10.2 | JIT 寄存器分配改进 + SSA 优化 pass | 2 周 |
| 0.10.3 | JIT tier-up 阈值调优 + 死代码消除 + 常量折叠 | 1.5 周 |
| 0.10.4 | GC pause 调优（young-gen 自适应、增量标记） | 2 周 |
| 0.10.5 | 对象布局打包（字段重排、字符串 intern） | 1 周 |
| 0.10.6 | Bytecode varint + 跨模块字符串池共享 | 1.5 周 |
| 0.10.7 | stdlib 性能审计（List/Dict/StringBuilder/Iterator fusion） | 2 周 |
| 0.10.8 | Native call 开销验证 + 冷启动优化 + 跨 mode bench 对比 | 1.5 周 |

**关键风险**：
- 单个优化点可能"测了没提升"——必须坚持"无数字提升的优化不合并"原则
- 部分优化（如 inline cache）需要 0.5 反射的元数据，但只在 JIT 内部用，不暴露给用户

---

## 1.0.x — 自举 + AOT + 稳定承诺 + Interop 3

**Charter**：z42 进入 1.0 的三道硬关——自举证明语言成熟度、AOT 兑现性能完整性、稳定承诺解锁生态投资。

**入口标准**：0.10.x 完成（perf 全指标达标）

**退出标准**：
- LLVM AOT 后端可用，NativeAOT 单文件部署可发布
- 编译器自身用 z42 重写并通过自举（z42 编译 z42 编译 z42 输出一致）
- 语言 / `.zbc` / stdlib API 进入稳定承诺
- SemVer + deprecation 周期生效

| 版本 | 内容 | 工作量 |
|------|------|:----:|
| 1.0.0-rc1 | LLVM AOT 后端（M9）—— bytecode → LLVM IR 翻译 | 4 周 |
| 1.0.0-rc2 | **跨架构 NativeAOT**：x86_64 + arm64（macOS/Linux/Windows）单文件部署 + tree-shaking | 3 周 |
| 1.0.0-rc3 | Interop Layer 3：typed FFI 生成 + async callback + zero-copy 数组 | 2 周 |
| 1.0.0-rc4 | 编译器自举（M10）—— z42 编译器用 z42 重写 | 4–6 周 |
| 1.0.0-rc5 | 文档完整性 sweep（语言规范、stdlib API、迁移指南） | 1 周 |
| **1.0.0-rc6** | **`z42up` 版本管理工具**：跨平台安装器 / toolchain 切换 / 自动升级 | 2 周 |
| **1.0.0** | API/`.zbc`/stdlib 稳定承诺 + SemVer 启用 + deprecation 周期生效 + 工具链 GA | — |

**关键风险**：
- 自举发现的语言缺陷可能逼回 0.x.y 修复，1.0 时间线弹性最大
- AOT 与 JIT 的语义一致性必须 100% 验证（同一份源码三模输出相同）

---

## GREEN 标准演进时间表

`.claude/rules/workflow.md` 阶段 8 的 GREEN 标准随版本扩展。**任一时点的 GREEN 标准 = 该时点之前所有已生效的项**。

| 起始版本 | 新增 GREEN 项 |
|:------:|------|
| 当前 | `dotnet build` + `cargo build` + `dotnet test` + `./scripts/test-vm.sh` 全绿 |
| **0.2.3** | Perf CI：关键 benchmark > 10% 退化阻塞 commit |
| **0.2.5** | 多平台 CI matrix（macOS x86_64/arm64 + Linux x86_64/arm64 + Windows x86_64）全绿 |
| **0.4.6** | `z42c test` 在 stdlib 与示例项目 100% 通过 |
| **0.4.7** | `z42c bench --diff baseline.json` 通过 |
| **0.4.8** | `z42-doc` 生成无错（doc comment 完整性检查） |
| **0.5.0** | 跨 zpkg 反射元数据一致性检查 |
| **0.5.7** | LSP conformance test 通过 |
| **0.6.7** | `z42-lint` 在 stdlib 零警告（或带 explicit allow 标记） |
| **0.8.6** | 多线程压力测试通过（race detector / TSan 等价） |
| **0.8.7** | DAP conformance test 通过 |
| **0.9.7** | WASM target build & test 全绿 |
| **0.10.0** | philosophy §9 五个量化指标全部达标（自动化基线对比） |
| **1.0.0** | 自举一致性（z42 → z42 → z42 输出 byte-identical）+ 跨架构 perf 数字进 release notes |

---

## 待裁决问题清单

以下问题需要在对应版本启动 spec 时由 User 裁决。提前列出避免实施时阻塞。

| 编号 | 版本 | 问题 |
|:--:|:----:|-----|
| Q1 | 0.3.3 | GC v1 是放在 0.3 还是延后到 0.8 与多线程一起？（已采推荐方案 A：放 0.3） |
| Q2 | 0.4.6 | `z42.test` 注解风格：`[Test]`（C# 风）vs `test "name" {}`（Zig 风）？（推荐 C# 风） |
| Q3 | 0.5.4 | Trait 与 interface 是否完全等价？是否同一类型可同时实现两者？ |
| Q4 | 0.6.0 | 闭包变量捕获语义：值捕获 vs 引用捕获 vs 显式标注？ |
| Q5 | 0.6.3 | 引入 `let` 后是否提供 `var → let` 的 codemod 工具？ |
| Q6 | 0.7.1 | `Option<T>` 与 `T?` 是否可隐式互转？是否在编译器层视为同一类型？ |
| Q7 | 0.8.5 | 类型层数据竞争预防——是 Send/Sync 风格 trait 还是注解 + 编译器分析？ |
| Q8 | 0.9.5 | VM 组件化的 feature flag 粒度——cargo feature 还是构建时 build profile？ |
| Q9 | 0.10.x | 性能强化 9 个 patch 是否独立发布，还是合并为 0.10.0 单次发布？ |
| Q10 | 1.0 | AOT 是否必须卡 1.0？（备选：拆 1.0 = 自举 + 稳定，1.1 = AOT） |
| Q11 | 0.2.5 | 多平台 CI 选 GitHub Actions matrix 还是自托管 runner？arm64 主机如何获取（GHA 已支持但限额） |
| Q12 | 0.2.5 | Release artifact 命名规范？（z42-{version}-{os}-{arch}.tar.gz？包含哪些 binary？） |
| Q13 | 0.5.7 | LSP server 用 .NET 实现（复用编译器）还是 Rust 实现（复用 VM）？复用哪边的 AST / type info？ |
| Q14 | 0.8.7 | DAP debugger 在多线程下的暂停语义：暂停一个 thread 还是全部？JIT/AOT 代码如何 step？ |
| Q15 | 0.9.7 | WASM 下 GC 选型：等 wasm-gc proposal stable，还是用自实现的 wasm-internal GC？ |
| Q16 | 0.9.8 | 嵌入式 ~200KB 目标的具体平台基准（cortex-M4 / esp32 / RISC-V？） |
| Q17 | 1.0-rc6 | `z42up` 用 Rust 实现还是等自举后用 z42 自身实现（吃自己的狗粮）？ |
| Q18 | 1.x+ | 包注册表（`z42-pkg`）是否走中心化（crates.io 模式）还是去中心化（go modules / git URL）？ |

---

## 维护说明

- **本文档每次有版本完成时同步更新**：将完成的子版本表行标 ✅，并把日期填入"实际完成"列（待加）
- **子版本顺序可调整，但 minor 边界不轻易移动**：minor 是发布单位，跨 minor 移动特性需要明确理由
- **新增子版本通过 spec/changes/ 走标准流程**：本文档反映已完成的规划，不替代 spec 流程
- **与 [features.md](features.md) / [roadmap.md](roadmap.md) 三向对齐**：任何 phase 归属或语言设计的变更必须三处同步

---

## 相关文档

- [features.md](features.md) —— 语言特性的设计决策与 phase 归属
- [roadmap.md](roadmap.md) —— Phase（L1/L2/L3）实现进度
- [design/philosophy.md](design/philosophy.md) —— 设计原则与目标受众
- [design/stdlib-organization.md](design/stdlib-organization.md) —— 标准库组织
- [.claude/rules/workflow.md](../.claude/rules/workflow.md) —— 工作流与 GREEN 标准
