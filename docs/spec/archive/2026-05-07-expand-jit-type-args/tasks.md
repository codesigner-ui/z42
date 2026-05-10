# Tasks: expand-jit-type-args

> 状态：🟢 已完成 | 创建：2026-05-07 | 完成：2026-05-07 | 类型：feat(vm)

## Tasks

- [x] 1.1 jit_obj_new 末尾加 `type_args_ptr: *const String, type_args_count: usize`；alloc 后 ctor 前 populate
- [x] 1.2 translate.rs `obj_new` `decl!` 加末尾两参；ObjNew 翻译时 `type_args.as_ptr() as i64 + len`
- [x] 1.3 删除 5 处 `interp_only` 标记
- [x] 1.4 dotnet build + cargo build 全绿
- [x] 1.5 test-vm.sh 300/300 全绿（interp 152 + jit 148；之前 jit 143，新增 5 个）
- [x] 1.6 docs/design/ir.md obj_new 段同步
- [x] 1.7 docs/deferred.md 更新备注
- [x] 1.8 commit + push + 归档
