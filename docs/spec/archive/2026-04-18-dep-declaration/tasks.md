# Tasks: [dependencies] 解析 + 第三方依赖过滤

> 状态：🟢 已完成 | 创建：2026-04-18

- [x] ProjectManifest 解析 `[dependencies]` section → DeclaredDep/DependencySection
- [x] ScanLibsForNamespaces: 有 [dependencies] 时只扫描声明的包 + stdlib (z42.*)
- [x] BuildDepIndex: 同上过滤
- [x] stdlib 始终隐式可见，不需声明
- [x] 验证: 442 + 114 全绿 + stdlib 重编译通过
