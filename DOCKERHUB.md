# tellikoroma/openclaw.net

Self-hosted OpenClaw.NET gateway + agent runtime in .NET (NativeAOT-friendly).

This image runs the gateway on port `18789` and persists memory under `/app/memory`.

## Quick start

```bash
docker run -d --name openclaw \
  -p 18789:18789 \
  -e MODEL_PROVIDER_KEY="sk-..." \
  -e OPENCLAW_AUTH_TOKEN="$(openssl rand -hex 32)" \
  -v openclaw-memory:/app/memory \
  -v "$(pwd)/workspace:/app/workspace" \
  tellikoroma/openclaw.net:latest
```

Open WebChat at `http://127.0.0.1:18789/chat` and paste your `OPENCLAW_AUTH_TOKEN`.

## Required environment variables

- `MODEL_PROVIDER_KEY`: your LLM provider API key.
- `OPENCLAW_AUTH_TOKEN`: required when binding to non-loopback (the container binds `0.0.0.0` by default).

## Common optional environment variables

- `MODEL_PROVIDER_MODEL` (default `gpt-4o`)
- `MODEL_PROVIDER_ENDPOINT` (default empty)

## Default hardening in the container

This image sets safe defaults for public binds:
- Shell tool disabled (`OpenClaw__Tooling__AllowShell=false`)
- File tool roots limited to `/app/workspace`
- JS plugin bridge disabled by default (`OpenClaw__Plugins__Enabled=false`)

If you intentionally want plugins, explicitly set:
- `OpenClaw__Plugins__Enabled=true`

## Volumes

- `/app/memory`: persisted sessions/branches/notes
- `/app/workspace`: optional workspace mount for read/write file tools

## Healthcheck

The image includes a healthcheck that runs:
- `/app/OpenClaw.Gateway --health-check`

## Docker Compose

```yaml
services:
  openclaw:
    image: tellikoroma/openclaw.net:latest
    restart: unless-stopped
    ports:
      - "18789:18789"
    environment:
      - MODEL_PROVIDER_KEY=${MODEL_PROVIDER_KEY}
      - OPENCLAW_AUTH_TOKEN=${OPENCLAW_AUTH_TOKEN}
      - OpenClaw__BindAddress=0.0.0.0
      - OpenClaw__Port=18789
    volumes:
      - openclaw-memory:/app/memory
      - ./workspace:/app/workspace

volumes:
  openclaw-memory:
```

## Notes for WebChat

WebChat connects to `/ws` with a query token (`?token=`). For non-loopback binds, enable legacy query tokens if you use the built-in WebChat:
- `OpenClaw__Security__AllowQueryStringToken=true`

