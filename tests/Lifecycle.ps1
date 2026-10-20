$ErrorActionPreference = 'Stop'
$Executable = Join-Path (Split-Path -Parent $PSScriptRoot) 'app\SzuConnect.exe'
$SettingsPath = Join-Path $env:LOCALAPPDATA 'SzuConnect\settings.json'
if (Get-Process -Name SzuConnect -ErrorAction SilentlyContinue) {
    throw 'Exit the running instance before executing lifecycle tests.'
}
if (-not (Test-Path -LiteralPath $SettingsPath)) {
    throw 'Configure an account before executing lifecycle tests.'
}
$OriginalHash = (Get-FileHash -LiteralPath $SettingsPath).Hash
$Background = $null
try {
    $Background = Start-Process -FilePath $Executable -PassThru -WindowStyle Hidden
    Start-Sleep -Seconds 1
    $Check = Start-Process -FilePath $Executable -ArgumentList '--check' -PassThru -Wait -WindowStyle Hidden
    if ($Check.ExitCode -ne 3) { throw 'A second check did not detect the running instance.' }
    $Import = Start-Process -FilePath $Executable -ArgumentList '--import-credentials' -PassThru -Wait -WindowStyle Hidden
    if ($Import.ExitCode -ne 4) { throw 'Import was not rejected while running.' }
    if ((Get-FileHash -LiteralPath $SettingsPath).Hash -ne $OriginalHash) { throw 'Rejected import changed the settings.' }
    if (@(Get-Process -Name SzuConnect).Count -ne 1) { throw 'Duplicate background instances found.' }
    $Stop = Start-Process -FilePath $Executable -ArgumentList '--stop' -PassThru -Wait -WindowStyle Hidden
    if ($Stop.ExitCode -ne 0) { throw 'Graceful stop failed.' }
    if (-not $Background.WaitForExit(3000)) { throw 'Background process remained alive after stopping.' }
    $Background.Dispose()
    $Background = $null
    $Background = Start-Process -FilePath $Executable -PassThru -WindowStyle Hidden
    Start-Sleep -Seconds 1
    if ($Background.HasExited) { throw 'Restart was discarded.' }
    $Check = Start-Process -FilePath $Executable -ArgumentList '--check' -PassThru -Wait -WindowStyle Hidden
    if ($Check.ExitCode -ne 3) { throw 'Restarted process does not hold the single-instance lock.' }
    Write-Host 'PASS: single instance, import protection, graceful stop, immediate restart'
} finally {
    Start-Process -FilePath $Executable -ArgumentList '--stop' -Wait -WindowStyle Hidden
    if ($Background) {
        if (-not $Background.WaitForExit(5000)) { throw 'Test cleanup could not stop its background instance.' }
        $Background.Dispose()
    }
}
