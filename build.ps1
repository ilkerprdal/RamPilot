# RamPilot build: safely stops a running engine (all limits released), runs the tests, publishes the engine
# (Native AOT) and the panel to artifacts\publish.
#   .\build.ps1            build + test + publish
#   .\build.ps1 -SkipTests
#   .\build.ps1 -Start     start the engine afterwards
param([switch]$Start, [switch]$SkipTests, [string]$Configuration = "Release")
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$out = Join-Path $root "artifacts\publish"
$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"
$env:DOTNET_NOLOGO = "1"

# --- dotnet: from PATH if it has an SDK, else the per-user install
function Find-Dotnet {
    $cmd = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($cmd) {
        $sdks = & $cmd.Source --list-sdks 2>$null
        if ($LASTEXITCODE -eq 0 -and $sdks) { return $cmd.Source }
    }
    $user = Join-Path $env:LOCALAPPDATA "Microsoft\dotnet\dotnet.exe"
    if (Test-Path $user) { return $user }
    throw "No .NET SDK found. Install the .NET 10 SDK (see global.json)."
}
$dotnet = Find-Dotnet

# --- Native AOT needs the MSVC linker; ILCompiler locates it through vswhere.exe on PATH
$vswhere = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer\vswhere.exe"
if (-not (Get-Command vswhere -ErrorAction SilentlyContinue)) {
    if (Test-Path $vswhere) { $env:PATH = (Split-Path $vswhere) + ";" + $env:PATH }
    else { Write-Warning "vswhere.exe not found; Native AOT publish needs the Visual Studio C++ build tools." }
}

# --- stop a running engine through its own protocol, so it releases every limit itself
function Send-Engine([string]$json) {
    $session = (Get-Process -Id $PID).SessionId
    try {
        # CurrentUserOnly exists on .NET Core / PowerShell 7 only; the server enforces it either way.
        $opts = [System.IO.Pipes.PipeOptions]::None
        if ([Enum]::GetNames([System.IO.Pipes.PipeOptions]) -contains "CurrentUserOnly") { $opts = [System.IO.Pipes.PipeOptions]"CurrentUserOnly" }
        $c = New-Object System.IO.Pipes.NamedPipeClientStream '.', "RamPilot.$session", ([System.IO.Pipes.PipeDirection]::InOut),
            $opts, ([System.Security.Principal.TokenImpersonationLevel]::Identification)
        $c.Connect(1500)
        $w = New-Object IO.StreamWriter $c; $w.AutoFlush = $true; $w.NewLine = "`n"
        $r = New-Object IO.StreamReader $c
        $w.WriteLine($json); $resp = $r.ReadLine(); $c.Dispose(); return $resp
    } catch { return $null }
}

function Get-Engine { Get-Process RamPilot -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.SessionId -eq (Get-Process -Id $PID).SessionId } }

Get-Process RamPilot.Panel -ErrorAction SilentlyContinue | ForEach-Object { $_.CloseMainWindow() | Out-Null }
if (Get-Engine) {
    Write-Host "stopping the running engine (limits are released by the engine)..."
    Send-Engine '{"cmd":"quit"}' | Out-Null
    for ($i = 0; $i -lt 30 -and (Get-Engine); $i++) { Start-Sleep -Milliseconds 500 }
    if (Get-Engine) { throw "The engine did not stop. Use 'Quit' in the tray menu, then build again." }
}

if (-not $SkipTests) {
    & $dotnet test (Join-Path $root "tests\RamPilot.Core.Tests\RamPilot.Core.Tests.csproj") -c $Configuration --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "tests failed" }
}

New-Item -ItemType Directory -Force $out | Out-Null
& $dotnet publish (Join-Path $root "src\RamPilot.Engine\RamPilot.Engine.csproj") -c $Configuration -r win-x64 -o $out --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "engine publish failed" }
& $dotnet publish (Join-Path $root "src\RamPilot.Panel\RamPilot.Panel.csproj") -c $Configuration -o $out --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "panel publish failed" }
Remove-Item (Join-Path $out "*.xml") -ErrorAction SilentlyContinue
Write-Host "published: $out"
if ($Start) { Start-Process (Join-Path $out "RamPilot.exe"); Write-Host "engine started" }
