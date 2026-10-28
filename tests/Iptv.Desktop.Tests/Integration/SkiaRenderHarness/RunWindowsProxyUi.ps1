param([string]$Root='\\Mac\Home\Projects\aptv-windows')
$ErrorActionPreference='Stop';[Console]::OutputEncoding=[Text.Encoding]::UTF8
foreach($case in @('windows','windows-negative')){
 $out=$Root+'\docs\review\evidence\proxy-ui-acceptance\'+$case;New-Item -ItemType Directory -Path $out -Force|Out-Null
 $arguments='"'+$out+'" --product-app --proxy-ui';if($case -eq 'windows-negative'){$arguments+=' --break-proxy-content'}
 $p=Start-Process -FilePath ($Root+'\tests\Iptv.Desktop.Tests\bin\ProxyUiWindows\SkiaRenderHarness.exe') -ArgumentList $arguments -RedirectStandardOutput ($out+'\render.log') -RedirectStandardError ($out+'\render-error.log') -PassThru
 if(!$p.WaitForExit(45000)){Stop-Process -Id $p.Id;throw 'Owned proxy UI timeout'};$p.WaitForExit()
 Get-Content ($out+'\render.log');Get-Content ($out+'\render-error.log');Write-Output ($case+'_EXIT='+$p.ExitCode)
 if($case -eq 'windows' -and !(Select-String -Path ($out+'\render.log') -SimpleMatch 'ACTUAL_PROXY_CRUD_ASSIGN_SYSTEM=True' -Quiet)){throw 'Positive UI failed'}
 if($case -eq 'windows-negative' -and !(Select-String -Path ($out+'\render-error.log') -SimpleMatch 'PROXY_SAVE_LABEL_MISSING' -Quiet)){throw 'Negative ROI failed to reject'}
}
