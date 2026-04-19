# 推荐库与参考实现

> 添加新依赖前先查此文件。核心原则：优先用成熟开源库，只在无合适库或需要深度定制时自行实现。

## 编译器（C#）

| 用途 | 推荐库 | 说明 |
|------|--------|------|
| 命令行解析 | `System.CommandLine` | 官方 CLI 框架 |
| 测试 | `xUnit` + `FluentAssertions` | 单元 / 集成测试 |
| 基准测试 | `BenchmarkDotNet` | 编译器性能回归 |
| 源码生成辅助 | `Roslyn`（只读引用）| 参考 C# AST 结构 |
| 二进制序列化 | `MessagePack-CSharp` | `.zbc` 读写 |
| 日志 | `Microsoft.Extensions.Logging` | 统一日志接口 |

## VM（Rust）

| 用途 | 推荐库 | 说明 |
|------|--------|------|
| JIT 代码生成 | `cranelift-jit` | Bytecode Alliance，Wasmtime 同款 |
| AOT / LLVM | `inkwell` | LLVM safe bindings for Rust |
| 二进制格式 | `bincode` ✅（已用）| 序列化 `.zbc` |
| 解析辅助（调试格式）| `nom` 或 `winnow` | 文本 IR（`.zasm`）解析 |
| GC（未来沙盒模式）| `gc-arena` | arena 式 GC，可选引入 |
| 并发运行时 | `tokio` | async VM task 调度 |
| 性能剖析 | `pprof-rs` | 火焰图 |
| 文件监听 | `notify` | 热更新文件系统事件，跨平台，支持 debounce |

## 参考实现

实现某个子系统前先调研：

| 子系统 | 参考项目 |
|--------|---------|
| 解释器结构 | CPython（ceval.c）、wren、lua 5.4 |
| SSA / IR 设计 | LLVM IR、Cranelift CLIF、QBE |
| 类型推导 | OCaml 编译器、Hindley-Milner 论文实现 |
| JIT 流水线 | LuaJIT、V8 Maglev、JavascriptCore |
| 模式匹配编译 | `rustc_mir_build`、MLton |
