# Tasks: clean up static-field-init workarounds

> 状态：🟢 已完成 | 创建：2026-05-15 | 完成：2026-05-15 | 类型：refactor

## 背景

commit `dfcd1495 fix(compiler+vm): unique __static_init__ name per source file` 已修复 z42 跨 CU `__static_init__` 函数名冲突导致 second-file static-field 初始化器被丢弃的 bug。本 spec 把 stdlib 中针对该 bug 留下的 workaround 全部回收：

- `Std.IO.Process` 用字面量 `3` 代替 `Stdio.MODE_FILE`
- `Std.Encoding.Hex` / `Std.Encoding.Base64` 用 per-call 局部变量代替 static 字段
- `Std.IO.Stdio` 头部注释提到"static field init lacking"
- `docs/design/stdlib/encoding.md` Deferred 段
- `docs/roadmap.md` Deferred Backlog Index 行

## 文件清单

| 文件 | 变更 | 说明 |
|------|------|------|
| `src/libraries/z42.io/src/Process.z42` | MODIFY | 4 处 `== 3` → `== Stdio.MODE_FILE` |
| `src/libraries/z42.io/src/Stdio.z42` | MODIFY | 删过时注释（lines 26-29） |
| `src/libraries/z42.encoding/src/Hex.z42` | MODIFY | inline `string alpha = "..."` → static `ALPHA_LOWER` / `ALPHA_UPPER` 字段 |
| `src/libraries/z42.encoding/src/Base64.z42` | MODIFY | 同 Hex |
| `docs/design/stdlib/encoding.md` | MODIFY | 删 Deferred "static field quirk" 段 |
| `docs/roadmap.md` | MODIFY | 删 Deferred Backlog Index "跨包 static field 初始化时机" 行 |

## 阶段 1: 实施

- [x] 1.1 `src/libraries/z42.io/src/Process.z42` — 4 处 `== 3` → `== Stdio.MODE_FILE`
- [x] 1.2 `src/libraries/z42.io/src/Stdio.z42` — 删过时注释 + Inherit/Pipe/Null/ToFile 工厂用 `Stdio.MODE_*` 命名常量代替字面量
- [x] 1.3 `src/libraries/z42.encoding/src/Hex.z42` — `Hex.ALPHA_LOWER` / `Hex.ALPHA_UPPER` static field + helper `_encodeWith` 去重 Encode/EncodeUpper 重复逻辑
- [x] 1.4 `src/libraries/z42.encoding/src/Base64.z42` — `Base64.ALPHA` static field
- [x] 1.5 `docs/design/stdlib/encoding.md` Decision 4 改写为正式实现 + 历史 note
- [x] 1.6 `docs/roadmap.md` Deferred Backlog Index "跨包 static field 初始化时机" 行删划线

## 阶段 2: 验证

- [x] 2.1 `./scripts/build-stdlib.sh` — 10/10 succeeded
- [x] 2.2 `./scripts/test-stdlib.sh` — 35/35 file(s) passed (10 libs)
- [x] 2.3 `dotnet test` — 1289/1289 passed

## 阶段 3: 归档

- [x] 3.1 mv `docs/spec/changes/cleanup-static-field-workarounds/` → `docs/spec/archive/2026-05-15-cleanup-static-field-workarounds/`
- [x] 3.2 commit
- [x] 3.3 push

## 实施期发现

1. **z42 不解析无修饰的同类静态字段引用**：`Hex.Encode` 内写 `ALPHA_LOWER`（同类静态字段）报 `E0401: undefined symbol 'ALPHA_LOWER'`，必须写 `Hex.ALPHA_LOWER`。Stdio / Base64 同样。和 C# 不一样 —— C# 同类内可省略类名。后续如果想 quality-of-life 改 z42 type resolver 支持 unqualified same-class static 引用，独立 spec。
2. **Hex 重复代码消除**：原来 Encode 和 EncodeUpper 完全复制粘贴 11 行循环，唯一差别 alpha。改成 `_encodeWith(bytes, alpha)` helper 后两个 Encode 入口都成 one-liner。该重复在 add-z42-encoding 时已存在，只是 inline alpha workaround 让 helper 不容易抽（每函数有独立局部变量）。本次顺手清理。

## 备注

`Stdio.z42` 的工厂方法 `Inherit() / Pipe() / Null() / ToFile()` 当前每次 `new Stdio(N, "")` 分配新实例。改成真正的 cached 静态实例（`static Stdio _inherit = new Stdio(1, "")`）虽然更优，但属于性能优化而非 workaround 回收 —— 留 follow-up。
