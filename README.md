# Windows Island

A Dynamic Island do Mac/iPhone, só que para Windows. Uma pílula preta no topo da tela que cresce com animação de mola para mostrar o que está acontecendo, e que **qualquer app pode controlar** por uma API HTTP local.

## O que já funciona

| Recurso | Como aparece |
| --- | --- |
| **Mídia**: Spotify, YouTube no navegador, Media Player e qualquer app que use os controles de mídia do Windows | Compacto: capa + música + equalizador na cor da capa. Quando a música muda, a ilha se abre por 3 s mostrando o que está tocando. Ao passar o mouse: capa, artista, barra de progresso (clique para pular para um ponto) e botões ⏮ ⏯ ⏭ |
| **YouTube no navegador** (Chrome, Edge, Brave, Firefox…) | Ao abrir a ilha na página de música, **o próprio vídeo toca lá em cima**: mudo (o som continua no navegador) e sincronizado com a posição e o play/pause da aba. Com **Fixar**, o vídeo sai da ilha para uma janelinha sempre visível que você arrasta pela tela (picture-in-picture) |
| **Notificações do Windows**: WhatsApp, Teams, Outlook, Discord… | Cada notificação nova aparece expandida com o ícone e o nome do app, o título e o texto. Dispensar no Central de Notificações também tira da ilha |
| **Volume** | Ao mudar o volume, a ilha vira uma barra de nível (acompanha a troca de dispositivo de saída) |
| **Bateria** | Avisos de carregador conectado/desconectado e de bateria fraca (20%, 10%, 5%) |
| **Relógio** | No modo ocioso. Ao passar o mouse, mostra hora e data |
| **API de integração** | Notificações e atividades ao vivo (downloads, timers, builds…) enviadas por qualquer programa |
| **Tela cheia** | A ilha se esconde em jogos, vídeos em tela cheia e apresentações |
| **Claude Code** | Sessões por projeto ao vivo ("Editando Menu.tsx", "Executando: npm test", "Aguardando você"), **uso diário e semanal** (tokens e respostas de hoje, dos últimos 7 dias e das últimas 5h, com gráfico por dia), alerta quando ele precisa de permissão e quando **termina**, com o começo da resposta |
| **Pergunte ao Claude** | Uma caixa de conversa com o Claude dentro da ilha (aba 💬 ou **Ctrl+Alt+Espaço** de qualquer lugar). A resposta chega em tempo real, a conversa continua entre perguntas e, se você fechar a ilha, ela avisa quando o Claude responder |
| **Buraco negro na bandeja** | Ícone de buraco negro nos ícones ocultos da barra de tarefas (as "setinhas"): um clique e a ilha é sugada girando para dentro de si mesma e some; outro clique e ela volta. Botão direito: mostrar/ocultar, iniciar com o Windows, sair |
| **Personalização** | Engrenagem ⚙️ na barra de abas: borda em degradê (presets como Apple Intelligence, Aurora e Pôr do sol, ou duas cores à sua escolha), espessura, degradê em movimento, brilho, relógio em repouso e iniciar com o Windows |

### Páginas: trocando entre apps

A ilha funciona como as Live Activities do iPhone. Cada fonte (música, Claude, notificações, atividades da API) é uma **página**:

- **Passe o mouse** para expandir. Embaixo aparece uma **barra de abas**: clique para trocar de página, ou use a **roda do mouse**.
- A página escolhida fica no modo compacto quando você tira o mouse.
- **Ilha dividida:** com duas coisas ativas ao mesmo tempo (ex.: Spotify tocando e Claude trabalhando), a segunda aparece numa **bolinha** ao lado. Clique nela para trocar.
- Alertas (volume, notificações novas, "Claude terminou") passam por cima de tudo por alguns segundos. **Clique** num alerta para abrir o app ou projeto correspondente.
- Na página de Notificações, clique numa mensagem para abrir o app (WhatsApp etc.). Na página do Claude, clique numa sessão para abrir o projeto no VS Code.

**Botão direito** abre o menu (notificações do Windows, iniciar com o Windows, copiar endereço da API, testar, sair).

## Instalando

Baixe o **`WindowsIsland-Setup-<versão>.exe`** na página de [Releases](https://github.com/PedroRuedas/Windows-Island/releases) e execute. Ele:

- instala em `C:\Program Files\Windows Island`, com o .NET embutido (não precisa instalar nada antes);
- cria o atalho no Menu Iniciar e, se você marcar, inicia com o Windows;
- já deixa as **notificações do Windows** (WhatsApp, Teams…) funcionando, sem Modo de Desenvolvedor: instala o certificado do pacote e registra a identidade do app (por isso pede permissão de administrador).

Requisitos: Windows 10 2004+ ou Windows 11 (x64). O vídeo do YouTube usa o WebView2, que já vem no Windows 11 e na maioria dos Windows 10.

> O Windows SmartScreen pode avisar que o instalador "não é reconhecido", porque ele não é assinado por uma autoridade comercial. Clique em **Mais informações → Executar assim mesmo**.

Para desinstalar: Configurações → Aplicativos → Windows Island → Desinstalar. O pacote e o certificado também são removidos.

### Gerando o instalador

```powershell
powershell -ExecutionPolicy Bypass -File .\installer\build.ps1
```

O script publica o app, empacota e assina o pacote de identidade e compila o instalador com o [Inno Setup](https://jrsoftware.org/isinfo.php) (`winget install JRSoftware.InnoSetup`). Na primeira execução ele cria o certificado de assinatura em `installer\.signing\`, que fica fora do Git. Guarde essa pasta: versões novas precisam ser assinadas pelo mesmo certificado para atualizar as instalações existentes.

## Rodando a partir do código

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

## Personalizando

Passe o mouse na ilha e clique na **engrenagem**, no fim da barra de abas. A ilha continua preta, e só a borda muda:

- **Borda:** "Clássica" (o contorno discreto de sempre), sete degradês prontos ou **Personalizada**, com cor inicial e final.
- **Espessura:** fina, média ou grossa.
- **Degradê em movimento:** as cores giram devagar em volta da ilha. Fica desligado por padrão, porque redesenhar a ilha continuamente custa um pouco de CPU.
- **Brilho na borda:** um halo suave na cor do degradê.
- **Relógio na ilha em repouso** e **Iniciar com o Windows**.

Tudo é aplicado na hora e salvo em `%LOCALAPPDATA%\WindowsIsland\settings.json`.

## Vídeo do YouTube na ilha

Os controles de mídia do Windows informam só o título, o canal e a duração do que o navegador toca, sem o link. Por isso a ilha busca o título no YouTube e só aceita um resultado com **o mesmo título** e **a mesma duração** (ou cuja aba aberta no navegador confirme que é YouTube). Anúncios nunca batem com nenhum resultado, então durante eles a ilha mostra a capa normal.

O vídeo encontrado roda num player do YouTube embutido ([YouTubeMirror.cs](src/WindowsIsland/Controls/YouTubeMirror.cs)), via WebView2, mudo e sincronizado com a aba a cada segundo. Ele pausa quando a ilha fecha, para não gastar processamento.

Limitações:
- Vídeos cujo dono desativou a incorporação em outros sites voltam a mostrar a capa.
- Ao carregar, pausar ou pular, o YouTube exibe a própria sobreposição (título, logo) por alguns segundos.
- O título do vídeo que está tocando é enviado ao YouTube na busca.

### Fixar o vídeo na tela (picture-in-picture)

Clique em **Fixar**, no canto do vídeo na ilha, e ele passa para uma janelinha que fica por cima de tudo, mesmo com a ilha fechada ou escondida no buraco negro:

- **Arraste** por qualquer ponto do vídeo para movê-la. Perto das bordas ou dos cantos da tela, ela gruda nelas, como no iPhone e no Mac.
- **Redimensione** pela alça no canto inferior direito ou com a **roda do mouse** sobre o vídeo. Ela mantém a proporção 16:9.
- **Passe o mouse** para ver o título, ⏮ ⏯ ⏭ e a barra de progresso (clique para pular). Os botões controlam a aba do navegador.
- **⤡ Voltar para a ilha** devolve o vídeo à ilha. O **✕** fecha a janelinha. Na ilha, a faixa "Vídeo fixado na tela" tem o botão **Trazer de volta**.
- A posição e o tamanho ficam salvos em `settings.json`.
- Durante anúncios a janelinha mostra a capa. Ela fecha sozinha alguns segundos depois que a aba ou o navegador fecha.

Enquanto o vídeo está fixado, só a janelinha toca o vídeo, e o player da ilha fica parado. Assim não há dois vídeos decodificando ao mesmo tempo.

## Pergunte ao Claude

Tire dúvidas e peça ajuda rápida sem sair do que está fazendo:

1. Aperte **Ctrl+Alt+Espaço** (ou abra a ilha e clique na aba 💬). Se outro app já usar esse atalho, a ilha usa **Ctrl+Shift+Espaço**.
2. Digite e aperte **Enter** (**Shift+Enter** quebra a linha). A resposta aparece em tempo real, com negrito, `código` e listas.
3. **Esc** fecha a ilha e devolve o foco para o app em que você estava. O Claude continua respondendo: a ilha mostra "Claude · Pensando…" e avisa com **"Claude respondeu"**. Clique no aviso para ler.

As perguntas seguintes continuam a mesma conversa. Use **Nova conversa** para recomeçar e **Copiar resposta** para levar o texto. O botão ■ interrompe uma resposta.

Como funciona: a ilha roda o Claude Code sem interface (`claude -p --output-format stream-json`, em [ClaudeChatService.cs](src/WindowsIsland/Services/ClaudeChatService.cs)) com o **seu login** do Claude Code. Não precisa de chave de API, e o uso conta no seu plano normalmente. O executável é encontrado no PATH, em `~/.local/bin` ou na extensão do Claude Code para VS Code. Os hooks ficam desligados nessas conversas, para elas não aparecerem como sessões na página do Claude Code.

Ele pode ler arquivos e pesquisar na web. Para tarefas que editam arquivos ou rodam comandos, ele explica o que faria e sugere abrir o Claude Code, porque a ilha não tem como pedir sua permissão.

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
├── VideoPipWindow.xaml(.cs)  Vídeo fixado: janelinha arrastável com ímã nas bordas, redimensionável, controles no hover
├── Core/
│   ├── IslandController.cs   Fila de atividades por prioridade + expiração + mídia
│   ├── IslandActivity.cs     Modelo de uma atividade
│   ├── Spring.cs             Física de mola (o "quique" da animação)
│   └── Icons.cs              Nomes de ícone → glifos Segoe Fluent Icons
├── Services/
│   ├── MediaService.cs       Windows.Media.Control (GSMTC): mídia de qualquer app
│   ├── VolumeService.cs      Core Audio via NAudio
│   ├── YouTubeResolver.cs      Descobre qual vídeo do YouTube o navegador está tocando
│   ├── TrayIcon.cs             Ícone do buraco negro na área de notificação
│   ├── ClaudeService.cs        Sessões do Claude Code (hooks) + tokens e títulos (históricos locais)
│   ├── ClaudeChatService.cs    "Pergunte ao Claude": roda `claude -p` com streaming e mantém a conversa (--resume)
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
