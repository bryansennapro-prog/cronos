# Fase 1 — Configurar a Meta e o Cloudflare

Siga na ordem. Tempo estimado: 20–30 minutos.
Todos os comandos de terminal rodam **dentro da pasta `InstaPanel\worker`** (PowerShell).

---

## Parte A — Meta (developers.facebook.com)

### A1. Criar o app
1. Acesse **https://developers.facebook.com/apps** e entre com o Facebook ligado a você
   (não precisa ser ligado à conta do Instagram).
2. **Criar app** → no caso de uso, escolha **"Gerenciar mensagens e conteúdo no Instagram"**
   (*Manage messaging & content on Instagram*).
3. Se pedir, não vincule a nenhum portfólio empresarial. Dê um nome (ex.: `InstaPanel`).
4. Deixe o app em **modo Desenvolvimento**. Não envie nada para análise (*App Review*):
   para uso na sua própria conta não é necessário.

### A2. Conectar sua conta Instagram e gerar o token
1. No painel do app: **Casos de uso → Instagram → Personalizar → Configuração da API com login do Instagram**
   (*API setup with Instagram login*).
2. Na etapa **"Gerar tokens de acesso"**, clique em **Adicionar conta** e entre com o seu Instagram profissional.
3. Autorize as permissões. Precisamos de **`instagram_business_basic`** e **`instagram_business_content_publish`**.
4. Clique em **Gerar token** ao lado da conta e **copie o token**. Ele vale ~60 dias e o Worker renova sozinho depois.

> Se a conta não aparecer ou der erro de permissão: no app do Instagram vá em
> **Configurações → Apps e sites → Convites de testador** e aceite o convite do app.

### A3. Copiar a chave secreta do app Instagram
Na mesma página (*Configuração da API com login do Instagram*), em **"Configurações do app do Instagram"**,
copie a **Chave secreta do app do Instagram** (*Instagram app secret*).
⚠️ É a chave **do Instagram**, não a "Chave secreta do app" geral em *Configurações → Básico*.

Guarde o **token** e a **chave secreta** num gerenciador de senhas. Não mande para ninguém.

---

## Parte B — Cloudflare

### B1. Conta e R2
1. Crie uma conta grátis em **https://dash.cloudflare.com/sign-up**.
2. No menu lateral, abra **R2 Object Storage** e clique em ativar.
   O Cloudflare pede um **cartão para ativar o R2**, mesmo no plano grátis. Nosso uso fica dentro dos 10 GB grátis.

### B2. Login do Wrangler (abre o navegador)
```powershell
npx wrangler login
```

### B3. Criar o banco D1 e o bucket R2
```powershell
npx wrangler d1 create instapanel
```
O comando mostra um `database_id`. **Cole esse valor no `wrangler.toml`**, no lugar de
`00000000-0000-0000-0000-000000000000`.

```powershell
npx wrangler r2 bucket create instapanel-media
npm run db:migrate:remote
```

### B4. Gerar e cadastrar os secrets
```powershell
node scripts/gen-secrets.mjs
```
Gera dois valores. **Guarde os dois** no gerenciador de senhas:
- `PANEL_API_KEY`: o painel do PC vai usar essa chave.
- `TOKEN_ENC_KEY`: criptografa o token da Meta. **Não troque depois**; se trocar, é só reconectar a conta.

Cadastre cada um (o comando pede o valor; cole e dê Enter):
```powershell
npx wrangler secret put PANEL_API_KEY
npx wrangler secret put TOKEN_ENC_KEY
npx wrangler secret put META_APP_SECRET
```
(`META_APP_SECRET` = a chave secreta do app Instagram do passo A3.)

**Opcional — análise com IA no Especialista.** Crie uma chave em https://platform.claude.com (API keys) e cadastre:
```powershell
npx wrangler secret put ANTHROPIC_API_KEY
```
A análise só roda quando você clica "Analisar com IA" e é cobrada por uso na sua conta da Anthropic.
Sem a chave, o resto do painel funciona normalmente e o botão avisa que falta configurar.

### B5. Publicar o Worker
```powershell
npm run deploy
```
No primeiro deploy, o Cloudflare pede para escolher um subdomínio `*.workers.dev`.
No fim aparece a URL, algo como `https://instapanel.SEU-SUBDOMINIO.workers.dev`.

---

## Parte C — Testar

No PowerShell, troque os valores entre `< >`:

```powershell
$W = "https://instapanel.<seu-subdominio>.workers.dev"
$H = @{ Authorization = "Bearer <PANEL_API_KEY>" }

# 1. Worker no ar
Invoke-RestMethod "$W/api/health"

# 2. Conectar a conta (cole o token do passo A2)
$body = @{ access_token = "<TOKEN_DA_META>" } | ConvertTo-Json
Invoke-RestMethod -Method Put -Uri "$W/api/account/token" -Headers $H -Body $body -ContentType "application/json"

# 3. Ver conta, validade do token e limite de publicação
Invoke-RestMethod -Uri "$W/api/account" -Headers $H | ConvertTo-Json -Depth 5
```

✅ **Fase 1 concluída** quando o passo 3 mostrar o seu `@usuario` e `publishing_limit` com `quota_usage`.

---

## Parte D — "Entrar com Instagram" (login pelo painel)

Permite conectar ou renovar a conta clicando em **Configurações › Entrar com Instagram**, sem copiar token.

1. Em **developers.facebook.com › seu app › Casos de uso › Instagram › Configuração da API com login do Instagram**,
   vá à etapa **"Configurar o login comercial do Instagram"** (*Set up Instagram business login*) › **Configurações de login comercial**.
2. Em **URIs de redirecionamento OAuth**, adicione exatamente:
   `https://instapanel.instapanel-worker.workers.dev/oauth/callback` e salve.
3. Na mesma página, copie o **ID do app do Instagram** (*Instagram app ID*). **Não é segredo.**
4. No `wrangler.toml`, preencha `INSTAGRAM_APP_ID = "<o ID>"` e publique: `npm run deploy`.

Pronto: o botão "Entrar com Instagram" abre o login da Meta dentro do painel. O Worker recebe o código,
troca pelo token (com a chave secreta que só ele tem), troca por um token de 60 dias e guarda criptografado.
Enquanto o app estiver em modo Desenvolvimento, só contas com papel no app (você) conseguem entrar.

---

## Referência rápida

| Rota | Auth | Função |
|---|---|---|
| `GET /api/health` | não | Worker no ar |
| `PUT /api/account/token` | sim | Valida o token na Meta, troca por longo se possível, salva criptografado |
| `GET /api/account` | sim | Conta, validade do token, limite de publicação |
| `DELETE /api/account` | sim | Desconecta a conta (apaga o token) |
| `POST /api/uploads` | sim | Abre upload multipart no R2 `{content_type, size_bytes}` → `{key, part_size, part_count}` |
| `PUT /api/uploads/part?key=…&part=N` | sim | Envia a parte N (até 50 MB) → `{part_number, etag}` |
| `POST /api/uploads/complete` | sim | Conclui `{key, parts}` |
| `DELETE /api/uploads?key=…` | sim | Apaga upload ainda não usado em post |
| `POST /api/posts` | sim | Cria post `{type: IMAGE\|REELS, caption, media:[{key}], options, scheduled_at?}` |
| `GET /api/posts` | sim | Lista. Filtros: `from`/`to` (calendário, por horário), `before` (paginação), `state=A,B`, `limit` |
| `GET /api/posts/:id` | sim | Detalhe com linha do tempo (eventos) |
| `PATCH /api/posts/:id` | sim | Remarca e/ou edita `{scheduled_at?, caption?, options?}` (antes de publicar; post com falha volta para a fila) |
| `DELETE /api/posts/:id` | sim | Cancela post ainda não publicado e apaga a mídia |
| `POST /api/posts/:id/retry` | sim | Recomeça um post com falha |
| `GET /m/<chave>` | não | Mídia para a Meta baixar (só enquanto o post não foi publicado; suporta Range) |
| `POST /api/oauth/start` | sim | Começa o "Entrar com Instagram" → `{authorize_url, state, redirect_uri}` |
| `GET /api/oauth/status?state=…` | sim | `PENDING`, `CONNECTED` (com `username`) ou `ERROR` |
| `GET /oauth/callback` | não | Para onde a Meta redireciona; troca o código pelo token e mostra "pode fechar" |
| `GET /updates/<arquivo>` | não | Instalador e atualizações do painel (R2, prefixo `updates/`) |

**Fila (a cada minuto):** SCHEDULED → PREPARING (cria container) → PROCESSING (espera a Meta) → READY →
PUBLISHING → PUBLISHED (apaga a mídia do R2). Erros temporários: novas tentativas em 1, 5 e 15 min.
"Publicar agora" não espera o cron: começa na hora.

**Manutenção diária (06:00 UTC):** renova o token quando ele tiver mais de 24h e faltarem
menos de 10 dias para vencer, ou quando a validade ainda for desconhecida; apaga uploads
nunca usados em posts depois de 48h.

**Teste local sem Meta real:**
```powershell
copy .dev.vars.example .dev.vars   # preencha com node scripts/gen-secrets.mjs e descomente META_GRAPH_BASE
npm run db:migrate:local
npm run fake-meta                  # em um terminal
npm run dev                        # em outro; tokens de teste começam com IGTEST
```
