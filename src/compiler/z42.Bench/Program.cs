// z42 compiler benchmark entry — invokes BenchmarkDotNet's switcher.
//
// Usage:
//   dotnet run --project src/compiler/z42.Bench -c Release           # interactive picker
//   dotnet run --project src/compiler/z42.Bench -c Release -- --filter '*'   # run all
//   dotnet run --project src/compiler/z42.Bench -c Release -- --list flat
//
// Or via just:
//   just bench-compiler

using BenchmarkDotNet.Running;

BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
