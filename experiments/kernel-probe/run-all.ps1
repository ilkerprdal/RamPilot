# Reproduces every kernel-probe measurement reported in ../README.md (about 90 minutes).
# Run from any directory:  powershell -NoProfile -ExecutionPolicy Bypass -File experiments\kernel-probe\run-all.ps1
# Requires the .NET 10 SDK. Non-admin is enough. Only synthetic child processes are capped.
# Note: "faults" (B) briefly allocates up to ~3.6 GB to push standby pages out; it evicts other processes' low-priority cache.
$ErrorActionPreference = "Stop"
$dotnet = if (Get-Command dotnet -ErrorAction SilentlyContinue) { "dotnet" } else { Join-Path $env:LOCALAPPDATA "Microsoft\dotnet\dotnet.exe" }
& $dotnet build "$PSScriptRoot\KernelProbe.csproj" -c Release | Out-Null
$exe = Join-Path $PSScriptRoot "bin\Release\net10.0-windows\kernel-probe.exe"

# C: which SetProcessWorkingSetSizeEx calls trim at once
& $exe trim --runs 3

# A: victim selection (400 MB private region, 40 MB hot set)
& $exe victim --runs 3 --caps 150 --step-s 60 --warmup-s 30 --tag A1_slam150
& $exe victim --runs 3 --caps 300,150 --step-s 30 --warmup-s 30 --tag A2_gentle300_then150
& $exe victim --runs 3 --caps 340,290,245,210,180,150 --step-s 15 --warmup-s 30 --tag A3_descent0.85
& $exe victim --runs 3 --caps 150 --step-s 60 --warmup-s 30 --layout block --tag A4_block   # A9 below repeats this with the address-decile column
& $exe victim --runs 3 --caps 150 --step-s 60 --warmup-s 30 --kind file --tag A5_file_clean
& $exe victim --runs 3 --caps 150 --step-s 60 --warmup-s 30 --interval-ms 2000 --tag A6_hot2s
& $exe victim --runs 3 --caps 150 --step-s 30 --warmup-s 60 --tag A7a_idle60s
& $exe victim --runs 3 --caps 150 --step-s 30 --warmup-s 120 --tag A7b_idle120s
& $exe victim --runs 3 --caps 150 --step-s 30 --warmup-s 300 --tag A7_idle300s
& $exe victim --runs 3 --caps 150 --step-s 30 --warmup-s 300 --tag A7r_idle300s_repeat
& $exe victim --runs 3 --caps 415,150 --step-s 15 --warmup-s 30 --tag A8_prime415_then150
& $exe victim --runs 3 --caps 150 --step-s 30 --warmup-s 30 --layout block --tag A9_block_fwdfill
& $exe victim --runs 3 --caps 150 --step-s 30 --warmup-s 30 --layout block --fill reverse --tag A10_block_revfill

# B: lifecycle of trimmed pages, then fault accounting
& $exe watch --mb 256 --secs 60
& $exe watch --mb 800 --secs 20
& $exe faults --runs 3 --wait-s 30
