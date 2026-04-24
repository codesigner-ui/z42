# Tasks: refactor-package-format

> 状态：🟢 已完成 | 创建：2026-04-04

**变更说明：** 将 `.zlib` 重命名为 `.zbin`（统一 exe/lib 打包格式）；在 compilation.md 中明确 Strategy D（zbc 生成粒度）架构。
**原因：** `.zlib` 语义只暗示库，但打包格式同时用于 exe 和 lib；metadata 中的 `kind` 字段已足以区分，后缀应中性化。
**文档影响：** `docs/design/compilation.md`、`docs/design/project.md`

---

## C# 编译器

- [x] 1.1 `z42.IR/PackageTypes.cs` — 重命名 `ZlibFile/ZlibExport/ZlibDep`，更新 JSON 属性名
- [x] 1.2 `z42.IR/ProjectTypes.cs` — `EmitKind.Zlib` → `EmitKind.Zbin`
- [x] 1.3 `z42.Driver/BuildCommand.cs` — `case "zlib"` → `case "zbin"`，更新类型引用
- [x] 1.4 `z42.Build/ProjectManifest.cs` — lib 默认 emit `"zlib"` → `"zbin"`
- [x] 1.5 `z42.Tests/GoldenTests.cs` — 更新 `ZlibFile` 引用 + `.zlib` 字符串
- [x] 1.6 `z42.Tests/ProjectManifestTests.cs` — 更新 `"zlib"` 断言 → `"zbin"`

## Rust 运行时

- [x] 2.1 `metadata/formats.rs` — 重命名类型 + 属性 + `ZLIB_MAGIC` → `ZBIN_MAGIC`
- [x] 2.2 `metadata/mod.rs` — 更新 re-export
- [x] 2.3 `metadata/loader.rs` — 更新扩展名检查、函数名、类型引用
- [x] 2.4 `project.rs` / `main.rs` / `vm.rs` — 更新注释

## 文档

- [x] 3.1 `docs/design/compilation.md` — 全文替换 `.zlib` → `.zbin`；新增 Strategy D（zbc 粒度）章节
- [x] 3.2 `docs/design/project.md` — emit 格式表更新

## 验证

- [x] 4.1 `dotnet build src/compiler/z42.slnx` — 无编译错误
- [x] 4.2 `cargo build --manifest-path src/runtime/Cargo.toml` — 无编译错误
- [x] 4.3 `dotnet test src/compiler/z42.Tests/z42.Tests.csproj` — 全绿

## 备注
