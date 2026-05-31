using Z42.Syntax.Parser;
using Z42.IR;
using Z42.Semantics.TypeCheck;

namespace Z42.Semantics.Codegen;

/// Per-function emission helpers: block management, line table tracking,
/// register allocation, name write-back, and Z42Type/TypeExpr → IrType
/// mapping. spec split-function-emitter (2026-05-12): extracted verbatim
/// from FunctionEmitter.cs to keep the main file under the 500 LOC hard
/// limit. These methods are called by every code path in the emitter; no
/// behavior change.
internal sealed partial class FunctionEmitter
{
    // ── Block management ─────────────────────────────────────────────────────

    private void StartBlock(string label)
    {
        _curLabel   = label;
        _curInstrs  = new List<IrInstr>();
        _blockEnded = false;
    }

    private void EndBlock(IrTerminator term)
    {
        if (_blockEnded) return;
        _blocks.Add(new IrBlock(_curLabel, _curInstrs, term));
        _blockEnded = true;
    }

    private string FreshLabel(string hint) => $"{hint}_{_nextLabelId++}";

    private void Emit(IrInstr instr)
    {
        if (!_blockEnded)
            _curInstrs.Add(instr);
    }

    /// Record a source location before emitting instructions for a node.
    /// Only emits a line table entry when the line number changes (RLE compression).
    /// 2026-05-10 span-column-propagate: also carries Span.Column so runtime
    /// stack traces can show `(file:line:col)` instead of `(file:line)`.
    /// 2026-05-31 fix-line-entry-file-population: always stamp the enclosing
    /// source file (was: only cross-file references). Without this, the
    /// common case (function body all in one file) produced LineEntry.file =
    /// null for every entry, so runtime stack traces fell back to the
    /// no-file `(line N, col M)` shape and IDE jump-to-source was unusable.
    /// 2026-05-31 fix-dbug-file-basename: bake only the file BASENAME, not the
    /// full source path. The path handed to the Lexer is absolute + machine-
    /// /OS-specific (`/Users/…/source.z42` vs `D:\a\…\source.z42`), which made
    /// DBUG bytes — and therefore the format golden fixtures — non-reproducible
    /// across machines (CI on Windows/Linux mismatched fixtures generated on
    /// macOS). Basename is stable everywhere and still gives `(source.z42:42)`
    /// for stack traces / IDE jump. (Repo-relative remap is a future option if
    /// basename ambiguity ever matters.)
    private void TrackLine(Core.Text.Span span)
    {
        if (span.Line <= 0 || span.Line == _lastLine) return;
        _lastLine = span.Line;
        int blockIdx = _blocks.Count; // current block = next to be sealed
        int instrIdx = _curInstrs.Count;
        string? file = BaseName(span.File ?? _sourceFile);
        _lineTable.Add(new IrLineEntry(blockIdx, instrIdx, span.Line, file, span.Column));
    }

    /// Last path segment, splitting on BOTH `/` and `\` so the result is
    /// identical regardless of the host OS that produced the path (we must
    /// not use `Path.GetFileName`, which only splits on the host separator —
    /// e.g. it leaves a Windows `\` path untouched on Unix). Empty/null →
    /// null (no file info). fix-dbug-file-basename (2026-05-31).
    private static string? BaseName(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        int cut = -1;
        for (int i = path.Length - 1; i >= 0; i--)
        {
            if (path[i] == '/' || path[i] == '\\') { cut = i; break; }
        }
        string name = cut >= 0 ? path[(cut + 1)..] : path;
        return name.Length == 0 ? null : name;
    }

    private TypedReg Alloc(IrType type = IrType.Unknown) => new(_nextReg++, type);

    // ── TypeExpr name + name write-back ──────────────────────────────────────

    private static string TypeName(TypeExpr t) => t switch
    {
        NamedType nt  => nt.Name,
        VoidType      => "void",
        OptionType ot => TypeName(ot.Inner) + "?",
        ArrayType at  => TypeName(at.Element) + "[]",
        // 2026-04-28 fix-generic-type-roundtrip：保留 generic type-args（之前
        // 落到 "unknown"），让 KeyValuePair<K, V>[] 等返回类型在 IR FUNC.RetType
        // 字段里保持完整名字，下游 TypeChecker 能正确还原 instantiation 关系。
        GenericType gt => $"{gt.Name}<{string.Join(", ", gt.TypeArgs.Select(TypeName))}>",
        _             => "unknown"
    };

    /// Write a new value back to a named variable (now pure register-based).
    private void WriteBackName(string name, TypedReg valReg)
    {
        if (_instanceFields.Contains(name))
        {
            Emit(new FieldSetInstr(new TypedReg(0, IrType.Ref), name, valReg));
        }
        else
        {
            // All local variables (mutable or not) now have a register ID
            if (!_locals.TryGetValue(name, out var varReg))
            {
                // First assignment to this variable: allocate a new register
                varReg = new TypedReg(_nextReg++, valReg.Type);
                _locals[name] = varReg;
            }
            // Copy the value to the variable's register
            if (varReg.Id != valReg.Id)
                Emit(new CopyInstr(varReg, valReg));
        }
    }

    // ── Debug: local variable table snapshot ─────────────────────────────────

    private List<IrLocalVarEntry>? SnapshotLocalVarTable()
    {
        if (_locals.Count == 0) return null;
        return _locals
            .Select(kv => new IrLocalVarEntry(kv.Key, kv.Value.Id))
            .OrderBy(e => e.RegId)
            .ToList();
    }

    // ── Z42Type / TypeExpr → IrType mapping ─────────────────────────────────
    // All mappings now come from TypeRegistry (single source of truth).

    /// Maps a Z42 semantic type to an IR type tag.
    internal static IrType ToIrType(Z42Type type) => type switch
    {
        Z42PrimType { Name: var n } => TypeRegistry.GetIrType(n),
        Z42ArrayType or Z42ClassType or Z42OptionType or Z42NullType => IrType.Ref,
        Z42VoidType => IrType.Void,
        _ => IrType.Unknown,
    };

    /// Maps a parser TypeExpr to an IrType (used for parameters where no Z42Type is available).
    internal static IrType ToIrType(TypeExpr typeExpr) => typeExpr switch
    {
        NamedType { Name: var n } => TypeRegistry.GetIrType(n),
        ArrayType or OptionType => IrType.Ref,
        VoidType => IrType.Void,
        _ => IrType.Unknown,
    };
}
