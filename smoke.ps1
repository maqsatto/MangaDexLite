param([Parameter(Mandatory)][string]$Executable)
$ErrorActionPreference = 'Stop'
$exe = (Resolve-Path -LiteralPath $Executable).Path
$appProcess = Start-Process -FilePath $exe -WorkingDirectory (Split-Path $exe) -WindowStyle Hidden -PassThru
try {
    $null = $appProcess.WaitForInputIdle(15000)
    if ($appProcess.WaitForExit(7000)) { throw "App exited during startup: $($appProcess.ExitCode)" }
    $appProcess.Refresh()
    if ($appProcess.MainWindowHandle -eq 0 -or $appProcess.MainWindowTitle -ne 'MangaDex Lite') {
        throw 'App did not create its main window'
    }
    Write-Output 'PASS published application creates MangaDex Lite window and remains running'
} finally {
    if (-not $appProcess.HasExited) { Stop-Process -Id $appProcess.Id }
    $appProcess.Dispose()
}
