param([string]$Root='\\Mac\Home\Projects\aptv-windows')
$ErrorActionPreference='Stop';$ProgressPreference='SilentlyContinue';[Console]::OutputEncoding=[Text.Encoding]::UTF8
$exe=$Root+'\tests\Iptv.Desktop.Tests\bin\SourceProxyWindows\SkiaRenderHarness.exe'
foreach($case in @('windows','windows-negative')){
 $out=$Root+'\docs\review\evidence\source-proxy-acceptance\'+$case;New-Item -ItemType Directory -Path $out -Force|Out-Null
 $args='"'+$out+'" --product-app --source-proxy';if($case -eq 'windows-negative'){$args+=' --break-source-content'}
 $p=Start-Process -FilePath $exe -ArgumentList $args -RedirectStandardOutput ($out+'\render.log') -RedirectStandardError ($out+'\render-error.log') -PassThru
 if(!$p.WaitForExit(45000)){Stop-Process -Id $p.Id;throw 'Owned source acceptance timeout'}
 $p.WaitForExit();$p.Refresh();Get-Content ($out+'\render.log');Get-Content ($out+'\render-error.log')
 # Read a second process Wait completion via GetProcess can lose ExitCode in PS; verdict log remains explicit.
 Write-Output ($case+'_HAS_EXITED='+$p.HasExited)
 if($case -eq 'windows' -and !(Select-String -Path ($out+'\render.log') -SimpleMatch 'SOURCE_PROXY_UI_ACCEPTANCE=PASS' -Quiet)){throw 'Positive renderer failed'}
 if($case -eq 'windows-negative' -and !(Select-String -Path ($out+'\render-error.log') -SimpleMatch 'SAVE_LABEL_PIXELS_MISSING' -Quiet)){throw 'Negative renderer did not fail expected ROI'}
}
