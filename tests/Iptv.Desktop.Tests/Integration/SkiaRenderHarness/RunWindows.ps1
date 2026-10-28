param([string]$Root = '\\Mac\Home\Projects\aptv-windows')
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$exe = $Root + '\tests\Iptv.Desktop.Tests\bin\SkiaWindows\SkiaRenderHarness.exe'
foreach ($case in @('windows', 'windows-dark', 'windows-negative')) {
    $out = $Root + '\docs\review\evidence\skia-render\' + $case
    New-Item -ItemType Directory -Path $out -Force | Out-Null
    $arguments = '"' + $out + '"'
    if ($case -eq 'windows-dark') { $arguments += ' --theme dark' }
    if ($case -eq 'windows-negative') { $arguments += ' --break-render' }
    $process = Start-Process -FilePath $exe -ArgumentList $arguments -RedirectStandardOutput ($out + '\render.log') -RedirectStandardError ($out + '\render-error.log') -PassThru -Wait
    Write-Output ($case + '_EXIT=' + $process.ExitCode)
    Get-Content ($out + '\render.log')
    Get-Content ($out + '\render-error.log')
    $expected = if ($case -eq 'windows-negative') { 1 } else { 0 }
    if ($process.ExitCode -ne $expected) { throw ('Unexpected renderer exit: ' + $case) }
}
