# Independent check (does not use RamPilot code): lists every process of the given images with its
# GetProcessWorkingSetSizeEx flags. A process with QUOTA_LIMITS_HARDWS_MAX_ENABLE (0x4) is hard-capped.
#   .\check-caps.ps1                    WhatsApp.Root + msedgewebview2
#   .\check-caps.ps1 -Names Discord
param([string[]]$Names = @("WhatsApp.Root", "msedgewebview2"))
Add-Type -Namespace RpCheck -Name W -MemberDefinition @'
[DllImport("kernel32.dll", SetLastError=true)] public static extern IntPtr OpenProcess(uint a, bool i, int pid);
[DllImport("kernel32.dll", SetLastError=true)] public static extern bool GetProcessWorkingSetSizeEx(IntPtr h, out UIntPtr min, out UIntPtr max, out uint flags);
[DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);
'@
$capped = 0
foreach ($p in Get-Process -Name $Names -ErrorAction SilentlyContinue) {
    $h = [RpCheck.W]::OpenProcess(0x1000, $false, $p.Id)
    if ($h -eq [IntPtr]::Zero) { "{0,6} {1,-20} (cannot open)" -f $p.Id, $p.ProcessName; continue }
    $min = [UIntPtr]::Zero; $max = [UIntPtr]::Zero; $f = 0
    [void][RpCheck.W]::GetProcessWorkingSetSizeEx($h, [ref]$min, [ref]$max, [ref]$f)
    [void][RpCheck.W]::CloseHandle($h)
    $hard = ($f -band 4) -ne 0
    if ($hard) { $capped++ }
    "{0,6} {1,-20} ws={2,7:N1} MB  max={3,8:N1} MB  flags=0x{4:X}  {5}" -f $p.Id, $p.ProcessName, ($p.WorkingSet64 / 1MB), ($max.ToUInt64() / 1MB), $f, ($(if ($hard) { "HARD_MAX" } else { "-" }))
}
"hard-capped processes: $capped"
