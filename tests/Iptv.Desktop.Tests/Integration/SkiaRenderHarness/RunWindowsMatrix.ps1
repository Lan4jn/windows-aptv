param([string]$Root = '\\Mac\Home\Projects\aptv-windows')
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$exe = $Root + '\tests\Iptv.Desktop.Tests\bin\SkiaWindows\SkiaRenderHarness.exe'
foreach ($case in @('matrix-windows', 'baseline-fixed-windows', 'matrix-negative-windows')) {
    $out = $Root + '\docs\review\evidence\skia-render\' + $case
    New-Item -ItemType Directory -Path $out -Force | Out-Null
    $arguments = '"' + $out + '" --product-app'
    if ($case -ne 'baseline-fixed-windows') { $arguments += ' --matrix' }
    if ($case -eq 'matrix-negative-windows') { $arguments += ' --break-content' }
    $process = Start-Process -FilePath $exe -ArgumentList $arguments -RedirectStandardOutput ($out + '\render.log') -RedirectStandardError ($out + '\render-error.log') -PassThru -Wait
    Write-Output ($case + '_EXIT=' + $process.ExitCode)
    Get-Content ($out + '\render.log')
    Get-Content ($out + '\render-error.log')
    $expected = if ($case -eq 'matrix-negative-windows') { 1 } else { 0 }
    if ($process.ExitCode -ne $expected) { throw ('Unexpected renderer exit: ' + $case) }
}
