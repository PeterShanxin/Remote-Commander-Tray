[CmdletBinding()]
param(
 [ValidateSet('x64','arm64')][string]$Architecture='x64',
 [Parameter(Mandatory)][string]$PackageVersion,
 [Parameter(Mandatory)][string]$OutputDirectory,
 [string]$IdentityFile=(Join-Path $PSScriptRoot '..\packaging\store-identity.json'),
 [string]$DotNet='dotnet',
 [string]$MakeAppx
)
$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$output=[IO.Path]::GetFullPath($OutputDirectory)
if(Test-Path -LiteralPath $output){throw 'OutputDirectory must be a new directory; existing artifacts are never overwritten.'}
$identity=Get-Content -LiteralPath $IdentityFile -Raw | ConvertFrom-Json
if($identity.packageName -notmatch '^[A-Za-z0-9.-]{3,50}$'){throw 'Invalid Store package name.'}
if($identity.publisher -notmatch '^CN=\S' -or [string]::IsNullOrWhiteSpace($identity.publisherDisplayName)){throw 'Copy the publisher values from Partner Center.'}
$version=$null
if(-not [Version]::TryParse($PackageVersion,[ref]$version) -or $version.Major -lt 1 -or $version.Revision -ne 0 -or
 @($version.Major,$version.Minor,$version.Build,$version.Revision | Where-Object {$_ -lt 0 -or $_ -gt 65535}).Count){throw 'Store version must be major.minor.build.0, major >= 1, with 16-bit components.'}
if(-not $MakeAppx){
 $sdk=Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
 $MakeAppx=Get-ChildItem -LiteralPath $sdk -Directory | Where-Object Name -match '^10\.0\.\d+\.0$' |
  Sort-Object {[Version]$_.Name} -Descending | ForEach-Object {Join-Path $_.FullName 'x64\makeappx.exe'} |
  Where-Object {Test-Path -LiteralPath $_} | Select-Object -First 1
}
if(-not $MakeAppx -or -not(Test-Path -LiteralPath $MakeAppx)){throw 'Windows SDK MakeAppx is required.'}
$layout=Join-Path $output 'layout'
New-Item -ItemType Directory -Path $layout -Force | Out-Null
$rid='win-'+$Architecture
& $DotNet publish (Join-Path $repo 'src\RemoteCommanderTray\RemoteCommanderTray.csproj') -c Release -r $rid --self-contained true `
 '-p:StoreDistribution=true' "-p:RuntimeIdentifiers=$rid" '-p:PublishSingleFile=false' '-p:IncludeNativeLibrariesForSelfExtract=false' -o $layout --nologo
if($LASTEXITCODE -ne 0){throw 'Store publish failed.'}
foreach($required in @('RemoteCommanderTray.exe','RemoteCommanderTray.dll','RemoteCommanderTray.Core.dll','coreclr.dll','hostfxr.dll','hostpolicy.dll','System.Windows.Forms.dll')){
 if(-not(Test-Path -LiteralPath (Join-Path $layout $required))){throw "The self-contained payload is missing $required"}
}
$exe=[IO.File]::ReadAllBytes((Join-Path $layout 'RemoteCommanderTray.exe'))
$pe=[BitConverter]::ToInt32($exe,0x3c)
$machine=[BitConverter]::ToUInt16($exe,$pe+4)
$expected=if($Architecture -eq 'x64'){0x8664}else{0xaa64}
if($machine -ne $expected){throw 'The executable architecture does not match the package.'}
Copy-Item -LiteralPath (Join-Path $repo 'packaging\Assets') -Destination $layout -Recurse
Copy-Item -LiteralPath (Join-Path $repo 'LICENSE') -Destination (Join-Path $layout 'LICENSE.txt')
$manifest=Get-Content -LiteralPath (Join-Path $repo 'packaging\Package.appxmanifest.in') -Raw
$values=@{PACKAGE_NAME=$identity.packageName;PUBLISHER=$identity.publisher;PUBLISHER_DISPLAY_NAME=$identity.publisherDisplayName;VERSION=$PackageVersion;ARCH=$Architecture}
foreach($key in $values.Keys){$manifest=$manifest.Replace('@@'+$key+'@@',[Security.SecurityElement]::Escape([string]$values[$key]))}
if($manifest -match '@@'){throw 'Unresolved manifest placeholder.'}
[IO.File]::WriteAllText((Join-Path $layout 'AppxManifest.xml'),$manifest,[Text.UTF8Encoding]::new($false))
$package=Join-Path $output ("RemoteCommanderTray_${PackageVersion}_${Architecture}.msix")
& $MakeAppx pack /d $layout /p $package /h SHA256 2>&1 | Tee-Object -FilePath (Join-Path $output 'makeappx.log') | Select-Object -Last 3
if($LASTEXITCODE -ne 0){throw 'MSIX validation or packaging failed.'}
$sourceCommit=(& git -C $repo rev-parse HEAD).Trim()
$dirty=[bool](& git -C $repo status --porcelain)
@{Package=[IO.Path]::GetFileName($package);SHA256=(Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash;
 PackageVersion=$PackageVersion;Architecture=$Architecture;StoreId=$identity.storeId;SourceCommit=$sourceCommit;WorkingTreeModified=$dirty;
 Signed=$false;RequiresStoreCertification=$true} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $output 'build-info.json') -Encoding UTF8
Write-Output $package
