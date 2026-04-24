# Proposal: VM zpkg-based Dependency Loading

## Why

当前 z42 VM 的懒加载器（`src/runtime/src/metadata/lazy_loader.rs`）以 **namespace**
作为触发键和解析键——`Call` miss 时从 FQ func_name 提取 namespace 前缀，
调用 `resolve_namespace` 在 libs 目录里找唯一一个声明了该 namespace 的 zpkg。
若多于一个 zpkg 声明同一 namespace，则直接 `bail!("AmbiguousNamespaceError")`
（见 [loader.rs:169](src/runtime/src/metadata/loader.rs#L169)）。

这个硬约束阻碍了 stdlib 的正常重组：`reorganize-stdlib-packages` (W1) 把
`List` / `Dictionary` 从 `z42.collections` 迁到 `z42.core/src/Collections/`
后，`Std.Collections` namespace 同时出现在 `z42.core.zpkg` 和
`z42.collections.zpkg` 里，触发 ambiguous 错误 → Stack/Queue 所在的
`z42.collections.zpkg` 懒加载失败 → 测试 `run/77_stdlib_stack_queue`
报 `undefined function Std.Collections.Stack.Push`。

C# BCL 的 assembly 加载模型是业内参考答案：**assembly (= z42 zpkg) 是
物理加载单位；namespace 只是逻辑分组，可跨多个 assembly 共享**。AssemblyRef
驱动依赖加载，TypeRef → TypeDef 查找遍历已加载 assembly 的类型表。

z42 的 zpkg 元数据已经支持这种模型：
- `ZpkgDep { file: String, namespaces: Vec<String> }`（[formats.rs:115](src/runtime/src/metadata/formats.rs#L115)）
- `LoadedArtifact.dependencies: Vec<ZpkgDep>`
- JIT/AOT 模式已经按 `user_artifact.dependencies` 做 eager 预加载（[main.rs:205](src/runtime/src/main.rs#L205)）

只有 **interp 模式的懒加载路径** 还在用 namespace 驱动。本提案把它改为
基于 zpkg 驱动，消除 namespace 独占约束，解锁 stdlib 重组。

## What Changes

1. **懒加载触发键从 namespace 改为 zpkg 依赖条目**。Call miss 时不再从
   func_name 提取 namespace，而是遍历 **已声明但未加载的依赖 zpkg**，
   依次加载直到函数表命中。
2. **依赖传递**。加载某个 zpkg 后，其自身的 `ZpkgDep` 列表也展开进
   "declared but not loaded" 集合（对齐 C# assembly transitive reference）。
3. **`resolve_namespace` 不再用于加载路径**。保留函数用于编译期 `using`
   语义 / 诊断工具，或完全删除（由 design.md 决定）。
4. **向后兼容 `.zbc` 主模块**：`.zbc` 无 DEPS section，从现有
   `import_namespaces` 字段反查 zpkg 文件名作为依赖列表（一次性，
   启动时完成）。
5. **`z42.core` 隐式 prelude 保留不变**：VM 启动时 eager 加载，不走
   懒加载路径。

## Scope（允许改动的文件/模块）

| 文件/模块 | 变更类型 | 说明 |
|-----------|---------|------|
| `src/runtime/src/metadata/lazy_loader.rs` | refactor | 核心：触发键换成 zpkg file；状态扩展 declared_deps 集合 |
| `src/runtime/src/metadata/lazy_loader_tests.rs` | add | 新增跨 zpkg 共享 namespace 的回归测试 |
| `src/runtime/src/main.rs` | edit | interp 路径初始化 lazy_loader 时传入 main module 的 dependencies |
| `src/runtime/src/metadata/loader.rs` | edit | `resolve_namespace` 不再 bail on ambiguous（或废弃）；新增 `resolve_dependency(zpkg_name)` |
| `src/compiler/z42.Pipeline/PackageCompiler.cs` | edit | **Scope 扩展（2026-04-25）**：编译期 `TsigCache._nsToPath` 从 `Dictionary<string, string>` → `Dictionary<string, List<string>>`，`LoadForUsings` / `LoadAll` 聚合所有路径。见下文 "Scope 扩展说明"。|
| `docs/design/ir.md` | edit | 若 zbc/zpkg 格式有变化（本变更预计无格式变更，仅加载语义变更） |
| `docs/design/stdlib.md` | edit | Module Auto-load Policy 章节同步"namespace 可跨 zpkg" |
| `docs/roadmap.md` | edit | L2 "VM 质量" 条目记录加载器架构升级 |

### Scope 扩展说明（2026-04-25 实施阶段追加）

实施阶段 5（回归验证）发现：VM 侧改造完成后 W1 仍有 `run/77_stdlib_stack_queue`
失败。根因定位为编译期 `TsigCache` 也有对称的 "一 namespace 独占一 zpkg" 限制：

```csharp
// PackageCompiler.cs:522 (改前)
private readonly Dictionary<string, string> _nsToPath = new(...);
public void RegisterNamespace(string ns, string zpkgPath) {
    _nsToPath.TryAdd(ns, zpkgPath);   // first-wins
}
```

W1 后 `Std.Collections` 同时出现在 `z42.core.zpkg` 和 `z42.collections.zpkg`，
`TsigCache` 只保留第一个，导致后一个包里的 Stack / Queue 的 TSIG 元数据在编译
时不可见 → `QualifyClassName` 对 Stack 返回 bare 名 → 生成错误 IR。

VM 和编译器必须对称支持 "namespace 可跨 zpkg" 才能真正解锁 W1。scope 扩展
一并纳入同变更，保持闭环（C# assembly 模型在 runtime 和 compile-time 都正确）。

## Out of Scope

- 预加载策略（User 已裁决保留懒加载）
- 编译器对 `using` 的解析改动（`using` 仍产生 namespace 级引用，不变）
- 自举相关 / 跨 zpkg 符号可见性规则（public / internal，L3-G 范畴）
- 动态插件 / 热加载（L3+）
- JIT/AOT 路径的改动（已经是 eager + dependency-based，无需动）

## Open Questions

- [ ] `resolve_namespace` 彻底删除 vs 保留为编译期工具？
  - 倾向：**保留但改语义为"返回所有声明该 namespace 的 zpkg 列表（不 bail）"**，供编译期/诊断使用
- [ ] `.zbc` 主模块的依赖推断失败时（`import_namespaces` 里的 namespace
  无对应 zpkg）是否报错？
  - 倾向：**warn + 跳过**（与当前行为一致，lazy miss 时才真正报错）
- [ ] Call miss 时遍历候选 zpkg 的顺序：按 declared 顺序 / 按 zpkg 文件名字母序？
  - 倾向：**按 declared 顺序**（对齐 C# AssemblyRef 列表顺序）

## Blocks

- ✅ `reorganize-stdlib-packages` (W1) — 本变更完成后即可验证 W1 测试转绿

## Unblocks

- Wave 2+ 的 stdlib 扩展（新接口可以和基础类型共存于 `Std` namespace，
  不受 zpkg 划分限制）
- 未来第三方库跨 `Std.*` namespace 扩展（例：`z42.linq` 扩展 `Std.Collections`
  的方法 trait）
