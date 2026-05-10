# design/runtime/

z42 Rust VM 内部架构、IR / zbc 格式、执行模式、嵌入与跨平台支持。

## 职责

- 描述 VM 内部数据结构（VmContext、堆布局、native interop registry）
- 描述 IR（SSA 中间表示）与 zbc（二进制字节码格式）
- 描述三种执行模式（Interp / JIT / AOT）切换机制
- 描述嵌入 API（Host C ABI）与跨平台支持

## 核心文件

### 执行核心

| 文件 | 职责 |
|------|------|
| [`vm-architecture.md`](vm-architecture.md) | VmContext 内部：状态管理、native interop registry、内置 API |
| [`ir.md`](ir.md) | SSA 中间表示：指令集、类型映射、Block 参数（**字节码格式权威**）|
| [`zbc.md`](zbc.md) | 二进制 wire format：section layout、版本号、token 编码 |
| [`execution-model.md`](execution-model.md) | 三种执行模式（Interp / JIT / AOT）切换机制 |
| [`jit.md`](jit.md) | Cranelift 后端：编译策略、JIT ABI、运行时上下文 |

### 运行时服务

| 文件 | 职责 |
|------|------|
| [`gc-handle.md`](gc-handle.md) | GCHandle 结构 + slab/freelist + Strong/Weak 区分 |
| [`hot-reload.md`](hot-reload.md) | 函数级代码替换 + 状态保留 + 签名变更检测 |
| [`concurrency.md`](concurrency.md) | L3 前瞻：染色 async/await + Future + 结构化并发 + Send/Sync |

### 嵌入与平台

| 文件 | 职责 |
|------|------|
| [`embedding.md`](embedding.md) | Host C ABI：initialize / load_zbc / resolve_entry / invoke / sinks / shutdown |
| [`cross-platform.md`](cross-platform.md) | VM 支持的目标平台与编译矩阵 |

## 入口点

- 新接手 VM 代码：[`vm-architecture.md`](vm-architecture.md) + [`execution-model.md`](execution-model.md)
- 加新 IR 指令：[`ir.md`](ir.md)（必须更新指令表）
- 改 zbc 格式：[`zbc.md`](zbc.md)（必须 bump 版本）
- 集成 z42 到外部 app：[`embedding.md`](embedding.md)

## 依赖关系

- 上游：[`../compiler/`](../compiler/)（编译产物消费）、[`../language/interop.md`](../language/interop.md)（FFI 语言表面）
- 下游：宿主应用 / 平台 facade（iOS / Android / WASM）
