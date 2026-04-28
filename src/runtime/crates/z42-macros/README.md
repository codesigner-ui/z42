# z42-macros

## 职责

z42 Tier 2 ergonomic API 的 proc macro 实现。提供 4 个入口：

| Macro | 形式 | 作用 |
|-------|------|------|
| `Z42Type` | `#[derive(Z42Type)]` | 生成 `Z42Type` impl + 静态 descriptor |
| `methods` | `#[z42::methods]` (`impl` 块) | 把每个方法包成 `extern "C"` shim |
| `trait_impl` | `#[z42::trait_impl("...")]` | 注册 trait impl 到 descriptor |
| `module` | `z42::module! { ... }` | 生成 `#[ctor]` 注册项 + 写出 `.z42abi` manifest |

## 核心文件

| 文件 | 职责 |
|------|------|
| `src/lib.rs` | 4 个 proc macro 入口；C1 阶段全部展开为 `compile_error!` |

## 状态

C1 接口骨架。所有 macro 注册了入口签名（IDE 能看到、`cargo check` 通过），但展开时报清晰编译错误指向 C3。

## 依赖关系

- 上：`z42-rs`（C3 后会从 z42-rs prelude 重导出）、用户 native 库
- 下：`proc-macro2`、`quote`、`syn`

## 实现原则（C3 起）

- derive 展开优先生成 `static <NAME>_DESC: ::z42_rs::Descriptor = ...;`，避免运行时构造 descriptor
- 所有 `extern "C"` shim 包 `std::panic::catch_unwind`，把 panic 转为 `Z42Error`
- 字段 / 方法签名解析阶段就校验类型 blittable 性，越早报错越好
