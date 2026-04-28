# include/ — Public C headers

## 职责

z42 runtime 对外暴露的 C ABI 公开头文件。供 host 应用 / native 库通过 `-I` include 路径引用。

## 核心文件

| 文件 | 职责 |
|------|------|
| `z42_abi.h` | Tier 1 native interop ABI（`Z42TypeDescriptor_v1` + `z42_*` 函数声明）。Rust 镜像见 [`crates/z42-abi/`](../crates/z42-abi/) |

## 入口点

`#include "z42_abi.h"`：

- 类型：`Z42TypeDescriptor_v1`、`Z42MethodDesc`、`Z42FieldDesc`、`Z42TraitImpl`
- 常量：`Z42_ABI_VERSION` + `Z42_TYPE_FLAG_*` / `Z42_METHOD_FLAG_*` / `Z42_FIELD_FLAG_*`
- 函数：`z42_register_type` / `z42_resolve_type` / `z42_invoke` / `z42_invoke_method` / `z42_last_error`

## ABI 演进

`abi_version` 字段永远在偏移 0；新字段只追加，major 版本升级 = 显式 break。详见 `docs/design/interop.md` §3.3。
