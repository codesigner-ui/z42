# Tasks: reorganize-stdlib-packages (W1)

> 状态：🟢 已完成 | 创建：2026-04-25 | 完成：2026-04-25

> **阻塞 + 解锁链**：阶段 4 初次验证时 `run/77_stdlib_stack_queue` 失败，根因
> 为 VM / 编译器都把 namespace 绑死在单个 zpkg 上。解决方案：拉独立变更
> `vm-zpkg-dependency-loading` 改造加载模型（C# assembly 对齐）；本 W1 暂挂。
> VM change 完成后，本 W1 文件改动无需变更即解锁转绿。

**变更说明：** 把 `List<T>` / `Dictionary<K,V>` 两个"最基础泛型集合"从 `z42.collections` 包源码目录上提到 `z42.core` 包源码目录；删除 `HashSet.z42` 空壳（无实现，仅 TODO 注释，未来直接在 `z42.core/src/` 新建）；`Queue` / `Stack` 留在 `z42.collections`。

**原因：**
- C# BCL 路线参考：`List/Dictionary/HashSet` 是最基础集合容器，与核心类型共享同一 assembly (`System.Private.CoreLib`)，属于"隐式 prelude 范围内的基础集合"。
- z42 `z42.core` 是隐式 prelude（VM 启动时自动加载），把最基础集合放入能让用户代码无需额外包依赖即可使用（当前已靠 pseudo-class 兜底达成"无需 `using`"，重组后和 Queue/Stack 等"需要显式 using"的高级集合在包/逻辑层面分离）。
- 为 Wave 2 起后续 core 侧接口补齐（Exception / IEnumerable<T>）铺路——这些接口会被 List/Dictionary 实现，三者位于同一包可避免跨包循环依赖。

**文档影响：**
- `src/libraries/z42.core/README.md`：新增 "Collections" 章节，列出 List / Dictionary / HashSet
- `src/libraries/z42.collections/README.md`：从表中移除 List / Dictionary / HashSet，保留 Queue / Stack，并说明"最基础三件套已上提到 z42.core"
- `src/libraries/README.md`：更新库列表表格的 `z42.core` 和 `z42.collections` 内容列
- `docs/design/stdlib.md`：更新 "Module Catalog" 的 `z42.core` 和 `z42.collections` 文件清单；更新 "Module Auto-load Policy" 表格（`Std.Collections` 的含义范围缩小为 Queue/Stack）
- `docs/roadmap.md`：L2 "标准库（基础）" 条目更新（`z42.core` / `z42.collections` 内容描述）
- **不涉及** `docs/design/language-overview.md`（语言语法无变化）
- **不涉及** `docs/design/ir.md`（IR / VM 不变）

---

## Scope 边界（明确）

**本波 IN scope：**
- 文件系统移动：`src/libraries/z42.collections/src/{List,Dictionary,HashSet}.z42` → `src/libraries/z42.core/src/*.z42`
- 两个包 `z42.*.z42.toml` 不变（`z42.collections` 仍声明依赖 `z42.core`）
- `Std.Collections` namespace 保留不变（文件移到 `z42.core/src/` 下，但 `namespace Std.Collections;` 头部保留）
- 包 README / libraries README / docs/design/stdlib.md / docs/roadmap.md 同步更新
- 构建 stdlib zpkg + 跑全套测试验证

**本波 OUT of scope（不动）：**
- 编译器 `SymbolCollector.cs:208-209` 的 pseudo-class 兜底映射（`"List" => Z42PrimType("List")` 等）—— Roadmap L2 backlog 已登记为 "TypeEnv.BuiltinClasses 动态注入 / IsReferenceType 硬编码"，属于 L3-G 泛型类型表示跨波工作，独立变更处理
- `Std.Collections` namespace 是否改为 `Std`（使 List 等进入 prelude 免 using）—— 当前 pseudo-class 路径已让用户代码无需 using，改变 namespace 是正交议题；本波仅做**物理包位置迁移**，namespace 不动
- HashSet 的实际实现（当前只是 TODO 注释）—— 与本波位置迁移正交，留给 Wave 2/3 补齐接口时一起做
- Queue / Stack 的具体内容修改

---

## 设计决策记录（供实施前裁决）

### D1: 命名空间保留 `Std.Collections` 不改
- **问题**：文件从 `z42.collections` 包迁到 `z42.core` 包后，`namespace Std.Collections;` 要不要改？
- **选项 A（保留）**：List.z42 文件头仍写 `namespace Std.Collections;`，即使它物理位于 `z42.core/src/` 目录下。对齐 C# BCL（List<T> 在 `System.Collections.Generic` namespace 但在 `System.Private.CoreLib` assembly）。
- **选项 B（改为 `Std`）**：让 List 进入隐式 prelude 自动可见，和 Object/String 平级。
- **裁决**：采用 **A**。理由：
  1. 包位置（物理）与 namespace（逻辑）本来就解耦
  2. prelude 应该精简；未来可能新增十几个集合类型，全挤 `Std` 会造成 namespace 污染
  3. pseudo-class 兜底目前已实现"用户代码无需 using"的效果，本波不靠 namespace 提升来达成这个体验
  4. 未来若真要让 List 免 using，可以独立做 "Std.Collections 的关键类型上提 prelude" 这个正交变更

### D2: HashSet 空壳直接删除（User 2026-04-25 裁决）
- **问题**：HashSet.z42 当前只是 TODO 注释壳，无实现。是否跟着 List/Dict 一起迁到 core？
- **裁决**：**直接删除 HashSet.z42**。理由：空壳无实际内容，迁移过去只是搬动一个 TODO 注释，没有价值；未来真正实现 HashSet 时直接在 `z42.core/src/` 新建，不留占位壳。

### D4: List/Dictionary 放 `z42.core/src/Collections/` 子目录（User 2026-04-25 追加裁决）
- **问题**：迁入 `z42.core` 后是扁平放 `src/` 还是建 `Collections/` 子目录？
- **裁决**：**建子目录**。理由：
  1. 对齐 C# BCL 源码组织（`System/Collections/Generic/List.cs`）
  2. core 扁平层已有 17 个文件，新增集合会继续膨胀，子目录分组更清晰
  3. `sources.include` 默认 `src/**/*.z42` 递归通配，构建无需改动
  4. 未来 `Collections/HashSet.z42` / `Collections/Array.z42` 等直接落入此目录

### D3: `z42.collections` 包是否保留
- **问题**：抽走三件套后 collections 只剩 Queue / Stack，是否干脆删包并入 core？
- **裁决**：**保留**。理由：
  1. Queue / Stack 是"次级集合"，C# 也在 `System.Collections.Generic`（和 List 同 namespace）但在 z42 我们已经让它们 **需要显式 using Std.Collections**（通过物理包分离）
  2. 保留独立包为未来 `LinkedList` / `SortedDictionary` / `PriorityQueue` 等 L2/L3 集合留位置
  3. 减少包合并后再拆的额外 churn

---

## 阶段 1：源码文件迁移 + 子目录组织
- [x] 1.1 `git mv src/libraries/z42.collections/src/List.z42 src/libraries/z42.core/src/Collections/List.z42`（D4 子目录）
- [x] 1.2 `git mv src/libraries/z42.collections/src/Dictionary.z42 src/libraries/z42.core/src/Collections/Dictionary.z42`（D4 子目录）
- [x] 1.3 `git rm src/libraries/z42.collections/src/HashSet.z42`（D2：空壳直接删除）
- [x] 1.4 验证 2 个迁移后文件头 `namespace Std.Collections;` 保留不变（D1）

## 阶段 2：包 manifest 审查
- [x] 2.1 `z42.core/z42.core.z42.toml` 不变（无依赖、隐式 prelude 保持）
- [x] 2.2 `z42.collections/z42.collections.z42.toml` 更新过时 NOTE 注释（说明三件套已迁移）
- [x] 2.3 确认 `z42.collections/src/` 下还有 `Queue.z42` + `Stack.z42`（目录不为空）

## 阶段 3：文档同步
- [x] 3.1 `src/libraries/z42.core/README.md` 加 `src/Collections/` 小节 + 设计决策（D1/D4）
- [x] 3.2 `src/libraries/z42.collections/README.md` 核心文件表瘦身到 Queue / Stack；顶部说明"基础三件套已上提到 z42.core/src/Collections/"
- [x] 3.3 `src/libraries/README.md` 库列表表格同步
- [x] 3.4 `docs/design/stdlib.md` Module Catalog（`z42.core` 加子目录树 + `z42.collections` 瘦身）+ Auto-load Policy 表格行备注
- [x] 3.5 `docs/roadmap.md` L2 "标准库（基础）" 条目拆为 core/Collections + collections 两行

## 阶段 4：构建 + 测试验证
- [x] 4.1 `./scripts/build-stdlib.sh` 成功（core.zpkg 39912 B, collections.zpkg 6545 B；子目录 Collections/ 被递归拾取）
- [x] 4.2 `dotnet build src/compiler/z42.slnx` 无编译错误
- [x] 4.3 `cargo build --manifest-path src/runtime/Cargo.toml` 无编译错误
- [x] 4.4 `dotnet test src/compiler/z42.Tests/z42.Tests.csproj` 573/573 通过（vm-zpkg-dependency-loading 完成后）
- [x] 4.5 `./scripts/test-vm.sh` 172/172 通过（interp + jit）
- [x] 4.6 pseudo-class 兜底无冲突 — 本波内不需修改

## 阶段 5：归档
- [x] 5.1 本 tasks.md 状态 → `🟢 已完成`
- [ ] 5.2 `spec/changes/reorganize-stdlib-packages/` → `spec/archive/2026-04-25-reorganize-stdlib-packages/`
- [ ] 5.3 单次 commit + push（scope `refactor(stdlib)`）

## 备注

- **阶段 4 首次验证失败** (2026-04-25)：77_stdlib_stack_queue 报
  `undefined function Std.Collections.Stack.Push`。根因在 VM + 编译器都假设
  "namespace 独占 zpkg"，W1 让 `Std.Collections` 跨 z42.core / z42.collections
  共享时触发。不属本波 scope，拉独立变更 `vm-zpkg-dependency-loading` 解决。
- **VM change 完成后 W1 自动转绿** — 本波源文件改动未动 VM / 编译器，只需
  重新跑验证即可。
