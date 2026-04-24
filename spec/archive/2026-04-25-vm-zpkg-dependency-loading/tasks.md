# Tasks: VM zpkg-based Dependency Loading

> 状态：🟢 已完成 | 创建：2026-04-25 | 完成：2026-04-25 | 类型：vm + compiler (scope 扩展)

## 进度概览
- [x] 阶段 1: LazyLoader 内部重构（核心数据结构 + 触发策略）
- [x] 阶段 2: main.rs 启动流程接入依赖列表
- [x] 阶段 3: loader.rs `resolve_namespace` 签名调整 + 新增 `resolve_dependency`
- [x] 阶段 3b: 编译期 TsigCache 对称改造（scope 扩展）
- [x] 阶段 4: 单元测试
- [x] 阶段 5: 回归验证 + W1 解锁
- [x] 阶段 6: 文档同步 + 归档

---

## 阶段 1: LazyLoader 内部重构

- [x] 1.1 新增 `ZpkgCandidate { file_path, namespaces }`
- [x] 1.2 `LazyLoader` 字段：`loaded_zpkgs` + `declared_zpkgs`
- [x] 1.3 `install_with_deps(libs_dir, main_pool_len, declared, initially_loaded)` + 向后兼容 `install`
- [x] 1.4 `resolve_function` 策略 C + 回退 B
- [x] 1.5 `resolve_type` 同步新路径
- [x] 1.6 `load_zpkg_file` 防重 + 递归展开 + first-wins 冲突保护

## 阶段 2: main.rs 启动流程接入

- [x] 2.1 `build_declared_candidates` 辅助函数（.zpkg DEPS + .zbc import_namespaces + DEPS file/namespaces 两种源）
- [x] 2.2 `install_with_deps` 替换旧 `install`，传入 declared + initially_loaded
- [x] 2.3 `initially_loaded_zpkgs` 正确记录（z42.core 预加载 + JIT eager 路径）

## 阶段 3: loader.rs API 调整

- [x] 3.1 `resolve_namespace` → `Result<Vec<PathBuf>>`，不 bail on ambiguous
- [x] 3.2 新增 `resolve_dependency(zpkg_file, libs_paths)` 按文件名查找
- [x] 3.3 修正 `main.rs` JIT 路径适配新签名

## 阶段 3b: 编译期 TsigCache 对称改造（scope 扩展 2026-04-25）

- [x] 3b.1 `TsigCache._nsToPath` → `Dictionary<string, List<string>> _nsToPaths`
- [x] 3b.2 `RegisterNamespace` 追加列表，去重
- [x] 3b.3 `LoadForUsings` 聚合多 zpkg 路径
- [x] 3b.4 `LoadAll` 聚合所有 zpkg 路径

## 阶段 4: 单元测试

- [x] 4.1 `namespace_prefix` 浅/深/无 namespace
- [x] 4.2 `install` / `install_with_deps` / `uninstall` 干净
- [x] 4.3 `candidates_routes_by_exact_namespace`
- [x] 4.4 `candidates_routes_by_descendant_namespace`（前缀匹配）
- [x] 4.5 **`candidates_routes_multi_zpkg_sharing_namespace`** — W1 场景直接回归
- [x] 4.6 `install_filters_already_loaded_from_declared`
- [x] 4.7 `remaining_declared_excludes_loaded` / `candidates_excludes_subsequently_loaded`
- [x] 4.8 `test_resolve_namespace_ambiguous_returns_both` / `test_resolve_dependency_by_file_name`（loader_tests.rs）

## 阶段 5: 回归验证 + W1 解锁

- [x] 5.1 `cargo build` 无编译错误
- [x] 5.2 `cargo test` 61/61 通过
- [x] 5.3 `dotnet build` 无编译错误
- [x] 5.4 `dotnet test` 573/573 通过（77_stdlib_stack_queue 转绿）
- [x] 5.5 `./scripts/test-vm.sh` 172/172 通过（interp + jit）
- [x] 5.6 W1 其他 golden 不破坏

## 阶段 6: 文档同步 + 归档

- [x] 6.1 `docs/design/stdlib.md` Module Auto-load Policy 更新（随 W1 同步）
- [x] 6.2 **新增** `docs/design/vm-architecture.md` — LazyLoader / VCall / TypeDesc / ConstStr 重映射
- [x] 6.3 **新增** `docs/design/compiler-architecture.md` — TsigCache / ImportedSymbolLoader / QualifyClassName
- [x] 6.4 `.claude/CLAUDE.md` 文档同步规则追加"编译器 / VM 实现原理"行（User 要求）
- [x] 6.5 `docs/roadmap.md` — 见 W1 同步
- [x] 6.6 tasks.md 状态 → `🟢 已完成`
- [ ] 6.7 `spec/changes/vm-zpkg-dependency-loading/` → `spec/archive/2026-04-25-vm-zpkg-dependency-loading/`
- [ ] 6.8 commit + push（scope `feat(vm+compiler)`）

## 备注

- **scope 扩展** (2026-04-25)：阶段 5 发现 VM 侧改造单做不够，编译期 `TsigCache`
  对称存在"一 namespace 独占一 zpkg"限制（first-wins dict）。根因同构，
  但作用点不同。合并进本 change 一次性解决，使 C# assembly 模型在 runtime 和
  compile-time 两端都正确。见 design.md Decision 7。
- **新规则落地** (2026-04-25)：User 要求"编译器和 VM 的实现原理要记录到设计
  文档"。本次同步新增 `vm-architecture.md` + `compiler-architecture.md`；
  `.claude/CLAUDE.md` 追加对应文档同步表行。
