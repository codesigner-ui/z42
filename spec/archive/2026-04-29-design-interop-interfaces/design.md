# Design: Interop Interfaces Scaffold

## Architecture

```
src/runtime/                        ← 转为 workspace（root package + 3 个子 crate）
├── Cargo.toml                      ← [workspace] + [package] z42_vm
├── src/                            ← VM 实现（z42_vm crate，不动）
│   ├── metadata/bytecode.rs        ← +4 个 IR opcode 变体（Trap dispatch）
│   └── interp/mod.rs               ← +新 opcode 桩分支
├── include/
│   └── z42_abi.h                   ← Tier 1 C ABI 头文件
└── crates/
    ├── README.md                   ← 第 3 层 README
    ├── z42-abi/                    ← Tier 1 Rust 镜像
    │   ├── Cargo.toml
    │   ├── README.md
    │   ├── src/lib.rs              ← #[repr(C)] 类型 + extern "C" 函数声明
    │   └── tests/abi_layout_tests.rs
    ├── z42-rs/                     ← Tier 2 用户 API 骨架
    │   ├── Cargo.toml
    │   ├── README.md
    │   ├── src/{lib,types,traits}.rs
    │   └── tests/skeleton_tests.rs
    └── z42-macros/                 ← proc macro 入口
        ├── Cargo.toml              ← proc-macro = true
        ├── README.md
        └── src/lib.rs              ← unimplemented!() 桩

src/compiler/z42.IR/BinaryFormat/
├── Opcodes.cs                      ← +4 opcode 常量
├── ZbcReader.Instructions.cs       ← +4 读取分支（构造桩 IR 节点）
└── ZbcWriter.Instructions.cs       ← +4 写入分支

docs/design/
├── manifest-schema.json            ← .z42abi v1 JSON Schema
├── error-codes.md                  ← +Z0905–Z0910 注册
├── interop.md                      ← Roadmap 加 C1 行
└── ir.md                           ← +4 opcode 描述
```

## Decisions

### Decision 1: Workspace Layout — 单一 manifest 双重身份

**问题**：`src/runtime/Cargo.toml` 当前是 single-package。要加子 crate，必须组成 workspace。两种做法：
- A: 把 `z42_vm` 移到 `src/runtime/z42-vm/`，`src/runtime/Cargo.toml` 改为纯 workspace 文件
- B: 在 `src/runtime/Cargo.toml` 同时声明 `[workspace]` 和 `[package]`，root package 也是 workspace member

**选项**：
- A：结构最规整；缺点：所有现有 `cargo build --manifest-path src/runtime/Cargo.toml` 调用、CI 路径、`scripts/test-vm.sh` 全部要改
- B：现有路径不变；Cargo 原生支持 root package as workspace member

**决定**：选 B（root package as workspace member）。
- 理由：最小破坏，现有 build/test/CI 完全无感；C2–C5 后续改动也无感
- 风险：稍微非典型，但官方支持，Cargo 文档明确允许

```toml
# src/runtime/Cargo.toml
[workspace]
members = [".", "crates/z42-abi", "crates/z42-rs", "crates/z42-macros"]

[package]
name = "z42_vm"
version = "0.1.0"
edition = "2021"
# ... (现有内容不变)

[dependencies]
# ... (现有依赖不变)
z42-abi = { path = "crates/z42-abi" }     # 新增（VM 内部需要 ABI 类型镜像）
```

### Decision 2: C 头文件位置

**问题**：`z42_abi.h` 放哪儿？
- A: `src/runtime/include/z42_abi.h`
- B: `src/runtime/crates/z42-abi/include/z42_abi.h`
- C: 顶层 `include/z42_abi.h`

**决定**：选 A。
- 理由：与 VM 二进制同级目录，方便外部 `-I` 引用；不嵌进 z42-abi crate（避免与 Rust 镜像产生路径耦合）；不放顶层（避免污染项目根）

### Decision 3: ABI Struct Field Order

**字段顺序一旦发布即冻结**。`Z42TypeDescriptor_v1` 字段顺序按"小→大、固定→可变"原则：

```c
typedef struct Z42TypeDescriptor_v1 {
    uint32_t  abi_version;          // [0..4)   总是第一个，所有版本一致
    uint32_t  flags;                // [4..8)
    const char* module_name;        // [8..16)
    const char* type_name;          // [16..24)
    size_t    instance_size;        // [24..32)
    size_t    instance_align;       // [32..40)
    void*   (*alloc)(void);         // [40..48)
    void    (*ctor)(void*, const Z42Args*);
    void    (*dtor)(void*);
    void    (*dealloc)(void*);
    void    (*retain)(void*);
    void    (*release)(void*);
    size_t                method_count;
    const Z42MethodDesc*  methods;
    size_t                field_count;
    const Z42FieldDesc*   fields;
    size_t                trait_impl_count;
    const Z42TraitImpl*   trait_impls;
} Z42TypeDescriptor_v1;
```

`abi_version` 必须是首字段——VM 在确定后续字段语义前先读它。

### Decision 4: IR Opcode 字节分配

**当前最大已用值**：`StrConcat = 0x85`（Arrays & Strings 段尾）。

**新增分配**：
- `CallNative      = 0x53` — Calls 段扩展（紧接 `VCall = 0x52`）
- `CallNativeVtable= 0x54` — 同段
- `PinPtr          = 0x90` — 新开 Pin 段
- `UnpinPtr        = 0x91` — 同段

**理由**：`Call*` 系归入现有 Calls 段；`PinPtr/UnpinPtr` 与现有任一段语义无关，独立段便于以后扩展（Pin 协议可能加 `RePin`、`PinTracking` 等）。

### Decision 5: Stub Behavior（VM 端）

**Interp 遇到新 opcode 的行为**：

```rust
// src/runtime/src/interp/mod.rs (示意)
Instruction::CallNative { .. } |
Instruction::CallNativeVtable { .. } |
Instruction::PinPtr { .. } |
Instruction::UnpinPtr { .. } => {
    // C1 scaffold: opcode declared but not implemented; subsequent specs (C2/C4) wire up.
    return Err(VmError::trap("Native interop opcode not implemented (see spec C2/C4)"));
}
```

**理由**：编译期可序列化反序列化，运行时遇到立即 trap，不留 silently-wrong 路径。

### Decision 6: proc macro Stub

**展开期行为**：`#[derive(Z42Type)]` 等不在编译期 panic（会让 `cargo check` 也炸），改为生成一个 `compile_error!` 宏调用：

```rust
// src/runtime/crates/z42-macros/src/lib.rs (示意)
#[proc_macro_derive(Z42Type, attributes(z42))]
pub fn derive_z42_type(input: TokenStream) -> TokenStream {
    quote! {
        ::core::compile_error!(
            "#[derive(Z42Type)] is declared in z42-macros but not yet implemented (spec C3)"
        );
    }.into()
}
```

**理由**：用户尝试用 macro 时立即在自己的 `cargo build` 看到清晰错误，而不是莫名其妙的 macro panic。`z42-macros` crate 本身仍能编译，cargo check 通过。

### Decision 7: Manifest Schema 版本与可演进性

**Schema 文件**：`docs/design/manifest-schema.json`（JSON Schema Draft 2020-12）。

**根字段**：
- `$schema`、`$id`、`title`、`description`
- `abi_version` (const = 1)
- `module`、`version`（semver）、`library_name`
- `types[]`：每项含 `name`、`size`、`align`、`flags`、`fields`、`methods`、`trait_impls`

**演进策略**：
- 新增字段：`additionalProperties: true`，老 reader 忽略未知字段
- 重命名字段：bump `abi_version` 到 2，发新 schema 文件 `manifest-schema-v2.json`
- 删除字段：与重命名同等对待，bump 版本

### Decision 8: Error Code 范围

**Z0905–Z0910 占位**（具体语义留给 C2–C5 各自的 spec）：

| Code | 占位语义 | 由哪个 spec 定语义 |
|------|---------|-----------------|
| Z0905 | Native type registration failure | C2 |
| Z0906 | ABI version mismatch | C2 |
| Z0907 | Native method signature mismatch | C3 / C5 |
| Z0908 | Pinned block constraint violation | C4 |
| Z0909 | Manifest parse / schema validation error | C5 |
| Z0910 | Native library load (dlopen) failure | C2 / C5 |

C1 中 `error-codes.md` 只占位（"Reserved by spec design-interop-interfaces; semantics defined by C2–C5"），避免后续 spec 再争抢编号。

## Implementation Notes

### Workspace 转换的 cargo.lock 影响

`cargo` 在 workspace 模式下重新生成 `Cargo.lock`，记录所有 member crate 的 transitive deps。**预期变更行数较大但全是工具自动产物**——commit 时不要试图人工 review，跑一次 `cargo build` 让 cargo 重写即可。

### `extern "C"` 函数声明的分布

`z42_abi.h` 声明、`z42-abi/src/lib.rs` 镜像（`extern "C"` block）。本 spec 中**所有函数实现位于 `z42_vm` crate**（占位返回 `Z42Error::NotImplemented`），通过 `#[no_mangle] pub extern "C" fn z42_*(...)` 暴露。

C2 在此基础上填充实现，**无需改动 abi/rs/macros 三个 crate**（关键的接口稳定性保证）。

### IR Opcode 序列化往返测试

每个新 opcode 必须有 round-trip 测试：

```
build IR → ZbcWriter → bytes → ZbcReader → IR'
assert IR == IR'
```

放置在 `tests/golden/` 或 `src/compiler/z42.Tests/`（与现有 opcode 测试同位）。

## Testing Strategy

| 测试类型 | 位置 | 验证内容 |
|---------|------|---------|
| Crate 编译 | `cargo build --workspace --manifest-path src/runtime/Cargo.toml` | 4 个 crate 全部编译通过 |
| ABI 布局 | `crates/z42-abi/tests/abi_layout_tests.rs` | `mem::offset_of!` / `mem::size_of` 验证关键字段 offset 与 C 头文件一致 |
| 骨架 trait/type 编译 | `crates/z42-rs/tests/skeleton_tests.rs` | 用户能成功导入 `Z42Type` trait（不实现也可） |
| Macro 错误路径 | `crates/z42-macros/tests/compile_fail/` (用 trybuild) | 使用 `#[derive(Z42Type)]` 触发清晰 `compile_error!` |
| IR 序列化往返 | `src/compiler/z42.Tests/Codegen/NativeOpcodeRoundTripTests.cs` | 4 个新 opcode 写出再读入相等 |
| VM Trap 行为 | `src/runtime/tests/golden/run/native_opcode_trap_test.rs` | 手工构造含新 opcode 的 zbc，VM 返回特定 Trap 错误 |
| Manifest schema 自校验 | `tests/manifest_schema_validation.test` | 用 `tests/data/example-manifest.json` 校验 schema |
| 全绿验证 | `dotnet test` + `./scripts/test-vm.sh` | 现有测试不回归 |

## Risk & Rollback

- **风险 1**：workspace 转换在 CI / 部分脚本下行为不一致
  - 缓解：本地先 `cargo build && cargo test --workspace`；脚本 `scripts/test-vm.sh` 不变
- **风险 2**：4 个新 opcode 的字节值与未来某个外部 zbc 文件冲突（不可能，但保守起见）
  - 缓解：本变更后，0x53/0x54/0x90/0x91 即被永久占用；roadmap 后续 opcode 不再争抢
- **回滚**：本 spec 全部为 NEW + 增量 MODIFY，回滚直接 `git revert` 单个 commit 即可，不影响 L1 路径
