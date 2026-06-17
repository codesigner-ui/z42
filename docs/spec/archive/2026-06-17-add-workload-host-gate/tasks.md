# Tasks: add-workload-host-gate

> 状态：🟢 已完成 | 创建：2026-06-17 | 锁：`toolchain`
> 小变更（B2-4 预留字段落地）。proposal/spec 见同目录；设计已在 runtime-workload-distribution.md。

- [x] 1.1 launcher_network.z42：`_fetchWorkloadEntry` 返回数组追加 host 列表（约定：`[archive, sha256, "::hosts::", host…, "::runtimes::", rid…]` 或更稳的双数组——实施时定一个清晰编码）；加 `bool _hostAllowed(string[] hosts)`（空=放行；含 `_hostRid()` 或 `"*"`=放行）
- [x] 1.2 launcher_workload.z42：`_workloadInstallNetwork` 解析 host → gate；不通过则清晰报错 + 非零退出（在任何下载/staging 之前）
- [x] 1.3 release.yml：`workloads.<wl>` 加 `host`（ios=`["macos-arm64"]`、android=4 桌面 RID、wasm=`["*"]`）；`jq -n` 干跑校验
- [x] 1.4 编译 launcher 清编
- [x] 1.5 本地 mock e2e：四态（host 命中放行 / 不命中拒绝不下载 / `"*"` 放行 / 缺失放行）+ `--from` 回归不受影响
- [x] 1.6 docs：runtime-workload-distribution.md host 字段「保留未实施」→「已实施」；Deferred 摘除 host gate 行
- [x] 1.7 GREEN（launcher 清编 + e2e）+ 归档 + 释放锁 + commit

## 备注
- host gate 仅对**联网（manifest）装**生效；本地 `--from` 不 gate（proposal Out-of-Scope）。
- CI 改动 correct-by-construction（同 B2-4，真 release 下次 tag 验）。

## 验证结果（本地 mock e2e，四态全过）
- host=[macos-arm64]（本机）→ INSTALLED ✅ · host=[linux-x64] → **拒绝**「not supported on this host (macos-arm64); requires one of: linux-x64」+ 不下载（仅 1 次 manifest GET）✅ · host=["*"] → INSTALLED ✅ · 无 host → INSTALLED（向后兼容）✅
- launcher 清编；CI jq+YAML 干跑校验；CI correct-by-construction（下次 tag 验）
