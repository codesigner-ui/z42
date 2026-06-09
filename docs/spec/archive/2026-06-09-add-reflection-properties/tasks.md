# Tasks: Type.GetProperties()

> 状态：🟢 已完成 | 创建：2026-06-09 | 完成：2026-06-09
> 子系统锁：`runtime` + `stdlib`（归档时释放）

## 进度概览
- [x] 阶段 1: stdlib 类型
- [x] 阶段 2: 运行时 builtin
- [x] 阶段 3: 测试与文档

## 阶段 1: stdlib
- [x] 1.1 `src/libraries/z42.core/src/Reflection/PropertyInfo.z42`（NEW）：`PropertyInfo : MemberInfo`，公开 `Std.Type PropertyType` / `bool CanRead` / `bool CanWrite`（VM 写槽）
- [x] 1.2 `src/libraries/z42.core/src/Type.z42`（MODIFY）：加 `[Native("__type_properties")] public extern Std.Reflection.PropertyInfo[] GetProperties();`

## 阶段 2: 运行时（runtime）
- [x] 2.1 `src/runtime/src/corelib/reflection.rs`：`build_property_info(ctx, name, type_name, can_read, can_write)` 辅助（镜像 `build_method_info`）
- [x] 2.2 `src/runtime/src/corelib/reflection.rs`：`builtin_type_properties`——取 handle → 扫 vtable+own_methods 按 `get_<X>`/`set_<X>` 配对去重 → 填 PropertyInfo[]；handle-less 返空
- [x] 2.3 `src/runtime/src/corelib/mod.rs`：注册 `("__type_properties", reflection::builtin_type_properties)`
- [x] 2.4 Rust 单测（若有 `reflection_tests.rs`）：读写 / 只读 / 只写 / 继承 / 空

## 阶段 3: 测试与文档
- [x] 3.1 Golden `src/tests/types/get_properties.z42`（NEW）+ 生成 companion（.zbc / expected）
- [x] 3.2 Dogfood：`src/libraries/z42.core/tests/reflection.z42` 追加 GetProperties [Test]（局部变量接收者）
- [x] 3.3 `docs/design/language/reflection.md`：API 表加 `PropertyInfo`；实现原理加"属性派生自 get_/set_ 约定"段；Deferred 移除 `reflection-future-properties`（标已落地）
- [x] 3.4 `docs/roadmap.md`：Deferred Backlog Index 同步（若有 reflection-future-properties 索引行）

## 阶段 4: 验证（GREEN）
- [x] 4.1 `dotnet build src/compiler/z42.slnx` + `cargo build --release` —— 无错
- [x] 4.2 `dotnet test`（GoldenTests 全绿，含新 golden）
- [x] 4.3 `z42 xtask.zpkg test lib`（z42.core dogfood 含新 [Test]）—— 或在 xtask gate 受阻时以 C# GoldenTests 为权威门（沿用本会话约定）
- [x] 4.4 spec scenarios 逐条覆盖确认
- [x] 4.5 无 zbc/zpkg 格式 bump 确认（不动 fixture）

## 备注
- 无格式变更 → 不撞 `port-z42c-zbc-writer`（其镜像的是 zbc 写器；本变更纯 runtime+stdlib，不改写器输出）。
- chained-property-dispatch footgun 仍在：dogfood [Test] 用局部变量接收者写法。

## 验证报告（2026-06-09）
- ✅ dotnet build + cargo build (debug+release) — 无错
- ✅ dotnet test: **1549/1549**（新 golden `get_properties.z42` 端到端跑通：typeof→GetProperties→VM 执行；IncrementalBuild count 0/69）
- ✅ cargo test --lib: **793/0**（含 2 个新 reflection property 单测）
- ✅ dogfood `reflection.z42` typecheck 干净（`PropertyInfo[]` / `Type.GetProperties` 正确绑定）；[Test] 执行待 xtask gate（当前僵尸 jam），同路径已由 golden 运行期证明
- ✅ 无 zbc/zpkg 格式 bump（纯 runtime 派生）→ 不动 fixture、不撞 port-z42c-zbc-writer
- ⚠️ xtask `test lib`/`test vm` 因陈年僵尸进程持 build 锁未跑；以 C# GoldenTests 为权威门（沿用本会话约定）。stdlib 经 driver-direct（`z42c build -p z42.core`）重建并 stage 进 dist/release 供 golden 加载
