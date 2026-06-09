# Tasks: add-ipaddress-tryparse

> 状态：🟢 完成 | 创建：2026-06-09 | 类型：feat (stdlib, 纯 z42)
> 模式：minimal —— 加一个 non-throwing parse 变体，无 lang/ir/vm。
> 子系统锁：`stdlib`（reflection 已归档释放，stdlib 空闲；仅动 z42.net）。

## 背景

IPAddress.z42 头注把 `Try*` non-throwing 变体列为 v0 "not in scope"，workaround 是
`try { Parse } catch`。但 non-throwing parse 是 untrusted 输入（config / 用户 / 网络）
的标准需求 —— 每个 reject 抛异常既有开销也累赘。补上 BCL 风格的 TryParse。

## 设计

`public static IPAddress? TryParse(string s)` —— nullable-return（失败返 `null`），
比 C# 的 `out bool` 更贴 z42 idiom（z42 有 `T?` nullable）。实现 = `try { Parse(s) }
catch (FormatException) { null }`，与 Parse 同语法。

## 任务

- [x] 1. `IPAddress.TryParse(string) → IPAddress?`（包 Parse + catch FormatException → null）
- [x] 2. 头注释把 `Try*` 从 "not in scope" 移到 "supports"
- [x] 3. 测试 `tests/ipaddress_tryparse.z42`（4）：valid v4 / v6 / v4-mapped（非 null + 类型 +
      ToString）· invalid（非法字符 / >255 / 不足段 / 空 / `:::1` / `gg::1`）→ null
- [x] 4. GREEN：canonical `test lib z42.net`（50/50 文件全过，含新 4）
- [x] 5. commit + push + 释锁归档

## 备注

z42 nullable 方法调用：`IPAddress? a = TryParse(...); Assert.True(a != null); a.IsIPv4()`
直接编过（无需 `!` 解包）。顺手修正 ACTIVE.md 关于 add-binary-float "未提交 WIP" 的 stale
note（已核其 0838475a 提交、HEAD 含 builtin、工作树干净）。zone-id 仍是 IPAddress deferred。
