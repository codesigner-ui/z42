# Tasks: 将 String 方法迁移到脚本实现

> 状态：🟡 进行中 | 创建：2026-04-23 | 实施：2026-04-24

## 进度概览
- [ ] 阶段 1: VM 侧 builtin 调整
- [ ] 阶段 2: 标准库脚本重写
- [ ] 阶段 3: 测试与验证
- [ ] 阶段 4: 文档与归档

## 阶段 1: VM 侧 builtin 调整

- [ ] 1.1 在 `src/runtime/src/corelib/string.rs` 新增 `builtin_str_char_at` / `builtin_str_from_chars`
- [ ] 1.2 新建 `src/runtime/src/corelib/char.rs`（或就地），实现 `builtin_char_is_whitespace` / `builtin_char_to_lower` / `builtin_char_to_upper`
- [ ] 1.3 `src/runtime/src/corelib/mod.rs` 的 dispatch 注册表中：
  - 新增 `__str_char_at` / `__str_from_chars` / `__char_is_whitespace` / `__char_to_lower` / `__char_to_upper`
  - 移除 `__str_contains` / `__str_starts_with` / `__str_ends_with` / `__str_index_of` / `__str_replace` / `__str_to_lower` / `__str_to_upper` / `__str_trim` / `__str_trim_start` / `__str_trim_end` / `__str_substring` / `__str_is_null_or_empty` / `__str_is_null_or_whitespace`
- [ ] 1.4 从 `src/runtime/src/corelib/string.rs` 删除对应的 13 条 `builtin_str_*` 函数 + 单元测试
- [ ] 1.5 `cargo build` + `cargo test` 确认 Rust 侧编译通过

## 阶段 2: 标准库脚本重写

- [ ] 2.1 扩展 `src/libraries/z42.core/src/Char.z42`：新增 `IsWhiteSpace` / `ToLower` / `ToUpper` 三个 extern
- [ ] 2.2 重写 `src/libraries/z42.core/src/String.z42`：
  - 保留 extern：`Length` / `Equals`(×2) / `CompareTo` / `GetHashCode` / `ToString` / `Split` / `Join`(×2) / `Concat`(×2) / `Format`(×2)
  - 新增 extern：`CharAt(int)` / `static FromChars(char[])`
  - 脚本方法：`IsEmpty`（已有） / `Contains` / `StartsWith` / `EndsWith` / `IndexOf` / `Replace` / `Substring`(×2) / `ToLower` / `ToUpper` / `Trim` / `TrimStart` / `TrimEnd` / `static IsNullOrEmpty` / `static IsNullOrWhiteSpace`
  - 移除：原有 14 条 `[Native]` extern 属性
- [ ] 2.3 运行 `./scripts/build-stdlib.sh` 重编译 zpkg 产物
- [ ] 2.4 `Replace` 空串 `old=""` 边界：`throw new Exception("Replace: oldValue cannot be empty")`（对齐 C# ArgumentException 语义，用 z42.core 已有的 Exception）
- [ ] 2.5 `Replace` 采用两遍扫描 + `char[]` 分配（不引入 List<T>，保持 z42.core 零依赖）

## 阶段 3: 测试与验证

- [ ] 3.1 新建 `src/runtime/tests/golden/run/88_string_script/` 端到端 golden，覆盖 spec 中每个 scenario
- [ ] 3.2 检查现有 golden（`examples/async.z42` 等使用 string 方法的）仍然通过
- [ ] 3.3 `src/compiler/z42.Tests/` 若有 string builtin 专项测试，调整为脚本方法形态
- [ ] 3.4 `dotnet build src/compiler/z42.slnx` 无错误
- [ ] 3.5 `cargo build --manifest-path src/runtime/Cargo.toml` 无错误
- [ ] 3.6 `dotnet test src/compiler/z42.Tests/z42.Tests.csproj` 100% 通过
- [ ] 3.7 `./scripts/test-vm.sh` 100% 通过（interp + jit 两模式）
- [ ] 3.8 spec scenarios 逐条覆盖确认（制作 Scenario → 测试位置对照表）

## 阶段 4: 文档与归档

- [ ] 4.1 更新 `src/libraries/z42.core/README.md`：反映 String.z42 extern 缩减 + 新增 Char 方法
- [ ] 4.2 更新 `docs/roadmap.md`：如有 "extern 预算削减" 进度项或 Script-First 推进追踪
- [ ] 4.3 更新 `docs/features.md`（若有关于 "stdlib extern 预算" 设计决策需记录）
- [ ] 4.4 若 Substring / IndexOf 语义统一到 char 需要明文记录，更新 `docs/design/language-overview.md` 或新建 `docs/design/stdlib-string.md`
- [ ] 4.5 归档：`spec/changes/simplify-string-stdlib/` → `spec/archive/2026-04-23-simplify-string-stdlib/`
- [ ] 4.6 自动提交：`git add src/ docs/ .claude/ spec/ *.md && git commit && git push`

## 备注

- **不引入新语法**：`CharAt(i)` 替代 `s[i]`；C-style for 循环代替 `0..n` range
- **ASCII casing**：`ToLower` / `ToUpper` 暂按 ASCII 规则；Unicode casing 留待独立 Phase
- **O(n·m) 性能**：`IndexOf` / `Contains` 朴素实现，Phase 1 可接受
- **Replace 边界**：`old=""` 抛 `throw new Exception(...)`（对齐 C# 语义；z42.core 无 ArgumentException 类型）
- **Replace 实现**：两遍扫描 + `char[]`。不引入 List<T>（List 在 z42.collections 包、依赖 z42.core，反向无法 import）
