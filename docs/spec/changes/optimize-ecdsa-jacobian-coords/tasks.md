# Tasks: Jacobian-coordinate scalar multiplication for short-Weierstrass ECDSA

> 状态：🟡 待实施（spec 已就绪，等 User 排期）| 创建：2026-06-01
> 类型：refactor（纯性能，输出 byte-identical）| 触发：recalibrate-secp256k1-timeout (581a28af)

## 进度概览
- [ ] 阶段 1: 基线测量 + 常量提升
- [ ] 阶段 2: secp256k1 Jacobian 移植
- [ ] 阶段 3: P-256 Jacobian 移植
- [ ] 阶段 4: 验证 + 收紧 timeout + 文档

## 阶段 1: 基线 + 常量提升
- [ ] 1.1 本地测量 `test_secp256k1_sign_verify_round_trip` 当前 wall time（记录基线）
- [ ] 1.2 `EcdsaSecp256k1.z42`：把 `p`/`n` 在入口解析一次，沿调用链传参，消除 `_curvePrime()`/`_curveOrder()` 的 per-op `ParseHex`
- [ ] 1.3 重测 round-trip，量化仅常量提升带来的提速

## 阶段 2: secp256k1 Jacobian（a=0）
- [ ] 2.1 加 `_jacDouble`（a=0 特化）/ `_jacAdd` / `_jacToAffine`（单次 ModInverse）
- [ ] 2.2 改写 `_scalarMult` 走 Jacobian double-and-add，出口转回 affine
- [ ] 2.3 用 `Z=0` 表示无穷远点，在边界与现有 `null` 约定互转
- [ ] 2.4 跑全部现有 secp256k1 向量 —— 必须 byte-identical 通过
- [ ] 2.5 加边界向量：identity(Z=0) / y=0 倍点 / P+(−P) / 标量 n−1

## 阶段 3: P-256 Jacobian（a=−3）
- [ ] 3.1 同样移植到 `EcdsaP256.z42`，倍点用 a=−3 的 `M=3(X−Z²)(X+Z²)` 技巧
- [ ] 3.2 跑全部现有 P-256 向量 —— 必须 byte-identical 通过
- [ ] 3.3 检查 P-256 是否也有贴近 timeout 的慢测试（Open Question）

## 阶段 4: 验证 + 收尾
- [ ] 4.1 把 `ecdsa_secp256k1_vectors.z42` 的 `[Timeout]` 从 600s stopgap 收紧到测量后的紧值（仍演示 override）
- [ ] 4.2 `./scripts/test-all.sh --scope=full` 全绿（本地 + 推后 CI 确认 ubuntu-x64 远离 cap）
- [ ] 4.3 spec scenarios 逐条覆盖确认（conformance + edge）
- [ ] 4.4 `docs/design/` EC 文档：记录 Jacobian 原理 + affine 输出不变式 + 决策权衡
- [ ] 4.5 归档到 `docs/spec/archive/`，更新 roadmap Deferred Backlog Index（移除该项）

## 备注
- 安全不变式（最高优先级）：所有现有参考向量必须 byte-identical 通过；任何一个漂移 = 移植错误，停下排查。
- X25519 / Ed25519 不在本 scope（Montgomery / 扭曲爱德华兹，另算）。
- A（timeout 600s stopgap, 581a28af）已上线解封 CI；本 spec 是 A 承诺的根治。
