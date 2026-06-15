# Tasks: simplify-examples-singlefile

> 状态：🟢 已完成 | 创建：2026-06-15 | 完成：2026-06-15 | 类型：refactor + docs

**变更说明：** 简化 examples/ 单文件 showcase —— 删 3 个冗余单文件 `.z42.toml`
(lambda/local_fn/closure_capture,均 0 引用、是 hello.z42.toml 的样板复制) + 删
不可编译的 aspirational 设计稿 `async.z42`(L3 async,设计本体在 concurrency.md)。

**原因：** showcase 目录应只放"可跑且不重复"的示例。3 个 toml 是同款清单格式的
重复样板;async.z42 编译不过会误导新人。

**文档影响：** examples/README.md 单文件表去掉 async 行。

**子系统占用：** examples/ + docs —— 均不上锁。

## Scope(允许改动的文件)

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `examples/lambda.z42.toml` | DELETE | 0 引用,hello.z42.toml 已演示同款清单格式 |
| `examples/local_fn.z42.toml` | DELETE | 同上 |
| `examples/closure_capture.z42.toml` | DELETE | 同上 |
| `examples/async.z42` | DELETE | 不可编译的 L3 设计稿;设计在 docs/design/runtime/concurrency.md |
| `examples/README.md` | MODIFY | 单文件表删 async 行 |

**只读引用：**
- `examples/hello.z42.toml` — 保留为唯一单文件清单格式演示
- `examples/{lambda,local_fn,closure_capture}.z42` — 保留(可跑 demo,直接 --emit zbc,不需 toml)

## Out of Scope
- 删/合并任何可跑的单文件 demo
- generics.z42(🚧 部分可跑,留)

## 任务
- [x] 1.1 删 3 个孤儿 .z42.toml + async.z42
- [x] 1.2 README 单文件表删 async 行
- [x] 1.3 验证:删的是 0 引用独立 showcase(无 build/test 编译它们)→ 不影响任何测试;残留引用仅在 docs/spec/archive(历史,保留)
- [x] 1.4 归档 + commit
