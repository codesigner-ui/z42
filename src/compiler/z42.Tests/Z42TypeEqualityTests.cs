using FluentAssertions;
using Z42.Semantics.TypeCheck;
using Z42.Syntax.Parser;

namespace Z42.Tests;

/// 2026-05-03 fix-z42type-structural-equality：验证 Z42InstantiatedType /
/// Z42InterfaceType / Z42FuncType 的 Equals/GetHashCode override 走元素级
/// 真比较（C# record 默认对 IReadOnlyList 字段是引用比较）。
public sealed class Z42TypeEqualityTests
{
    private static Z42ClassType MakeClass(string name, params string[] typeParams) =>
        new(
            Name: name,
            Fields: new Dictionary<string, Z42Type>(),
            Methods: new Dictionary<string, Z42FuncType>(),
            StaticFields: new Dictionary<string, Z42Type>(),
            StaticMethods: new Dictionary<string, Z42FuncType>(),
            MemberVisibility: new Dictionary<string, Visibility>(),
            BaseClassName: null,
            TypeParams: typeParams.Length == 0 ? null : new List<string>(typeParams));

    private static Z42InterfaceType MakeInterface(string name, IReadOnlyList<Z42Type>? typeArgs = null,
        IReadOnlyList<string>? typeParams = null)
    {
        var methods = new Dictionary<string, Z42FuncType>();
        return new Z42InterfaceType(name, methods, typeArgs, null, typeParams);
    }

    // ── Z42InstantiatedType ──────────────────────────────────────────────────

    [Fact]
    public void Instantiated_SameDef_SameArgs_AreEqual()
    {
        var fooDef = MakeClass("Foo", "T");
        var a = new Z42InstantiatedType(fooDef, new List<Z42Type> { Z42Type.Int });
        var b = new Z42InstantiatedType(fooDef, new List<Z42Type> { Z42Type.Int });
        a.Equals(b).Should().BeTrue();
        a.GetHashCode().Should().Be(b.GetHashCode());
        (a == b).Should().BeTrue();
    }

    [Fact]
    public void Instantiated_NestedSame_AreEqual()
    {
        var foo = MakeClass("Foo", "T");
        var bar = MakeClass("Bar", "T");
        var inner1 = new Z42InstantiatedType(bar, new List<Z42Type> { Z42Type.Int });
        var inner2 = new Z42InstantiatedType(bar, new List<Z42Type> { Z42Type.Int });
        var a = new Z42InstantiatedType(foo, new List<Z42Type> { inner1 });
        var b = new Z42InstantiatedType(foo, new List<Z42Type> { inner2 });
        a.Equals(b).Should().BeTrue();
        a.GetHashCode().Should().Be(b.GetHashCode());
    }

    [Fact]
    public void Instantiated_DiffDef_NotEqual()
    {
        var foo = MakeClass("Foo", "T");
        var bar = MakeClass("Bar", "T");
        var a = new Z42InstantiatedType(foo, new List<Z42Type> { Z42Type.Int });
        var b = new Z42InstantiatedType(bar, new List<Z42Type> { Z42Type.Int });
        a.Equals(b).Should().BeFalse();
    }

    [Fact]
    public void Instantiated_DiffArgs_NotEqual()
    {
        var foo = MakeClass("Foo", "T");
        var a = new Z42InstantiatedType(foo, new List<Z42Type> { Z42Type.Int });
        var b = new Z42InstantiatedType(foo, new List<Z42Type> { Z42Type.String });
        a.Equals(b).Should().BeFalse();
    }

    [Fact]
    public void Instantiated_DiffArity_NotEqual()
    {
        var foo = MakeClass("Foo", "T", "U");
        var a = new Z42InstantiatedType(foo, new List<Z42Type> { Z42Type.Int });
        var b = new Z42InstantiatedType(foo, new List<Z42Type> { Z42Type.Int, Z42Type.String });
        a.Equals(b).Should().BeFalse();
    }

    // ── Z42InterfaceType ─────────────────────────────────────────────────────

    [Fact]
    public void Interface_SameName_SameArgs_AreEqual()
    {
        var a = MakeInterface("ISubscription", new List<Z42Type> { Z42Type.Int });
        var b = MakeInterface("ISubscription", new List<Z42Type> { Z42Type.Int });
        a.Equals(b).Should().BeTrue();
        a.GetHashCode().Should().Be(b.GetHashCode());
        (a == b).Should().BeTrue();
    }

    [Fact]
    public void Interface_NonGeneric_BothNullArgs_AreEqual()
    {
        var a = MakeInterface("IDisposable");
        var b = MakeInterface("IDisposable");
        a.Equals(b).Should().BeTrue();
        a.GetHashCode().Should().Be(b.GetHashCode());
    }

    [Fact]
    public void Interface_DiffArgs_NotEqual()
    {
        var a = MakeInterface("ISubscription", new List<Z42Type> { Z42Type.Int });
        var b = MakeInterface("ISubscription", new List<Z42Type> { Z42Type.String });
        a.Equals(b).Should().BeFalse();
    }

    [Fact]
    public void Interface_DiffTypeParams_NotEqual()
    {
        var a = MakeInterface("ISubscription", typeParams: new List<string> { "T" });
        var b = MakeInterface("ISubscription", typeParams: new List<string> { "U" });
        a.Equals(b).Should().BeFalse();
    }

    // ── Z42FuncType ─────────────────────────────────────────────────────────

    [Fact]
    public void FuncType_SameParams_SameRet_AreEqual()
    {
        var a = new Z42FuncType(new List<Z42Type> { Z42Type.Int, Z42Type.String }, Z42Type.Bool);
        var b = new Z42FuncType(new List<Z42Type> { Z42Type.Int, Z42Type.String }, Z42Type.Bool);
        a.Equals(b).Should().BeTrue();
        a.GetHashCode().Should().Be(b.GetHashCode());
        (a == b).Should().BeTrue();
    }

    [Fact]
    public void FuncType_DiffArity_NotEqual()
    {
        var a = new Z42FuncType(new List<Z42Type> { Z42Type.Int }, Z42Type.Bool);
        var b = new Z42FuncType(new List<Z42Type> { Z42Type.Int, Z42Type.Int }, Z42Type.Bool);
        a.Equals(b).Should().BeFalse();
    }

    [Fact]
    public void FuncType_DiffParam_NotEqual()
    {
        var a = new Z42FuncType(new List<Z42Type> { Z42Type.Int }, Z42Type.Bool);
        var b = new Z42FuncType(new List<Z42Type> { Z42Type.String }, Z42Type.Bool);
        a.Equals(b).Should().BeFalse();
    }

    [Fact]
    public void FuncType_DiffRet_NotEqual()
    {
        var a = new Z42FuncType(new List<Z42Type> { Z42Type.Int }, Z42Type.Bool);
        var b = new Z42FuncType(new List<Z42Type> { Z42Type.Int }, Z42Type.Int);
        a.Equals(b).Should().BeFalse();
    }

    // ── HashSet behaviour ───────────────────────────────────────────────────

    [Fact]
    public void HashSet_LookupHits_AllRecords()
    {
        var foo = MakeClass("Foo", "T");
        var inst1 = new Z42InstantiatedType(foo, new List<Z42Type> { Z42Type.Int });
        var inst2 = new Z42InstantiatedType(foo, new List<Z42Type> { Z42Type.Int });
        var iface1 = MakeInterface("ISub", new List<Z42Type> { Z42Type.Int });
        var iface2 = MakeInterface("ISub", new List<Z42Type> { Z42Type.Int });
        var fn1 = new Z42FuncType(new List<Z42Type> { Z42Type.Int }, Z42Type.Bool);
        var fn2 = new Z42FuncType(new List<Z42Type> { Z42Type.Int }, Z42Type.Bool);

        var set = new HashSet<Z42Type> { inst1, iface1, fn1 };
        set.Contains(inst2).Should().BeTrue();
        set.Contains(iface2).Should().BeTrue();
        set.Contains(fn2).Should().BeTrue();
    }

    // ── D2b 集成场景：IsAssignableTo 触底路径 ──────────────────────────────

    [Fact]
    public void IsAssignableTo_InterfaceTypeWithSameTypeArgs_True()
    {
        // 复现 D2b "cannot assign ISubscription<(T)->void> to ISubscription<(T)->void>"
        // bug：同名 + 同 TypeArgs 但不同对象的两个 Z42InterfaceType 应可互相赋值。
        var fnTy = new Z42FuncType(new List<Z42Type> { Z42Type.Int }, Z42Type.Void);
        var src = MakeInterface("ISubscription", new List<Z42Type> { fnTy });
        var tgt = MakeInterface("ISubscription", new List<Z42Type> { fnTy });
        Z42Type.IsAssignableTo(tgt, src).Should().BeTrue();
    }
}
