param([switch]$Run)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
if (!(Test-Path -LiteralPath $compiler)) {
    $compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework/v4.0.30319/csc.exe'
}
if (!(Test-Path -LiteralPath $compiler)) { throw '.NET Framework 4.x C# compiler is required.' }
$output = Join-Path $root 'dist'
New-Item -ItemType Directory -Force $output | Out-Null
$exe = Join-Path $output 'RANS0M_v18.exe'
$options = @('/nologo', '/target:winexe', '/optimize+', '/reference:System.Windows.Forms.dll', '/reference:System.Drawing.dll', ('/out:' + $exe))
$assetRoot = Join-Path $root 'assets'
$audioRoot = Join-Path $assetRoot 'audio'
foreach ($file in Get-ChildItem -LiteralPath @($assetRoot,$audioRoot) -File | Where-Object { $_.Extension -in '.png','.wav' } | Sort-Object Name) {
    $options += '/resource:' + $file.FullName + ',' + $file.Name
}
$options += '/win32icon:' + (Join-Path $root 'assets/app.ico')
$options += (Get-ChildItem -LiteralPath (Join-Path $root 'src') -Filter '*.cs').FullName
& $compiler @options
if ($LASTEXITCODE -ne 0) { throw 'Compilation failed.' }
Write-Host "Built $exe"
if ($Run) { Start-Process -FilePath $exe }
