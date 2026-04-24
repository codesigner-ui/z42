# src/libraries — z42 标准库源码

## 职责

z42 标准库的 `.z42` 源文件。每个库是独立的 z42 包，通过 `build-stdlib.sh` 编译为 `.zpkg` 产物后供用户程序引用。

## 库列表

| 目录 | 包名 | 内容 |
|------|------|------|
| `z42.core/` | `z42.core` | 核心类型：`Object`、`String`、`Type`、`Assert`、`Convert`、核心接口；`Collections/` 子目录内含基础泛型集合 `List<T>` / `Dictionary<K,V>` |
| `z42.collections/` | `z42.collections` | 次级集合类型：`Queue`、`Stack`（未来 `LinkedList` / `SortedDictionary` / `PriorityQueue`） |
| `z42.io/` | `z42.io` | IO 类型：`Console`、`File`、`Path`、`Environment` |
| `z42.math/` | `z42.math` | 数学函数：`Math` |
| `z42.text/` | `z42.text` | 文本处理：`StringBuilder`、`Regex` |

## 实现规范（必须遵守）

### 1. Script-First：优先脚本实现

**尽可能把逻辑放到 `.z42` 脚本实现，减少 VM 侧的 extern / intrinsic。**

- 新增方法默认用 `.z42` 脚本实现，即使暂时性能不优
- 性能问题延后优化（profile → JIT 优化 → 必要时再下沉为 intrinsic）
- 现存 extern 逐步评估下沉：若能用"更小的 intrinsic 核 + 脚本组合"表达，
  优先迁移。例：`Contains` / `IndexOf` / `Trim` / `Substring` 已迁脚本；
  `String.Length` 等仍是 extern 的方法，在更基础原语（如 `char[]` 视图）
  就绪后也应评估是否能下沉，不把"性能担忧"当作保留 extern 的默认理由。
- 只有真正无法用脚本表达的原语才保留 extern：内存布局 / 原子指令 / 底层
  分配 / 与 VM ABI 绑定的协议方法（`Equals` / `GetHashCode` / `ToString`）。

### 2. VM 接口集中在 z42.core

**VM 提供的 extern / intrinsic 接口只能出现在 `z42.core`。**

其他包（`z42.collections` / `z42.math` / `z42.text` / ...）**一律不允许**
声明任何 VM extern，必须通过调用 `z42.core` 的公开 API 间接使用 VM 能力。

**唯一例外：`z42.io`。**
`z42.io` 依赖**另一个** native 库（文件系统 / 控制台 / 环境变量等操作系统
能力），该 native 接口与 "VM corelib intrinsic" 是两套独立通道——它不占
用 VM intrinsic 预算，走的是 host function (FFI) 机制。除 `z42.io` 外，
其他包禁止引入任何 native 依赖。

> 目的：保持 VM 表面最小、可审计；stdlib 绝大部分逻辑由脚本驱动，便于
> 自举、调试和演进。新增 VM extern 视同新增语言原语，需走 vm 类型完整
> 变更流程。

---

## 构建

```bash
./scripts/build-stdlib.sh           # debug
./scripts/build-stdlib.sh release   # release
```

产物输出到 `artifacts/z42/libs/*.zpkg`（已在 `.gitignore` 中，不纳入版本控制）。

## 修改后

修改任意 `.z42` 源文件后必须重新运行 `build-stdlib.sh` 更新 zpkg 产物。
