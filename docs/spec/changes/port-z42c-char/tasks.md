# Tasks: port-z42c-char

> 状态：🟢 已完成 | 创建+批准+实施+归档：2026-06-11 | 子系统锁：z42c（已释放）

- [x] CH-1 syntax：lexer 规则 + CharLitExpr + parser 分支 + 单测×2
- [x] CH-2 semantics：BoundLitChar + 绑定 + char 运算放行（C# 校准）+ 单测
- [x] CH-3 ir：ConstCharInstr + 编码（0x08/tag/dst/i32）+ REGT visit + codegen 单测
- [x] CH-4 charcheck zbc e2e 第 4 源（执行 + byte-compare 4/4）+ gate 全绿 + 文档 + commit

## 实施记录（2026-06-11）
- **惊喜起点**：z42c Lexer 早在 6c 期就埋了 `_lexChar`（CharLiteral token 完整，含转义跳过）——CH-1 只补了 CharLitExpr+Parser 分支（剥引号复用 Lexer.DecodeString 取首 char，镜像 C# UnescapeChar）。
- 链一次成型：BoundLitChar（char 已在 IsOrderable，比较规则免改）→ ConstCharInstr → 编码 op 0x08+tag(FromIrType→0x0C)+dst+i32 → REGT visit。**charcheck 首次双编译即逐字节一致**。
- zbc e2e 升 **4/4 源**（charcheck：char 声明/==/!=/< + div-zero oracle，注意 'A'(65)>'.'(46) 的比较方向）。
- 延后（按 proposal）：Unicode 转义 / char↔int 宽化全表 / s[i] 索引。
