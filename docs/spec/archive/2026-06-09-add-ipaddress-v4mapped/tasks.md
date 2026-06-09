# Tasks: add-ipaddress-v4mapped

> 状态：🟢 完成 | 创建：2026-06-09 | 类型：feat (stdlib, 纯 z42)
> 模式：minimal —— IPv6 解析器加 dotted-quad 尾部支持，无 lang/ir/vm。
> 子系统锁：`stdlib`（与 add-reflection-mvp 例外共存，仅动 z42.net，文件零重叠）。

## 背景

`net-future-ipaddress-v4mapped`：`IPAddress.Parse` v0 拒绝 mixed `.`+`:` 字符串，
所以 RFC 4291 §2.5.5 的 IPv4-in-IPv6 dotted form（`::ffff:192.0.2.1` v4-mapped /
`::192.0.2.1` v4-compatible / `2001:db8::1.2.3.4` 带前缀 + NAT64）无法解析 —— 双栈
socket 代码把 IPv4 经 IPv6-only API round-trip 时会撞上。workaround 是手搓 16 字节。

## 设计（改写复用现有解析器）

`_parseIPv6` 检测到 `.` → `_rewriteEmbeddedV4`：最后一个 `:` 之后的 dotted-quad
（占低 32 bit）用 `_parseV4Bytes` 校验 + 拿 4 字节，改写成两个 hex group
`…:HHHH:HHHH`，再走原有 `::` elision + group 拼装（零改动）。dotted-quad 校验
复用从 `_parseIPv4` 抽出的 `_parseV4Bytes`（返回 `byte[]`，octet >255 / 不足 4 段抛）。

## 任务

- [x] 1. `_parseIPv6` 加 dotted-quad 预处理 + `_rewriteEmbeddedV4` + `_hex2` helper
- [x] 2. 重构 `_parseIPv4` → 抽 `_parseV4Bytes(string)→byte[]`（`_parseIPv4` wrap 它）。
      此重构同时绕开"静态方法返回 IPAddress 后链式调实例方法"的类型解析报错（E0402）
- [x] 3. 更新 IPAddress.z42 头注释（IPv4-in-IPv6 从 "not in scope" 移到 "supports"）
- [x] 4. 测试 `tests/ipaddress_v4mapped.z42`（7）：v4-mapped 字节布局 · ==hex 形式 ·
      v4-compatible · 带 IPv6 前缀 · ToString round-trip · 非法 octet / 不足段抛异常
- [x] 5. 文档：net.md `net-future-ipaddress-v4mapped` → ✅ landed
- [x] 6. GREEN：canonical `test lib z42.net`（49/49 文件全过，含新 7 + 30 现有 IPAddress）
- [x] 7. commit + push + 释锁归档

## 备注

zone-id（`fe80::1%eth0`）+ TryParse 仍是 IPAddress 的 deferred（见 IPAddress.z42 头注）。
`ToString` 仍 emit 规范 hex 形式（非 dotted），round-trip via Parse 相等 —— 与 BCL 的
dotted ToString 不同，但 z42 v0 选规范 hex（已有行为，不改）。
