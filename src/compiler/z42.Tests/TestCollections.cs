using Xunit;

namespace Z42.Tests;

/// Sequential collection for tests that read/write artifacts/build/libraries/.
/// IncrementalBuildIntegrationTests deletes and rebuilds that directory;
/// StdlibSidecarPairingTests reads it. Running them in the same xUnit
/// collection forces sequential execution and prevents the race.
[CollectionDefinition("StdlibArtifacts")]
public sealed class StdlibArtifactsCollection { }
