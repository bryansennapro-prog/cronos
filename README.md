# Cronos

Painel nativo para Windows 11 que publica e agenda posts no Instagram pela API oficial da Meta.
O agendamento roda num Worker do Cloudflare, então o post sai no horário mesmo com o PC desligado.

- **Novo post, biblioteca, calendário e histórico**: imagem, carrossel, Reel e Story, com validação e conversão via FFmpeg.
- **Especialista do perfil**: análise da conta com as regras das 9 skills de Instagram (perfil, audiência, legendas,
  ganchos, hashtags, revisão anti-IA, carrosséis, planejamento e reaproveitamento), nota por área e plano de 7 dias.
- **Análise com IA (opcional)**: o Claude lê o relatório e os posts e sugere nome, bio, ganchos e ideias.
  Só roda quando você clica.

## Estrutura

| Pasta | O que tem |
|---|---|
| `desktop/` | App WinUI 3 (.NET 10): telas, regras, cliente do Worker, FFmpeg, SQLite e testes. Veja [desktop/README.md](desktop/README.md). |
| `worker/` | Worker do Cloudflare (TypeScript): API do painel, fila de publicação, D1 e R2. Veja [worker/SETUP.md](worker/SETUP.md). |
| `PLANEJAMENTO.md` | Arquitetura, endpoints, máquina de estados e decisões. |

> Nome técnico interno: `InstaPanel` (projetos .NET, URL do Worker, pasta `%LOCALAPPDATA%\InstaPanel`).
> Foi mantido de propósito para não quebrar a atualização automática nem o login com Instagram.

## Segredos

Nenhum segredo fica no repositório. As chaves ficam como secrets no Cloudflare (`wrangler secret put`)
e a chave do painel fica protegida pelo DPAPI do Windows.
