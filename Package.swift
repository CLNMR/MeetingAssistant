// swift-tools-version: 6.0
import PackageDescription

let package = Package(
    name: "MeetingAssistant",
    platforms: [.macOS("15.0")],
    products: [
        .executable(name: "MeetingAssistant", targets: ["MeetingAssistant"]),
        .executable(name: "msctl", targets: ["msctl"]),
    ],
    dependencies: [
        .package(url: "https://github.com/FluidInference/FluidAudio.git", exact: "0.18.0"),
        .package(url: "https://github.com/soniqo/speech-swift.git", exact: "0.0.29"),
    ],
    targets: [
        .target(
            name: "MeetingAssistantCore",
            dependencies: [
                .product(name: "FluidAudio", package: "FluidAudio"),
                .product(name: "Qwen3ASR", package: "speech-swift"),
                .product(name: "AudioCommon", package: "speech-swift"),
            ],
            swiftSettings: [.swiftLanguageMode(.v5)]
        ),
        .executableTarget(
            name: "MeetingAssistant",
            dependencies: ["MeetingAssistantCore"],
            swiftSettings: [.swiftLanguageMode(.v5)]
        ),
        .executableTarget(
            name: "msctl",
            dependencies: ["MeetingAssistantCore"],
            swiftSettings: [.swiftLanguageMode(.v5)]
        ),
        .testTarget(
            name: "MeetingAssistantCoreTests",
            dependencies: ["MeetingAssistantCore"],
            swiftSettings: [.swiftLanguageMode(.v5)]
        ),
    ]
)
