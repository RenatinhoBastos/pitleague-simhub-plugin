# 🏁 PitLeague SimHub Plugin — Guia de Instalação

## Pré-requisitos

- **SimHub** instalado (https://www.simhubdash.com) — versão 9.x ou superior
- **Visual Studio 2022** ou **.NET SDK 4.8** para compilar
- **Windows 10/11** (SimHub é Windows-only)
- Conta PitLeague com liga AV criada

---

## 1. Compilar o plugin

### Opção A — Visual Studio 2022
1. Abra `PitLeaguePlugin/PitLeaguePlugin.csproj` no Visual Studio
2. Defina a variável de ambiente `SIMHUB_PATH`:
   - Clique com botão direito no projeto → Properties → Build → Environment Variables
   - Adicione: `SIMHUB_PATH = C:\Program Files (x86)\SimHub`
3. Build → Release

### Opção B — Terminal (dotnet CLI)
```bat
set SIMHUB_PATH=C:\Program Files (x86)\SimHub
cd PitLeaguePlugin
dotnet build -c Release
```

### Resultado
Arquivo compilado: `PitLeaguePlugin/bin/Release/PitLeaguePlugin.dll`

---

## 2. Instalar no SimHub

1. **Feche o SimHub** completamente
2. Copie `PitLeaguePlugin.dll` para:
   ```
   C:\Program Files (x86)\SimHub\PluginManager\plugins\
   ```
3. **Abra o SimHub**
4. Vá em **Additional Plugins** (menu lateral)
5. O plugin **PitLeague** deve aparecer na lista — clique em **Enable**

---

## 3. Configurar o plugin

1. No SimHub, clique em **PitLeague** no menu lateral
2. Preencha:
   - **API Key**: gerada em PitLeague → Admin → Configurações → Integrações → SimHub
   - **League ID**: UUID da sua liga AV
   - **Jogo**: selecione o jogo que você usa
3. Clique em **Testar** para verificar a conexão
4. Ative **Enviar resultado automaticamente** se quiser envio automático ao fim da corrida

---

## 4. Usar no dia da corrida

### Modo automático (recomendado)
- Ative "Enviar resultado automaticamente" nas configurações
- Ao terminar a corrida, o plugin detecta o fim de sessão e envia sozinho
- Você receberá uma notificação no PitLeague

### Modo manual
- Ao fim da corrida, vá nas configurações do plugin
- Clique em **🚀 Enviar Resultado Agora**
- O resultado aparece no PitLeague para revisão do admin

---

## 5. Configurar o jogo

### F1 25
- Não precisa de configuração extra — SimHub já capta os dados UDP nativamente
- Verifique que o F1 25 está em: `Opções → Telemetria` → Ativo

### ACC / iRacing / AMS2
- SimHub conecta automaticamente quando o jogo está aberto
- Nenhuma configuração extra necessária

---

## 6. Testar no Mac (sem SimHub)

Use o emulador Python:

```bash
# Instalar dependência
pip3 install requests

# Configurar
export PITLEAGUE_API_KEY=sk_live_sua_chave
export PITLEAGUE_LEAGUE_ID=uuid-da-liga
export PITLEAGUE_GAME="F1 25"

# Testar conexão
python3 simhub_emulator.py --test

# Modo manual (digitar resultados)
python3 simhub_emulator.py --manual

# Modo auto (capturar UDP do F1 25 via PS5)
python3 simhub_emulator.py
```

---

## 7. Fluxo completo

```
Corrida termina
      ↓
Plugin detecta "Checkered"
      ↓
Coleta posições + tempos do SimHub
      ↓
POST /api/integrations/simhub/result
      ↓
API mapeia gamertags → pilotos da liga
      ↓
Resultado salvo em webhook_logs
      ↓
Admin recebe notificação push: "Resultado recebido via SimHub"
      ↓
Admin revisa e publica em championship/results
```

---

## 8. Troubleshooting

| Problema | Solução |
|----------|---------|
| Plugin não aparece no SimHub | Verifique se o DLL está na pasta correta e se foi habilitado |
| "❌ API Key não configurada" | Gere uma nova chave em Admin → Integrações → SimHub |
| "❌ Erro 401" | API Key inválida ou expirada |
| "❌ Erro 404" | League ID incorreto |
| 0 gamertags vinculados | Os nomes do jogo não batem com os gamertags cadastrados na liga — admin ajusta manualmente |
| Plugin não detecta fim de corrida | Verifique se o jogo está sendo reconhecido pelo SimHub (ícone verde no SimHub) |

---

## 9. Versões de jogo testadas

| Jogo | Status |
|------|--------|
| F1 25 | ✅ Testado |
| F1 24 | ✅ Compatível |
| ACC 1.10 | 🔄 Compatível (não testado) |
| iRacing | 🔄 Compatível (não testado) |
| AMS2 | 🔄 Compatível (não testado) |

---

*PitLeague v1.0.0 — pitleague.com.br*
