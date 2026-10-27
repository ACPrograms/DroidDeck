# Build script for DroidDeck Xbox Host UWP application
# Entry point for local Windows dev or GitHub Actions Windows runner
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [string]$Platform = 'x64'
)

$ErrorActionPreference = 'Stop'
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $ScriptDir

Write-Host "=== Building DroidDeck Xbox Host ($Configuration | $Platform) ==="

# Package artwork is derived from the original app's logo at UWP-required sizes.
Add-Type -AssemblyName System.Drawing
$logoPath = Join-Path $ScriptDir '..\app\src\main\res\drawable-nodpi\logo.png'
$logo = [System.Drawing.Image]::FromFile((Resolve-Path $logoPath))
try {
    foreach ($asset in @(
        @{ Name = 'Square150x150Logo.png'; Width = 150; Height = 150; Mark = 150 },
        @{ Name = 'Square44x44Logo.png'; Width = 44; Height = 44; Mark = 44 },
        @{ Name = 'StoreLogo.png'; Width = 50; Height = 50; Mark = 50 },
        @{ Name = 'SplashScreen.png'; Width = 620; Height = 300; Mark = 180 }
    )) {
        $bitmap = [System.Drawing.Bitmap]::new($asset.Width, $asset.Height)
        $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
        try {
            $graphics.Clear([System.Drawing.Color]::FromArgb(10, 11, 13))
            $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $x = [int](($asset.Width - $asset.Mark) / 2)
            $y = [int](($asset.Height - $asset.Mark) / 2)
            $graphics.DrawImage($logo, $x, $y, [int]$asset.Mark, [int]$asset.Mark)
            $bitmap.Save((Join-Path $ScriptDir ('Assets\' + $asset.Name)), [System.Drawing.Imaging.ImageFormat]::Png)
        }
        finally { $graphics.Dispose(); $bitmap.Dispose() }
    }
}
finally { $logo.Dispose() }

# 1. Verify UI production assets exist
$uiDir = Join-Path $ScriptDir 'Assets\DroidDeckUI'
if (!(Test-Path $uiDir) -or !(Get-ChildItem $uiDir -Filter *.wasm)) {
    throw "Wasm assets missing from $uiDir. Ensure wasmJsBrowserDistribution has been run."
}
Write-Host "Verified bundled Compose Wasm UI assets in $uiDir."

# 2. Locate MSBuild via vswhere if not in path
$msbuild = Get-Command 'MSBuild.exe' -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source
if (!$msbuild) {
    $vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
    if (Test-Path $vswhere) {
        $vs = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild -property installationPath
        if ($vs) {
            $candidate = Join-Path $vs 'MSBuild\Current\Bin\MSBuild.exe'
            if (Test-Path $candidate) { $msbuild = $candidate }
        }
    }
}

if (!$msbuild) {
    throw "MSBuild.exe not found. Visual Studio or Build Tools with UWP workload required."
}
Write-Host "Using MSBuild: $msbuild"

# 3. Generate temporary self-signed cert for sideload packaging
$certSubject = 'CN=DroidDeckXboxHost'
$cert = New-SelfSignedCertificate -Type Custom -Subject $certSubject -KeyUsage DigitalSignature `
    -CertStoreLocation 'Cert:\CurrentUser\My' `
    -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3','2.5.29.19={text}')

$certPassword = [guid]::NewGuid().ToString()
$pfxPath = Join-Path $env:TEMP ('xbox-host-' + [guid]::NewGuid().ToString() + '.pfx')
$cerPath = Join-Path $ScriptDir 'DroidDeckXboxHost.cer'

try {
    Export-PfxCertificate -Cert $cert -FilePath $pfxPath -Password (ConvertTo-SecureString $certPassword -AsPlainText -Force) | Out-Null
    Export-Certificate -Cert $cert -FilePath $cerPath | Out-Null
    certutil -f -addstore Root $cerPath | Out-Null

    $csproj = Join-Path $ScriptDir 'xbox-host.csproj'
    $packageDir = Join-Path $ScriptDir 'AppPackages'

    Write-Host "Restoring and building $csproj..."
    & $msbuild $csproj /restore /m:2 /v:minimal `
        "/p:Configuration=$Configuration" `
        "/p:Platform=$Platform" `
        "/p:UapAppxPackageBuildMode=SideloadOnly" `
        "/p:AppxPackageDir=$packageDir\" `
        "/p:PackageCertificateKeyFile=$pfxPath" `
        "/p:PackageCertificatePassword=$certPassword"

    if ($LASTEXITCODE -ne 0) {
        throw "MSBuild exited with code $LASTEXITCODE"
    }

    $packages = @(Get-ChildItem $packageDir -Recurse -File | Where-Object { $_.Extension -in '.appx', '.msix' -and $_.Name -like 'xbox-host_*' })
    if ($packages.Count -eq 0) {
        throw "No Appx/Msix package was produced in $packageDir."
    }

    Write-Host "Build complete! Package output: $($packages[0].FullName)"
}
finally {
    Remove-Item $pfxPath -Force -ErrorAction SilentlyContinue
    Remove-Item "Cert:\CurrentUser\My\$($cert.Thumbprint)" -Force -ErrorAction SilentlyContinue
}
