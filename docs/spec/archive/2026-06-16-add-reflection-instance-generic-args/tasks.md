# Tasks: 实例泛型 args

> 状态：🟢 已完成 | 创建：2026-06-16 | 完成：2026-06-16 | 类型：vm（新反射行为，纯运行期无格式 bump）

## 阶段 1: runtime
- [x] 1.1 `object.rs`：`builtin_obj_get_type` Object 分支——type_args 非空 → `make_constructed_type(td.name, args)`

## 阶段 2: 测试与验证
- [x] 2.1 golden `src/tests/types/instance_generic_args.z42`（interp+jit；实例 args + 与 typeof 一致 + 取定义 + 非泛型回归）
- [x] 2.2 cargo build（debug+release）+ cargo test --lib 全绿
- [x] 2.3 dotnet GoldenTests 全绿
- [x] 2.4 xtask vm 366/0（interp 187+jit 179）/ cross-zpkg 2/0 / stdlib 272·22

## 阶段 3: docs + 归档
- [x] 3.1 `reflection.md`：构造型泛型节补实例侧 + Deferred（标 instance-generic-args 落地）
- [x] 3.2 ACTIVE.md 释放 runtime 锁 + 归档

## 备注
- 纯运行期，无格式 bump → 无 xtask version dance / fixture regen / z42c 同步。
- 嵌套泛型实例（`new Box<Map<K,V>>()`）arg 仍扁平名串，递归延后（同 #1 nested-generic-args）。
