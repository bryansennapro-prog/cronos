# Cronos — Planejamento

Painel **nativo para Windows 11** para subir vídeos e imagens, publicar no Instagram na hora
ou agendar, usando a **API oficial da Meta (Instagram Graph API — Content Publishing)**.
O agendamento roda num **serviço seu no Cloudflare** (Worker + R2 + D1), então o post sai
no horário **mesmo com o PC desligado**.

> Nome do produto: **Cronos** (antes InstaPanel). Os projetos, a URL do Worker e a pasta de dados mantêm o nome técnico `InstaPanel`, para não quebrar a atualização automática nem o login com Instagram.
> Conta: **uma única conta Instagram Profissional** (sua).

---

## 1. Objetivo e escopo

### MVP (v1)
- Biblioteca local de mídia: arrastar e soltar vídeos e imagens, miniaturas, validação e conversão de formato.
- Compositor de post: **Imagem (feed)**, **Carrossel**, **Reel** e **Story**.
- Legenda com contador (2.200 caracteres), hashtags (máx. 30) e prévia estilo celular.
- **Publicar agora** ou **agendar**. Depois de agendar, o PC pode ser desligado.
- Calendário (mês/semana), remarcar e cancelar posts agendados.
- Histórico com link do post publicado, erros legíveis e botão "tentar de novo".
- Status do token da Meta e do limite de publicação.
- Notificação de "publicado/falhou" no celular (opcional), além de notificações no painel.

### Fora do MVP (v2+)
- Insights/métricas, modelos de legenda, banco de hashtags, sugestão de legenda com IA,
  publicação cruzada em Página do Facebook, marcação de usuários/colaboradores.

---

## 2. Como funciona (visão geral)

```
┌──────────────────────────── SEU PC (Windows 11) ───────────────────────────┐
│  InstaPanel (WinUI 3)                                                       │
│  • biblioteca local + FFmpeg (validar/converter/miniatura)                   │
│  • compositor, calendário, histórico                                         │
└───────┬───────────────────────────────┬─────────────────────────────────────┘
        │ (1) envia o arquivo           │ (2) cria o post: legenda, tipo, horário
        │     via URL pré-assinada      │     (HTTPS + chave de API)
        ▼                               ▼
┌──────────────────────────── CLOUDFLARE (sua conta) ─────────────────────────┐
│  R2  ← mídias pendentes          Worker (API + Cron a cada minuto)           │
│                                  D1   ← posts, estados, token criptografado  │
│                                                                              │
│  Cron: post perto do horário → cria container na Meta (ela baixa a mídia     │
│        de https://<worker>/m/<chave-aleatória>) → espera processar           │
│        → publica no minuto agendado → apaga a mídia do R2 → grava o link     │
└───────────────────────────────────────┬──────────────────────────────────────┘
                                        ▼
                               Instagram Graph API (Meta)
```

**Regra simples:** o painel nunca fala direto com a Meta para publicar. Tudo passa pelo Worker,
tanto o "publicar agora" quanto o agendado. Assim existe um único caminho de publicação,
testado uma vez só.

---

## 3. Pontos críticos da API da Meta

| Fato da API | Consequência no projeto |
|---|---|
| Só funciona com conta **Profissional**. | ✅ Já atendido. |
| Publicar é em 2 etapas: criar **container** (`POST /{ig-user-id}/media`) → aguardar `status_code = FINISHED` → `POST /{ig-user-id}/media_publish`. | Máquina de estados no Worker, avançada pelo Cron. |
| A Meta **baixa a mídia de uma URL pública** (`image_url` / `video_url`). | O Worker serve cada arquivo do R2 em `/m/<chave-aleatória>` (sem bucket público). O link só existe até o post ser publicado. |
| **Não existe agendamento nativo** para Instagram na API. | O Cron do Worker é o agendador. |
| Container **expira em 24h**. | O container é criado **~15 min antes** do horário (pré-preparo), não no momento do agendamento. |
| Limite de posts via API por 24h (documentado como 100, mas varia entre fontes). | Consultar `/{ig-user-id}/content_publishing_limit` antes de publicar e mostrar no painel. **Não fixar o número no código.** |
| Token de longa duração vale **60 dias**, renovável (precisa ter mais de 24h). | Cron diário renova quando faltar menos de 10 dias. Token guardado criptografado no D1. |
| Uso só na sua conta: o app Meta pode ficar em **modo Desenvolvimento**, com você como administrador. | **Não precisa de App Review**. |
| Imagem: **JPEG**, proporção entre 4:5 e 1.91:1. Reel: MP4/MOV, H.264 + AAC, 9:16 recomendado. Story não aceita legenda pela API. | O painel valida e converte com FFmpeg **antes** de enviar. *(Confirmar na doc os limites atuais de tamanho e duração.)* |

**Variante da API:** **Instagram API com Login do Instagram** (`graph.instagram.com`).
Não exige Página do Facebook. Permissões: `instagram_business_basic` e `instagram_business_content_publish`.

---

## 4. Stack tecnológica

### Painel (PC)
| Camada | Escolha |
|---|---|
| UI | **WinUI 3 (Windows App SDK)**: nativo do Windows 11, com Fluent, Mica e tema claro/escuro |
| Linguagem | **C# / .NET 10 (LTS)**, MVVM com CommunityToolkit.Mvvm |
| Cache local | **SQLite** (EF Core): biblioteca, rascunhos, cópia local dos posts sincronizados |
| Mídia | **FFmpeg** embutido: probe, miniatura, conversão, recorte de proporção, capa do Reel |
| Segredos | **Windows DPAPI**: guarda a URL do Worker + chave de API |
| Notificações | Windows App Notifications (ao abrir/sincronizar) |
| Instalador | **Velopack**: instalador `.exe` + atualização automática |

### Nuvem (Cloudflare — plano grátis)
| Peça | Uso | Limite grátis relevante |
|---|---|---|
| **Workers** (TypeScript) | API do painel + rota pública `/m/<chave>` para a Meta | 100 mil requisições/dia |
| **Cron Triggers** | `* * * * *` (a cada minuto) para a fila; `0 6 * * *` para manutenção e token | suficiente |
| **R2** | Mídias pendentes de publicação | 10 GB armazenados, saída grátis |
| **D1** (SQLite) | Posts, itens de mídia, eventos, token | 5 GB |
| **Wrangler** | Deploy, migrations do D1, secrets | — |

Custo esperado para uma conta: **US$ 0/mês**.

---

## 5. Estrutura do repositório

```
InstaPanel/
├── PLANEJAMENTO.md
├── desktop/
│   ├── InstaPanel.sln
│   ├── InstaPanel.App      (WinUI 3: telas, ViewModels, notificações)
│   ├── InstaPanel.Core     (modelos, validações por tipo de post, regras)
│   ├── InstaPanel.Cloud    (cliente HTTP da API do Worker, upload para R2)
│   ├── InstaPanel.Media    (FFmpeg: probe, thumbnail, transcode)
│   ├── InstaPanel.Data     (EF Core + SQLite)
│   └── InstaPanel.Tests
└── worker/
    ├── wrangler.toml       (bindings: D1, R2, crons)
    ├── migrations/         (schema D1)
    ├── src/
    │   ├── index.ts        (roteamento fetch + scheduled)
    │   ├── api/            (posts, uploads, account)
    │   ├── meta/           (cliente Graph API: container, status, publish, limit, refresh)
    │   ├── queue.ts        (máquina de estados executada pelo cron)
    │   ├── crypto.ts       (AES-GCM do token)
    │   └── notify.ts       (Telegram/ntfy opcional)
    └── test/               (Vitest + Miniflare)
```

---

## 6. Worker: API, dados e fila

### Endpoints
> Implementado na Fase 3: o upload passa pelo Worker em partes de 50 MB (multipart do R2), em vez de URL
> pré-assinada. Assim não é preciso criar chaves de API do R2 e o limite de 100 MB por requisição do plano
> grátis não atrapalha vídeos grandes. Rotas reais: `POST /api/uploads`, `PUT /api/uploads/part`,
> `POST /api/uploads/complete`, `DELETE /api/uploads`. Ver `worker/SETUP.md`.

| Método e rota | Função |
|---|---|
| `POST /api/uploads` | Abre um upload multipart no R2 e devolve a chave aleatória e o tamanho das partes. |
| `POST /api/posts` | Cria post: tipo, legenda, mídias (chaves R2 em ordem), `scheduled_at` (UTC) ou `now`. |
| `GET /api/posts?since=` | Sincronização: status, erros, permalink. |
| `PATCH /api/posts/{id}` | Remarcar ou editar a legenda (só antes do pré-preparo). |
| `DELETE /api/posts/{id}` | Cancelar e apagar as mídias do R2. |
| `POST /api/posts/{id}/retry` | Tentar de novo um post que falhou. |
| `GET /api/account` | Usuário conectado, validade do token, uso do limite de publicação. |
| `PUT /api/account/token` | Salvar ou trocar o token da Meta (criptografado antes de gravar). |
| `GET /api/insights?limit=` | Perfil (nome, bio, link, seguidores) e até 100 posts publicados com curtidas e comentários, lidos ao vivo da Meta. Usado pela tela Especialista. |
| `POST /api/insights/ai` | Análise com IA (Claude, `claude-opus-5-5`) do relatório do Especialista + posts recentes e agendados. Só quando o usuário clica "Analisar com IA". Chave no secret `ANTHROPIC_API_KEY`; sem ela, responde `ai_not_configured`. |
| `GET /m/{chave}` | **Público**: serve a mídia para a Meta baixar. Chave de 32 bytes aleatórios, válida só enquanto o post está pendente. |

**Autenticação do painel:** cabeçalho `Authorization: Bearer <chave>` (secret `PANEL_API_KEY`),
comparado em tempo constante. Com limite de requisições na rota `/api`.

### Tabelas D1 (resumo)
- `posts`: id, tipo, legenda, opções (share_to_feed, capa…), `scheduled_at`, `state`, `attempts`, `next_run_at`, `ig_container_id`, `ig_media_id`, `permalink`, `error`, timestamps.
- `post_media`: post_id, ordem, r2_key, tipo, `ig_child_container_id`.
- `events`: log de cada transição (para o histórico e para depuração).
- `account`: ig_user_id, username, token criptografado, `token_expires_at`.

### Máquina de estados (executada pelo Cron, um passo por vez)
```
SCHEDULED ──(faltam ≤15 min)──▶ PREPARING  (cria containers; carrossel: filhos + pai)
PREPARING ──────────────────▶ PROCESSING (consulta status_code a cada minuto)
PROCESSING ─(FINISHED)──────▶ READY
READY ──(chegou o horário + limite ok)──▶ PUBLISHING ──▶ PUBLISHED (grava permalink, apaga R2)
qualquer passo ──(erro)──▶ RETRY (espera crescente: 1, 5, 15 min; máx. 3) ──▶ FAILED
```
- "Publicar agora" é só um post com `scheduled_at = agora`.
- Container `EXPIRED` ou `ERROR` → volta para PREPARING (dentro do limite de tentativas).
- O cron trava cada post (`UPDATE … WHERE state = ? AND locked_until < now`) para evitar publicação dupla se duas execuções se sobrepuserem.
- **Precisão:** publicação dentro do minuto agendado (até ~1 min de atraso).

### Cron diário de manutenção
- Renova o token se faltar menos de 10 dias (`GET /refresh_access_token`).
- Apaga do R2 os arquivos órfãos (enviados e nunca usados em um post depois de 48h).
- Token perto de vencer ou com erro aparece no painel (Início e Configurações); sem aviso no celular.

---

## 7. Telas do painel

1. **Início:** próximos posts, falhas recentes, uso do limite (x/limite em 24h), validade do token, status da conexão com o Worker.
2. **Biblioteca:** grade de miniaturas, arrastar e soltar, filtros, detalhes técnicos e alertas de compatibilidade (com botão "converter").
3. **Novo post:** tipo, seleção/ordem das mídias, recorte, capa do Reel, legenda, prévia estilo celular, **Publicar agora** / **Agendar**. Barra de progresso do envio ao R2.
4. **Calendário:** mês/semana, arrastar para remarcar, cores por status.
5. **Histórico:** status sincronizado do Worker, link do post, erro, tentar de novo, duplicar.
6. **Configurações:** URL do Worker + chave de API, token da Meta (colar/trocar), fuso horário, notificações no celular, pasta da biblioteca.
7. **Especialista:** análise do perfil com as regras das 9 skills de Instagram (perfil, audiência, legendas, ganchos,
   hashtags, revisão anti-IA, carrosséis, planejamento, reaproveitamento). Usa `GET /api/insights`, a fila do Worker e a
   biblioteca local; roda no painel (`InstaPanel.Core/Expert`). Nota por área, prioridades, plano de 7 dias e relatório
   em Markdown. Salvamentos/envios ficam de fora: exigem `instagram_business_manage_insights`, que o app não pede.

**Offline:** rascunhos e biblioteca funcionam sem internet. Agendar exige conexão, porque o post só
conta como agendado depois que o Worker confirma.

---

## 8. Conexão da conta Meta

- **MVP:** gerar o token no painel de desenvolvedor da Meta (*App → Instagram → Gerar token*), colar no painel,
  que envia ao Worker por `PUT /api/account/token`. O Worker troca pelo token longo (60 dias), testa com `/me`
  e grava criptografado no D1. A chave AES fica no secret `TOKEN_ENC_KEY`.
- **v1.1:** login dentro do painel via **WebView2** (OAuth do Instagram), com o redirect apontando para o Worker
  (`/oauth/callback`), que troca o `code` e guarda o token. O painel não vê o token.

---

## 9. Roteiro de desenvolvimento

| Fase | Entrega | Pronto quando |
|---|---|---|
| **0. Setup** | App Meta (modo Dev) + produto Instagram + token; conta Cloudflare (Workers, R2, D1); instalar .NET 10, Visual Studio com WinUI, Node.js + Wrangler, FFmpeg. | Token testado no Graph API Explorer. |
| **1. Worker base** ✅ | `wrangler.toml`, schema D1, bucket R2, autenticação do painel, `PUT /api/account/token`, `GET /api/account`, deploy. | `curl` no Worker mostra seu @usuario e o limite de publicação. |
| **2. Esqueleto do painel** ✅ | Solução WinUI 3, NavigationView + Mica, Configurações (URL + chave via DPAPI), teste de conexão. *(SQLite passou para a Fase 3, onde a biblioteca passa a usá-lo.)* | Painel abre e mostra sua conta vinda do Worker. |
| **3. Publicar agora** ✅ | Biblioteca + FFmpeg, upload pré-assinado ao R2, `POST /api/posts`, fila no cron para **imagem** e **Reel**. | Primeiro post real saindo pelo Worker. |
| **4. Agendamento** ✅ *(sem aviso no celular por decisão: só notificações do Windows)* | Pré-preparo, travas, retentativas, remarcar/cancelar, calendário, sincronização de status, notificação no celular. | Agendar, **desligar o PC** e o post sair no horário. |
| **5. Formatos** ✅ *(capa do Reel por quadro do vídeo; capa por imagem separada ficou de fora)* | Carrossel, Stories, capa do Reel, prévia estilo celular. | Todos os tipos publicados com sucesso. |
| **6. Acabamento** ✅ *("Entrar com Instagram" ativado e testado com a conta real em 22/10/2026)* | Renovação automática de token, limpeza de órfãos, OAuth via WebView2, mensagens de erro amigáveis, instalador + atualização automática. | Uso contínuo sem intervenção. |

---

## 10. Riscos e mitigação

| Risco | Mitigação |
|---|---|
| Alguém descobrir a URL do Worker | Toda rota `/api` exige a chave do painel; `/m/` só serve chaves aleatórias de 32 bytes de posts pendentes. |
| Token da Meta vazar | Criptografado (AES-GCM) no D1, chave em secret do Cloudflare; nunca volta para o painel. |
| Meta não conseguir baixar a mídia | Retentativas; o painel valida o formato antes do envio; erro detalhado no histórico. |
| Publicação duplicada | Trava por post no D1 + `ig_media_id` gravado assim que a Meta responde. |
| Token expira | Renovação diária automática + aviso no celular e no painel. |
| Limite de publicação atingido | Consulta antes de publicar; post fica em espera e o painel avisa. |
| Mudanças na API da Meta | Versão da Graph API em variável de ambiente; cliente isolado em `worker/src/meta`. |

---

## 11. Decisões restantes

1. ~~Notificação no celular~~ — **decidido (22/10/2026): nenhuma por enquanto**; só notificações do Windows no painel. `notify.ts` não foi criado.
2. **Endereço do Worker:** subdomínio grátis `*.workers.dev` (padrão) ou um domínio seu?
