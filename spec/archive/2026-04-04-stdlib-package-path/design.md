# Design: stdlib-package-path

## Architecture

```
项目根/
├── src/
│   ├── runtime/          ← VM (Rust)
│   └── libraries/        ← stdlib 源码 (.z42)
├── scripts/
│   └── package.sh        ← NEW: 打包脚本
└── artifacts/
    └── z42/              ← package.sh 输出根（.gitignore 已忽略）
        ├── bin/
        │   └── z42vm
        └── libs/
            ├── z42.core.zbc
            ├── z42.core.zpkg
            ├── z42.io.zbc
            ├── z42.io.zpkg
            ├── z42.math.zbc
            ├── z42.math.zpkg
            ├── z42.text.zbc
            ├── z42.text.zpkg
            ├── z42.collections.zbc
            └── z42.collections.zpkg
```

## Decisions

### Decision 1: 目录名 `libs/` 而非 `stdlib/`

**问题：** 原 `stdlib.md` 规范使用 `stdlib/`，用户要求改为 `libs/`。
**决定：** 全部改为 `libs/`，同步更新规范。

### Decision 2: 同时输出 `.zbc` + `.zpkg`

**问题：** VM 已支持两种格式，应输出哪种？
**决定：** 两种都输出。`.zbc` 供 VM 按模块单独加载；`.zpkg` packed mode
提供版本元数据，为未来依赖解析做准备。

### Decision 3: 搜索路径优先级

```
1. $Z42_LIBS                     ← 显式环境变量（CI / 自定义覆盖）
2. <binary-dir>/../libs/         ← adjacent 模式：artifacts/z42/bin/z42vm → artifacts/z42/libs/
3. <cwd>/artifacts/z42/libs/     ← 开发便捷：cargo run 时 CWD 为项目根
```

**为什么没有 `~/.z42/libs/`：** 当前阶段统一输出到 `artifacts/z42/`，
用户目录安装推迟到有正式分发需求时再加。

### Decision 4: 当前阶段只 log，不加载

**问题：** stdlib `.z42` 尚无法编译成 `.zbc`（`[Native]` 未实现），
`libs/` 下是占位空文件，VM 不应该尝试加载它们。
**决定：** `main.rs` 中只做路径探测 + `tracing::info!` 日志，
不修改 `Vm::new()` / `load_artifact()` 调用链。M7 再补实际加载逻辑。

## Implementation Notes

### `scripts/package.sh`

```bash
#!/usr/bin/env bash
set -euo pipefail

PROFILE="${1:-debug}"           # debug | release
ARTIFACTS="artifacts/z42"

# 1. Build VM
if [ "$PROFILE" = "release" ]; then
    cargo build --release --manifest-path src/runtime/Cargo.toml
    VM_BIN="src/runtime/target/release/z42vm"
else
    cargo build --manifest-path src/runtime/Cargo.toml
    VM_BIN="src/runtime/target/debug/z42vm"
fi

# 2. Create output dirs
mkdir -p "$ARTIFACTS/bin" "$ARTIFACTS/libs"

# 3. Copy VM binary
cp "$VM_BIN" "$ARTIFACTS/bin/z42vm"

# 4. Populate libs/ (placeholder — real .zbc/.zpkg produced by M7 build-stdlib)
for mod in z42.core z42.io z42.math z42.text z42.collections; do
    touch "$ARTIFACTS/libs/${mod}.zbc"
    touch "$ARTIFACTS/libs/${mod}.zpkg"
done

echo "Packaged z42 to $ARTIFACTS/"
```

### `src/runtime/src/main.rs` 搜索路径探测

在 `Vm::new()` 调用前，新增 `resolve_libs_dir()` 函数：

```rust
fn resolve_libs_dir() -> Option<PathBuf> {
    // 1. $Z42_LIBS
    if let Ok(v) = std::env::var("Z42_LIBS") {
        let p = PathBuf::from(v);
        if p.is_dir() { return Some(p); }
    }
    // 2. <binary-dir>/../libs/
    if let Ok(exe) = std::env::current_exe() {
        let p = exe.parent()?.parent()?.join("libs");
        if p.is_dir() { return Some(p); }
    }
    // 3. <cwd>/artifacts/z42/libs/
    if let Ok(cwd) = std::env::current_dir() {
        let p = cwd.join("artifacts/z42/libs");
        if p.is_dir() { return Some(p); }
    }
    None
}
```

找到目录后扫描 `.zpkg` / `.zbc` 文件并 `tracing::info!` 输出，不做加载。

## Testing Strategy

- `scripts/package.sh` 执行后检查 `artifacts/z42/bin/z42vm` 存在
- `artifacts/z42/libs/` 下有 10 个占位文件（5 × `.zbc` + 5 × `.zpkg`）
- `z42vm --verbose <file.z42ir.json>` 时 log 中出现 "libs dir:" 信息
- 现有 golden tests 不受影响（VM 行为未改变）
