param(
    [string]$Sdk = "$env:USERPROFILE\android-sdk",
    [string]$NdkVersion = '27.3.13750724'
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$compiler = Join-Path $Sdk "ndk\$NdkVersion\toolchains\llvm\prebuilt\windows-x86_64\bin\aarch64-linux-android26-clang++.cmd"
if (-not (Test-Path -LiteralPath $compiler -PathType Leaf)) { throw 'Android ARM64 compiler is unavailable' }
Push-Location $repo
try {
    New-Item -ItemType Directory -Path build\vr\android-tests -Force | Out-Null
    foreach ($name in @('test_xr_tracking', 'test_xr_frames', 'test_xr_vsync', 'send_xr_frame')) {
        & $compiler -std=c++17 -Wall -Wextra -Werror -static-libstdc++ -pthread "tools/vr/tests/$name.cpp" -o "build/vr/android-tests/$name"
        if ($LASTEXITCODE -ne 0) { throw "Android test compilation failed: $name" }
    }
    Write-Output "Built Android test executables: $(Join-Path $repo 'build\vr\android-tests')"
} finally {
    Pop-Location
}
