# Tasks: Str → CStr marshal (C8)

> 状态：🟢 已完成 | 完成：2026-04-29 | 创建：2026-04-29

## 阶段 1: marshal Arena + Str/CStr 分支

- [x] 1.1 `src/runtime/src/native/marshal.rs`：加 `pub struct Arena { cstrings: Vec<CString> }` + `impl Arena { fn new() }`
- [x] 1.2 `value_to_z42` 签名扩展第三个参数 `&mut Arena`
- [x] 1.3 加 (Value::Str, SigType::CStr) / (Value::Str, SigType::Ptr) 分支：CString::new + push to arena + 返回 NATIVEPTR
- [x] 1.4 interior NUL → Err("Z0908: interior NUL in string passed to native function")

## 阶段 2: CallNative dispatch 改造

- [x] 2.1 `src/runtime/src/interp/exec_instr.rs::CallNative` 分支：
  - 创建 `let mut arena = marshal::Arena::new();`
  - 透传到每个 `value_to_z42(...)` 调用
  - dispatch::call 之后 arena 作用域结束自动 drop

## 阶段 3: 现有 marshal 调用方更新

- [x] 3.1 grep 全仓 `value_to_z42(` 找其他调用方；都加 `&mut arena` 参数
  - 预期至少 marshal_tests.rs 的几个测试 + dispatch 调用点

## 阶段 4: numz42-c PoC 加 strlen

- [x] 4.1 `tests/data/numz42-c/numz42.c`：
  - 加 `static int64_t counter_strlen(const char* s)` 函数
  - 加进 `COUNTER_METHODS` 数组（signature `"(*const u8) -> i64"`，Z42_METHOD_FLAG_STATIC）

## 阶段 5: 单元测试 + e2e

- [x] 5.1 `marshal_tests.rs`：
  - `str_to_cstr_round_trip` —— ASCII string 到 CStr，从 ptr 反向读出验证内容 + NUL 终止
  - `str_with_interior_nul_z0908` —— 含 \0 的字符串报 Z0908
  - `arena_drops_release_cstrings` —— Arena drop 后 CString 释放（用计数 helper 验证）
- [x] 5.2 `tests/native_interop_e2e.rs`：
  - `e2e_str_marshal_via_strlen` —— 手工 IR 含 ConstStr + CallNative numz42::Counter::strlen + Ret；assert == 5

## 阶段 6: 文档 + GREEN + 归档

- [x] 6.1 `docs/design/interop.md` §6 marshal 表加 (Str, CStr) 行；§6.x 描述 Arena 协议
- [x] 6.2 `docs/design/error-codes.md` Z0908 加 "interior NUL in marshal" 条目
- [x] 6.3 `docs/roadmap.md` 加 C8 行 ✅
- [x] 6.4 全绿验证（cargo workspace + dotnet test + test-vm.sh）
- [x] 6.5 归档 + commit + push
