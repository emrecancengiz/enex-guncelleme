# Enex Hizmet: ÖKC köprüsü + ağ ortak verisi (ana kasa). Başlatıcı (enex-baslat.ps1) gizli olarak açar.
# Elle çalıştırmak için: powershell -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File enex-hizmet.ps1
$base = Split-Path -Parent $MyInvocation.MyCommand.Path
$mtx = New-Object System.Threading.Mutex($false, "Local\EnexHizmet")
if (-not $mtx.WaitOne(0)) { exit 0 }   # zaten çalışıyor
try {
  $src = [System.IO.File]::ReadAllText((Join-Path $base "EnexHizmet.cs"), [System.Text.Encoding]::UTF8)
  Add-Type -TypeDefinition $src -Language CSharp -IgnoreWarnings
  [EnexHizmet]::Run($base)
} catch {
  try { Add-Content -Path (Join-Path $base "enex-hizmet.log") -Value ("{0} BASLATILAMADI: {1}" -f (Get-Date -Format "yyyy-MM-dd HH:mm:ss"), $_.Exception.Message) } catch {}
} finally { $mtx.ReleaseMutex() }
