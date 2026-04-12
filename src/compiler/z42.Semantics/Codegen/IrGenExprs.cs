using Z42.Core.Text;
using Z42.Syntax.Parser;
using Z42.IR;

namespace Z42.Semantics.Codegen;

/// Expression, call, and string interpolation emission — part of the IrGen partial class.
public sealed partial class IrGen
{
    // ── Expressions ──────────────────────────────────────────────────────────

    private int EmitExpr(Expr expr)
    {
        switch (expr)
        {
            case LitStrExpr s:
            {
                int dst = Alloc();
                Emit(new ConstStrInstr(dst, Intern(s.Value)));
                return dst;
            }
            case LitIntExpr n:
            {
                int dst = Alloc();
                Emit(new ConstI64Instr(dst, n.Value));
                return dst;
            }
            case LitFloatExpr f:
            {
                int dst = Alloc();
                Emit(new ConstF64Instr(dst, f.Value));
                return dst;
            }
            case LitBoolExpr b:
            {
                int dst = Alloc();
                Emit(new ConstBoolInstr(dst, b.Value));
                return dst;
            }
            case LitNullExpr:
            {
                int dst = Alloc();
                Emit(new ConstNullInstr(dst));
                return dst;
            }
            case InterpolatedStrExpr interp:
                return EmitInterpolation(interp);

            case LitCharExpr c:
            {
                int dst = Alloc();
                Emit(new ConstI32Instr(dst, (int)c.Value));
                return dst;
            }
            case IdentExpr id:
            {
                if (_locals.TryGetValue(id.Name, out int reg))
                    return reg;
                if (_mutableVars.Contains(id.Name))
                {
                    int dst = Alloc();
                    Emit(new LoadInstr(dst, id.Name));
                    return dst;
                }
                // Implicit `this.fieldName` — bare field access inside an instance method
                if (_instanceFields.Contains(id.Name))
                {
                    int dst = Alloc();
                    Emit(new FieldGetInstr(dst, 0, id.Name));  // reg 0 = this
                    return dst;
                }
                throw new InvalidOperationException($"undefined variable `{id.Name}`");
            }

            case AssignExpr assign:
                return EmitAssign(assign);

            case CallExpr call:
                return EmitCall(call);

            case BinaryExpr bin:
                return EmitBinary(bin);

            case UnaryExpr u:
                return EmitUnary(u);

            case PostfixExpr post:
                return EmitPostfix(post);

            case ConditionalExpr ternary:
                return EmitTernary(ternary);

            case NullConditionalExpr nc:
                return EmitNullConditional(nc);

            case NullCoalesceExpr nc:
                return EmitNullCoalesce(nc);

            // `expr is TypeName binding` — is_instance for bool + as_cast for binding
            case IsPatternExpr ipe:
            {
                int objReg  = EmitExpr(ipe.Target);
                int boolReg = Alloc();
                var qualName = QualifyName(ipe.TypeName);
                Emit(new IsInstanceInstr(boolReg, objReg, qualName));
                // Also emit the as_cast and store to a local so the then-block can use it
                int castReg = Alloc();
                Emit(new AsCastInstr(castReg, objReg, qualName));
                _locals[ipe.Binding] = castReg;
                return boolReg;
            }

            case CastExpr cast:
                return EmitExpr(cast.Operand);

            // Enum member access: Direction.North → ConstI64
            case MemberExpr m when m.Target is IdentExpr enumId
                                && _enumConstants.TryGetValue($"{enumId.Name}.{m.Member}", out long enumVal):
            {
                int dst = Alloc();
                Emit(new ConstI64Instr(dst, enumVal));
                return dst;
            }

            // Static field read: ClassName.fieldName
            case MemberExpr m when m.Target is IdentExpr { Name: var clsName }
                && TryGetStaticFieldKey(clsName, m.Member) is { } sfKey:
            {
                int dst = Alloc();
                Emit(new StaticGetInstr(dst, sfKey));
                return dst;
            }

            case MemberExpr m:
            {
                int objReg = EmitExpr(m.Target);
                int dst = Alloc();
                Emit(new FieldGetInstr(dst, objReg, m.Member));
                return dst;
            }

            case IndexExpr ix:
            {
                int targetReg = EmitExpr(ix.Target);
                int idxReg    = EmitExpr(ix.Index);
                int dst       = Alloc();
                Emit(new ArrayGetInstr(dst, targetReg, idxReg));
                return dst;
            }

            case ArrayCreateExpr ac:
            {
                int sizeReg = EmitExpr(ac.Size);
                int dst = Alloc();
                Emit(new ArrayNewInstr(dst, sizeReg));
                return dst;
            }

            case ArrayLitExpr al:
            {
                var elemRegs = al.Elements.Select(EmitExpr).ToList();
                int dst = Alloc();
                Emit(new ArrayNewLitInstr(dst, elemRegs));
                return dst;
            }

            // new StringBuilder() → __sb_new builtin (pseudo-class, like List/Dict)
            case NewExpr when expr is NewExpr { Type: NamedType { Name: "StringBuilder" } }:
            {
                int dst = Alloc();
                Emit(new BuiltinInstr(dst, "__sb_new", []));
                return dst;
            }

            // new Dictionary<K,V>() → __dict_new builtin
            case NewExpr newExpr when newExpr.Type is NamedType { Name: "Dictionary" }:
            {
                int dst = Alloc();
                Emit(new BuiltinInstr(dst, "__dict_new", []));
                return dst;
            }

            // new List<T>() → __list_new builtin
            case NewExpr newExpr when newExpr.Type is NamedType { Name: "List" }:
            {
                int dst = Alloc();
                Emit(new BuiltinInstr(dst, "__list_new", []));
                return dst;
            }

            case NewExpr newExpr when newExpr.Type is NamedType nt:
            {
                var argRegs = newExpr.Args.Select(EmitExpr).ToList();
                // Expand omitted constructor arguments using default values.
                string ctorKey = $"{QualifyName(nt.Name)}.{nt.Name}";
                argRegs = FillDefaults(ctorKey, argRegs);
                int dst = Alloc();
                Emit(new ObjNewInstr(dst, QualifyName(nt.Name), argRegs));
                return dst;
            }

            case SwitchExpr sw:
                return EmitSwitchExpr(sw);

            default:
                throw new NotSupportedException(
                    $"expression type {expr.GetType().Name} not yet supported in IrGen");
        }
    }

    // ── Assignment ───────────────────────────────────────────────────────────

    private int EmitAssign(AssignExpr assign)
    {
        int valReg = EmitExpr(assign.Value);

        if (assign.Target is IdentExpr id)
        {
            if (_mutableVars.Contains(id.Name))
                Emit(new StoreInstr(id.Name, valReg));
            else if (_instanceFields.Contains(id.Name))
                Emit(new FieldSetInstr(0, id.Name, valReg));  // reg 0 = this
            else
                _locals[id.Name] = valReg;
        }
        else if (assign.Target is IndexExpr ix)
        {
            int arrReg = EmitExpr(ix.Target);
            int idxReg = EmitExpr(ix.Index);
            Emit(new ArraySetInstr(arrReg, idxReg, valReg));
        }
        else if (assign.Target is MemberExpr { Target: IdentExpr { Name: var aClsName }, Member: var aField }
            && TryGetStaticFieldKey(aClsName, aField) is { } sfKey)
        {
            Emit(new StaticSetInstr(sfKey, valReg));
        }
        else if (assign.Target is MemberExpr fm)
        {
            int objReg = EmitExpr(fm.Target);
            Emit(new FieldSetInstr(objReg, fm.Member, valReg));
        }

        return valReg;
    }

    // ── Unary / postfix ──────────────────────────────────────────────────────

    private int EmitUnary(UnaryExpr u)
    {
        if (u.Op == "await") return EmitExpr(u.Operand);

        // Prefix ++ / -- on static field: ClassName.field++
        if (u.Op is "++" or "--" && u.Operand is MemberExpr { Target: IdentExpr { Name: var ucn }, Member: var ufn }
            && TryGetStaticFieldKey(ucn, ufn) is { } uSfKey)
        {
            int oldReg = Alloc(); Emit(new StaticGetInstr(oldReg, uSfKey));
            int one    = Alloc(); Emit(new ConstI64Instr(one, 1));
            int newReg = Alloc();
            Emit(u.Op == "++" ? new AddInstr(newReg, oldReg, one) : (IrInstr)new SubInstr(newReg, oldReg, one));
            Emit(new StaticSetInstr(uSfKey, newReg));
            return newReg;
        }

        // Prefix ++ / -- : increment/decrement operand, return new value.
        if (u.Op is "++" or "--" && u.Operand is IdentExpr prefixId)
        {
            int oldReg = EmitExpr(u.Operand);
            int one    = Alloc();
            int newReg = Alloc();
            Emit(new ConstI64Instr(one, 1));
            Emit(u.Op == "++" ? new AddInstr(newReg, oldReg, one)
                              : (IrInstr)new SubInstr(newReg, oldReg, one));
            WriteBackName(prefixId.Name, newReg);
            return newReg;   // prefix: return NEW value (differs from postfix)
        }

        int src = EmitExpr(u.Operand);
        int dst = Alloc();
        Emit(u.Op switch
        {
            "!" => (IrInstr)new NotInstr(dst, src),
            "-" => new NegInstr(dst, src),
            "~" => new BitNotInstr(dst, src),
            _   => new CopyInstr(dst, src)  // unary "+" is identity
        });
        return dst;
    }

    private int EmitPostfix(PostfixExpr post)
    {
        // Postfix ++ / -- on static field: ClassName.field++
        if (post.Operand is MemberExpr { Target: IdentExpr { Name: var pcn }, Member: var pfn }
            && TryGetStaticFieldKey(pcn, pfn) is { } pSfKey)
        {
            int oldReg = Alloc(); Emit(new StaticGetInstr(oldReg, pSfKey));
            int one    = Alloc(); Emit(new ConstI64Instr(one, 1));
            int newReg = Alloc();
            Emit(post.Op == "++" ? new AddInstr(newReg, oldReg, one) : (IrInstr)new SubInstr(newReg, oldReg, one));
            Emit(new StaticSetInstr(pSfKey, newReg));
            return oldReg;  // postfix returns OLD value
        }

        if (post.Operand is IdentExpr id)
        {
            int oldReg = EmitExpr(post.Operand);
            int one    = Alloc();
            int newReg = Alloc();
            Emit(new ConstI64Instr(one, 1));
            Emit(post.Op == "++" ? new AddInstr(newReg, oldReg, one)
                                 : (IrInstr)new SubInstr(newReg, oldReg, one));
            WriteBackName(id.Name, newReg);
            return oldReg;
        }
        return EmitExpr(post.Operand);
    }

    /// Write a new value back to a named variable — mutable local, SSA local, or implicit this.field.
    private void WriteBackName(string name, int valReg)
    {
        if (_mutableVars.Contains(name))
            Emit(new StoreInstr(name, valReg));
        else if (_instanceFields.Contains(name))
            Emit(new FieldSetInstr(0, name, valReg));
        else
            _locals[name] = valReg;
    }

    private int EmitTernary(ConditionalExpr ternary)
    {
        int condReg    = EmitExpr(ternary.Cond);
        string thenLbl = FreshLabel("tern_then");
        string elseLbl = FreshLabel("tern_else");
        string endLbl  = FreshLabel("tern_end");

        int result = Alloc();

        EndBlock(new BrCondTerm(condReg, thenLbl, elseLbl));

        StartBlock(thenLbl);
        int thenReg = EmitExpr(ternary.Then);
        Emit(new CopyInstr(result, thenReg));
        EndBlock(new BrTerm(endLbl));

        StartBlock(elseLbl);
        int elseReg = EmitExpr(ternary.Else);
        Emit(new CopyInstr(result, elseReg));
        EndBlock(new BrTerm(endLbl));

        StartBlock(endLbl);
        return result;
    }

    // ── Null coalescing ───────────────────────────────────────────────────────

    private int EmitNullCoalesce(NullCoalesceExpr nc)
    {
        int leftReg  = EmitExpr(nc.Left);
        int nullReg  = Alloc();
        int cmpReg   = Alloc();
        int result   = Alloc();

        Emit(new ConstNullInstr(nullReg));
        Emit(new EqInstr(cmpReg, leftReg, nullReg));

        string nullLbl = FreshLabel("nc_null");
        string endLbl  = FreshLabel("nc_end");
        EndBlock(new BrCondTerm(cmpReg, nullLbl, endLbl));

        // non-null path: result = left
        StartBlock(endLbl);
        Emit(new CopyInstr(result, leftReg));
        string afterNonNull = FreshLabel("nc_after");
        EndBlock(new BrTerm(afterNonNull));

        // null path: result = right
        StartBlock(nullLbl);
        int rightReg = EmitExpr(nc.Right);
        Emit(new CopyInstr(result, rightReg));
        EndBlock(new BrTerm(afterNonNull));

        StartBlock(afterNonNull);
        return result;
    }

    // ── Null-conditional ─────────────────────────────────────────────────────

    /// `target?.member` — null if target is null, else `target.member`
    private int EmitNullConditional(NullConditionalExpr nc)
    {
        int targetReg = EmitExpr(nc.Target);
        int nullReg   = Alloc();
        int cmpReg    = Alloc();
        int result    = Alloc();

        Emit(new ConstNullInstr(nullReg));
        Emit(new EqInstr(cmpReg, targetReg, nullReg));

        string nullLbl    = FreshLabel("nc_null");
        string nonNullLbl = FreshLabel("nc_member");
        string endLbl     = FreshLabel("nc_end");
        EndBlock(new BrCondTerm(cmpReg, nullLbl, nonNullLbl));

        // non-null path: result = target.member
        StartBlock(nonNullLbl);
        int memberReg = Alloc();
        Emit(new FieldGetInstr(memberReg, targetReg, nc.Member));
        Emit(new CopyInstr(result, memberReg));
        EndBlock(new BrTerm(endLbl));

        // null path: result = null
        StartBlock(nullLbl);
        int nullResult = Alloc();
        Emit(new ConstNullInstr(nullResult));
        Emit(new CopyInstr(result, nullResult));
        EndBlock(new BrTerm(endLbl));

        StartBlock(endLbl);
        return result;
    }

    // ── Binary ────────────────────────────────────────────────────────────────

    private int EmitBinary(BinaryExpr bin)
    {
        // `is` and `as` have a type name on the right, not a value
        if (bin.Op is "is" or "as" && bin.Right is IdentExpr typeIdent)
        {
            int objReg  = EmitExpr(bin.Left);
            int typeReg = Alloc();
            // Qualify the class name with the namespace (e.g. "Circle" → "Demo.Circle")
            var qualName = QualifyName(typeIdent.Name);
            Emit(bin.Op == "is"
                ? new IsInstanceInstr(typeReg, objReg, qualName)
                : (IrInstr)new AsCastInstr(typeReg, objReg, qualName));
            return typeReg;
        }

        // `expr is TypeName binding` — evaluates to bool, also stores typed cast in binding
        if (bin.Op == "is")
        {
            // Fallback: emit as plain is_instance (right might not be IdentExpr in edge cases)
            int objReg  = EmitExpr(bin.Left);
            int typeReg = Alloc();
            Emit(new IsInstanceInstr(typeReg, objReg, bin.Right is IdentExpr ti ? QualifyName(ti.Name) : "__unknown"));
            return typeReg;
        }

        int a   = EmitExpr(bin.Left);
        int b   = EmitExpr(bin.Right);
        int dst = Alloc();
        IrInstr instr = bin.Op switch
        {
            "+"  => new AddInstr(dst, a, b),
            "-"  => new SubInstr(dst, a, b),
            "*"  => new MulInstr(dst, a, b),
            "/"  => new DivInstr(dst, a, b),
            "%"  => new RemInstr(dst, a, b),
            "==" => new EqInstr(dst, a, b),
            "!=" => new NeInstr(dst, a, b),
            "<"  => new LtInstr(dst, a, b),
            "<=" => new LeInstr(dst, a, b),
            ">"  => new GtInstr(dst, a, b),
            ">=" => new GeInstr(dst, a, b),
            "&&" => new AndInstr(dst, a, b),
            "||" => new OrInstr(dst, a, b),
            "&"  => new BitAndInstr(dst, a, b),
            "|"  => new BitOrInstr(dst, a, b),
            "^"  => new BitXorInstr(dst, a, b),
            "<<" => new ShlInstr(dst, a, b),
            ">>" => new ShrInstr(dst, a, b),
            _    => throw new NotSupportedException($"operator `{bin.Op}` not yet supported")
        };
        Emit(instr);
        return dst;
    }

    // ── Call ─────────────────────────────────────────────────────────────────

    private int EmitCall(CallExpr call)
    {
        // ── Static user-defined class method call: ClassName.StaticMethod(args) ──
        // Must be checked BEFORE instance builtins (e.g. "Add" is both a list builtin and a static method name)
        if (call.Callee is MemberExpr { Target: IdentExpr { Name: var staticCls }, Member: var staticMethod }
            && _classStaticMethods.TryGetValue(QualifyName(staticCls), out var staticSet))
        {
            // Try exact name first, then arity-qualified name for overloaded methods.
            var arityKey = $"{staticMethod}${call.Args.Count}";
            var resolvedMethod = staticSet.Contains(staticMethod) ? staticMethod
                               : staticSet.Contains(arityKey)     ? arityKey
                               : null;
            if (resolvedMethod is not null)
            {
                var callName = $"{QualifyName(staticCls)}.{resolvedMethod}";
                var argRegs  = FillDefaults(callName, call.Args.Select(EmitExpr).ToList());
                int dst = Alloc();
                Emit(new CallInstr(dst, callName, argRegs));
                return dst;
            }
        }

        // ── Type-keyword static calls: string.X, int.X, double.X ────────────────
        // z42 type keywords (lowercase) used as static class prefixes.
        // Variadic methods (Join, Concat, Format with N args) emit BuiltinInstr directly
        // because fixed-arity stdlib stubs cannot forward variable argument counts.
        if (call.Callee is MemberExpr { Target: IdentExpr { Name: var kwCls }, Member: var kwMethod }
            && kwCls is "string" or "int" or "long" or "double" or "float" or "bool" or "char")
        {
            var kwArgRegs = call.Args.Select(EmitExpr).ToList();
            string? builtinName = (kwCls, kwMethod) switch {
                ("string", "IsNullOrEmpty")       => "__str_is_null_or_empty",
                ("string", "IsNullOrWhiteSpace")  => "__str_is_null_or_whitespace",
                ("string", "Join")                => "__str_join",
                ("string", "Concat")              => "__str_concat",
                ("string", "Format")              => "__str_format",
                ("int",    "Parse")               => "__int_parse",
                ("long",   "Parse")               => "__long_parse",
                ("double", "Parse")               => "__double_parse",
                ("float",  "Parse")               => "__double_parse",
                _ => null
            };
            if (builtinName != null)
            {
                int dst = Alloc();
                Emit(new BuiltinInstr(dst, builtinName, kwArgRegs));
                return dst;
            }
        }

        // ── Stdlib static calls: Console.X, Assert.X, Math.X, String.IsNull... ─
        // Resolved against the pre-loaded StdlibCallIndex; emits CallInstr to the
        // fully-qualified stdlib function rather than a raw BuiltinInstr.
        if (call.Callee is MemberExpr { Target: IdentExpr { Name: var stdCls }, Member: var stdMethod }
            && _stdlibIndex.TryGetStatic(stdCls, stdMethod, out var stdStaticEntry))
        {
            var argRegs = call.Args.Select(EmitExpr).ToList();
            // Console.Write* with >1 arg: concat all into a single string first.
            if (stdCls == "Console" && argRegs.Count != 1)
                argRegs = [EmitConcat(argRegs)];
            _usedStdlibNamespaces.Add(stdStaticEntry.Namespace);
            int dst = Alloc();
            Emit(new CallInstr(dst, stdStaticEntry.QualifiedName, argRegs));
            return dst;
        }

        // ── List / Dictionary pseudo-class instance methods ──────────────────
        // List<T> and Dictionary<K,V> use Array/Map VM values; VCallInstr cannot dispatch
        // on non-object values, so their methods must be routed to builtins directly.
        // `Contains` is also intercepted here: __contains handles both strings and arrays,
        // removing the Assert.Contains / String.Contains instance-index ambiguity entirely.
        if (call.Callee is MemberExpr { Target: var collTarget, Member: var collMethod })
        {
            string? collBuiltin = collMethod switch {
                "Add"         => "__list_add",
                "RemoveAt"    => "__list_remove_at",
                "Insert"      => "__list_insert",
                "Clear"       => "__list_clear",
                "Sort"        => "__list_sort",
                "Reverse"     => "__list_reverse",
                "ContainsKey" => "__dict_contains_key",
                // Remove: handles both List.Remove(value) and Dictionary.Remove(key)
                "Remove"      => "__dict_remove",
                // Contains: __contains handles both String and Array receivers
                "Contains"    => "__contains",
                _             => null
            };
            if (collBuiltin != null)
            {
                int receiverReg = EmitExpr(collTarget);
                var collArgRegs = new List<int> { receiverReg };
                collArgRegs.AddRange(call.Args.Select(EmitExpr));
                int dst = Alloc();
                Emit(new BuiltinInstr(dst, collBuiltin, collArgRegs));
                return dst;
            }
        }

        // ── Stdlib instance calls: str.Substring, str.ToLower, etc. ─────────
        // For unambiguous instance method names, emit CallInstr([receiver, ...args]).
        // Ambiguous names fall through to VCallInstr.
        if (call.Callee is MemberExpr { Target: var instTarget, Member: var instMethod }
            && _stdlibIndex.TryGetInstance(instMethod, call.Args.Count, out var stdInstEntry))
        {
            int receiverReg = EmitExpr(instTarget);
            var argRegs     = new List<int> { receiverReg };
            argRegs.AddRange(call.Args.Select(EmitExpr));
            _usedStdlibNamespaces.Add(stdInstEntry.Namespace);
            int dst = Alloc();
            Emit(new CallInstr(dst, stdInstEntry.QualifiedName, argRegs));
            return dst;
        }

        // ── Instance method call via virtual dispatch: obj.Method(args) ─────
        if (call.Callee is MemberExpr mMethod)
        {
            int objReg  = EmitExpr(mMethod.Target);
            var argRegs = call.Args.Select(EmitExpr).ToList();
            // Fill default args: search _classMethods for a class that owns this method
            // and use its _funcParams entry to expand omitted trailing arguments.
            var vcallKey = FindVcallParamsKey(mMethod.Member, argRegs.Count);
            if (vcallKey is not null)
                argRegs = FillDefaults(vcallKey, argRegs);
            int dst = Alloc();
            Emit(new VCallInstr(dst, objReg, mMethod.Member, argRegs));
            return dst;
        }

        // ── Free function call: Foo(args) ─────────────────────────────────────
        if (call.Callee is IdentExpr funcId)
        {
            string callName;
            if (_topLevelFunctionNames.Contains(funcId.Name))
                callName = QualifyName(funcId.Name);
            else if (_currentClassName is not null
                && _classStaticMethods.TryGetValue(QualifyName(_currentClassName), out var curStaticSet)
                && curStaticSet.Contains(funcId.Name))
                callName = $"{QualifyName(_currentClassName)}.{funcId.Name}";
            else
                callName = funcId.Name;
            var argRegs = FillDefaults(callName, call.Args.Select(EmitExpr).ToList());
            int dst = Alloc();
            Emit(new CallInstr(dst, callName, argRegs));
            return dst;
        }

        throw new NotSupportedException($"call pattern not supported: {call.Callee.GetType().Name}");
    }

    /// Fill omitted trailing args with their default value expressions.
    private List<int> FillDefaults(string qualifiedName, List<int> argRegs)
    {
        if (!_funcParams.TryGetValue(qualifiedName, out var parms)) return argRegs;
        if (argRegs.Count >= parms.Count) return argRegs;
        var filled = new List<int>(argRegs);
        for (int i = argRegs.Count; i < parms.Count; i++)
        {
            var defaultExpr = parms[i].Default
                ?? throw new InvalidOperationException(
                    $"missing argument {i + 1} for `{qualifiedName}` and no default value");
            filled.Add(EmitExpr(defaultExpr));
        }
        return filled;
    }

    private int EmitBuiltin(string name, List<int> args)
    {
        int dst = Alloc();
        Emit(new BuiltinInstr(dst, name, args));
        return dst;
    }

    // ── String interpolation ──────────────────────────────────────────────────

    private int EmitInterpolation(InterpolatedStrExpr interp)
    {
        if (interp.Parts.Count == 0)
        {
            int dst = Alloc();
            Emit(new ConstStrInstr(dst, Intern("")));
            return dst;
        }

        var partRegs = interp.Parts.Select(EmitPart).ToList();
        return EmitConcat(partRegs);
    }

    private int EmitPart(InterpolationPart part) => part switch
    {
        TextPart tp => EmitTextPart(tp),
        ExprPart ep => EmitExprPart(ep),
        _           => throw new NotSupportedException(part.GetType().Name)
    };

    private int EmitTextPart(TextPart tp)
    {
        int dst = Alloc();
        Emit(new ConstStrInstr(dst, Intern(tp.Text)));
        return dst;
    }

    private int EmitExprPart(ExprPart ep)
    {
        int exprReg = EmitExpr(ep.Inner);
        bool isStringLit = ep.Inner is LitStrExpr or InterpolatedStrExpr;
        if (!isStringLit)
        {
            int strReg = Alloc();
            Emit(new ToStrInstr(strReg, exprReg));
            return strReg;
        }
        return exprReg;
    }

    private int EmitConcat(List<int> regs)
    {
        int result = regs[0];
        for (int i = 1; i < regs.Count; i++)
        {
            int dst = Alloc();
            Emit(new StrConcatInstr(dst, result, regs[i]));
            result = dst;
        }
        return result;
    }

    // ── Switch expression ─────────────────────────────────────────────────────

    /// Compiles `subject switch { p1 => e1, p2 => e2, _ => e3 }` as an if-else chain.
    private int EmitSwitchExpr(SwitchExpr sw)
    {
        int subjReg = EmitExpr(sw.Subject);
        int result  = Alloc();
        string endLbl = FreshLabel("sw_end");

        foreach (var arm in sw.Arms)
        {
            if (arm.Pattern == null)
            {
                // default arm: unconditionally emit body
                int defReg = EmitExpr(arm.Body);
                Emit(new CopyInstr(result, defReg));
                EndBlock(new BrTerm(endLbl));
                break;
            }

            string thenLbl = FreshLabel("sw_arm");
            string nextLbl = FreshLabel("sw_next");

            int patReg  = EmitExpr(arm.Pattern);
            int cmpReg  = Alloc();
            Emit(new EqInstr(cmpReg, subjReg, patReg));
            EndBlock(new BrCondTerm(cmpReg, thenLbl, nextLbl));

            StartBlock(thenLbl);
            int bodyReg = EmitExpr(arm.Body);
            Emit(new CopyInstr(result, bodyReg));
            EndBlock(new BrTerm(endLbl));

            StartBlock(nextLbl);
        }

        // If no arm matched and no default, fall through to end
        if (!_blockEnded) EndBlock(new BrTerm(endLbl));

        StartBlock(endLbl);
        return result;
    }
}
