[CmdletBinding()]
param([Parameter(Mandatory)][string]$PackagePath)
$ErrorActionPreference='Stop'
# This test changes certificate trust only on an ephemeral GitHub Actions runner.
# It must never be used to weaken trust on an end-user computer.
if($env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted'){
 throw 'This installation test is restricted to disposable GitHub-hosted runners.'
}
$identity=Get-Content (Join-Path $PSScriptRoot '..\packaging\store-identity.json') -Raw | ConvertFrom-Json
$installed=$null;$cert=$null;$trusted=$null;$app=$null
try {
 if(Get-AppxPackage -Name $identity.packageName){throw 'The test runner already has this package.'}
 $cert=New-SelfSignedCertificate -Type Custom -Subject $identity.publisher -KeyUsage DigitalSignature -CertStoreLocation Cert:\CurrentUser\My `
  -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3','2.5.29.19={text}')
 $cer=Join-Path $env:RUNNER_TEMP 'tray-test.cer'
 Export-Certificate -Cert $cert -FilePath $cer | Out-Null
 $trusted=Import-Certificate -FilePath $cer -CertStoreLocation Cert:\LocalMachine\TrustedPeople
 $sdk=Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
 $signTool=Get-ChildItem $sdk -Directory | Where-Object Name -match '^10\.0\.\d+\.0$' |
  Sort-Object {[Version]$_.Name} -Descending | ForEach-Object {Join-Path $_.FullName 'x64\signtool.exe'} |
  Where-Object {Test-Path $_} | Select-Object -First 1
 if(-not $signTool){throw 'SignTool was not found.'}
 & $signTool sign /fd SHA256 /sha1 $cert.Thumbprint /s My $PackagePath
 if($LASTEXITCODE -ne 0){throw 'Test package signing failed.'}
 Add-AppxPackage -Path ([IO.Path]::GetFullPath($PackagePath))
 $installed=Get-AppxPackage -Name $identity.packageName
 if(-not $installed){throw 'Package installation did not register the app.'}
 $log=Join-Path $env:LOCALAPPDATA ("Packages\"+$installed.PackageFamilyName+'\LocalState\logs\agent.log')
 $app=Start-Process -FilePath (Join-Path $installed.InstallLocation 'RemoteCommanderTray.exe') -PassThru
 $deadline=[DateTime]::UtcNow.AddSeconds(30)
 do {
  Start-Sleep -Milliseconds 250
  $content=if(Test-Path $log){Get-Content $log -Raw}else{''}
  $ready=$content -match 'Startup registration: NotRegistered\.' -and $content -match 'Agent launch unavailable'
 }until($ready -or [DateTime]::UtcNow -ge $deadline -or $app.HasExited)
 if(-not $ready){throw 'Packaged startup/data paths or missing-dependency handling failed.'}
 if($app.HasExited){throw 'The packaged tray exited unexpectedly.'}
 $run=Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -ErrorAction SilentlyContinue
 if($run.PSObject.Properties.Name -contains 'RemoteCommanderTray'){throw 'Store launch unexpectedly wrote a legacy Run entry.'}
 'PASS: installed package identity, LocalState logs, opt-in startup, no automatic CLI download, no legacy Run entry.'
}finally {
 if($app -and -not $app.HasExited){$app.Kill();$app.WaitForExit()}
 if($installed){Remove-AppxPackage -Package $installed.PackageFullName}
 if($trusted){Remove-Item -LiteralPath $trusted.PSPath -ErrorAction SilentlyContinue}
 if($cert){Remove-Item -LiteralPath $cert.PSPath -ErrorAction SilentlyContinue}
}
