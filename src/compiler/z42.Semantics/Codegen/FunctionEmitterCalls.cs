using Z42.Semantics.Bound;
using Z42.Semantics.TypeCheck;
using Z42.Syntax.Parser;
using Z42.IR;

namespace Z42.Semantics.Codegen;

/// Call, string interpolation, and switch expression emission — part of FunctionEmitter.
internal sealed partial class FunctionEmitter
{
    // ── Indirect call (lambda / delegate / function-value) ────────────────────

    /// split-symbol-from-type Phase 4: emits IR for `BoundIndirectCall` —
    /// the callee is a `Z42FuncType`-typed expression (lambda literal, function
    /// variable, closure, or method-group conversion). Always emits
    /// `CallIndirectInstr` (no static dispatch path).
    private TypedReg EmitBoundIndirectCall(BoundIndirectCall ic)
    {
        var calleeReg = EmitExpr(ic.Callee);
        var argRegs   = ic.Args.Select(EmitExpr).ToList();
        var dst       = Alloc(ToIrType(ic.Type));
        Emit(new CallIndirectInstr(dst, calleeReg, argRegs));
        return dst;
    }

    // ── Bound call dispatcher ─────────────────────────────────────────────────

    private TypedReg EmitBoundCall(BoundCall call)
    {
        // Spec impl-ref-out-in-runtime: detect ref/out/in callsite arguments
        // (`BoundModifiedArg`) and emit `LoadLocalAddr` / `LoadElemAddr` /
        // `LoadFieldAddr` to materialise a `Value::Ref` register before the
        // Call. The Ref flows through the Call's args; runtime entry
        // copy-in derefs into a normal value, exit copy-out stores back.
        var argRegs = call.Args.Select(EmitCallArg).ToList();
        return call.Kind switch
        {
            BoundCallKind.Static   => EmitStaticBoundCall(call, argRegs),
            BoundCallKind.Instance => EmitInstanceBoundCall(call, argRegs),
            BoundCallKind.Virtual  => EmitVirtualBoundCall(call, argRegs),
            BoundCallKind.Free     => EmitFreeBoundCall(call, argRegs),
            _                      => throw new NotSupportedException($"call kind {call.Kind}"),
        };
    }

    /// Static method call: DepIndex (stdlib) → user-defined fallback.
    /// Console's variadic surface gets a special concat path before emit.
    private TypedReg EmitStaticBoundCall(BoundCall call, List<TypedReg> argRegs)
    {
        // Try DepIndex first (for stdlib methods)
        // Try arity-qualified name first, then bare name for overloaded methods
        var arityMethod = $"{call.MethodName}${argRegs.Count}";
        if (!_ctx.DepIndex.TryGetStatic(call.ReceiverClass!, arityMethod, out var depEntry))
            _ctx.DepIndex.TryGetStatic(call.ReceiverClass!, call.MethodName!, out depEntry);
        if (depEntry is not null)
        {
            if (call.ReceiverClass == "Console" && argRegs.Count != 1)
                argRegs = [EmitConcat(argRegs)];
            _ctx.TrackDepNamespace(depEntry.Namespace);
            var dst = Alloc(ToIrType(call.Type));
            Emit(new CallInstr(dst, depEntry.QualifiedName, argRegs));
            return dst;
        }

        // Fall back to user-defined static methods
        var qualClass = _ctx.QualifyClassName(call.ReceiverClass!);
        // Track dependency namespace for imported class calls
        if (_ctx.ImportedClassNamespaces.TryGetValue(call.ReceiverClass!, out var depNs))
            _ctx.TrackDepNamespace(depNs);
        var arityKey  = $"{call.MethodName}${argRegs.Count}";
        string resolved = call.MethodName!;
        if (_ctx.ClassRegistry.TryGetStaticMethods(qualClass, out var sSet))
            resolved = sSet.Contains(call.MethodName!) ? call.MethodName!
                     : sSet.Contains(arityKey)         ? arityKey : call.MethodName!;
        var callName = $"{qualClass}.{resolved}";
        argRegs = FillDefaults(callName, argRegs);
        var dst2 = Alloc(ToIrType(call.Type));
        Emit(new CallInstr(dst2, callName, argRegs));
        return dst2;
    }

    /// Instance method call: builtin-collection BuiltinInstr → DepIndex
    /// (imported only) → VCall fallback with FillDefaults fallback chain.
    private TypedReg EmitInstanceBoundCall(BoundCall call, List<TypedReg> argRegs)
    {
        var objReg = EmitExpr(call.Receiver!);

        // Builtin type method: Array
        // 2026-04-26 script-first-stringbuilder: StringBuilder removed from
        // pseudo-class list (now pure z42 script). Only Array remains as
        // pseudo-class (its methods like .Length live on built-in array values).
        if (IsBuiltinCollectionType(call.ReceiverClass))
        {
            string? builtinName = ResolveBuiltinMethod(call.MethodName!, argRegs.Count);
            if (builtinName is not null)
            {
                var fullArgRegs = new List<TypedReg> { objReg };
                fullArgRegs.AddRange(argRegs);
                var dst = Alloc(ToIrType(call.Type));
                Emit(new BuiltinInstr(dst, builtinName, fullArgRegs));
                return dst;
            }
        }

        // For non-builtin-collection types, just try DepIndex

        // Instance methods: try DepIndex next (for stdlib methods not in builtin resolution).
        // L3-G4d: only consult DepIndex when the receiver is an imported class
        // (or unknown). A locally-defined class with the same short name as a stdlib
        // class must dispatch to its OWN method, not be hijacked by a DepIndex match
        // on method-name+arity alone (e.g. user `class Stack` vs `Std.Collections.Stack`).
        bool receiverIsLocalClass = call.ReceiverClass is not null
            && !_ctx.ImportedClassNamespaces.ContainsKey(call.ReceiverClass);
        if (!receiverIsLocalClass
            && _ctx.DepIndex.TryGetInstance(call.MethodName!, call.Args.Count, out var depEntry))
        {
            // 2026-05-04 fix-default-param-cross-cu (D-9)：DepIndex 路径
            // 此前不调 FillDefaults 导致跨 CU 默认参数漏填。FillDefaults
            // 用 _funcSignatures fallback 走 type-default 路径。
            argRegs = FillDefaults(depEntry.QualifiedName, argRegs);
            var fullArgRegs = new List<TypedReg> { objReg };
            fullArgRegs.AddRange(argRegs);
            _ctx.TrackDepNamespace(depEntry.Namespace);
            var dst = Alloc(ToIrType(call.Type));
            Emit(new CallInstr(dst, depEntry.QualifiedName, fullArgRegs));
            return dst;
        }

        // User-defined class instance methods: fall back to virtual dispatch
        return EmitInstanceVCallFallback(call, argRegs, objReg);
    }

    /// VCall fallback for instance calls when DepIndex / builtin-collection
    /// didn't match. Tries `_ctx.FindVcallParamsKey` first, then a
    /// receiver-qualified `_funcSignatures` lookup (fix-default-param-cross-cu D-9
    /// — imported classes whose FuncParams aren't in scope).
    private TypedReg EmitInstanceVCallFallback(BoundCall call, List<TypedReg> argRegs, TypedReg objReg)
    {
        var vcallKey = _ctx.FindVcallParamsKey(call.MethodName!, argRegs.Count);
        if (vcallKey is not null)
        {
            argRegs = FillDefaults(vcallKey, argRegs);
        }
        else if (call.ReceiverClass is not null)
        {
            // 2026-05-04 fix-default-param-cross-cu (D-9)：FuncParams 没找到
            // （imported 类）→ 用 ReceiverClass 构造 qualified key 查
            // _funcSignatures 直接 fallback。受限于具体 receiver 避免
            // 全 _entries 遍历的歧义匹配（如多类同名方法）。
            var receiverKey = $"{_ctx.QualifyClassName(call.ReceiverClass)}.{call.MethodName}";
            if (_ctx.TryGetMethodSignature(receiverKey, out _))
                argRegs = FillDefaults(receiverKey, argRegs);
        }
        var dst = Alloc(ToIrType(call.Type));
        Emit(new VCallInstr(dst, objReg, call.MethodName!, argRegs));
        return dst;
    }

    /// Virtual dispatch for interface / abstract methods.
    private TypedReg EmitVirtualBoundCall(BoundCall call, List<TypedReg> argRegs)
    {
        var objReg = EmitExpr(call.Receiver!);
        var vcallKey = _ctx.FindVcallParamsKey(call.MethodName!, argRegs.Count);
        if (vcallKey is not null) argRegs = FillDefaults(vcallKey, argRegs);
        var dst = Alloc(ToIrType(call.Type));
        Emit(new VCallInstr(dst, objReg, call.MethodName!, argRegs));
        return dst;
    }

    /// Free function / lambda / closure / static-method-as-free-name dispatch.
    /// Three cases (in order): indirect callee → capturing-local-fn closure
    /// indirect → static name resolution (lifted local / top-level /
    /// current-class static / bare).
    private TypedReg EmitFreeBoundCall(BoundCall call, List<TypedReg> argRegs)
    {
        // impl-lambda-l2: indirect call via FuncRef-typed expression.
        // BindCall populates `Receiver` (with `CalleeName == null`) when the
        // callee is a local var or arbitrary expr of `Z42FuncType`.
        if (call.Receiver is { } indirectCallee)
        {
            var calleeReg = EmitExpr(indirectCallee);
            var idst      = Alloc(ToIrType(call.Type));
            Emit(new CallIndirectInstr(idst, calleeReg, argRegs));
            return idst;
        }

        // impl-closure-l3-core: a call to a name defined as a *capturing*
        // local fn in the current emitter scope is dispatched indirectly
        // via the closure value stored in `_locals[name]`. Capturing
        // local fns are removed from `_localFnLiftedNames` (handled at
        // the declaration site). See impl-closure-l3-core Decision 9.
        if (call.CalleeName is { } cnCap
            && !_localFnLiftedNames.ContainsKey(cnCap)
            && _locals.TryGetValue(cnCap, out var closureReg))
        {
            var dstClosure = Alloc(ToIrType(call.Type));
            Emit(new CallIndirectInstr(dstClosure, closureReg, argRegs));
            return dstClosure;
        }

        // impl-local-fn-l2: a call to a name defined as a local fn in
        // the current emitter scope routes to its lifted module-level
        // name (`<Owner>__<LocalName>`). Local fn shadows top-level.
        string callName;
        if (call.CalleeName is { } cn0
            && _localFnLiftedNames.TryGetValue(cn0, out var liftedName))
            callName = liftedName;
        else if (_ctx.TopLevelFunctionNames.Contains(call.CalleeName!))
            callName = _ctx.QualifyName(call.CalleeName!);
        else if (_currentClassName is not null
            && _ctx.ClassRegistry.TryGetStaticMethods(
                _ctx.QualifyName(_currentClassName), out var cSet)
            && cSet.Contains(call.CalleeName!))
            callName = $"{_ctx.QualifyName(_currentClassName)}.{call.CalleeName!}";
        else
            callName = call.CalleeName!;
        argRegs = FillDefaults(callName, argRegs);
        var dst = Alloc(ToIrType(call.Type));
        Emit(new CallInstr(dst, callName, argRegs));
        return dst;
    }

    /// L3-G4h step3 + 2026-04-26 script-first-stringbuilder: pseudo-class List /
    /// Dictionary / StringBuilder removed —— 仅 Array 仍走 BuiltinInstr fast path
    /// (.Length / .Resize 直接作用于 VM 的 Array Value)。
    private bool IsBuiltinCollectionType(string? className)
    {
        return className == "Array";
    }

    /// Map builtin type method names to their BuiltinInstr function name.
    /// 当前无 Array 实例方法走这条路径（Array.Length 通过 builtin 但不经由此处）。
    private string? ResolveBuiltinMethod(string method, int userArgCount)
    {
        return null;
    }

}
