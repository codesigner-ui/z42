# Tasks: remove-host-toplevel (consolidate S5) — 🟡 进行中

**变更说明：** consolidate-platform-into-workload S5——移除已掏空的 `src/toolchain/host/` 顶层（仅剩 tombstone README + 废弃 .gitignore），文档收口到 workload/runtime 新结构。
**原因：** embed(S1) + platforms(S3') 已迁出，host/ 无剩余内容；删除完成 host 解散弧。Tier1 C ABI + 头始终在 runtime（不动）。
**文档影响：** src/toolchain/README.md（删 host 行 + 列表）、hello_rust main.rs 陈旧注释。
**锁：** `toolchain`。

- [ ] 1.1 `git rm -r src/toolchain/host/`（tombstone README + .gitignore）
- [ ] 1.2 src/toolchain/README.md：删 host/ 行；line 22 列表去 host；line 11 launcher 行去 apphost 命令引用（apphost 已改 export desktop）
- [ ] 1.3 examples/embedding/hello_rust/src/main.rs：陈旧注释 `src/toolchain/host/examples/hello_rust`→`examples/embedding/hello_rust`
- [ ] 1.4 consolidate-platform-into-workload/tasks.md：标 S1/S2/S3'/S5 done，B 余下
- [ ] 1.5 GREEN：全仓零 live `toolchain/host/` 引用；dotnet/cargo 不受影响（host/ 无代码）
- [ ] 1.6 COMMIT + 归档，释放 toolchain 锁

## 备注
- runtime/src/host/ + runtime/include/（Tier1 C ABI）是独立的，**不**随此变更动。
- roadmap.md:406 对 host/examples 的历史提及保留（deferred 条目的历史叙述）。
