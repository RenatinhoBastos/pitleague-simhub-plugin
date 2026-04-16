# PitLeague SimHub Plugin

Plugin para [SimHub](https://www.simhubdash.com) que envia resultados de corrida automaticamente para o [PitLeague](https://app.pitleague.com.br).

## Download

[**Baixar última versão**](https://github.com/RenatinhoBastos/pitleague-simhub-plugin/releases/latest)

## Como instalar

1. Baixe `PitLeaguePlugin.dll` do link acima
2. Copie para `C:\Program Files (x86)\SimHub\PluginManager\plugins\`
3. Reinicie o SimHub
4. Vá em **Additional Plugins** → ative **PitLeague**
5. Configure **API Key** e **League ID** nas configurações do plugin

> API Key: gere em PitLeague → Admin → Configurações → Integrações → SimHub

## Jogos compatíveis

- F1 25 / F1 24
- ACC (Assetto Corsa Competizione)
- iRacing
- AMS2
- 200+ jogos suportados pelo SimHub

## Compilar (dev)

Requer Visual Studio 2022 ou .NET SDK com .NET Framework 4.8.

```bat
set SIMHUB_PATH=C:\Program Files (x86)\SimHub
cd PitLeaguePlugin
dotnet build -c Release
```

CI baixa as DLLs do SimHub automaticamente via GitHub Actions.

## Emulador (Mac/Linux)

Não tem SimHub? Use o emulador Python para capturar telemetria UDP do PS5:

```bash
pip3 install requests
export PITLEAGUE_API_KEY=sk_live_xxx
export PITLEAGUE_LEAGUE_ID=uuid-da-liga
python3 simhub_emulator.py          # modo auto (UDP)
python3 simhub_emulator.py --manual # modo manual
python3 simhub_emulator.py --test   # testar conexão
```

## Documentação

- [Guia de instalação completo](INSTALL.md)
- [Schema do payload](SCHEMA.md)
