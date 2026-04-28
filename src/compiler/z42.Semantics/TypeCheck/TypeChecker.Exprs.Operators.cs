using Z42.Core.Text;
using Z42.Core.Diagnostics;
using Z42.Semantics.Bound;
using Z42.Syntax.Parser;

namespace Z42.Semantics.TypeCheck;

/// Assignment + binary operator binding (incl. operator overload dispatch +
/// indexer / property-setter helpers) — part of the TypeChecker partial class.
///
/// Pairs with `TypeChecker.Exprs.cs` (dispatcher + simple bindings) and
/// `TypeChecker.Exprs.Members.cs` (member access).
public sealed partial class TypeChecker
{
    // ── Assignment ────────────────────────────────────────────────────────────

    private BoundExpr BindAssign(AssignExpr assign, TypeEnv env)
    {
        // L3-G4e: `obj[i] = v` on a class receiver with set_Item → dispatch to setter.
        // Detect BEFORE binding target (which would go through get_Item for class receiver).
        if (assign.Target is IndexExpr ixTgt)
        {
            var recv   = BindExpr(ixTgt.Target, env);
            var idx    = BindExpr(ixTgt.Index,  env);
            if (TryFindSetter(recv.Type, out var setter, out var className))
            {
                var value = BindExpr(assign.Value, env);
                RequireAssignable(setter!.Params[^1], value.Type, assign.Value.Span);
                var args = new List<BoundExpr> { idx, value };
                return new BoundCall(BoundCallKind.Virtual, recv, className, "set_Item",
                    null, args, value.Type, assign.Span);
            }
        }

        // Auto-property setter dispatch: `obj.Name = v` on a class/instantiated/interface
        // receiver with `set_<Name>` method → BoundCall to setter.
        // Detect BEFORE binding target (which would go through get_<Name> for property).
        if (assign.Target is MemberExpr memTgt)
        {
            var recv = BindExpr(memTgt.Target, env);
            if (TryFindPropertySetter(recv.Type, memTgt.Member,
                    out var propSetter, out var propClassName))
            {
                var value = BindExpr(assign.Value, env);
                RequireAssignable(propSetter!.Params[0], value.Type, assign.Value.Span);
                return new BoundCall(BoundCallKind.Virtual, recv, propClassName!,
                    $"set_{memTgt.Member}", null,
                    new List<BoundExpr> { value }, Z42Type.Void, assign.Span);
            }
        }

        var target    = BindExpr(assign.Target, env);
        var intLitVal = ExtractIntLiteralValue(assign.Value);
        BoundExpr value2;
        if (intLitVal != null)
        {
            var rangeOk = TryCheckIntLiteralRange(target.Type, intLitVal.Value, assign.Value.Span);
            value2 = BindExpr(assign.Value, env, target.Type);
            if (rangeOk == null)
                RequireAssignable(target.Type, value2.Type, assign.Value.Span);
        }
        else
        {
            value2 = BindExpr(assign.Value, env);
            RequireAssignable(target.Type, value2.Type, assign.Value.Span);
        }
        // Narrow from Unknown after first assignment
        if (assign.Target is IdentExpr id && target.Type is Z42UnknownType)
            env.Define(id.Name, value2.Type);
        return new BoundAssign(target, value2, value2.Type, assign.Span);
    }

    /// L3-G4e: if `recvType` is a class/instantiated class with `get_Item`, bind
    /// `obj[idx]` as a Virtual call to that method (with type-param substitution
    /// for instantiated generics). Returns null otherwise.
    private BoundExpr? TryBindIndexerGet(BoundExpr recv, BoundExpr index, Span span)
    {
        if (TryFindIndexer(recv.Type, "get_Item", out var mt, out var className, out var subMap))
        {
            var retType = SubstituteTypeParams(mt!.Ret, subMap);
            return new BoundCall(BoundCallKind.Virtual, recv, className, "get_Item",
                null, new List<BoundExpr> { index }, retType, span);
        }
        return null;
    }

    /// Tries to locate a `set_Item` on a class/instantiated class receiver, returning
    /// the substituted param list's value type via `setter.Params[^1]`.
    private bool TryFindSetter(Z42Type recvType,
                               out Z42FuncType? setter,
                               out string? className)
    {
        if (TryFindIndexer(recvType, "set_Item", out var mt, out var cls, out var subMap))
        {
            setter    = (Z42FuncType)SubstituteTypeParams(mt!, subMap);
            className = cls;
            return true;
        }
        setter = null; className = null; return false;
    }

    /// Auto-property setter lookup: `set_<propName>(value)` on class /
    /// instantiated class / interface receiver. Substitutes type params for
    /// instantiated generics. Returns false if no setter exists (readonly
    /// property or no property at all).
    private bool TryFindPropertySetter(Z42Type recvType, string propName,
                                       out Z42FuncType? setter,
                                       out string? className)
    {
        var setterName = $"set_{propName}";
        switch (recvType)
        {
            case Z42ClassType ct
                when ct.Methods.TryGetValue(setterName, out var mt)
                  && mt.Params.Count == 1:
                setter = mt; className = ct.Name; return true;
            case Z42InstantiatedType it
                when it.Definition.Methods.TryGetValue(setterName, out var mt)
                  && mt.Params.Count == 1:
                var subMap = BuildSubstitutionMap(it);
                setter = (Z42FuncType)SubstituteTypeParams(mt, subMap);
                className = it.Definition.Name;
                return true;
            case Z42InterfaceType ifa
                when ifa.Methods.TryGetValue(setterName, out var mt)
                  && mt.Params.Count == 1:
                setter = mt; className = ifa.Name; return true;
        }
        setter = null; className = null; return false;
    }

    private bool TryFindIndexer(Z42Type recvType, string name,
                                out Z42FuncType? method,
                                out string? className,
                                out IReadOnlyDictionary<string, Z42Type>? subMap)
    {
        method = null; className = null; subMap = null;
        Z42ClassType? def = null;
        switch (recvType)
        {
            case Z42ClassType ct:        def = ct; break;
            case Z42InstantiatedType it: def = it.Definition; subMap = BuildSubstitutionMap(it); break;
        }
        if (def is null) return false;
        if (!def.Methods.TryGetValue(name, out var mt)) return false;
        method = mt;
        className = def.Name;
        return true;
    }

    // ── Binary ────────────────────────────────────────────────────────────────

    private BoundExpr BindBinary(BinaryExpr bin, TypeEnv env)
    {
        var left  = BindExpr(bin.Left,  env);
        var right = BindExpr(bin.Right, env);
        var op    = ToBinaryOp(bin.Op);
        if (left.Type is Z42ErrorType || right.Type is Z42ErrorType)
            return new BoundBinary(op, left, right, Z42Type.Error, bin.Span);

        if (bin.Op == "+" && (left.Type == Z42Type.String || right.Type == Z42Type.String))
            return new BoundBinary(op, left, right, Z42Type.String, bin.Span);

        // L3 operator overload: `a + b` → call static `op_Add(a, b)` on a class/struct
        // or instance `a.op_Add(b)` on generic-param / class when static form absent.
        if (TryBindOperatorCall(bin.Op, left, right, bin.Span) is { } opCall)
            return opCall;

        if (!BinaryTypeTable.Rules.TryGetValue(bin.Op, out var rule))
            return new BoundBinary(op, left, right, Z42Type.Unknown, bin.Span);

        CheckBinaryOperand(rule.LeftOk,  rule.Requirement, left.Type,  bin.Left.Span,  bin.Op);
        CheckBinaryOperand(rule.RightOk, rule.Requirement, right.Type, bin.Right.Span, bin.Op);

        var outType = (left.Type is Z42ErrorType || right.Type is Z42ErrorType)
            ? Z42Type.Error : rule.Output(left.Type, right.Type);
        return new BoundBinary(op, left, right, outType, bin.Span);
    }

    /// L3 operator overload: try resolving `a <op> b` to an operator method call.
    /// Priority:
    ///   1. Primitive numeric pair — early exit to BinaryTypeTable (IR AddInstr etc.)
    ///   2. Static `op_<Name>(a, b)` on left.Type or right.Type (C# 11 operator /
    ///      static abstract interface member)
    ///   3. Generic param T with `where T: I<T>` interface declaring a 2-param
    ///      `static abstract op_<Name>` — VCall value-driven dispatch
    ///   4. User-class instance method `left.op_<Name>(right)` (non-INumber;
    ///      users can still define instance-form operators on concrete classes)
    /// Returns null when none match; caller falls back to BinaryTypeTable.
    private BoundExpr? TryBindOperatorCall(string op, BoundExpr left, BoundExpr right, Span span)
    {
        string? methodName = op switch
        {
            "+" => "op_Add",
            "-" => "op_Subtract",
            "*" => "op_Multiply",
            "/" => "op_Divide",
            "%" => "op_Modulo",
            _   => null,
        };
        if (methodName is null) return null;

        // Skip overload when both sides are plain primitive numeric — let BinaryTypeTable
        // emit the IR AddInstr fast path (no method dispatch).
        if (left.Type is Z42PrimType && right.Type is Z42PrimType
            && Z42Type.IsNumeric(left.Type) && Z42Type.IsNumeric(right.Type))
            return null;

        // 1. Static operator method on either side's class. Signature must match (L, R).
        if (TryLookupStaticOperator(left.Type, methodName, left.Type, right.Type) is { } ls)
            return new BoundCall(BoundCallKind.Static, null, ls.ClassName, methodName,
                null, [left, right], ls.ReturnType, span);
        if (TryLookupStaticOperator(right.Type, methodName, left.Type, right.Type) is { } rs)
            return new BoundCall(BoundCallKind.Static, null, rs.ClassName, methodName,
                null, [left, right], rs.ReturnType, span);

        // 2. Virtual dispatch on left.Type. Covers:
        //    - Generic param T: looked up through constraint interface's static abstract
        //      members (value-driven VCall — receiver's runtime class decides).
        //    - Concrete user class / struct: user-defined instance `op_<Name>(R)` method
        //      (non-INumber; pattern for classes that prefer instance-form operators).
        if (TryLookupInstanceOperator(left.Type, methodName, right.Type) is { } li)
            return new BoundCall(BoundCallKind.Virtual, left, li.ClassName, methodName,
                null, [right], li.ReturnType, span);

        return null;
    }

    private (string ClassName, Z42Type ReturnType)? TryLookupStaticOperator(
        Z42Type t, string methodName, Z42Type leftArg, Z42Type rightArg)
    {
        string? className = t switch
        {
            Z42ClassType ct         => ct.Name,
            Z42InstantiatedType inst => inst.Definition.Name,
            _                        => null,
        };
        if (className is null) return null;
        if (!_symbols.Classes.TryGetValue(className, out var classType)) return null;
        if (!classType.StaticMethods.TryGetValue(methodName, out var sig)) return null;
        if (sig.Params.Count != 2) return null;
        // Signature match: both arguments must be assignable to the declared params.
        if (!Z42Type.IsAssignableTo(sig.Params[0], leftArg)) return null;
        if (!Z42Type.IsAssignableTo(sig.Params[1], rightArg)) return null;
        return (className, ResolveStubType(sig.Ret));
    }

    /// Normalize a stub Z42ClassType (returned during first-pass signature collection,
    /// before class shapes were populated) to the fully-populated Z42ClassType from
    /// `_symbols.Classes`. Needed when operator method signatures reference their
    /// own enclosing class as return type.
    private Z42Type ResolveStubType(Z42Type t) =>
        t is Z42ClassType ct && _symbols.Classes.TryGetValue(ct.Name, out var full) ? full : t;

    private (string ClassName, Z42Type ReturnType)? TryLookupInstanceOperator(
        Z42Type t, string methodName, Z42Type rightArg)
    {
        // Concrete class / struct — instance method takes 1 param (other).
        if (t is Z42ClassType ct
            && _symbols.Classes.TryGetValue(ct.Name, out var classType)
            && classType.Methods.TryGetValue(methodName, out var mt)
            && mt.Params.Count == 1
            && Z42Type.IsAssignableTo(mt.Params[0], rightArg))
            return (ct.Name, ResolveStubType(mt.Ret));
        if (t is Z42InstantiatedType inst
            && inst.Definition.Methods.TryGetValue(methodName, out var instMt)
            && instMt.Params.Count == 1)
        {
            var subMap = BuildSubstitutionMap(inst);
            var substParam = SubstituteTypeParams(instMt.Params[0], subMap);
            if (!Z42Type.IsAssignableTo(substParam, rightArg)) return null;
            return (inst.Definition.Name, SubstituteTypeParams(instMt.Ret, subMap));
        }
        // Generic parameter — look through interface constraints (INumber<T> etc.)
        // for a `static abstract T op_Add(T a, T b)` declaration. IR-level
        // BoundCall(Virtual) prepends receiver, so VCall dispatches (left, right)
        // into the implementer's `static op_Add(a, b)` — 2 args match perfectly.
        if (t is Z42GenericParamType gp)
        {
            // Collect both the type-param's own InterfaceConstraints (built during
            // class/function signature collection) and the active where-clause bundle.
            var ifaces = new List<Z42InterfaceType>();
            if (gp.InterfaceConstraints is { } direct) ifaces.AddRange(direct);
            var bundle = _symbols.LookupEffectiveConstraints(gp.Name);
            foreach (var iface in bundle.Interfaces)
                if (!ifaces.Any(i => i.Name == iface.Name)) ifaces.Add(iface);

            foreach (var iface in ifaces)
                if (iface.StaticMembers is { } sm
                    && sm.TryGetValue(methodName, out var staticMember)
                    && staticMember.Signature.Params.Count == 2)
                    return (iface.Name, SubstituteGenericReturnType(staticMember.Signature.Ret, gp));
        }
        return null;
    }

    /// For `where T: I<T>` dispatch, substitute the interface's T in the return
    /// type with the caller's generic-param reference. Handles both
    /// `Z42GenericParamType` (modern static abstract signatures) and
    /// `Z42PrimType("T")` (legacy pre-TSIG-fix signatures).
    private static Z42Type SubstituteGenericReturnType(Z42Type ret, Z42GenericParamType callerGp) =>
        ret switch
        {
            Z42GenericParamType          => callerGp,
            Z42PrimType p when p.Name == callerGp.Name => callerGp,
            _                             => ret,
        };

    private void CheckBinaryOperand(
        Func<Z42Type, bool>? constraint, string requirement,
        Z42Type t, Span span, string op)
    {
        if (constraint == null || t is Z42UnknownType or Z42ErrorType) return;
        if (!constraint(t))
            _diags.Error(DiagnosticCodes.TypeMismatch,
                $"operator `{op}` requires {requirement} operand, got `{t}`", span);
    }
}
