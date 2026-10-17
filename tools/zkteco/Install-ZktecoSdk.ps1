<#
  Registers the OFFICIAL ZKTeco Standalone SDK (32-bit zkemkeeper.dll) for VELTRIX OS. Run once, as administrator.
  Get the SDK from ZKTeco: https://github.com/ZKTeco/Standalone-SDK (folder "Communication Protocol SDK(32Bit Ver6.2.4.11)\sdk").
  On 64-bit Windows a 32-bit COM DLL must live in SysWOW64 and be registered by the 32-bit regsvr32
  (ZKTeco's own Auto-install_sdk.bat copies to System32, which a 32-bit process does not see on 64-bit Windows).
  Usage:  powershell -ExecutionPolicy Bypass -File Install-ZktecoSdk.ps1 -SdkDir "C:\path\to\sdk"
#>
param([Parameter(Mandatory = $true)][string]$SdkDir)
$ErrorActionPreference = 'Stop'
$admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $admin) { throw 'Run this script as administrator.' }
if (-not (Test-Path (Join-Path $SdkDir 'zkemkeeper.dll'))) { throw "zkemkeeper.dll not found in $SdkDir" }
$target = if ([Environment]::Is64BitOperatingSystem) { Join-Path $env:windir 'SysWOW64' } else { Join-Path $env:windir 'System32' }
Copy-Item (Join-Path $SdkDir '*.dll') $target -Force
$p = Start-Process -FilePath (Join-Path $target 'regsvr32.exe') -ArgumentList '/s', "`"$(Join-Path $target 'zkemkeeper.dll')`"" -Wait -PassThru
if ($p.ExitCode -ne 0) { throw "regsvr32 failed with code $($p.ExitCode)" }
Write-Host "ZKTeco SDK registered from $target. Restart the 'VELTRIX Connector' service." -ForegroundColor Green
