# src/libraries — z42 标准库源码

## 职责

z42 标准库的 `.z42` 源文件。每个库是独立的 z42 包，通过 `build-stdlib.sh` 编译为 `.zpkg` 产物后供用户程序引用。

## 库列表

| 目录 | 包名 | 内容 |
|------|------|------|
| `z42.core/` | `z42.core` | 核心类型：`Object`、`String`、`Type`、`Assert`、`Convert`、核心接口 |
| `z42.collections/` | `z42.collections` | 集合类型：`List`、`Dictionary`、`HashSet`、`Queue`、`Stack` |
| `z42.io/` | `z42.io` | IO 类型：`Console`、`File`、`Path`、`Environment` |
| `z42.math/` | `z42.math` | 数学函数：`Math` |
| `z42.text/` | `z42.text` | 文本处理：`StringBuilder`、`Regex` |

## 构建

```bash
./scripts/build-stdlib.sh           # debug
./scripts/build-stdlib.sh release   # release
```

产物输出到 `artifacts/z42/libs/*.zpkg`（已在 `.gitignore` 中，不纳入版本控制）。

## 修改后

修改任意 `.z42` 源文件后必须重新运行 `build-stdlib.sh` 更新 zpkg 产物。
