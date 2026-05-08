# Tasks: Convert Remaining Test Cases to Assert

> 状态：🟢 已完成 | 完成：2026-05-08 | 创建：2026-05-08
> 类型：refactor（最小化模式）

**变更说明：** 把剩余 24 个仍有 expected_output.txt 但适合 assert-only 的 case 转换。模式：`Console.WriteLine(expr) → Assert.Equal(<literal>, expr)`；catch-flow 用 `bool flag + Assert.True`；删 `using Std.IO`；删 `expected_output.txt`；如无其他 sidecar 则扁平化（refs/21* 有 interp_only sidecar → 保 dir）。

**原因：** 调研报告（2026-05-08）识别出 24 个"短 expected + 无 `$"..."` 插值" case，是机械可转的最后一批。转完后批 3 范围内的 sentinel + 模糊计算清零。

**文档影响：** 无（约定已建立，README 不需更新）。

## 阶段 1: 哨兵清理（2 个）

- [x] 1.1 `src/tests/basic/46_object_protocol/`：仅删 1-byte 残留 expected_output.txt + flatten
- [x] 1.2 `src/tests/classes/42_static_fields/`：删 Console + Std.IO + expected + flatten

## 阶段 2: 简单 println→Assert（15 个，扁平化）

每个：`Console.WriteLine(expr) → Assert.Equal(<expected literal>, expr)` × N，删 `using Std.IO`，删 expected，flatten。

> 注：原计划 16 个；侦查发现 `basic/25_zlib_format/` 含 `emit_format.txt = zlib` sidecar（golden 验证 zlib emit 格式），保 dir 模式不动 → 不在本批范围。

- [x] 2.1 src/tests/classes/23_access_control/
- [x] 2.2 src/tests/classes/43_static_method_cross_call/
- [x] 2.3 src/tests/closures/closure_l3_mono/
- [x] 2.4 src/tests/generics/70_generic_constraints/
- [x] 2.5 src/tests/generics/71_generic_baseclass/
- [x] 2.6 src/tests/generics/72_generic_bare_typeparam/
- [x] 2.7 src/tests/generics/84_generic_enum_constraint/
- [x] 2.8 src/tests/generics/86_extern_impl_user_class/
- [x] 2.9 src/tests/operators/default_generic_param/
- [x] 2.10 src/tests/operators/default_generic_param_field_init/
- [x] 2.11 src/tests/operators/default_generic_param_pair/
- [x] 2.12 src/tests/types/33_typeof/
- [x] 2.13 src/tests/types/array_cast_back/
- [x] 2.14 src/tests/types/array_is_instance/（if/else 分支替换为 Assert.True）
- [x] 2.15 src/tests/types/object_get_type/

## 阶段 3: catch-flow 改写（2 个，扁平化）

`bool caught = false; try {...} catch { caught = true; } Assert.True(caught);` 模式。

- [x] 3.1 src/tests/exceptions/67_stack_trace/
- [x] 3.2 src/tests/exceptions/catch_wildcard_compat/

## 阶段 4: refs/21* 转 assert（4 个，保 dir）

有 interp_only sidecar → 保 dir 模式，仅删 expected_output + Console。

- [x] 4.1 src/tests/refs/21_ref_local/
- [x] 4.2 src/tests/refs/21b_out_var/
- [x] 4.3 src/tests/refs/21c_in_param/
- [x] 4.4 src/tests/refs/21d_ref_nested/

## 阶段 5: 验证

- [x] 5.1 `./scripts/regen-golden-tests.sh --no-stdlib` 全绿
- [x] 5.2 `./scripts/test-vm.sh` interp + jit 全绿
- [x] 5.3 `dotnet test src/compiler/z42.Tests/z42.Tests.csproj` 全绿

## Scope

| 文件路径 | 类型 | 说明 |
|---|---|---|
| `src/tests/basic/46_object_protocol/` → `src/tests/basic/46_object_protocol.z42` | RENAME + DELETE expected | |
| `src/tests/classes/42_static_fields/` → `src/tests/classes/42_static_fields.z42` | RENAME + MODIFY src + DELETE expected | |
| 17 个阶段 2 case dir → flat .z42 | RENAME + MODIFY src + DELETE expected | |
| 2 个阶段 3 case dir → flat .z42 | RENAME + MODIFY src + DELETE expected | catch-flow 改写 |
| 4 个 refs/21* `expected_output.txt` | DELETE | 保 dir |
| 4 个 refs/21* `source.z42` | MODIFY | 删 Console + Std.IO |

**只读引用：** scripts/test-vm.sh / GoldenTests.cs（dual-mode 已就位，无需改）

## 备注

无新 spec 类问题预期；完全是 batch 1+2 模式的延续。
