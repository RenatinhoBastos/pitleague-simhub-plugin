# Deploy VPS — PitLeague SimHub Plugin

## VPS Info

| Item | Valor |
|------|-------|
| **OS** | Windows Server 2025 (build 26100) |
| **IP público** | 181.224.24.142 |
| **SSH porta** | 2222 (porta 22 bloqueada pelo provedor) |
| **User** | Administrador |
| **SimHub** | 9.11.11 |
| **.NET** | 4.8.1 |
| **Git** | 2.54.0 |
| **Python** | 3.12.9 |

## Caminhos no VPS

| O quê | Caminho |
|-------|---------|
| **Pasta de plugins SimHub** | `C:\Program Files (x86)\SimHub\` (raiz!) |
| **DLL do plugin** | `C:\Program Files (x86)\SimHub\PitLeaguePlugin.dll` |
| **Repo clonado** | `C:\pitleague\pitleague-simhub-plugin\` |
| **Logs SimHub** | `C:\Program Files (x86)\SimHub\Logs\` ou `%APPDATA%\SimHub\SimHub.log` |

## SSH do Mac

```bash
# Conexão rápida
ssh pitleague-vps

# Config em ~/.ssh/config:
# Host pitleague-vps
#   HostName 181.224.24.142
#   Port 2222
#   User Administrador
#   IdentityFile ~/.ssh/pitleague_vps_ed25519
```

## Scripts

### Deploy DLL para VPS
```bash
# Última release do GitHub
./scripts/deploy-to-vps.sh

# DLL local (compilada manualmente ou via Actions)
./scripts/deploy-to-vps.sh --local /caminho/PitLeaguePlugin.dll

# Git pull no VPS (sem compilação)
./scripts/deploy-to-vps.sh --pull
```

### Testar endpoints da API
```bash
export PITLEAGUE_API_KEY=sk_live_xxx
export PITLEAGUE_LEAGUE_ID=uuid-da-liga
./scripts/test-simhub-endpoints.sh
```

## Troubleshooting

### SSH não conecta
- Porta 22 bloqueada pelo provedor — usar porta **2222**
- Verificar serviço: `Get-Service sshd` no VPS
- Debug: `ssh -vvv pitleague-vps`

### SimHub não carrega plugin
- DLL deve estar na **raiz** `C:\Program Files (x86)\SimHub\`, não em subpasta
- Verificar permissões: `icacls "C:\Program Files (x86)\SimHub\PitLeaguePlugin.dll"`
- Log: `Get-Content "$env:APPDATA\SimHub\SimHub.log" -Tail 50`

### Windows em português
- Grupo admin = `Administradores` (não `Administrators`)
- icacls usa nomes localizados

## Setup inicial (feito na Sessão 34)

1. sshd habilitado (porta 2222, auto-start)
2. Firewall liberado (regra `OpenSSH-Server-In-TCP-2222`)
3. Shell default = PowerShell
4. Chave SSH ed25519 autorizada em `C:\ProgramData\ssh\administrators_authorized_keys`
5. Git 2.54.0 instalado via winget
6. Repo clonado em `C:\pitleague\pitleague-simhub-plugin\`
