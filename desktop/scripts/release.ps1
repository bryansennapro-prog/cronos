<#
  Gera uma versão do Cronos: compila, empacota com o Velopack (instalador + atualização)
  e, com -Upload, envia para o R2 (servido pelo Worker em /updates).

  Exemplos:
    .\scripts\release.ps1 -Version 0.6.0 -Upload        # publica para o painel instalado se atualizar sozinho
    .\scripts\release.ps1 -Version 0.6.1 -OutDir C:\tmp\teste -PackId InstaPanelAppTeste -Shortcuts None # teste local

  O instalador fica em: <OutDir>\releases\<PackId>-win-Setup.exe
  Link público depois do -Upload: <URL do Worker>/updates/InstaPanel-Setup.exe
#>
param(
  [Parameter(Mandatory)] [string]$Version,
  [string]$PackId = 'InstaPanelApp',      # ≠ "InstaPanel": a pasta de instalação não pode ser a pasta de dados
  [string]$Channel = 'win',
  [string]$OutDir = (Join-Path (Split-Path $PSScriptRoot) 'out'),
  [string]$Shortcuts = 'Desktop,StartMenuRoot',
  [string]$WorkerDir = (Join-Path (Split-Path (Split-Path $PSScriptRoot)) 'worker'),
  [string]$Bucket = 'instapanel-media',
  [string]$FeedUrl = 'https://instapanel.instapanel-worker.workers.dev/updates',
  [switch]$Upload
)
$ErrorActionPreference = 'Stop'
$env:Path = [Environment]::GetEnvironmentVariable('Path', 'Machine') + ';' + [Environment]::GetEnvironmentVariable('Path', 'User') + ";$env:USERPROFILE\.dotnet\tools"
$root = Split-Path $PSScriptRoot
$releases = Join-Path $OutDir 'releases'
New-Item -ItemType Directory -Force $releases | Out-Null

Write-Host "== 1/3 Compilando $Version" -ForegroundColor Cyan
# PublishDir RELATIVO ao projeto: um caminho absoluto com apóstrofo (ex.: C:\Users\Bryan'\...) quebra
# as transformações de itens do MSBuild (erro MSB3094). "-o" sempre vira absoluto, por isso não é usado.
$relPublish = "obj\publish-$Version\"
$publish = Join-Path (Join-Path $root 'InstaPanel.App') $relPublish
if (Test-Path $publish) { Remove-Item -Recurse -Force $publish }
dotnet publish (Join-Path $root 'InstaPanel.App') -c Release -p:Platform=x64 -r win-x64 --self-contained true `
  -p:Version=$Version -p:WindowsAppSDKSelfContained=true "-p:PublishDir=$relPublish"
if ($LASTEXITCODE -ne 0) { throw 'Falha ao compilar.' }

if ($Upload) {
  # Baixa a versão anterior para o Velopack gerar o pacote "delta" (atualização menor).
  Write-Host '   baixando a versão anterior para gerar o delta…'
  vpk download http --url $FeedUrl --channel $Channel -o $releases 2>$null
}

Write-Host "== 2/3 Empacotando" -ForegroundColor Cyan
# O PowerShell 5.1 descarta argumentos vazios ao chamar programas; "None" = sem atalhos.
if ([string]::IsNullOrWhiteSpace($Shortcuts)) { $Shortcuts = 'None' }
$started = Get-Date
vpk pack --packId $PackId --packVersion $Version --packDir $publish --mainExe InstaPanel.exe `
  --packTitle Cronos --packAuthors Cronos --icon (Join-Path $root 'InstaPanel.App\Assets\AppIcon.ico') `
  --channel $Channel --shortcuts $Shortcuts --outputDir $releases
if ($LASTEXITCODE -ne 0) { throw 'Falha ao empacotar.' }

if (-not $Upload) {
  Write-Host "Pronto (sem enviar). Arquivos em $releases" -ForegroundColor Green
  return
}

Write-Host "== 3/3 Enviando para o R2 ($Bucket/updates)" -ForegroundColor Cyan
function Put([string]$file, [string]$name, [string]$type) {
  Push-Location $WorkerDir
  try {
    npx wrangler r2 object put "$Bucket/updates/$name" --file $file --content-type $type --remote
    if ($LASTEXITCODE -ne 0) { throw "Falha ao enviar $name" }
  } finally { Pop-Location }
}
# Pacotes primeiro, índice por último (quem estiver atualizando nunca vê um índice apontando para arquivo inexistente).
$new = Get-ChildItem $releases -File | Where-Object { $_.LastWriteTime -ge $started }
foreach ($f in $new | Where-Object { $_.Extension -eq '.nupkg' }) { Put $f.FullName $f.Name 'application/octet-stream' }
$setup = $new | Where-Object { $_.Name -like '*-Setup.exe' } | Select-Object -First 1
if ($setup) {
  Put $setup.FullName $setup.Name 'application/vnd.microsoft.portable-executable'
  Put $setup.FullName 'InstaPanel-Setup.exe' 'application/vnd.microsoft.portable-executable'
}
foreach ($f in $new | Where-Object { $_.Extension -eq '.json' -or $_.Name -eq 'RELEASES' }) { Put $f.FullName $f.Name 'application/json' }

Write-Host "Publicado. Instalador: $FeedUrl/InstaPanel-Setup.exe" -ForegroundColor Green
