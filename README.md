# Traiding

Sistema de trading algoritmico para Deriv (indices sinteticos): diseno de
estrategias, backtesting masivo y ejecucion en vivo, controlable por interfaz
o por API local.

## Estructura

```
Traiding/
├── WpfDerivClientVB/   Aplicacion de escritorio (WPF, VB.NET, .NET Framework 4.8)
├── scripts/            Scripts PowerShell que usan las APIs locales de la app
│   ├── agent_auth.ps1  Agrega el token X-Agent-Token a llamadas a localhost:8765/8766
│   ├── mcp/            Servidor MCP "indicadores-deriv" (lo usa Claude)
│   ├── bots/           Bots de operacion automatica (bot_r100, bot_seykota, analizar_operar)
│   ├── backtest/       Lanzadores de baterias de backtest
│   └── descargas/      Descarga e importacion de historicos de velas
├── infra/              PostgreSQL en Docker (docker-compose.yml, init.sql, .env)
├── docs/               Documentacion y maquetas de interfaz
└── legacy/             Prototipos de la primera prueba (Python y scripts sueltos)
```

## Arranque rapido

1. Base de datos (crear antes `infra/.env` con `POSTGRES_PASSWORD=...`):

   ```
   docker compose -f infra/docker-compose.yml up -d
   ```

2. Aplicacion: abrir `WpfDerivClientVB.sln` o ejecutar
   `WpfDerivClientVB/bin/Debug/net48/WpfDerivClientVB.exe`.
   Modo agente (sin ventanas): `WpfDerivClientVB.exe --heavy-api --minimized`.

## Seguridad

- Ningun secreto va en el codigo ni en git: tokens de Deriv cifrados en la BD
  (DPAPI), contrasena de Postgres en `infra/.env`, resto en `db_config.json`
  (carpeta de la app, fuera de git).
- APIs locales 8765/8766: requieren `X-Agent-Token` (ver `scripts/agent_auth.ps1`).
- Instrucciones SignalR que modifican estado: firmadas con HMAC (`SignalRSecret`).
- Gestor de riesgo: monto maximo, perdida maxima diaria, contratos abiertos y
  parada global (ver `WpfDerivClientVB/Infrastructure/RiskManager.vb`).
