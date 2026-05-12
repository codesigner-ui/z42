# scripts

## 职责

仓库根级开发脚本：编译 stdlib、打包发行、回归 golden test、跨包端到端验证。
所有脚本都用 `bash`，假定从仓库任意位置调用都能定位 root（脚本顶部 `cd` 到
项目根），不依赖当前工作目录。

## 脚本一览

| 脚本 | 触发时机 | 关键依赖 | 主要产物 |
|------|---------|---------|---------|
| `test-all.sh` | **每次 commit / 归档前必跑** | 下面 5 个 stage 脚本 | 串联全部 GREEN 验证 |
| `build-stdlib.sh` | 改了 stdlib `.z42` 源 | `dotnet`（或 `--use-dist` 用打包过的 z42c） | `artifacts/build/libraries/<lib>/<profile>/dist/<lib>.zpkg` |
| `package.sh` | 准备发行 / 测发行包 | `dotnet publish` + `cargo build` | `artifacts/packages/z42-<version>-<rid>-<config>/{bin,libs,native}` |
| `regen-golden-tests.sh` | 编译器变更使 `.zbc` 基线漂移 | `dotnet` + `z42.Driver` | 更新 `src/tests/<cat>/<name>/source.zbc` + `src/libraries/<lib>/tests/<name>/source.zbc` |
| `test-vm.sh` | 跑 VM 端到端 golden test（最常用） | `cargo build` + `regen-golden-tests.sh` 的产物 | 终端报告 interp / jit 通过率 |
| `test-cross-zpkg.sh` | 跨包路径 / 元数据相关变更 | `dotnet` + `cargo build` | 终端报告 target/ext/main 三方协作通过率 |
| `test-stdlib.sh` | stdlib 源 / 编译器变动 | `build-stdlib.sh` + `z42-test-runner` | 6 个 stdlib lib 的 [Test] 通过率 |
| `test-dist.sh` | 验证打包后的发行版能独立工作 | `package.sh` + `build-stdlib.sh` 的产物 | 终端报告 packaged z42c+z42vm 跑 golden 通过率 |

## 典型流程

**commit 前 / 归档前（必跑，workflow 阶段 8 全绿入口）**：
```bash
./scripts/test-all.sh             # 串联 dotnet build/test + test-vm + test-cross-zpkg + test-stdlib
./scripts/test-all.sh --with-dist # 追加 packaged binary 验证（要先跑 package.sh release）
```

> 不要单独只跑其中一个 stage 就当作通过 —— 历史上 cross-zpkg subclass catch
> bug 就是因为 `test-stdlib.sh` 不在默认 GREEN 路径里，每次 spec 验证都被漏掉。

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
                  ┌─→ dotnet build / test
                  ├─→ test-vm.sh
test-all.sh ──────┼─→ test-cross-zpkg.sh
                  ├─→ test-stdlib.sh   (← build-stdlib.sh)
                  └─→ test-dist.sh     (← package.sh; only --with-dist)

regen-golden-tests.sh ─→ test-vm.sh    (开发循环单独用)
```

每个脚本的详细 Usage（参数、模式选择）见脚本顶部注释。
