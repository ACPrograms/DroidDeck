param(
    [Parameter(Mandatory = $true)][string]$KeyDirectory,
    [Parameter(Mandatory = $true)][string]$Adb,
    [string]$Sdk = "$env:USERPROFILE\android-sdk",
    [string]$NdkVersion = '27.3.13750724',
    [string]$BuildTools = '35.0.0'
)
$ErrorActionPreference = 'Stop'
function Invoke-Checked([string]$Program, [string[]]$Arguments) {
    $ErrorActionPreference = 'Continue'
    & $Program @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$Program exited with $LASTEXITCODE" }
}
$repo = Split-Path $PSScriptRoot -Parent
Push-Location $repo
try {
    Invoke-Checked .\gradlew.bat @(':questxr:assembleDebug', "-PndkVersion=$NdkVersion", '-PskipRust=true', '--console=plain')
    $tools = Join-Path $Sdk "build-tools\$BuildTools"
    $aligned = 'questxr\build\questxr-aligned.apk'
    $signed = 'questxr\build\questxr-signed.apk'
    Invoke-Checked "$tools\zipalign.exe" @('-f', '-p', '4', 'questxr\build\outputs\apk\debug\questxr-debug.apk', $aligned)
    Invoke-Checked "$tools\apksigner.bat" @('sign', '--key', "$KeyDirectory\droiddeck-release.pk8", '--cert', "$KeyDirectory\droiddeck-release.x509.pem", '--out', $signed, $aligned)
    $certificate = Invoke-Checked "$tools\apksigner.bat" @('verify', '--print-certs', $signed)
    $expected = (Get-Content keystore\release-signer.sha256 -Raw).Trim()
    if (-not ($certificate -match "certificate SHA-256 digest: $expected")) { throw 'Unexpected signing certificate' }
    Invoke-Checked $Adb @('install', '--no-incremental', '-r', $signed)
} finally {
    Pop-Location
}
