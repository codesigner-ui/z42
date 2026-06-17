# Tasks: dry-ci-release-index  🟢 已完成

**变更说明：** 抽 `release.yml`（tagged）与 `ci.yml` publish-nightly（rolling）重复的 release 资产组装逻辑到
共享脚本 `scripts/release/*.sh`，由 label 参数化（`<ver>` vs `nightly`，归档名统一 `z42-<kind>-<LABEL>-<rid>`）。
**原因：** 两 workflow 的 `Assemble single desktop workload` + `Generate release-index.json` 近乎相同；本会话
每个 workload 改动都要 hand-sync 两处——正是早些 CI 变红的同类漂移源。refactor，**输出字节不变**。
**文档影响：** 无（CI 内部重构；行为/产物不变）。

- [x] 1.1 NEW `scripts/release/assemble-desktop-workload.sh <LABEL> [<dist>]`：合并 4 RID 的 desktop workload 片为单 `z42-workload-<LABEL>-desktop.tar.gz`（移除中间件）
- [x] 1.2 NEW `scripts/release/gen-release-index.sh <LABEL> <dist> <channel> <tag> <version>`：从 `<dist>/SHA256SUMS` 读 sha → 产 release-index.json（runtimes + workloads，含 desktop 单包）
- [x] 1.3 release.yml：`Assemble single desktop workload` + `Generate release-index.json` 改调脚本（label=`$v`）
- [x] 1.4 ci.yml publish-nightly：同上（label=`nightly`）
- [x] 1.5 验证：本地 sim 跑脚本（stub 归档 → SHA256SUMS → assemble → gen-index）→ 产出 release-index.json 与重构前 schema 字节等价（diff）；YAML 合法
- [x] 1.6 commit + push（CI 自验）+ 归档

## 备注
- Archive 步**不**统一（release per-runner / nightly 集中 rename，结构本就不同）；只抽 assemble + index（高重复、高漂移）。
- SHA256SUMS（1-liner）留 inline，漂移风险低。

## 验证结果
- release + nightly 两路：脚本输出与重构前 inline jq **逐字节相同**（diff modulo .published）✓；scripts 100755 可执行；release.yml + ci.yml YAML 合法；bash -n 通过。Archive 步未动（结构本异）。
