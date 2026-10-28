param([string]$Root = '\\Mac\Home\Projects\aptv-windows')
$ErrorActionPreference='Stop'
$ProgressPreference='SilentlyContinue'
[Console]::OutputEncoding=[Text.Encoding]::UTF8
$exe=$Root+'\tests\Iptv.Desktop.Tests\bin\ContinuationWindows\SkiaRenderHarness.exe'
foreach($case in @('windows','windows-negative')) {
    $out=$Root+'\docs\review\evidence\continuation-acceptance\'+$case
    New-Item -ItemType Directory -Path $out -Force | Out-Null
    $arguments='"'+$out+'" --product-app --continuation'
    if($case -eq 'windows-negative'){$arguments+=' --break-content'}
    $p=Start-Process -FilePath $exe -ArgumentList $arguments -RedirectStandardOutput ($out+'\render.log') -RedirectStandardError ($out+'\render-error.log') -PassThru -Wait
    Write-Output ($case+'_EXIT='+$p.ExitCode)
    Get-Content ($out+'\render.log')
    Get-Content ($out+'\render-error.log')
    $expected=if($case -eq 'windows-negative'){1}else{0}
    if($p.ExitCode -ne $expected){throw ('Unexpected exit '+$case)}
}
