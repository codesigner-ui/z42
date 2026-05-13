# Release 工作流

> **状态**：📋 placeholder（自动化部分）。本地打 per-arch SDK package 已落地（Phase 1.x，9 RID），见 [`packaging.md`](packaging.md)。

## 本地打 SDK package（已落地，2026-05-13）

```bash
./scripts/package.sh release                       # host RID
./scripts/package.sh release --rid ios-arm64       # 任一 9 RID 之一
./scripts/package.sh --help                        # RID 矩阵 + 选项
```

完整 9 RID 矩阵 + 平台前置 + 验证 + 失败排查见 [`packaging.md`](packaging.md)。

## 旧的"打 release tarball"手工流程

```bash
./scripts/package.sh release                  # 1. 打 host RID SDK 到 artifacts/packages/
./scripts/build-stdlib.sh --use-dist          # 2. 用分发版 z42c 重编译 stdlib
./scripts/test-dist.sh                        # 3. 分发版 binary 跑全套 goldens（interp + jit）
./scripts/test-dist.sh interp                 # 仅 interp 模式
```

手动 `tar czf` 或 `zip` 即可（CI 自动化在 0.2.6 接管）。

## 0.2.6 之后

`git tag v0.X.Y` → GitHub Actions 自动：

1. 在多平台 CI matrix 上 `package.sh release`
2. 测试 release binary 全套通过
3. 打 tarball / zip + 计算 checksums
4. 创建 GitHub Release，挂载跨平台 artifact

Artifact 命名规范（Q12 待裁决，见 [`docs/roadmap.md`](../roadmap.md)）：

```
z42-{version}-{os}-{arch}.tar.gz    # 候选
z42-{version}-{os}-{arch}.zip       # Windows
```

含：

- `z42c`（单文件 binary）
- `z42vm`（单文件 binary）
- `libs/<lib>.zpkg`（6 个 stdlib zpkg）
- `LICENSE`、`README`

## 1.0 之后

`z42up` 跨平台安装器（rustup 等价物）启用，用户走 `z42up install stable` 而非手工下载 tarball。详见 [`docs/roadmap.md`](../roadmap.md) §1.0.x charter。
