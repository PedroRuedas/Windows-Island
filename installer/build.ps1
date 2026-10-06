# Builds installer\out\WindowsIsland-Setup-<version>.exe
#   powershell -ExecutionPolicy Bypass -File .\installer\build.ps1
#
# Steps: publish a self-contained single-file app -> pack the sparse package (makeappx) -> sign it with the
# project's self-signed certificate (signtool) -> compile the Inno Setup script.
#
# The signing certificate is created on first run in installer\.signing (git-ignored). Keep that folder and copy it
# to every machine that builds releases: a new certificate makes the installer replace the package registration
# (instead of updating it), so users may have to allow notification access again.

$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$project = Join-Path $root 'src\WindowsIsland\WindowsIsland.csproj'
$out = Join-Path $PSScriptRoot 'out'
$tools = Join-Path $PSScriptRoot '.tools'
$signing = Join-Path $PSScriptRoot '.signing'

$version = ([xml](Get-Content $project)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $version) { throw "No <Version> in $project" }
Write-Host "Windows Island $version" -ForegroundColor Cyan

# ── Tools ──────────────────────────────────────────────────────────────
$iscc = @("${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
          "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw 'Inno Setup 6 not found. Install it with: winget install JRSoftware.InnoSetup' }

# makeappx/signtool ship in Microsoft's Windows SDK BuildTools NuGet package; no full SDK install needed.
$sdkTools = Join-Path $tools 'sdk'
if (-not (Test-Path $sdkTools)) {
    Write-Host 'Downloading Windows SDK build tools (makeappx, signtool)...'
    New-Item -ItemType Directory -Force $tools | Out-Null
    $nupkg = Join-Path $tools 'sdk-buildtools.zip'
    Invoke-WebRequest -UseBasicParsing 'https://www.nuget.org/api/v2/package/Microsoft.Windows.SDK.BuildTools' -OutFile $nupkg
    Expand-Archive $nupkg $sdkTools -Force
    Remove-Item $nupkg
}
$makeappx = Get-ChildItem $sdkTools -Recurse -Filter makeappx.exe | Where-Object FullName -match '\\x64\\' | Select-Object -First 1 -ExpandProperty FullName
$signtool = Get-ChildItem $sdkTools -Recurse -Filter signtool.exe | Where-Object FullName -match '\\x64\\' | Select-Object -First 1 -ExpandProperty FullName
if (-not $makeappx -or -not $signtool) { throw "makeappx/signtool not found under $sdkTools" }

# ── Signing certificate ────────────────────────────────────────────────
# The subject must equal the Publisher in Package\AppxManifest.xml and app.manifest.
$pfx = Join-Path $signing 'WindowsIsland.pfx'
$cer = Join-Path $signing 'WindowsIsland.cer'
$passwordFile = Join-Path $signing 'password.txt'
if (-not (Test-Path $pfx)) {
    Write-Host 'Creating the package signing certificate (CN=WindowsIsland)...'
    New-Item -ItemType Directory -Force $signing | Out-Null
    $password = [Convert]::ToBase64String((1..24 | ForEach-Object { Get-Random -Maximum 256 }) -as [byte[]])
    Set-Content $passwordFile $password -NoNewline
    $cert = New-SelfSignedCertificate -Type Custom -Subject 'CN=WindowsIsland' -KeyUsage DigitalSignature `
        -FriendlyName 'Windows Island package signing' -CertStoreLocation 'Cert:\CurrentUser\My' -NotAfter (Get-Date).AddYears(10) `
        -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}')
    Export-PfxCertificate -Cert $cert -FilePath $pfx -Password (ConvertTo-SecureString $password -AsPlainText -Force) | Out-Null
    Export-Certificate -Cert $cert -FilePath $cer | Out-Null
    Remove-Item "Cert:\CurrentUser\My\$($cert.Thumbprint)"   # the .pfx is the only copy we need
}
$password = Get-Content $passwordFile -Raw
$thumbprint = (New-Object System.Security.Cryptography.X509Certificates.X509Certificate2 $cer).Thumbprint

# ── Publish ────────────────────────────────────────────────────────────
$appDir = Join-Path $out 'app'
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
Write-Host 'Publishing (self-contained, single file)...'
dotnet publish $project -c Release -r win-x64 --self-contained true -o $appDir `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }
# NuGet IntelliSense docs (WebView2 *.xml) are not needed at runtime.
Get-ChildItem $appDir -Filter *.xml | Where-Object Name -ne 'AppxManifest.xml' | Remove-Item

# ── Sparse package ─────────────────────────────────────────────────────
$packageSrc = Join-Path $out 'package-src'
New-Item -ItemType Directory -Force (Join-Path $packageSrc 'Assets') | Out-Null
Copy-Item (Join-Path $root 'src\WindowsIsland\Package\Assets\*.png') (Join-Path $packageSrc 'Assets')
$manifest = Get-Content (Join-Path $root 'src\WindowsIsland\Package\AppxManifest.xml') -Raw
$manifest = $manifest -replace '(<Identity [^>]*Version=")[^"]+', "`${1}$version.0"
[IO.File]::WriteAllText((Join-Path $packageSrc 'AppxManifest.xml'), $manifest, (New-Object Text.UTF8Encoding $false))

$msix = Join-Path $out 'WindowsIsland.msix'
& $makeappx pack /o /nv /d $packageSrc /p $msix | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'makeappx failed' }
& $signtool sign /q /fd SHA256 /f $pfx /p $password $msix
if ($LASTEXITCODE -ne 0) { throw 'signtool failed' }

# ── Installer ──────────────────────────────────────────────────────────
Write-Host 'Compiling the installer...'
& $iscc /Q "/DAppVersion=$version" "/DAppDir=$appDir" "/DMsix=$msix" "/DCer=$cer" "/DCertThumbprint=$thumbprint" `
    "/DIconFile=$(Join-Path $root 'src\WindowsIsland\Resources\blackhole.ico')" "/O$out" (Join-Path $PSScriptRoot 'WindowsIsland.iss')
if ($LASTEXITCODE -ne 0) { throw 'Inno Setup failed' }

$setup = Join-Path $out "WindowsIsland-Setup-$version.exe"
Write-Host ("Done: {0} ({1:N1} MB)" -f $setup, ((Get-Item $setup).Length / 1MB)) -ForegroundColor Green
