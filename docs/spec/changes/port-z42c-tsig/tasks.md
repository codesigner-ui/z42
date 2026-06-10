# Tasks: port-z42c-tsig

> 状态：⚪ DRAFT 待审批 | 创建：2026-06-10 | 子系统锁：z42c
> **未经 User 批准不动代码（Spec-First gate）。**

## 进度概览
- [ ] TS-1 ExportedTypes.z42 模型（ir）
- [ ] TS-2 ExportedTypeExtractor.z42（semantics，CU 声明序）+ 单测
- [ ] TS-3 ZpkgWriterZ TSIG/IMPL 两段 + intern 时机（deps 后、逐模块前）+ 单测
- [ ] TS-4 driver build 接线（每文件 Extract → ZpkgFileZ.ExportedModules）
- [ ] TS-5 xtask e2e 全文件 byte-compare（buildapp + demo.minimal ×2 工程）+ gate 全绿 + 文档（README×2 + self-hosting.md）+ commit
