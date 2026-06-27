---
name: project-libraries-naming-convention
description: src/libraries/ 包前缀命名约定（deferred）—— stdlib=z42.* / compiler=z42c.* / SDK=z42t.*
metadata:
  type: project
---

**src/libraries/ 包前缀约定（方向已确认，实施时机待定）**

- `z42.*` = 标准库（Std.* 命名空间，用户代码直接 using）
- `z42c.*` = 编译器（Z42.* 命名空间，1.0 后从 src/z42c/ 迁入）
- `z42t.*` = 工具链 SDK（Z42.* 命名空间，如 z42.build→z42t.build / z42.project→z42t.project）

**Why:** src/libraries/ 目前混放 stdlib 和 SDK 包，仅靠命名空间（Std.* vs Z42.*）区分，目录名层面无法一眼辨别。z42c 前缀在 compiler 上验证了"前缀即分类"模式有效。

**How to apply:** 将来给 SDK/toolchain 基础设施包命名时用 z42t.* 前缀；现有 z42.build / z42.project 暂不改，等合适时机一起做 rename。
