# Tasks: port-z42c-char

> 状态：⚪ DRAFT 待审批 | 创建：2026-06-11 | 子系统锁：z42c

- [ ] CH-1 syntax：lexer 规则 + CharLitExpr + parser 分支 + 单测×2
- [ ] CH-2 semantics：BoundLitChar + 绑定 + char 运算放行（C# 校准）+ 单测
- [ ] CH-3 ir：ConstCharInstr + 编码（0x08/tag/dst/i32）+ REGT visit + codegen 单测
- [ ] CH-4 charcheck zbc e2e 第 4 源（执行 + byte-compare 4/4）+ gate 全绿 + 文档 + commit
