# Tasks: add-reflection-parameter-names

> 状态：🟢 已完成 | 创建：2026-06-11 | 完成：2026-06-11 | 类型：feat（runtime-only，无格式 bump）

**变更说明：** `ParameterInfo.Name` 返回真实源参数名（来自 debug symbols），无符号时回落 `arg{n}`。
**原因：** 此前恒 `arg{n}`；参数名已存在于 Function 的 DBUG local-vars（参数入口占寄存器 `0..param_count`，`reg==参数索引`），运行期读取即可。
**文档影响：** `docs/design/language/reflection.md`（`reflection-future-parameter-names` Deferred → 落地）。

## 实施
- [x] 1.1 `reflection.rs` `resolve_func_sig`：返回值加 `param_names: Vec<String>`（从 `Function.local_vars()` 按 `reg` 映射；缺省空串）
- [x] 1.2 `build_method_info`：用 param_names[i]（非空）否则 `arg{pos}`；同步 2 处属性派生处的 sig 解构（+1 元素）
- [x] 1.3 golden e2e `src/tests/types/parameter_names.z42`（`Add(int alpha,int beta)` → Name alpha/beta + Position 0/1）
- [x] 1.4 docs reflection.md 同步

## 验证
- [x] dotnet GoldenTests **1560/1560**（新 parameter_names + 回归全绿；无现存测试断言 arg{n} 参数名）
- [x] cargo test --lib **764 + 21**
- golden-compile 路径含 DBUG → 真实名生效；strip 构建回落 `arg{n}`（设计预期）。无格式 bump。
