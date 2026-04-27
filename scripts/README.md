# scripts

## 职责

仓库根级开发脚本：编译 stdlib、打包发行、回归 golden test、跨包端到端验证。
所有脚本都用 `bash`，假定从仓库任意位置调用都能定位 root（脚本顶部 `cd` 到
项目根），不依赖当前工作目录。

## 脚本一览

| 脚本 | 触发时机 | 关键依赖 | 主要产物 |
|------|---------|---------|---------|
| `build-stdlib.sh` | 改了 stdlib `.z42` 源 | `dotnet`（或 `--use-dist` 用打包过的 z42c） | `artifacts/libraries/<lib>/dist/<lib>.zpkg` + `cache/<file>.zbc` |
| `package.sh` | 准备发行 / 测发行包 | `dotnet publish` + `cargo build` | `artifacts/z42/bin/{z42c,z42vm}` + `artifacts/z42/libs/*.zpkg` |
| `regen-golden-tests.sh` | 编译器变更使 `.zbc` 基线漂移 | `dotnet` + `z42.Driver` | 更新 `src/runtime/tests/golden/run/*/source.zbc` |
| `test-vm.sh` | 跑 VM 端到端 golden test（最常用） | `cargo build` + `regen-golden-tests.sh` 的产物 | 终端报告 interp / jit 通过率 |
| `test-cross-zpkg.sh` | 跨包路径 / 元数据相关变更 | `dotnet` + `cargo build` | 终端报告 target/ext/main 三方协作通过率 |
| `test-dist.sh` | 验证打包后的发行版能独立工作 | `package.sh` + `build-stdlib.sh` 的产物 | 终端报告 packaged z42c+z42vm 跑 golden 通过率 |

## 典型流程

**日常开发循环（最高频）**：
```bash
./scripts/regen-golden-tests.sh   # 改了编译器后重生 .zbc 基线
./scripts/test-vm.sh              # 跑 VM 端到端（interp + jit）
```

**改了 stdlib `.z42` 源**：
```bash
./scripts/build-stdlib.sh         # 重编 stdlib zpkg
./scripts/regen-golden-tests.sh   # golden 用到的 stdlib 也会被引用
./scripts/test-vm.sh
```

**完整发行验证**：
```bash
./scripts/package.sh release      # 打包 z42c + z42vm
./scripts/build-stdlib.sh         # 编 stdlib（可选 --use-dist 用 packaged z42c）
./scripts/test-dist.sh            # 端到端验证发行包
```

**跨包 e2e**（改 zpkg 路径解析、元数据格式时必跑）：
```bash
./scripts/test-cross-zpkg.sh
```

## 依赖关系图

```
package.sh ─────────┐
                    ├─→ test-dist.sh
build-stdlib.sh ────┤
                    └─→ test-cross-zpkg.sh

regen-golden-tests.sh ─→ test-vm.sh
```

每个脚本的详细 Usage（参数、模式选择）见脚本顶部注释。
