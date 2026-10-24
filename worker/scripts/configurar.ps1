<#
  Configura os secrets do Worker e conecta a conta Instagram.
  Nada é mostrado na tela enquanto você cola a chave secreta e o token.

  Primeira vez:          .\scripts\configurar.ps1
  Só trocar o token:     .\scripts\configurar.ps1 -SomenteToken
#>
param([switch]$SomenteToken)

$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
Set-Location (Split-Path $PSScriptRoot)

$WorkerUrl = 'https://instapanel.instapanel-worker.workers.dev'
$KeyFile   = Join-Path $env:LOCALAPPDATA 'InstaPanel\panel-key.dat'   # protegido por DPAPI (só seu usuário do Windows lê)

function Read-Secret([string]$prompt) {
  $secure = Read-Host $prompt -AsSecureString
  $bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
  try { [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr).Trim() }
  finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr) }
}

function New-RandomBytes([int]$n) {
  $b = New-Object byte[] $n
  [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($b)
  $b
}

if (-not $SomenteToken) {
  Write-Host "`n== 1/3  Secrets do Worker ==" -ForegroundColor Cyan
  $panelKey = [Convert]::ToBase64String((New-RandomBytes 32)).TrimEnd('=').Replace('+', '-').Replace('/', '_')
  $encKey   = [Convert]::ToBase64String((New-RandomBytes 32))
  $appSecret = Read-Secret 'Cole a CHAVE SECRETA DO APP DO INSTAGRAM (não aparece na tela)'
  if ($appSecret.Length -lt 16) { throw 'Chave secreta parece incompleta.' }

  $tmp = Join-Path $env:TEMP ("instapanel-secrets-{0}.json" -f [guid]::NewGuid())
  try {
    @{ PANEL_API_KEY = $panelKey; TOKEN_ENC_KEY = $encKey; META_APP_SECRET = $appSecret } |
      ConvertTo-Json | Set-Content -Path $tmp -Encoding Ascii
    npx wrangler secret bulk $tmp
    if ($LASTEXITCODE -ne 0) { throw 'Falha ao enviar os secrets (wrangler secret bulk).' }
  } finally {
    Remove-Item $tmp -Force -ErrorAction SilentlyContinue
  }

  New-Item -ItemType Directory -Force (Split-Path $KeyFile) | Out-Null
  ConvertTo-SecureString $panelKey -AsPlainText -Force | ConvertFrom-SecureString | Set-Content $KeyFile

  Write-Host "`nCHAVE DO PAINEL (guarde no seu gerenciador de senhas):" -ForegroundColor Yellow
  Write-Host "  $panelKey"
  Write-Host "  Também ficou salva protegida em $KeyFile`n"
  Write-Host 'Aguardando o Worker carregar os secrets...'
  Start-Sleep -Seconds 8
} else {
  if (-not (Test-Path $KeyFile)) { throw "Chave do painel não encontrada em $KeyFile. Rode sem -SomenteToken." }
  $secure = Get-Content $KeyFile | ConvertTo-SecureString
  $bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
  try { $panelKey = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr) }
  finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr) }
}

$headers = @{ Authorization = "Bearer $panelKey" }

Write-Host "`n== 2/3  Conectar a conta Instagram ==" -ForegroundColor Cyan
$token = Read-Secret 'Cole o TOKEN DE ACESSO gerado no painel da Meta (não aparece na tela)'
$body = @{ access_token = $token } | ConvertTo-Json
try {
  $r = Invoke-RestMethod -Method Put -Uri "$WorkerUrl/api/account/token" -Headers $headers -Body $body -ContentType 'application/json; charset=utf-8'
} catch {
  Write-Host "Falhou: $($_.ErrorDetails.Message)" -ForegroundColor Red
  exit 1
}
Write-Host "Conectado como @$($r.account.username)" -ForegroundColor Green

Write-Host "`n== 3/3  Conferindo ==" -ForegroundColor Cyan
$a = Invoke-RestMethod -Uri "$WorkerUrl/api/account" -Headers $headers
$t = $a.account.token
Write-Host ("Conta:           @{0} ({1})" -f $a.account.username, $a.account.account_type)
Write-Host ("Token vale até:  {0}  ({1} dias){2}" -f $t.expires_at, $t.days_left, $(if ($t.expiry_known) { '' } else { ' — estimado; confirmado na 1ª renovação' }))
if ($a.publishing_limit.error) {
  Write-Host ("Limite:          erro da Meta: {0}" -f $a.publishing_limit.error.message) -ForegroundColor Yellow
} else {
  Write-Host ("Limite:          {0} de {1} posts usados nas últimas 24h" -f $a.publishing_limit.quota_usage, $a.publishing_limit.quota_total)
}
Write-Host "`nFase 1 concluída." -ForegroundColor Green
