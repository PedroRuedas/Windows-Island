# Windows Island

A Dynamic Island do Mac/iPhone, só que para Windows. Uma pílula preta no topo da tela que cresce com animação de mola para mostrar o que está acontecendo, e que **qualquer app pode controlar** por uma API HTTP local.

## O que já funciona

| Recurso | Como aparece |
| --- | --- |
| **Mídia**: Spotify, YouTube no navegador, Media Player e qualquer app que use os controles de mídia do Windows | Compacto: capa + música + equalizador na cor da capa. Quando a música muda, a ilha se abre por 3 s mostrando o que está tocando. Ao passar o mouse: capa, artista, barra de progresso (clique para pular para um ponto) e botões ⏮ ⏯ ⏭ |
| **YouTube no navegador** (Chrome, Edge, Brave, Firefox…) | Ao abrir a ilha na página de música, **o próprio vídeo toca lá em cima**: mudo (o som continua no navegador) e sincronizado com a posição e o play/pause da aba |
| **Notificações do Windows**: WhatsApp, Teams, Outlook, Discord… | Cada notificação nova aparece expandida com o ícone e o nome do app, o título e o texto. Dispensar no Central de Notificações também tira da ilha |
| **Volume** | Ao mudar o volume, a ilha vira uma barra de nível (acompanha a troca de dispositivo de saída) |
| **Bateria** | Avisos de carregador conectado/desconectado e de bateria fraca (20%, 10%, 5%) |
| **Relógio** | No modo ocioso. Ao passar o mouse, mostra hora e data |
| **API de integração** | Notificações e atividades ao vivo (downloads, timers, builds…) enviadas por qualquer programa |
| **Tela cheia** | A ilha se esconde em jogos, vídeos em tela cheia e apresentações |
| **Claude Code** | Sessões por projeto ao vivo ("Editando Menu.tsx", "Executando: npm test", "Aguardando você"), tokens consumidos hoje e nas últimas 5h, alerta quando ele precisa de permissão e quando **termina**, com o começo da resposta |

### Páginas: trocando entre apps

A ilha funciona como as Live Activities do iPhone. Cada fonte (música, Claude, notificações, atividades da API) é uma **página**:

- **Passe o mouse** para expandir. Embaixo aparece uma **barra de abas**: clique para trocar de página, ou use a **roda do mouse**.
- A página escolhida fica no modo compacto quando você tira o mouse.
- **Ilha dividida:** com duas coisas ativas ao mesmo tempo (ex.: Spotify tocando e Claude trabalhando), a segunda aparece numa **bolinha** ao lado. Clique nela para trocar.
- Alertas (volume, notificações novas, "Claude terminou") passam por cima de tudo por alguns segundos. **Clique** num alerta para abrir o app ou projeto correspondente.
- Na página de Notificações, clique numa mensagem para abrir o app (WhatsApp etc.). Na página do Claude, clique numa sessão para abrir o projeto no VS Code.

**Botão direito** abre o menu (notificações do Windows, iniciar com o Windows, copiar endereço da API, testar, sair).

## Rodando

Requisitos: Windows 10 (2004+) ou 11, com o [.NET 10 SDK](https://dotnet.microsoft.com/download).

```powershell
dotnet run --project src/WindowsIsland
```

Para gerar um executável:

```powershell
dotnet publish src/WindowsIsland -c Release -r win-x64 --self-contained false -o publish
.\publish\WindowsIsland.exe
```

## Ativando as notificações do Windows

O Windows só deixa um app ler notificações se ele tiver *identidade de pacote*. A ilha resolve isso com um **pacote esparso** ([Package/AppxManifest.xml](src/WindowsIsland/Package/AppxManifest.xml)): o mesmo `WindowsIsland.exe`, registrado no Windows sem precisar de instalador.

1. Ative o **Modo de Desenvolvedor**: Configurações → Sistema → Para desenvolvedores. Ele permite registrar o pacote sem certificado.
2. Clique com o botão direito na ilha → **Ativar notificações do Windows…**. A ilha registra o pacote e reinicia sozinha.
3. Se o Windows negar o acesso, o mesmo menu abre Configurações → Privacidade → Notificações.

O registro aponta para a pasta de onde o exe rodou. Se você trocar de pasta (por exemplo, de `bin\Debug` para `publish`), use o menu de novo. Para remover: `Get-AppxPackage WindowsIsland | Remove-AppxPackage`.

> Para distribuir para outras pessoas sem o Modo de Desenvolvedor, é preciso assinar o pacote com um certificado.

Por privacidade, o `GET /status` da API **não** expõe o conteúdo das notificações espelhadas.

## Vídeo do YouTube na ilha

Os controles de mídia do Windows informam só o título, o canal e a duração do que o navegador toca, sem o link. Por isso a ilha busca o título no YouTube e só aceita um resultado com **o mesmo título** e **a mesma duração** (ou cuja aba aberta no navegador confirme que é YouTube). Anúncios nunca batem com nenhum resultado, então durante eles a ilha mostra a capa normal.

O vídeo encontrado roda num player do YouTube embutido ([YouTubeMirror.cs](src/WindowsIsland/Controls/YouTubeMirror.cs)), via WebView2, mudo e sincronizado com a aba a cada segundo. Ele pausa quando a ilha fecha, para não gastar processamento.

Limitações:
- Vídeos cujo dono desativou a incorporação em outros sites voltam a mostrar a capa.
- Ao carregar, pausar ou pular, o YouTube exibe a própria sobreposição (título, logo) por alguns segundos.
- O título do vídeo que está tocando é enviado ao YouTube na busca.

## Conectando o Claude Code

O consumo de tokens é lido sozinho dos históricos locais (`~/.claude/projects`). Para o status ao vivo das sessões, adicione estes hooks em `~/.claude/settings.json`. Eles mandam cada evento para a ilha em segundo plano (`async`), sem atrasar o Claude, e não fazem nada se a ilha estiver fechada:

```json
{
  "hooks": {
    "SessionStart":     [{ "hooks": [{ "type": "command", "async": true, "command": "curl -s -m 2 -X POST http://127.0.0.1:5199/claude/hook -H \"Content-Type: application/json\" --data-binary @- || true" }] }],
    "UserPromptSubmit": [{ "hooks": [{ "type": "command", "async": true, "command": "…mesmo comando…" }] }],
    "PreToolUse":       [{ "matcher": "*", "hooks": [{ "type": "command", "async": true, "command": "…mesmo comando…" }] }],
    "PostToolUse":      [{ "matcher": "*", "hooks": [{ "type": "command", "async": true, "command": "…mesmo comando…" }] }],
    "Notification":     [{ "hooks": [{ "type": "command", "async": true, "command": "…mesmo comando…" }] }],
    "Stop":             [{ "hooks": [{ "type": "command", "async": true, "command": "…mesmo comando…" }] }],
    "SessionEnd":       [{ "hooks": [{ "type": "command", "async": true, "command": "…mesmo comando…" }] }]
  }
}
```

A rota `POST /claude/hook` responde `204` sem corpo. Isso importa porque, no `UserPromptSubmit`, qualquer texto impresso pelo hook entraria no contexto do Claude. Use `/hooks` no Claude Code para revisar ou desativar.

## Integrando com outros apps

A ilha escuta em `http://127.0.0.1:5199`, apenas localmente. A porta pode ser trocada pela variável de ambiente `WINDOWS_ISLAND_PORT`.

| Rota | Função |
| --- | --- |
| `POST /notify` | Notificação temporária: expande sozinha e some após 5 s (padrão) |
| `POST /activity` | Atividade ao vivo: fica até ser removida ou até `duration` acabar. Envie de novo com o mesmo `id` para atualizar |
| `DELETE /activity/{id}` | Remove uma atividade |
| `GET /status` | Atividades atuais e mídia tocando |

Campos do JSON (exceto `title`/`progress`, todos opcionais; é preciso pelo menos um dos dois):

| Campo | Tipo | Descrição |
| --- | --- | --- |
| `id` | string | Identificador para atualizar/remover. Gerado automaticamente se omitido |
| `title` | string | Texto principal |
| `subtitle` | string | Texto secundário (aparece expandido) |
| `icon` | string | `bell` `chat` `mail` `call` `download` `upload` `timer` `clock` `calendar` `check` `error` `warning` `info` `code` `sync` `heart` `star` `mic` `camera` `location` `wifi` `bluetooth` `music` `volume` `battery` `charging` |
| `color` | string | Cor de destaque, ex.: `#30D158` |
| `progress` | 0–1 | Barra de progresso |
| `duration` | segundos | Some automaticamente após esse tempo |
| `priority` | 0–99 | A maior prioridade é a que aparece (padrão 50; o volume do sistema usa 100) |
| `action` | URL | Link `http(s)` aberto ao clicar |
| `style` | `"level"` | Mostra uma barra de nível no modo compacto (como o volume) |
| `expand` | bool | Expandir ao chegar (padrão: `true` em `/notify`, `false` em `/activity`) |

### Exemplos

**curl**
```bash
curl -X POST http://127.0.0.1:5199/notify -H "Content-Type: application/json" \
  -d '{"title":"Deploy concluído","subtitle":"api v2.3 em produção","icon":"check","color":"#30D158"}'
```

**PowerShell**: [examples/powershell/WindowsIsland.psm1](examples/powershell/WindowsIsland.psm1)
```powershell
Import-Module .\examples\powershell\WindowsIsland.psm1
Send-IslandNotification -Title "Backup feito" -Icon check -Color "#30D158"
Set-IslandActivity -Id render -Title "Renderizando vídeo" -Icon sync -Progress 0.6
Remove-IslandActivity -Id render
```
Demonstração completa: `powershell -ExecutionPolicy Bypass -File .\examples\powershell\demo.ps1`

**Python**: [examples/python/island.py](examples/python/island.py) (só biblioteca padrão)
```python
from island import notify, activity
activity("treino", title="Treinando modelo", icon="code", progress=0.3)
```

**Node**: [examples/node/island.mjs](examples/node/island.mjs)
```js
import { notify } from "./island.mjs";
await notify({ title: "Testes passaram", icon: "check", color: "#30D158" });
```

**Claude Code**: avise na ilha quando o Claude terminar uma tarefa. Adicione em `~/.claude/settings.json`:
```json
{
  "hooks": {
    "Stop": [{ "hooks": [{ "type": "command",
      "command": "curl -s -X POST http://127.0.0.1:5199/notify -H \"Content-Type: application/json\" -d \"{\\\"title\\\":\\\"Claude terminou\\\",\\\"icon\\\":\\\"code\\\",\\\"color\\\":\\\"#D97757\\\"}\"" }] }]
  }
}
```

## Arquitetura

```
src/WindowsIsland/
├── MainWindow.xaml(.cs)      A ilha: janela transparente, views e animação de mola
├── Core/
│   ├── IslandController.cs   Fila de atividades por prioridade + expiração + mídia
│   ├── IslandActivity.cs     Modelo de uma atividade
│   ├── Spring.cs             Física de mola (o "quique" da animação)
│   └── Icons.cs              Nomes de ícone → glifos Segoe Fluent Icons
├── Services/
│   ├── MediaService.cs       Windows.Media.Control (GSMTC): mídia de qualquer app
│   ├── VolumeService.cs      Core Audio via NAudio
│   ├── YouTubeResolver.cs      Descobre qual vídeo do YouTube o navegador está tocando
│   ├── ClaudeService.cs        Sessões do Claude Code (hooks) + tokens e títulos (históricos locais)
│   ├── NotificationService.cs  Espelha as notificações do Windows (UserNotificationListener) + histórico
│   ├── PackageRegistration.cs  Registra o pacote esparso que dá identidade ao exe
│   ├── BatteryService.cs     GetSystemPowerStatus
│   └── ApiServer.cs          HTTP em 127.0.0.1 (TcpListener, sem precisar de admin)
└── Interop/NativeMethods.cs  Janela sem foco/fora do Alt+Tab, detecção de tela cheia
```

Logs de erro ficam em `%LOCALAPPDATA%\WindowsIsland\log.txt`.

## Próximos passos

- Responder e executar ações das notificações direto na ilha
- Lista de apps silenciados / respeitar o modo Não Incomodar
- Limites do plano do Claude (janela de 5h/semanal) em vez de só tokens
- Brilho da tela, Bluetooth (fone conectado + bateria), microfone/câmera em uso
- Cor de destaque extraída da capa do álbum
- Tela de configurações (posição, monitor, tamanho, tema)
- Suporte a múltiplos monitores
