# Proposal: Marshal `Value::Str` to `*const c_char` directly (C8)

## Why

C6/C7 让 z42 用户代码能用 `[Native(lib=, type=, entry=)]` 调 native 函数，但**字符串参数仍需借助 `pinned` 块**：

```z42
// 今天必须写的样子
pinned p = s {
    c_strlen(p.ptr, p.len);
}
```

而很多 libc 风格 native 函数只接受 `const char*`（NUL-terminated），用户应该能直接写：

```z42
[Native(lib="numz42", type="Counter", entry="strlen")]
public static extern long Strlen(string s);

void Main() {
    long n = Strlen("hello");   // 一行；无需 pinned
}
```

C8 把 `(Value::Str, SigType::CStr)` 映射加入 marshal 路径——marshal 时构造一次 NUL-terminated 临时 `CString`，把指针传给 libffi，调用结束后释放。**只覆盖只读借用语义**：调 native 的瞬间字符串不可变，与 z42 String 的 `Cow`-friendly 语义对齐。

> 写入 / 输出参数（`*mut char`）和 owned-string 返回（native 分配字符串再交回 z42）属于更复杂的 marshal 协议，留作后续 spec。本 spec 只解决最常见的"借出只读 C 字符串"用例。

## What Changes

- **Marshal arena**：`marshal::Arena` 新类型，承载一次 CallNative 调用期间的临时 CString / 其他 owned 数据
- **`marshal::value_to_z42`** 签名扩展第三个参数 `&mut Arena`；`(Value::Str, SigType::CStr)` 路径分配 CString 进 arena，返回 `Z42Value::NATIVEPTR(arena.last().as_ptr())`
- **`exec_instr.rs::CallNative`** 在 marshal/dispatch 之间持有 arena，dispatch 完成后 drop 释放
- **PoC 扩展**：`numz42-c` 加 `counter_strlen(*const char) -> i64` 方法
- **e2e 测试**：扩展 `tests/native_interop_e2e.rs` 加一个测试，z42 源码直接传 string 调 native strlen
- 可选：扩展 `(Value::Str, SigType::Ptr)` 同样路径（让用户在签名声明 `*const u8` 时也能直接传 string）

## Scope

| 文件 | 变更 | 说明 |
|------|------|------|
| `src/runtime/src/native/marshal.rs` | MODIFY | `pub struct Arena`；`value_to_z42` 签名扩展；新增 (Str, CStr) / (Str, Ptr) 分支 |
| `src/runtime/src/native/marshal_tests.rs` | MODIFY | 加 Str 分支单测 |
| `src/runtime/src/interp/exec_instr.rs` | MODIFY | CallNative 分支创建 Arena 并 drop |
| `src/runtime/tests/data/numz42-c/numz42.c` | MODIFY | 加 `counter_strlen(const char*) -> i64` 方法 + method 表条目 |
| `src/runtime/tests/native_interop_e2e.rs` | MODIFY | 加 `strlen_via_str_marshal` 测试，验证 IR 直传 String 到 CStr 参数 |
| `src/runtime/tests/data/z42_native_e2e/source.z42` | MODIFY | 在 NumZ42 类加 Strlen 声明 + Main 调用一次（可选；保持 tests 简洁）|
| `docs/design/interop.md` | MODIFY | §6 marshal 表加 (Str, CStr) 行；记录 arena 协议 |

## Out of Scope

- **`*mut char` / 输出参数**：native 写入 z42 String backing 是不安全的（z42 String 是 immutable）；如果需要请用 `pinned p = s { ... }`
- **owned-string 返回**：native 分配 + 交回 z42 需要协商释放 ABI（free fn pointer），独立 spec
- **`Value::Array<u8>` → `*const u8`**：留给 byte buffer Value variant spec
- **`Value::Object` marshal**：跨 FFI 传递 GC 引用需 retain/release 协议，独立 spec

## Open Questions

- [ ] **Q1**：z42 String 内含 interior NUL 怎么处理？
  - 倾向：marshal 期 CString::new 失败 → bail Z0908（"interior NUL"）；不静默截断
- [ ] **Q2**：Arena 是否复用跨调用？
  - 倾向：**一次 CallNative 一个 Arena**（栈上 drop）。简单，零生命周期复杂度
