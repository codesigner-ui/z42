# Tasks: add-path-separator

> 状态：🟢 已完成 | 类型：feat (stdlib API) | 创建：2026-04-27 | 完成：2026-04-27

**变更说明：** 给 `Std.IO.Path` 加 `public static char Separator = '/';` 常量，对应 BCL `Path.DirectorySeparatorChar`。

**原因：** Wave 1.4 (wave1-path-script) 当时计划添加但被 fix-static-field-access 之前的 bug 阻塞。现在 `Math.PI` 已能正常访问，同样的机制让 `Path.Separator` 也能用了。

## Tasks

- [x] 1.1 `src/libraries/z42.io/src/Path.z42`：取消 TODO，加 `public static char Separator = '/';`
- [x] 2.1 更新 `src/runtime/tests/golden/run/16_path/source.z42` + `expected_output.txt`：在最前面加 `Console.WriteLine(Path.Separator);` 输出 `/`
- [x] 3.1 build-stdlib + regen golden + dotnet test + test-vm 全绿
- [x] 4.1 commit + push + 归档

## 备注

- 当前硬编码 `'/'`（Unix 语义），Windows 支持留 L3+
- L3+ 真要支持 Windows 时，可走 platform HAL 注入；常量声明可以保留，但值由 platform 注入而非硬编码
