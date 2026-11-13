# Minimal IPC v2 client for manual/live tests (docs/IPC.md).
#   .\pipe-client.ps1 hello
#   .\pipe-client.ps1 status
#   .\pipe-client.ps1 '{"cmd":"pause"}'
param([string]$Request = "status", [int]$TimeoutMs = 3000)
$ErrorActionPreference = "Stop"
switch ($Request) {
    "hello"  { $Request = '{"cmd":"hello","protocol":2}' }
    "status" { $Request = '{"cmd":"status"}' }
    "quit"   { $Request = '{"cmd":"quit"}' }
}
$session = (Get-Process -Id $PID).SessionId
# CurrentUserOnly exists on .NET Core / PowerShell 7; Windows PowerShell 5.1 (.NET Framework) lacks it.
$opts = [System.IO.Pipes.PipeOptions]::None
if ([Enum]::GetNames([System.IO.Pipes.PipeOptions]) -contains "CurrentUserOnly") { $opts = [System.IO.Pipes.PipeOptions]"CurrentUserOnly" }
$c = New-Object System.IO.Pipes.NamedPipeClientStream '.', "RamPilot.$session", ([System.IO.Pipes.PipeDirection]::InOut),
    $opts, ([System.Security.Principal.TokenImpersonationLevel]::Identification)
try {
    $c.Connect($TimeoutMs)
    $w = New-Object IO.StreamWriter $c; $w.AutoFlush = $true; $w.NewLine = "`n"
    $r = New-Object IO.StreamReader $c
    $w.WriteLine($Request)
    $r.ReadLine()
} finally { $c.Dispose() }
