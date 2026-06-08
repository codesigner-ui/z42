# Tasks: add-uri-ipv6-host

> 状态：🟢 完成 | 创建：2026-06-09 | 类型：feat + fix (stdlib, 纯 z42)
> 模式：minimal —— 修 Uri 解析器 + 加 2 个 accessor，无 lang/ir/vm。
> 子系统锁：`stdlib`（与 add-reflection-mvp 例外共存，仅动 z42.uri，文件零重叠）。

## 背景

`uri-future-ipv6-parse`：调研发现 `Std.Net.IPAddress.Parse` **早已**解码 IPv6
（full-form + `::` shorthand → 16-bit 组件）。但 Uri 侧有 **latent bug**：host 解析
在第一个 `:` 处截断（`Uri.z42` 原 host 扫描 `if (c == ':') break`），所以
`Uri.Parse("http://[::1]:8443/")` 实际把 host 解析成 `[`、端口解析也错 —— v0 注释
"host 保留 `[::1]` 形式" 是错的，Uri **根本无法解析 IPv6 URL**（无测试覆盖）。

## 改动

1. **修解析器**（`Uri.z42` authority 段）：host 扫描前若 `Peek()=='['`，消费到 `]`
   （含），整个 `[...]` 作 host，之后的 `:` 才是端口；未闭合 `[` 抛 UriException。
2. **加 accessor**：`Uri.IsIPv6Literal()`（host 是 `[...]` 形式）+ `Uri.GetHostName()`
   （剥 IPv6 括号 → `::1`，非 IPv6 等于 GetHost），桥接 `IPAddress.Parse(uri.GetHostName())`。
   z42.uri 不依赖 z42.net —— 拆解交 IPAddress.Parse，Uri 只提供剥括号的 host。

## 任务

- [x] 1. Uri.z42 authority 解析处理 `[...]` bracket host + 未闭合抛异常
- [x] 2. Uri.IsIPv6Literal() + GetHostName()；更新文件头 IPv6 注释
- [x] 3. 测试 `tests/ipv6_host.z42`（7）：带/不带端口/路径 IPv6 解析 · ToString round-trip ·
      IPv4/regname 不受影响 · 未闭合 `[` 抛 UriException
- [x] 4. 文档：uri.md `uri-future-ipv6-parse` → ✅ landed（注明 latent-bug 性质 + IPAddress 分工）
- [x] 5. GREEN：canonical `test lib z42.uri`（build-stdlib 22/22 + 6/6 文件全过含 ipv6_host 7/7）
- [x] 6. commit + push + 释锁归档

## 备注

IPv6 → 16-bit 组件拆解的重活在 `Std.Net.IPAddress.Parse`（z42.net，已落地 2026-05-27）。
本变更只解决 Uri 侧：正确**解析** bracket host（修 latent bug）+ 提供 `GetHostName()`
桥接。IPv4-in-IPv6 dotted / zone-id 仍是 IPAddress 的 deferred（见 IPAddress.z42 头注）。
