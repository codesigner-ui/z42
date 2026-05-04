# Tasks: core-reorg-directories

> 状态：🟢 已完成 | 创建：2026-05-04 | 完成：2026-05-04
> 类型：refactor (minimal mode)
> **变更说明**：把 z42.core/src/ 根目录散落的 ~30 个 .z42 文件按概念分组到子目录（Primitives / Delegates / Protocols），并把 Exception 基类移入既有 Exceptions/ 子目录。
> **原因**：`docs/design/delegates-events.md` 定稿后 z42.core 增量到 A0–A8；继续平铺会让目录扫读困难。
> **文档影响**：`src/libraries/README.md` + `docs/design/stdlib.md` Module Catalog；新增 `src/libraries/z42.core/src/README.md`。

## 进度概览
- [ ] 阶段 1: 创建子目录
- [ ] 阶段 2: 移动 Primitives (6 文件)
- [ ] 阶段 3: 移动 Delegates / Multicast / ISubscription (8 文件)
- [ ] 阶段 4: 移动 Protocols (9 文件)
- [ ] 阶段 5: 移动 Exception 基类到 Exceptions/
- [ ] 阶段 6: 同步文档
- [ ] 阶段 7: 构建 + 测试验证
- [ ] 阶段 8: commit + push + archive

## 阶段 1: 创建子目录
- [x] 1.1 mkdir Primitives/, Delegates/, Protocols/

## 阶段 2: 移动 Primitives (6 文件)
- [x] 2.1 git mv Bool.z42 Char.z42 Int.z42 Long.z42 Float.z42 Double.z42 → Primitives/

## 阶段 3: 移动 Delegates 体系 (8 文件)
- [x] 3.1 git mv Delegates.z42 DelegateOps.z42 → Delegates/
- [x] 3.2 git mv ISubscription.z42 SubscriptionRefs.z42 WeakHandle.z42 → Delegates/
- [x] 3.3 git mv MulticastAction.z42 MulticastFunc.z42 MulticastPredicate.z42 → Delegates/

## 阶段 4: 移动 Protocols (9 文件)
- [x] 4.1 git mv IComparable IComparer IDisposable IEnumerable IEnumerator IEqualityComparer IEquatable IFormattable INumber → Protocols/

## 阶段 5: 移动 Exception 基类
- [x] 5.1 git mv Exception.z42 → Exceptions/

## 阶段 5.1: 留根的工具类（无 move）
- Object.z42 / Type.z42 / String.z42 — 绝对基石
- Convert.z42 / Assert.z42 / GC.z42 / Disposable.z42 — 孤立工具类（Disposable 是 IDisposable 的具体实现 + From 工厂；与 Convert/Assert/GC 同级）

## 阶段 6: 同步文档
- [x] 6.1 src/libraries/README.md 更新 z42.core 文件清单
- [x] 6.2 docs/design/stdlib.md "Module Catalog" 段更新 z42.core 目录树
- [x] 6.3 新增 src/libraries/z42.core/src/README.md 描述 5 个子目录的职责

## 阶段 7: 构建 + 测试验证
- [x] 7.1 dotnet build src/compiler/z42.slnx
- [x] 7.2 cargo build (走 test-vm.sh 内置)
- [x] 7.3 ./scripts/build-stdlib.sh —— 6/6 zpkg
- [x] 7.4 dotnet test —— 988/988 通过
- [x] 7.5 ./scripts/test-vm.sh —— 256/256 (128 × interp + 128 × jit)
- [x] 7.6 ./scripts/test-stdlib.sh —— 6/6

## 阶段 8: 提交
- [ ] 8.1 git commit (refactor scope)
- [ ] 8.2 archive spec → spec/archive/
- [ ] 8.3 git push origin main

## 备注
- z42.core/z42.core.z42.toml **无 sources.include 字段** → 默认 `src/**/*.z42` 递归通配，目录重组**不影响 build**
- 用 `git mv` 而非 cp+rm，保留文件历史
