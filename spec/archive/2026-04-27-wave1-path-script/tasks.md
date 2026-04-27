# Tasks: wave1-path-script

> 状态：🟢 已完成 | 类型：refactor | 创建：2026-04-27 | 完成：2026-04-27

**变更说明：** Wave 1.4：`Path.Join` / `GetExtension` / `GetFileName` / `GetDirectoryName` / `GetFileNameWithoutExtension` 5 个方法从 `[Native]` 迁纯脚本，删除 5 个 `__path_*` builtin。新增公开常量 `Path.Separator = '/'`。

**原因：** BCL `System.IO.Path` 全部是纯字符串扫描；Rust `std::path::Path` 同样。z42 已具备 `__str_char_at` + `__str_from_chars` + char 比较，足够实现。

**新加 API**：`Path.Separator: char`（`'/'`）—— 与 BCL `Path.DirectorySeparatorChar` 对应。

## Tasks

- [x] 1.1 重写 `src/libraries/z42.io/src/Path.z42`：5 方法去 `[Native]` + 脚本实现 + 新增 `public static char Separator = '/';`
- [x] 1.2 新增 golden test `src/runtime/tests/golden/run/16_path/`（source.z42 + expected_output.txt）覆盖 5 方法的常见场景
- [x] 2.1 `src/runtime/src/corelib/mod.rs`：删 5 行 `__path_*` dispatch + 注释
- [x] 2.2 `src/runtime/src/corelib/fs.rs`：删 5 个 `builtin_path_*` 函数
- [x] 3.1 `src/libraries/README.md` 审计表：path 行 🟡 → ✅；Wave 进度 + 总数
- [x] 4.1 `build-stdlib.sh` + `cp dist/*.zpkg → artifacts/z42/libs/`
- [x] 4.2 `regen-golden-tests.sh`、`dotnet test`、`test-vm.sh` 全绿
- [x] 5.1 commit + push + 归档

## 备注

- **L1 限定 Unix-only**：硬编码 `/` 分隔符。Windows 支持（`'\\'`）留给 L3+
- **删除 `Path.Separator` 常量计划**：z42 当前不支持静态字段访问语法 (`Class.Field`)，只支持静态方法 (`Class.Method()`)。`public static char Separator = '/';` 可声明，但 `Path.Separator` 调用编译报 `undefined symbol 'Path'`。同样问题影响 `Math.PI`/`Math.E`/`Math.Tau` —— 已在 stdlib 但暂时不可用。建议另开 spec 修编译器静态字段访问通路。本 Wave 暂不引入
- **build-stdlib.sh 静默跳过编译失败的源文件**：Path.z42 早先版本（while 单语句无大括号）触发 5 个 parser 错误，但 build-stdlib.sh 仍报 "5 succeeded, 0 failed"，并复用旧 zpkg。这是 bug，建议另开 spec 让 workspace 编译失败时立即报错。本 Wave 通过手动 `dotnet ... Path.z42` 单文件编译验证才发现
- **行为不保证 Rust `std::path::Path` 完全一致**：本变更为 z42 stdlib 自持，简化版本；golden test 16_path 是行为锁定基准
- 边界处理：
  - `Join("", b)` → `b`（empty a）
  - `Join(a, "")` → `a`（empty b）
  - `Join(a, "/abs")` → `"/abs"`（b 以 `/` 开头视为绝对路径）
  - `Join("a/", "b")` → `"a/b"`（不重复 `/`）
  - `GetFileName` 跳过末尾的 `/`
  - `GetExtension` 忽略 leading dot（`.bashrc` → `""`）
  - `GetDirectoryName` 路径中无 `/` 时返回 `""`，根 `/x` 返回 `"/"`
