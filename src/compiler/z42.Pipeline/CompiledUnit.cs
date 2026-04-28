using Z42.IR;
using Z42.Project;

namespace Z42.Pipeline;

/// 单个源文件的编译结果（IrModule + 元数据），由 `PackageCompiler` 在 Phase 2 产出，
/// 在 zpkg 组装阶段被 `ZpkgBuilder.BuildPacked` / `BuildIndexed` 消费。
public sealed record CompiledUnit(
    string          SourceFile,
    string          Namespace,
    string          SourceHash,
    List<string>    Exports,
    IrModule        Module,
    List<string>    Usings,
    List<string>    UsedDepNamespaces,
    ExportedModule? ExportedTypes = null
)
{
    public ZbcFile ToZbcFile() =>
        new ZbcFile(ZbcFile.CurrentVersion, SourceFile, SourceHash, Namespace, Exports, [], Module);
}
