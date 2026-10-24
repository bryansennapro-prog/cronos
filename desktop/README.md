# Cronos — painel para Windows 11

App WinUI 3 (.NET 10), desempacotado e com o runtime do Windows App SDK incluído.

## Rodar
```powershell
dotnet run --project InstaPanel.App -p:Platform=x64
```
Ou abra direto: `InstaPanel.App\bin\x64\Debug\net10.0-windows10.0.26100.0\win-x64\InstaPanel.exe`

## Testes
```powershell
dotnet test InstaPanel.Tests
```

Requer **FFmpeg** (`winget install Gyan.FFmpeg`). O app procura no PATH e na pasta do winget.

Abrir com arquivos importa direto para a Biblioteca: `InstaPanel.exe "C:\video.mp4" "C:\foto.png"`.

## Projetos
| Projeto | Conteúdo |
|---|---|
| `InstaPanel.App` | Janela, telas (Início, Novo post, Biblioteca, Configurações), ViewModels |
| `InstaPanel.Cloud` | Cliente HTTP do Worker: conta, upload multipart com progresso, posts |
| `InstaPanel.Core` | Regras da Meta para mídia e legenda, configurações, chave do painel (DPAPI) |
| `InstaPanel.Media` | FFmpeg: leitura (ffprobe), miniaturas, conversão para JPEG / MP4 H.264+AAC |
| `InstaPanel.Data` | SQLite local: biblioteca e registro dos posts feitos neste PC |
| `InstaPanel.Tests` | xUnit, incluindo conversões reais com FFmpeg |

## Dados locais
`%LOCALAPPDATA%\InstaPanel\` (ou a pasta em `INSTAPANEL_DATA_DIR`, útil para testar sem mexer na biblioteca real)
- `settings.json`: URL do Worker
- `panel-key.dat`: chave do painel, protegida por DPAPI (o mesmo arquivo que o `worker\scripts\configurar.ps1` cria)
- `instapanel.db`: biblioteca e posts
- `thumbs\`: miniaturas · `cache\`: arquivos convertidos aguardando envio (apagados depois)

## Instalar
Baixe e rode **https://instapanel.instapanel-worker.workers.dev/updates/InstaPanel-Setup.exe**.
Instala só para o seu usuário em `%LOCALAPPDATA%\InstaPanelApp` (sem pedir administrador), cria atalhos na
Área de Trabalho e no Menu Iniciar e aparece em "Aplicativos instalados" para desinstalar.
Os dados (biblioteca, chave, banco) continuam em `%LOCALAPPDATA%\InstaPanel` e não são apagados na desinstalação.
O FFmpeg não vem no instalador: `winget install Gyan.FFmpeg`.

## Lançar uma versão nova (atualização automática)
```powershell
.\scripts\release.ps1 -Version 0.6.1 -Upload
```
Compila, empacota com o Velopack (gera a atualização completa e a "delta", só com o que mudou) e envia ao R2.
O painel instalado procura versões ao abrir e a cada 6 h, avisa na tela Início e atualiza em
Configurações › Atualizações (baixa, aplica e reinicia). Requer o `vpk` (`dotnet tool install -g vpk`) e o
`wrangler login` feito.

Teste local sem publicar: `-OutDir <pasta> -PackId InstaPanelAppTeste -Shortcuts None` e, no app,
`INSTAPANEL_UPDATE_URL=<pasta>\releases` aponta a busca de atualizações para a pasta.

## Ícone
`scripts\make-icon.ps1` gera `Assets\AppIcon.ico` (16 a 256 px) e os PNGs, desenhados com o System.Drawing.

## Notificações do Windows
O app usa a API clássica de notificações com o AppUserModelID `InstaPanel.Desktop`, registrado em
`HKCU\Software\Classes\AppUserModelId\InstaPanel.Desktop` ao abrir. (O `AppNotificationManager` do
Windows App SDK não funciona em app sem pacote MSIX: falha com 0x8007007E.)
Teste em Configurações › Notificações do Windows › "Enviar notificação de teste".

## Testes automatizados de interface
`INSTAPANEL_BACKGROUND=1` abre a janela atrás das outras, sem roubar o foco de quem está usando o PC.
Combine com `INSTAPANEL_DATA_DIR` apontando para uma pasta de teste.

## Formatos
| Formato | Mídias | Regras aplicadas pelo painel |
|---|---|---|
| Post | 1 imagem | JPEG, recorte para 4:5–1.91:1, até 1440 px |
| Carrossel | 2 a 10 imagens/vídeos | Imagens recortadas para a proporção do 1º item; vídeos de 3 a 60 s |
| Reel | 1 vídeo | H.264/AAC, 3 s a 15 min; capa escolhida por um quadro do vídeo (`thumb_offset`) |
| Story | 1 imagem ou vídeo | Sem recorte; vídeo até 60 s e 100 MB; sem legenda (a API não aceita) |

## Limitações conhecidas
- HEIC/HEIF (fotos de iPhone) ainda não são aceitos: exporte como JPG.
- Carrossel: a API aceita até 10 itens (o app do Instagram aceita mais).
- Capa do Reel: só por quadro do vídeo; capa com imagem separada (`cover_url`) não foi implementada.
- Arrastar um post para outro dia no Calendário não pôde ser verificado por automação (mouse simulado
  não inicia o arrastar no WinUI). Remarcar pelo diálogo do post foi verificado.
- Notificações aparecem com o painel aberto (ou ao abri-lo); com ele fechado, o post sai normalmente, mas o aviso
  só vem ao abrir o painel de novo.
