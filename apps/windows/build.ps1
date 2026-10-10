# Builds the VELTRIX OS Windows installer. Requires .NET 10 SDK and Inno Setup 6.
param([string]$Version = "0.1.0")
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$publish = Join-Path $root "publish"
dotnet publish "$root\Veltrix.Desktop\Veltrix.Desktop.csproj" -c Release -r win-x64 --self-contained true -p:VeltrixVersion=$Version -o $publish
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }
$iscc = "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe"
& $iscc "/DAppVersion=$Version" "/DPublishDir=$publish" "/O$root\out" "$root\installer\VeltrixOS.iss"
if ($LASTEXITCODE -ne 0) { throw "ISCC failed" }
Get-ChildItem "$root\out"
