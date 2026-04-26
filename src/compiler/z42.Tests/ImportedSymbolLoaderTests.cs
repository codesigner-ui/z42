using FluentAssertions;
using Z42.IR;
using Z42.Semantics.TypeCheck;

namespace Z42.Tests;

/// 验证 ImportedSymbolLoader 两阶段加载消除 self/forward-reference 降级。
///
/// 旧实现（单阶段）：RebuildClassType 解析自身字段时 classes 字典还没填到自己，
/// `Exception.InnerException: Exception` 字段类型被降级为 Z42PrimType("Exception")。
/// 两阶段后：Phase 1 先建空骨架登记进 classes，Phase 2 填充字段时 lookup 命中
/// → 字段类型是 Z42ClassType("Exception")。
public sealed class ImportedSymbolLoaderTests
{
    private static ExportedFieldDef Field(string name, string typeName) =>
        new(name, typeName, "public", IsStatic: false);

    private static ExportedClassDef Class(string name, string? baseClass,
        params ExportedFieldDef[] fields) =>
        new(name, baseClass,
            IsAbstract: false, IsSealed: false, IsStatic: false,
            Fields:     fields.ToList(),
            Methods:    new List<ExportedMethodDef>(),
            Interfaces: new List<string>(),
            TypeParams: null);

    private static ExportedClassDef GenericClass(string name,
        List<string> typeParams, params ExportedFieldDef[] fields) =>
        new(name, BaseClass: null,
            IsAbstract: false, IsSealed: false, IsStatic: false,
            Fields:     fields.ToList(),
            Methods:    new List<ExportedMethodDef>(),
            Interfaces: new List<string>(),
            TypeParams: typeParams);

    private static ExportedInterfaceDef Iface(string name, params ExportedMethodDef[] methods) =>
        new(name, methods.ToList(), TypeParams: null);

    private static ExportedMethodDef Method(string name, string retType,
        params ExportedParamDef[] parms) =>
        new(name, parms.ToList(), retType, MinArgCount: parms.Length,
            IsStatic: false, IsVirtual: false, IsAbstract: false,
            Visibility: "public");

    private static ExportedModule Module(string ns, params ExportedClassDef[] classes) =>
        new(ns, classes.ToList(),
            new List<ExportedInterfaceDef>(),
            new List<ExportedEnumDef>(),
            new List<ExportedFuncDef>());

    private static ExportedModule InterfaceModule(string ns,
        params ExportedInterfaceDef[] ifaces) =>
        new(ns,
            new List<ExportedClassDef>(),
            ifaces.ToList(),
            new List<ExportedEnumDef>(),
            new List<ExportedFuncDef>());

    [Fact]
    public void Load_SelfReference_ProducesClassTypeNotPrimType()
    {
        // class Exception { Exception InnerException; }
        var mod = Module("Std",
            Class("Exception", baseClass: null,
                Field("InnerException", "Exception")));

        var loaded = ImportedSymbolLoader.Load(new[] { mod }, new[] { "Std" });

        loaded.Classes.Should().ContainKey("Exception");
        var exType = loaded.Classes["Exception"];
        exType.Fields.Should().ContainKey("InnerException");
        exType.Fields["InnerException"].Should().BeOfType<Z42ClassType>(
            because: "self-reference 字段必须升级为 Z42ClassType，不得降级为 PrimType")
            .Which.Name.Should().Be("Exception");
    }

    [Fact]
    public void Load_ForwardReference_ProducesClassTypeNotPrimType()
    {
        // class A { B b; }   class B {} — A 在 B 之前
        var mod = Module("Std",
            Class("A", baseClass: null, Field("b", "B")),
            Class("B", baseClass: null));

        var loaded = ImportedSymbolLoader.Load(new[] { mod }, new[] { "Std" });

        loaded.Classes.Should().ContainKey("A");
        var aType = loaded.Classes["A"];
        aType.Fields["b"].Should().BeOfType<Z42ClassType>(
            because: "forward-reference 字段（A→B）应正确升级为 Z42ClassType")
            .Which.Name.Should().Be("B");
    }

    [Fact]
    public void Load_TrueUnknownType_StillPrimType()
    {
        // class A { NoSuchType field; } — NoSuchType 没声明
        var mod = Module("Std",
            Class("A", baseClass: null, Field("field", "NoSuchType")));

        var loaded = ImportedSymbolLoader.Load(new[] { mod }, new[] { "Std" });

        var aType = loaded.Classes["A"];
        aType.Fields["field"].Should().BeOfType<Z42PrimType>(
            because: "真正未知的类型名仍降级为 Z42PrimType；TypeChecker 后续报错")
            .Which.Name.Should().Be("NoSuchType");
    }

    [Fact]
    public void Load_InterfaceForwardReference_ResolvesToInterfaceType()
    {
        // interface IEnumerable<T> { IEnumerator<T> GetEnumerator(); }
        // interface IEnumerator<T> {}
        // 注意：此处简化为非泛型形式（IEnumerable 引用 IEnumerator），验证接口间 forward-ref
        var mod = InterfaceModule("Std",
            Iface("IEnumerable",
                Method("GetEnumerator", "IEnumerator")),
            Iface("IEnumerator"));

        var loaded = ImportedSymbolLoader.Load(new[] { mod }, new[] { "Std" });

        loaded.Interfaces.Should().ContainKey("IEnumerable");
        var ie = loaded.Interfaces["IEnumerable"];
        ie.Methods.Should().ContainKey("GetEnumerator");
        ie.Methods["GetEnumerator"].Ret.Should().BeOfType<Z42InterfaceType>(
            because: "接口间 forward-reference 必须解析为 Z42InterfaceType")
            .Which.Name.Should().Be("IEnumerator");
    }

    [Fact]
    public void Load_GenericParam_TakesPriorityOverClassLookup()
    {
        // 假设有 class T 和 generic class A<T>，A 内部的 T 应该是 generic param 而非 class T。
        // class T {}  class A<T> { T value; }
        var mod = Module("Std",
            Class("T", baseClass: null),
            GenericClass("A", new List<string> { "T" },
                Field("value", "T")));

        var loaded = ImportedSymbolLoader.Load(new[] { mod }, new[] { "Std" });

        loaded.Classes.Should().ContainKey("A");
        var aType = loaded.Classes["A"];
        aType.Fields["value"].Should().BeOfType<Z42GenericParamType>(
            because: "在 generic class 内部，同名 T 应作为 generic param，不被 class T 覆盖")
            .Which.Name.Should().Be("T");
    }

    // ── L3-Impl2: cross-zpkg `impl` Phase 3 merge ────────────────────────────

    [Fact]
    public void Load_Impl_AddsMethodToImportedClass()
    {
        // 模拟两包：z42.core 导出 class Robot；z42.greet 通过 impl IGreet for Robot 加方法。
        var coreMod = new ExportedModule("Core",
            new List<ExportedClassDef> { Class("Robot", baseClass: null) },
            new List<ExportedInterfaceDef> { Iface("IGreet", Method("Hello", "string")) },
            new List<ExportedEnumDef>(),
            new List<ExportedFuncDef>());
        var greetMod = new ExportedModule("Greet",
            new List<ExportedClassDef>(),
            new List<ExportedInterfaceDef>(),
            new List<ExportedEnumDef>(),
            new List<ExportedFuncDef>(),
            Impls: new List<ExportedImplDef>
            {
                new("Core.Robot", "Core.IGreet",
                    new List<string>(),
                    new List<ExportedMethodDef> { Method("Hello", "string") })
            });

        var loaded = ImportedSymbolLoader.Load(new[] { coreMod, greetMod }, new[] { "Core", "Greet" });

        loaded.Classes.Should().ContainKey("Robot");
        loaded.Classes["Robot"].Methods.Should().ContainKey("Hello",
            because: "impl 块声明的方法必须合并进 imported Robot 的 Methods 字典");
        loaded.ClassInterfaces.Should().ContainKey("Robot");
        loaded.ClassInterfaces!["Robot"].Should().Contain("IGreet",
            because: "impl 的 trait 必须加入 ClassInterfaces[Robot]");
    }

    [Fact]
    public void Load_Impl_TargetMissing_SilentSkip()
    {
        // impl 引用一个不存在的 target —— 直接跳过，不抛异常。
        var greetMod = new ExportedModule("Greet",
            new List<ExportedClassDef>(),
            new List<ExportedInterfaceDef>(),
            new List<ExportedEnumDef>(),
            new List<ExportedFuncDef>(),
            Impls: new List<ExportedImplDef>
            {
                new("Core.GhostClass", "Core.IGhostTrait",
                    new List<string>(),
                    new List<ExportedMethodDef> { Method("DoNothing", "void") })
            });

        // 不抛异常；loaded 不含 GhostClass
        var loaded = ImportedSymbolLoader.Load(new[] { greetMod }, new[] { "Greet" });
        loaded.Classes.Should().NotContainKey("GhostClass");
    }

    [Fact]
    public void Load_Impl_ConflictMethod_FirstWins()
    {
        // class Robot 已声明 Hello() 自身方法；impl IGreet for Robot 试图再加 Hello() —
        // 应保留 Robot 自身原版方法，impl 那条 silent skip。
        var coreMod = new ExportedModule("Core",
            new List<ExportedClassDef>
            {
                new("Robot", null, false, false, false,
                    Fields:  new List<ExportedFieldDef>(),
                    Methods: new List<ExportedMethodDef> { Method("Hello", "string") },
                    Interfaces: new List<string>())
            },
            new List<ExportedInterfaceDef> { Iface("IGreet", Method("Hello", "string")) },
            new List<ExportedEnumDef>(),
            new List<ExportedFuncDef>());
        var greetMod = new ExportedModule("Greet",
            new List<ExportedClassDef>(),
            new List<ExportedInterfaceDef>(),
            new List<ExportedEnumDef>(),
            new List<ExportedFuncDef>(),
            Impls: new List<ExportedImplDef>
            {
                new("Core.Robot", "Core.IGreet",
                    new List<string>(),
                    new List<ExportedMethodDef>
                    {
                        // 不同的返回类型，验证 first-wins (原版的 string，不被改成 int)
                        Method("Hello", "int")
                    })
            });

        var loaded = ImportedSymbolLoader.Load(new[] { coreMod, greetMod }, new[] { "Core", "Greet" });

        loaded.Classes["Robot"].Methods["Hello"].Ret.Should().Be(Z42Type.String,
            because: "Robot 已声明 Hello —— impl 的同名方法被 first-wins 跳过");
    }
}
