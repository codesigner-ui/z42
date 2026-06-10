# Design: port-z42c-char

> DRAFT。C# 权威：TokenDefs CharLiteral / ExprParser.Atoms.UnescapeChar / ZbcWriter ConstChar（op 0x08 + TypeTagFromIrType + dst + (int)val）。

## 链路
lexer `'…'` token（转义内联，整 token 含引号）→ parser 剥引号+DecodeString 复用取首 char → CharLitExpr(char Value)
→ BoundLitChar(Z42PrimType("char")) → ExprEmitter ConstCharInstr(dst=Alloc(IrType.Char), Value)
→ ZbcInstr：op 0x08 + Tag.FromIrType(dst.Type)（=0x0C）+ dst u16 + WriteU32((int)val)
→ REGT walk + dump `const.char`。

## Decisions
- D1 AST 存**解码后 char**（镜像 C# LitCharExpr；区别于 Int/Str 存 raw——C# 本来如此）
- D2 char 运算面按 C# BinaryTypeTable 实测最小集放行（比较 char×char；(int)c 显式转换走既有 cast 路径——若 cast 链未通则 corpus 退回纯比较，挂账）
- D3 验证双轨：charcheck zbc 源（执行+byte-compare）为主，单元为辅

## Testing
lexer token 形态/parser dump/typecheck 0 错/codegen dump + charcheck e2e。
