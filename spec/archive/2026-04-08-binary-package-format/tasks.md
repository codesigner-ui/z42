# Tasks: binary-package-format

> 状态：🟢 已完成 | 创建：2026-04-07 | 完成：2026-04-08

**变更说明：** zbc 升级到 v0.4（Section Directory + is_static in SIGS），zpkg 新建 v0.1 binary format（ZPK_MAGIC + unified STRS + 跨模块字符串复用）。Runtime 只读 binary，JSON 仅用于 disasm 调试输出。

**文档影响：** docs/design/ir.md（已更新）

## 进度概览
- [x] 阶段 1: C# 编译器端
- [x] 阶段 2: Rust 运行时端
- [x] 阶段 3: 测试更新
- [x] 阶段 4: 验证 + 文档

## 阶段 1: C# 编译器端

- [x] 1.1 ZbcWriter.cs: 版本升 minor=4，添加 Section Directory，SIGS 添加 is_static 字节
- [x] 1.2 ZbcReader.cs: 按 Directory 随机访问；读 is_static (minor >= 4)
- [x] 1.3 ZpkgWriter.cs: 新建，实现 WritePacked()/WriteIndexed()；unified STRS，SIGS 全局，MODS 内嵌 FUNC+TYPE body
- [x] 1.4 ZpkgReader.cs: 新建，ReadModules()/ReadNamespaces()/ReadMeta()，ReadSigsSection 含 is_static
- [x] 1.5 ZpkgBuilder.cs: WriteZpkg() 调用 ZpkgWriter
- [x] 1.6 BuildCommand.cs: StdlibIndex + namespace 扫描改用 ZpkgReader 读 binary
- [x] 1.7 Program.cs: disasm 支持 .zpkg 输入

## 阶段 2: Rust 运行时端

- [x] 2.1 zbc_reader.rs: 新建全量 binary decoder（Cursor, read_directory, read_strs, read_sigs, read_func, read_zpkg_*）
- [x] 2.2 loader.rs: binary-only，移除 JSON fallback
- [x] 2.3 loader_tests.rs: 更新使用 binary artifact

## 阶段 3: 测试更新

- [x] 3.1 GoldenTests.cs: BuildIndexFromDir 改 ZpkgReader
- [x] 3.2 IrGenTests.cs: BuildStdlibIdxFromDir 改 ZpkgReader
- [x] 3.3 regen-golden-tests.sh: --emit zbc；所有 45 golden tests 全部重生成

## 阶段 4: 验证 + 文档

- [x] 4.1 dotnet build && cargo build — 无错误
- [x] 4.2 dotnet test — 381 passed
- [x] 4.3 build-stdlib.sh — 5 succeeded
- [x] 4.4 test-vm.sh interp — 43 passed, 0 failed
- [x] 4.5 docs/design/ir.md — zbc v0.4 + SIGS is_static + zpkg v0.1 sections 全部更新
