$ErrorActionPreference = 'Stop'
$Root = $PSScriptRoot
$Compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$Output = Join-Path $Root 'app'
New-Item -ItemType Directory -Path $Output -Force | Out-Null
$References = @('/r:System.Net.Http.dll', '/r:System.Web.dll', '/r:System.Web.Extensions.dll', '/r:System.Security.dll', '/r:System.Windows.Forms.dll', '/r:System.Drawing.dll')
$Sources = Get-ChildItem -LiteralPath (Join-Path $Root 'src') -Filter '*.cs' | ForEach-Object { $_.FullName }
& $Compiler /nologo /optimize+ /platform:x86 /codepage:65001 /target:winexe "/out:$Output\SzuConnect.exe" @References @Sources
if ($LASTEXITCODE -ne 0) { throw 'Application build failed' }
& $Compiler /nologo /optimize+ /platform:x86 /codepage:65001 /target:exe /main:SzuConnect.Tests "/out:$Output\SzuConnect.Tests.exe" @References @Sources (Join-Path $Root 'tests\Tests.cs')
if ($LASTEXITCODE -ne 0) { throw 'Test build failed' }
& (Join-Path $Output 'SzuConnect.Tests.exe')
if ($LASTEXITCODE -ne 0) { throw 'Tests failed' }
Write-Host "Built: $Output\SzuConnect.exe"
