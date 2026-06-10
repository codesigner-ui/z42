# Tasks: port-z42c-import

> 状态：⚪ DRAFT 待审批 | 创建：2026-06-10 | 子系统锁：z42c
> **未经 User 批准不动代码（Spec-First gate）。**

## 进度概览
- [ ] CI-1 ZpkgReader 子集（META/NSPC/SIGS/TSIG；不解码 FUNC 体）+ 自产 zpkg 往返单测
- [ ] CI-2 DependencyIndex（static/instance 键 + 歧义剔除）+ DepScan（prelude-first 扫描 + nsMap）+ 单测
- [ ] CI-3 ImportedSymbolLoader Phase1/2 子集（类骨架→成员填充 + 自由函数 + TypeResolver 子集 + usings∪prelude 过滤）+ 单测
- [ ] CI-4 接线：SymbolTable 合并 / ExprEmitter 静态 DepIndex 命中 + TrackDepNamespace / DEPS 真实条目 / driver 组装
- [ ] CI-5 e2e hello-stdlib（直跑 + 全文件 byte-compare 第 3 工程）+ gate 全绿 + 文档（README×4 + self-hosting.md）+ commit
