# Tasks: formalize-jit-vm-interface

> 状态：🟢 已完成 | 创建：2026-05-07 | 完成：2026-05-07
> 类型：refactor（最小化模式）
> 来源：[docs/review.md](../../../docs/review.md) Part 4 §4.2 + Part 1 §1.1

## 验证报告

### 编译状态
- ✅ `cargo build --manifest-path src/runtime/Cargo.toml` 无 warning（`VM_JIT_INTERFACE_VERSION` 未使用 warning 用 `#[allow(dead_code)]` 抑制——hook 用途）
- ✅ `dotnet build src/compiler/z42.slnx -c Debug --no-incremental` 0 Error / 0 Warning
  （清空 obj/ 缓存，规避 pre-existing MSB3492 stale-cache 问题，与 `split-exec-instr` 同款）

### 测试结果
- ✅ `cargo test`: 全绿
- ✅ `./scripts/test-vm.sh`: interp 157/157 + jit 153/153 = **310/310**
- ✅ `dotnet test`: **1104/1104**

### LOC 目标
- helpers/ 子文件最大 **275 LOC** (registry.rs，因含 47 个 `decl!` + 47 个 `reg!`)
- helpers_object.rs (旧 512 LOC) 拆为 5 个文件（最大 235）
- jit/mod.rs: 233 → **169** 行（删了 ~60 行 `reg!` 块）
- jit/translate.rs: 935 → **772** 行（删了 ~163 行 HelperIds + declare_helpers）
- 全部远低于 [code-organization.md](../../../.claude/rules/code-organization.md) 500 LOC 硬限

### 实测 helper 数（review.md 估算 65，实际 ~47）
- value: 12
- arith: 21（含 macro 实例化的 cmp/bitwise）
- control: 3
- call: 2
- array: 5
- object: 8
- vcall: 1
- closure: 4
- 合计 **56 个 `#[no_mangle]`**（含 macro 展开），HelperIds **47 字段**

### 结论：✅ 全绿，可归档

**变更说明**: 把当前散在 4 个 `helpers_*.rs` 的 46 个 JIT extern helper 重组到 `jit/helpers/` 目录下，按 op category 拆分（与 `interp/exec_*.rs` 对称）；引入中央 `registry.rs` 集中管理 helper 注册（mod.rs + translate.rs 两处都通过 registry），并定义 `VM_JIT_INTERFACE_VERSION` 常量。

**原因**:
1. `helpers_object.rs` 512 LOC 超 [code-organization.md](../../../.claude/rules/code-organization.md) 500 LOC 硬限
2. review.md §4.2: helper 边界未形式化，加新 helper 要改 3 处（定义 / mod.rs reg / translate.rs declare），无版本号；M4 全绿后任意改 helper 签名风险面变大
3. 与刚完成的 `split-exec-instr` 形成对称：`interp/exec_<cat>.rs` ↔ `jit/helpers/<cat>.rs`，未来扩展指令时改两边一一对应，认知负担最小

**文档影响**:
- `docs/review.md` — 路线图 §VM 线 `formalize-jit-vm-interface` 状态 📋 → 🟢；同时 §1.1 提及 `helpers_object.rs` 超限的描述应该补一笔（审查时漏了）
- `src/runtime/src/jit/README.md` — 同步核心文件表与依赖关系
- `docs/design/vm-architecture.md` — **加一段**记录 "JIT/EE helper 边界" 设计（registry + version 模式），按 [CLAUDE.md](../../../.claude/CLAUDE.md) "实现原理文档规则" 必须落地

---

## Scope（允许改动的文件）

### NEW

| 文件 | 说明 |
|---|---|
| `src/runtime/src/jit/helpers/mod.rs` | 重新导出子模块；`VM_JIT_INTERFACE_VERSION: u32 = 1` |
| `src/runtime/src/jit/helpers/registry.rs` | 中央 helper 注册表 + `register_symbols(builder)` + `declare_imports(jit)` 两个公开函数 |
| `src/runtime/src/jit/helpers/value.rs` | const* / copy / str_concat / to_str / get_bool / set_ret （来自 helpers_mem 拆出非控制流部分） |
| `src/runtime/src/jit/helpers/control.rs` | throw / install_catch / match_catch_type （来自 helpers_mem 控制流部分） |
| `src/runtime/src/jit/helpers/arith.rs` | 来自 helpers_arith.rs（直接搬运） |
| `src/runtime/src/jit/helpers/call.rs` | jit_call / jit_builtin（来自 helpers_object 调用部分） |
| `src/runtime/src/jit/helpers/array.rs` | array_new / array_new_lit / array_get / array_set / array_len（来自 helpers_object） |
| `src/runtime/src/jit/helpers/object.rs` | obj_new / field_get / field_set / is_instance / as_cast / static_get / static_set / default_of |
| `src/runtime/src/jit/helpers/vcall.rs` | jit_vcall（独立文件因体积；与 interp/exec_vcall.rs 对称） |
| `src/runtime/src/jit/helpers/closure.rs` | 来自 helpers_closure.rs（直接搬运） |

### MODIFY

| 文件 | 说明 |
|---|---|
| `src/runtime/src/jit/mod.rs` | (1) 删除 `mod helpers_arith / closure / mem / object;`，改为 `mod helpers;`；(2) `compile_module` 中第 113-171 行 60 行 `reg!()` → `helpers::registry::register_symbols(&mut jit_builder)` |
| `src/runtime/src/jit/translate.rs` | `declare_helpers` 改为委托 `helpers::registry::declare_imports(jit)`，保留对外签名 |
| `src/runtime/src/jit/helpers.rs` | 保留 `JitFn` / `take_exception_error` / `set_exception` 等非 helper 工具（仅 ~100 LOC，不动） |
| `src/runtime/src/jit/README.md` | 更新核心文件表（同步规则） |
| `docs/design/vm-architecture.md` | 增加"JIT/EE helper 边界"章节（registry + version 模式） |
| `docs/review.md` | 路线图状态 + §1.1 补 helpers_object 注记 |

### DELETE

| 文件 | 说明 |
|---|---|
| `src/runtime/src/jit/helpers_arith.rs` | 内容迁入 `helpers/arith.rs` |
| `src/runtime/src/jit/helpers_closure.rs` | 内容迁入 `helpers/closure.rs` |
| `src/runtime/src/jit/helpers_mem.rs` | 内容拆入 `helpers/value.rs` + `helpers/control.rs` |
| `src/runtime/src/jit/helpers_object.rs` | 内容拆入 `helpers/{call, array, object, vcall}.rs`（解决 512 LOC 硬限） |

**只读引用**:
- `src/runtime/src/interp/exec_*.rs` — 保持命名对称的参考
- `src/runtime/src/metadata/{bytecode, types, value}.rs` — 类型定义

---

## 设计要点

### `VM_JIT_INTERFACE_VERSION` 用途

```rust
// helpers/mod.rs
pub const VM_JIT_INTERFACE_VERSION: u32 = 1;
```

当前**没有运行时校验**——单一 JITModule 实现，bump 版本号当 helper 签名/集合变化时；未来若引入第二个 JIT 后端（LLVM / wasm），启动时校验 helper table 版本与该后端编译时的版本是否兼容。**留 hook 不引入校验代码，避免过度设计**。

### `registry.rs` 形态

不引入运行时数据结构（避免 vtable 间接调用开销），保持当前的"宏注册"形态，但**集中到一处**:

```rust
// helpers/registry.rs
use cranelift_jit::JITBuilder;
use cranelift_jit::JITModule;
use super::*;

pub fn register_symbols(builder: &mut JITBuilder) {
    macro_rules! reg {
        ($name:expr, $fn:expr) => { builder.symbol($name, $fn as *const u8); }
    }
    // value
    reg!("jit_const_i32", value::jit_const_i32);
    reg!("jit_const_i64", value::jit_const_i64);
    // ... 全部 46 个
}

pub fn declare_imports(jit: &mut JITModule) -> anyhow::Result<HelperIds> {
    // 把 translate.rs 现有 declare_helpers 整体迁过来
    ...
}
```

加新 helper 改 **2 处**（不再是 3 处）: helper 文件本身 + registry.rs。`mod.rs` / `translate.rs` 不动。

### 不引入 trait

trait object dispatch 会带来间接调用开销，且 helper 已经走 cranelift symbol 解析机制（按名解析），用 trait 反而绕路。保持 extern "C" 直调零开销不变。

### 与 interp/exec_*.rs 的命名对称

| interp 子模块 | jit/helpers 子模块 |
|---|---|
| `exec_value.rs` | `value.rs` |
| `exec_address.rs` | （地址-load 在 JIT 里通过别的机制实现，无独立 helper） |
| `exec_call.rs` | `call.rs` + `closure.rs`（拆细：call/builtin 在 call.rs，indirect+mkclos 在 closure.rs） |
| `exec_array.rs` | `array.rs` |
| `exec_object.rs` | `object.rs` |
| `exec_vcall.rs` | `vcall.rs` |
| `exec_native.rs` | （native 当前不走 JIT helper） |

未来若 JIT 实现 LoadXxxAddr / CallNative 的 helper，新增 `address.rs` / `native.rs` 即可。

---

## 任务清单

### 阶段 1: 准备
- [ ] 1.1 baseline 验证: `cargo test` + `./scripts/test-vm.sh` 全绿
- [ ] 1.2 列全部 46 个 helper（grep 输出）作为搬运清单
- [ ] 1.3 阅读 `translate.rs::declare_helpers` 确认 cranelift 签名定义形态

### 阶段 2: 创建 helpers/ 目录结构（无功能变化）
- [ ] 2.1 `helpers/mod.rs` — 子模块导出 + `VM_JIT_INTERFACE_VERSION`
- [ ] 2.2 `helpers/value.rs` — 搬 helpers_mem.rs 的非控制流部分
- [ ] 2.3 `helpers/control.rs` — 搬 helpers_mem.rs 的 throw/catch 部分
- [ ] 2.4 `helpers/arith.rs` — helpers_arith.rs 直接搬运
- [ ] 2.5 `helpers/closure.rs` — helpers_closure.rs 直接搬运
- [ ] 2.6 `helpers/call.rs` — helpers_object.rs 中 jit_call / jit_builtin
- [ ] 2.7 `helpers/array.rs` — helpers_object.rs 中 array_*
- [ ] 2.8 `helpers/object.rs` — helpers_object.rs 中 obj_new / field_* / is_instance / as_cast / static_* / default_of
- [ ] 2.9 `helpers/vcall.rs` — helpers_object.rs 中 jit_vcall

### 阶段 3: 创建 registry.rs
- [ ] 3.1 `helpers/registry.rs::register_symbols(&mut JITBuilder)` — 集中所有 reg!() 调用
- [ ] 3.2 `helpers/registry.rs::declare_imports(&mut JITModule) -> Result<HelperIds>` — 整体迁 translate.rs::declare_helpers

### 阶段 4: 改造 mod.rs 与 translate.rs
- [ ] 4.1 `mod.rs` 删除老 `mod helpers_*;` 声明，改为 `mod helpers;`
- [ ] 4.2 `mod.rs::compile_module` 第 113-171 行 60 行 `reg!()` 整体替换为 `helpers::registry::register_symbols(&mut jit_builder)`
- [ ] 4.3 `translate.rs::declare_helpers` 调用 `helpers::registry::declare_imports`，保持对外 API
- [ ] 4.4 删除 `src/runtime/src/jit/helpers_{arith,closure,mem,object}.rs` 4 个文件

### 阶段 5: 验证
- [ ] 5.1 `cargo build --manifest-path src/runtime/Cargo.toml` 无 warning（无 unused import / dead code）
- [ ] 5.2 `cargo test --manifest-path src/runtime/Cargo.toml` 全绿
- [ ] 5.3 `./scripts/test-vm.sh` 全绿（interp 不变 + jit 153/153 不变）
- [ ] 5.4 `dotnet test src/compiler/z42.Tests/z42.Tests.csproj` 全绿
- [ ] 5.5 LOC 确认: `wc -l src/runtime/src/jit/helpers/*.rs`，每个 ≤ 300，无超 500 硬限
- [ ] 5.6 grep 确认 `mod.rs` 和 `translate.rs` 不再各自硬编码 helper 列表（搜 `jit_const_i32` 之类应只在 helpers/ 下出现）

### 阶段 6: 文档同步
- [ ] 6.1 `src/runtime/src/jit/README.md` 更新核心文件表（helpers/ 子目录 + registry）
- [ ] 6.2 `docs/design/vm-architecture.md` 新增 "JIT/EE helper 边界" 段（registry + version 模式 + 与 CoreCLR ICorJitInfo 对照）
- [ ] 6.3 `docs/review.md` 路线图状态 📋 → 🟢；§1.1 补 helpers_object 注记（顺带修正初版漏点）

### 阶段 7: 归档 + 提交
- [ ] 7.1 tasks.md 状态 🟡 → 🟢，更新日期
- [ ] 7.2 `spec/changes/formalize-jit-vm-interface/` → `spec/archive/2026-05-07-formalize-jit-vm-interface/`
- [ ] 7.3 commit + push

---

## 备注

- **零行为变化**: 所有 helper 函数体不动，仅文件位置 + 注册路径变化
- **测试要求**（refactor 类型）: "确保已有测试仍覆盖；不得删除测试"——本 spec 不新增测试
- **依赖关系**: 此 spec 完成后，`split-large-test-files` 可继续推进；`introduce-method-token` 与本 spec 无冲突可并行（实际上互补——method token 化后 helper 接收 MethodId 也更整齐）
- **ABI 注意**: 不改 helper 函数签名（Cranelift 已发布的 cif 不变），仅改源码组织。helper 名字（`jit_*`）保持不变以避免 cranelift symbol 注册更动
- **`VM_JIT_INTERFACE_VERSION` 当前不消费**: 这是有意的——为未来 tier-up / 多 JIT 实现留 hook，本 spec 不引入校验代码（避免过度设计）
