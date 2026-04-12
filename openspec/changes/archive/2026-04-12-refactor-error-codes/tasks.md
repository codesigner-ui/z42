# Tasks: refactor-error-codes

**变更说明：** 将所有诊断码前缀从 `Z` 改为 `E`（Z0101 → E0101），统一错误码体系。
**原因：** roadmap 规定错误码格式为 `E####`；Driver 的 explain 命令示例已用 `E0001`，与实际码值 `Z####` 不一致。
**文档影响：** 无（错误码格式未记录在 docs/design/ 中，仅在 DiagnosticCodes 注释里）

- [x] 1.1 `Diagnostic.cs`：将 DiagnosticCodes 13 个常量值 Z0xxx → E0xxx，更新文档注释
- [x] 1.2 `DiagnosticCatalog.cs`：更新注释中的示例码
- [x] 1.3 20 个 `errors/*/expected_error.txt` golden test 文件：Z0xxx → E0xxx
- [x] 1.4 `dotnet build && dotnet test` — 全绿验证（381 passed）
