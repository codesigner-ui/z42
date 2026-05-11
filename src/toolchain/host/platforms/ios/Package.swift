// swift-tools-version: 5.9
//
// Z42VM — SwiftPM package for embedding the z42 VM into iOS / macOS
// apps. Spec: docs/spec/archive/2026-05-12-add-platform-ios/

import PackageDescription

let package = Package(
    name: "Z42VM",
    platforms: [
        .iOS(.v14),
        .macOS(.v13),    // host-side build + dev workflow
    ],
    products: [
        .library(name: "Z42VM", targets: ["Z42VM"]),
    ],
    targets: [
        // Swift facade. Depends on the C bridge module to call into
        // z42_host_* symbols supplied by Z42VM.xcframework (produced
        // by build.sh).
        .target(
            name: "Z42VM",
            dependencies: ["Z42VMC"],
            path: "Sources/Z42VM"
        ),

        // C bridge — exposes z42_host.h + z42_abi.h to Swift via a
        // clang module. The actual symbols are linked from
        // Z42VM.xcframework's binary library; this target only
        // contributes headers + module map.
        //
        // The dummy.c source is required by SwiftPM (a regular target
        // needs ≥ 1 source file). It's empty.
        .target(
            name: "Z42VMC",
            path: "Sources/Z42VMC",
            sources: ["dummy.c"],
            publicHeadersPath: "include"
        ),
    ]
)
