<#
 KOALIX — Windows acceptance test for REAL per-app split tunneling (run as Administrator on Windows 10/11).
 This is the test that must pass before split tunneling is declared working. It has NOT been run yet.

 Preparation:
   1. Copies curl.exe into two folders:  C:\KoalixTest\tunneled\curl.exe  and  C:\KoalixTest\direct\curl.exe
   2. In KOALIX → Split Tunneling: Mode = "Only selected apps", add C:\KoalixTest\tunneled\curl.exe as Tunnel.
   3. Connect in KOALIX, then run this script.
 Pass criteria:
   - tunneled curl egress IP  != direct curl egress IP   (TCP)
   - direct curl egress IP    == IP measured before connecting
   - UDP: tunneled nslookup via 1.1.1.1 shows in Routing Inspector with outbound=proxy (manual check)
   - IPv6 (if available): tunneled curl -6 does not reveal the ISP IPv6 (blocked or tunneled)
#>
param([string]$Baseline)
$ErrorActionPreference = 'Stop'
$root = 'C:\KoalixTest'
foreach ($d in 'tunneled','direct') { New-Item -ItemType Directory -Force "$root\$d" | Out-Null; Copy-Item "$env:SystemRoot\System32\curl.exe" "$root\$d\curl.exe" -Force }
if (-not $Baseline) { Write-Host "Tip: run once BEFORE connecting with: .\Run-SplitTunnelAcceptance.ps1 -Baseline (curl https://api.ipify.org)" }

function Get-Ip($exe, $fam) { try { (& $exe -s $fam --max-time 10 https://api64.ipify.org).Trim() } catch { 'ERROR' } }
$t4 = Get-Ip "$root\tunneled\curl.exe" '-4'; $d4 = Get-Ip "$root\direct\curl.exe" '-4'
$t6 = Get-Ip "$root\tunneled\curl.exe" '-6'; $d6 = Get-Ip "$root\direct\curl.exe" '-6'
"tunneled IPv4: $t4`ndirect   IPv4: $d4`ntunneled IPv6: $t6`ndirect   IPv6: $d6"
$ok = $true
if ($t4 -eq 'ERROR' -or $d4 -eq 'ERROR') { Write-Host 'FAIL: could not measure IPv4 egress' -f Red; $ok = $false }
elseif ($t4 -eq $d4) { Write-Host 'FAIL: tunneled and direct apps share the same egress IP' -f Red; $ok = $false }
if ($Baseline -and $d4 -ne $Baseline) { Write-Host 'FAIL: direct app does not use the ISP IP' -f Red; $ok = $false }
if ($d6 -ne 'ERROR' -and $t6 -eq $d6) { Write-Host 'FAIL: IPv6 leak — tunneled app exposes ISP IPv6' -f Red; $ok = $false }
if ($ok) { Write-Host 'PASS: per-app routing verified for TCP (IPv4/IPv6). Verify UDP/DNS in Routing Inspector.' -f Green }
exit [int](-not $ok)
