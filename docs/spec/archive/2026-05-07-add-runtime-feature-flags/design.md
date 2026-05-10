# Design: Runtime Feature Flags

## Architecture

```
                  ┌──────────────────────────────────────┐
                  │     src/runtime/Cargo.toml            │
                  │                                       │
                  │  [features]                           │
                  │  default      = ["jit"]               │
                  │  jit          = [<cranelift deps>]    │
                  │  aot          = []                    │
                  │  interp-only  = []                    │
                  │  wasm         = ["interp-only"]       │
                  │  ios          = ["interp-only","aot"] │
                  │  android      = ["interp-only","aot"] │
                  └──────────────────────────────────────┘
                                   │
                ┌──────────────────┼──────────────────┐
                ▼                  ▼                  ▼
       ┌─────────────────┐  ┌─────────────────┐  ┌─────────────────┐
       │ src/lib.rs      │  │ jit/mod.rs      │  │ aot/mod.rs      │
       │                 │  │ #[cfg(feature   │  │ #[cfg(feature   │
       │ #[cfg(feature   │  │   = "jit")]     │  │   = "aot")]     │
       │   = "jit")]     │  │                 │  │                 │
       │ pub mod jit;    │  │  Cranelift 接入  │  │  AOT 占位        │
       │                 │  │                 │  │                 │
       │ #[cfg(feature   │  └─────────────────┘  └─────────────────┘
       │   = "aot")]     │
       │ pub mod aot;    │
       │                 │
       │ pub mod interp; │  ← always present
       └─────────────────┘
```

## Decisions

### Decision 1: features 完整定义（锁定）

[src/runtime/Cargo.toml](src/runtime/Cargo.toml) `[features]` 段：

```toml
[features]
default = ["jit"]

# 执行模式 features
jit = ["dep:cranelift", "dep:cranelift-jit", "dep:cranelift-module", "dep:cranelift-frontend", "dep:cranelift-codegen"]
aot = []
interp-only = []

# 平台 preset features (= 推荐 feature 组合)
wasm    = ["interp-only"]
ios     = ["interp-only", "aot"]
android = ["interp-only", "aot"]
```

**说明**：
- `default = ["jit"]` 保证 `cargo build`（无 flag）行为完全不变 —— **向后兼容硬指标**
- `interp-only` 是空 feature，仅作意图标识；`mod interp` 永远编译，不依赖任何 feature
- 平台 preset 是"feature 组合"，方便 `--no-default-features --features wasm` 一次激活全套

### Decision 2: cranelift 依赖改 optional

```toml
[dependencies]
# ... 其他依赖 ...

cranelift = { version = "0.111", optional = true }
cranelift-jit = { version = "0.111", optional = true }
cranelift-module = { version = "0.111", optional = true }
cranelift-frontend = { version = "0.111", optional = true }
cranelift-codegen = { version = "0.111", optional = true }
```

`dep:` 前缀表示该 dep 仅由 feature 启用（不会自动暴露同名 feature）。

### Decision 3: 源码 cfg gate 规则

#### 模块声明侧（[src/runtime/src/lib.rs](src/runtime/src/lib.rs)）

```rust
// always present
pub mod interp;
pub mod gc;
pub mod metadata;

// feature-gated
#[cfg(feature = "jit")]
pub mod jit;

#[cfg(feature = "aot")]
pub mod aot;
```

#### 模块内部

模块内部**不重复加** `#![cfg]`（依赖 lib.rs 的 mod 声明 gate）。例外：模块内某个公共类型在不启用 jit 时也可见（如 `enum ExecutionMode { Interp, Jit }`），那个类型本身不加 cfg，但 `ExecutionMode::Jit` variant 加 cfg：

```rust
pub enum ExecutionMode {
    Interp,
    #[cfg(feature = "jit")]
    Jit,
}
```

#### 跨模块引用

任何引用 `crate::jit::*` 的代码点都加 cfg：

```rust
fn create_executor(mode: ExecutionMode) -> Box<dyn Executor> {
    match mode {
        ExecutionMode::Interp => Box::new(interp::Interpreter::new()),
        #[cfg(feature = "jit")]
        ExecutionMode::Jit => Box::new(jit::JitExecutor::new()),
    }
}
```

### Decision 4: z42vm CLI 处理（运行时）

[src/runtime/src/bin/z42vm.rs](src/runtime/src/bin/z42vm.rs)：

```rust
#[derive(clap::ValueEnum, Clone)]
enum Mode {
    Interp,
    #[cfg(feature = "jit")]
    Jit,
    #[cfg(feature = "aot")]
    Aot,
}
```

`#[derive(clap::ValueEnum)]` 会自动从可见 variant 生成 `--mode` 取值列表。这意味着：

- `cargo build` (default + jit) → `z42vm --help` 显示 `--mode <interp|jit>`
- `cargo build --no-default-features --features interp-only` → `--mode <interp>`
- 用户传 `--mode jit` 在 interp-only 编译产物上 → clap 报错 `"invalid value 'jit' for '--mode': possible values are: interp"`（友好原生）

无需手写 runtime check。

### Decision 5: 测试 cfg

部分现有测试可能依赖 JIT。规则：

- 普通测试（不涉及 JIT）：无 cfg
- JIT-only 测试：加 `#[cfg(feature = "jit")]`
- 需要在 `interp-only` 下也跑的测试：保持无 cfg，但断言用 `--mode interp`

[src/runtime/tests/zbc_compat.rs](src/runtime/tests/zbc_compat.rs) 与 P3 后的 `vm_core/runner.rs` 默认走 interp；`if cfg!(feature = "jit") { ... }` 控制是否额外跑 jit。

### Decision 6: justfile 加 build 子命令

[justfile](justfile)（P0/P3 之后）：

```just
build-interp-only:
    cargo build --manifest-path src/runtime/Cargo.toml \
        --no-default-features --features interp-only

build-wasm-feature:
    # 仅验证 feature 切分；实际 wasm-target 编译在 P4.2
    cargo build --manifest-path src/runtime/Cargo.toml \
        --no-default-features --features wasm

build-ios-feature:
    cargo build --manifest-path src/runtime/Cargo.toml \
        --no-default-features --features ios

build-android-feature:
    cargo build --manifest-path src/runtime/Cargo.toml \
        --no-default-features --features android
```

注意：本 spec 阶段 `build-wasm-feature` 等只验证 host target 上能编译通过（即 feature 拆分正确），**不**为 wasm32-unknown-unknown 等 target 编译。P4.2 才接入实际 target。

### Decision 7: CI 验证

[.github/workflows/ci.yml](.github/workflows/ci.yml) 加一个 job：

```yaml
feature-matrix:
  runs-on: ubuntu-latest
  steps:
    - uses: actions/checkout@v4
    - uses: dtolnay/rust-toolchain@stable
    - name: Build interp-only
      run: just build-interp-only
    - name: Build wasm feature (host target)
      run: just build-wasm-feature
    - name: Build ios feature (host target)
      run: just build-ios-feature
    - name: Build android feature (host target)
      run: just build-android-feature
```

任一 feature combo 编译失败 → CI 红。

### Decision 8: 互斥检查放弃

不加 `compile_error!` 互斥检查（如 `jit + interp-only` 同时启用时报错）。

理由：
- Cargo features 设计上就是 additive；`compile_error!` 与 cargo 哲学冲突
- 平台 preset 是组合，不是互斥；`wasm = ["interp-only"]` 是允许的且合理
- 真正的互斥（如 wasm + jit）由开发者自查；CI feature-matrix job 会暴露不可编译组合

### Decision 9: 默认行为不变（硬指标）

**`cargo build --manifest-path src/runtime/Cargo.toml`**（无任何 flag）：

- 编译产物含 cranelift JIT
- z42vm 二进制 `--mode` 支持 `interp` 和 `jit`
- 现有所有测试通过
- 二进制大小不显著变化（< 1% 浮动可接受）

任何偏离都是回归。

## Implementation Notes

### 重构步骤建议

1. 先在 [src/runtime/Cargo.toml](src/runtime/Cargo.toml) 加 `[features]` + 改 cranelift 为 optional
2. `cargo build` 失败（mod jit 找不到 cranelift）→ 回到 [src/runtime/src/lib.rs](src/runtime/src/lib.rs) 加 cfg
3. 逐个解决跨模块引用的 cfg 缺失（compile error 驱动）
4. 编译通过后，跑现有测试（jit 模式）→ 全绿
5. 测 `--no-default-features --features interp-only` 编译 → 应通过
6. 测 interp-only 下 z42vm 能跑 vm_core 用例（用 P3 的 runner 即可）
7. CI 加 feature-matrix job

### 与现有代码的耦合点（已知）

需要 grep 确认：

- `src/runtime/src/lib.rs` 是否在非 jit 路径引用 jit 类型
- `src/runtime/src/bin/z42vm.rs` 是否硬编码 mode 字符串
- 测试文件是否引用 `crate::jit`

实施时第一步是 grep 这些点。

### Cargo features 文档

[docs/design/cross-platform.md](docs/design/cross-platform.md) 主要内容：

- 平台-feature 矩阵（同父 spec design.md Decision 8）
- features 完整列表与含义
- 推荐组合（wasm / ios / android preset）
- 添加新 feature 的指南
- cfg 编写规范（模块声明 gate vs 内部 gate）

## Testing Strategy

- ✅ `cargo build` 默认行为不变（含 JIT；现有测试全绿）
- ✅ `cargo build --no-default-features --features interp-only` 编译通过
- ✅ interp-only 编译产物的 z42vm 能跑 vm_core 全部用例（默认 mode=interp）
- ✅ interp-only 编译产物的 `z42vm --mode jit` 报错（clap 拒绝该 enum 值）
- ✅ `cargo build --no-default-features --features wasm` 编译通过（host target）
- ✅ `cargo build --no-default-features --features ios` 编译通过
- ✅ `cargo build --no-default-features --features android` 编译通过
- ✅ `cargo test` 默认全绿；`cargo test --no-default-features --features interp-only` 全绿（jit-only 测试自动跳过）
- ✅ 二进制大小 (`ls -l target/release/z42vm`) 在 interp-only 下显著减小（≥ 30%，因移除 cranelift）
- ✅ CI feature-matrix job 全绿
