param(
    [ValidateSet('auto', 'arm64', 'x64', 'x86')]
    [string]$Architecture = 'auto'
)
$ErrorActionPreference = 'Stop'
if ($env:OS -ne 'Windows_NT') { throw 'Build WinUI on Windows with the .NET 8 SDK.' }
if ($Architecture -eq 'auto') {
    $Architecture = [Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString().ToLowerInvariant()
}
$platform = @{ arm64 = 'ARM64'; x64 = 'x64'; x86 = 'x86' }[$Architecture]
if (-not $platform) { throw "Unsupported architecture: $Architecture" }
$artifacts = Join-Path $PSScriptRoot 'artifacts'
$output = Join-Path $artifacts "win-$Architecture"
$exe = Join-Path $output 'ToastMonitor.WinUI.exe'
if (Get-Process -Name 'ToastMonitor.WinUI' -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe }) {
    throw 'Exit ToastMonitor from its tray menu before rebuilding this output.'
}
$staging = Join-Path $artifacts ('.publish-' + [Guid]::NewGuid().ToString('N'))
New-Item $staging -ItemType Directory -Force | Out-Null
try {
    $project = Join-Path $PSScriptRoot 'ToastMonitor.WinUI\ToastMonitor.WinUI.csproj'
    & dotnet publish $project -c Release -r "win-$Architecture" "-p:Platform=$platform" `
        -p:SelfContained=true -p:WindowsAppSDKSelfContained=true -p:PublishSingleFile=false `
        -o $staging --nologo
    if ($LASTEXITCODE -ne 0) { throw "Publish failed (exit $LASTEXITCODE)." }
    foreach ($file in @('ToastMonitor.WinUI.exe', 'ToastMonitor.WinUI.pri',
        'Microsoft.UI.Xaml.dll', 'Microsoft.WindowsAppRuntime.dll', 'CoreMessagingXP.dll',
        'coreclr.dll', 'Assets\AppIcon.ico')) {
        if (-not (Test-Path (Join-Path $staging $file))) { throw "Incomplete publish: $file is missing." }
    }
    if (Test-Path $output) {
        $previous = "$output.previous-$(Get-Date -Format 'yyyyMMdd-HHmmss')-$([Guid]::NewGuid().ToString('N').Substring(0,8))"
        Move-Item $output $previous
    }
    Move-Item $staging $output
    Write-Host "Ready: $exe"
    Write-Host 'Keep the whole output folder together. Launch the EXE, then use the tray icon to open Dashboard or Settings.'
}
finally {
    if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
}
