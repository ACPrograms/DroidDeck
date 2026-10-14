param(
    [Parameter(Mandatory = $true)][string]$KeyDirectory,
    [Parameter(Mandatory = $true)][string]$Adb,
    [string]$Sdk = "$env:USERPROFILE\android-sdk",
    [string]$NdkVersion = '27.3.13750724',
    [string]$BuildTools = '35.0.0'
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
Push-Location $repo
try {
    & .\gradlew.bat :questxr:assembleDebug "-PndkVersion=$NdkVersion" '-PskipRust=true' --console=plain
    if ($LASTEXITCODE) { throw 'Quest XR build failed' }
    $tools = Join-Path $Sdk "build-tools\$BuildTools"
    $aligned = 'questxr\build\questxr-aligned.apk'
    $signed = 'questxr\build\questxr-signed.apk'
    & "$tools\zipalign.exe" -f -p 4 questxr\build\outputs\apk\debug\questxr-debug.apk $aligned
    if ($LASTEXITCODE) { throw 'APK alignment failed' }
    & "$tools\apksigner.bat" sign --key "$KeyDirectory\droiddeck-release.pk8" --cert "$KeyDirectory\droiddeck-release.x509.pem" --out $signed $aligned
    if ($LASTEXITCODE) { throw 'APK signing failed' }
    $certificate = & "$tools\apksigner.bat" verify --print-certs $signed
    if ($LASTEXITCODE) { throw 'APK verification failed' }
    $expected = (Get-Content keystore\release-signer.sha256 -Raw).Trim()
    if (-not ($certificate -match "certificate SHA-256 digest: $expected")) { throw 'Unexpected signing certificate' }
    & $Adb install --no-incremental -r $signed
    if ($LASTEXITCODE) { throw 'Quest XR deployment failed' }
} finally {
    Pop-Location
}
