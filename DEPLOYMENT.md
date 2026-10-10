# Deployment

## Server (API + PostgreSQL + Control Center)
Requirements: Linux host with Docker 24+, a domain and a TLS reverse proxy (Caddy/Nginx/Traefik) in front of ports 8080 (API) and 3000 (Control Center).

1. `cp deploy/.env.example deploy/.env` and fill in every secret (`openssl rand -base64 48` for `AUTH_SIGNING_KEY`, `openssl rand -base64 32` for `BACKUP_ENCRYPTION_KEY`).
2. Create the license signing key **on the server only**:
   `mkdir -p deploy/secrets && openssl ecparam -name prime256v1 -genkey -noout | openssl pkcs8 -topk8 -nocrypt -out deploy/secrets/license-signing-key.pem`
   Clients verify certificates with the matching public key, which is published by the API; the private key never leaves the server.
3. `docker compose -f deploy/docker-compose.yml --env-file deploy/.env up -d --build`
4. Sign in to the Control Center with the bootstrap owner, enable MFA, then remove `BOOTSTRAP_ADMIN_*` from `.env` and restart.
5. Back up `deploy/secrets/` and the `BACKUP_ENCRYPTION_KEY` offline. Backups are useless without it.

## Telegram notifications (optional)
1. Create a bot with @BotFather and set `TELEGRAM_BOT_TOKEN` (server secret only).
2. Set `TELEGRAM_ALLOWED_CHAT_ID` to your chat id, `TELEGRAM_WEBHOOK_SECRET` to a random string, `TELEGRAM_ENABLED=true`.
3. Register the webhook: `curl "https://api.telegram.org/bot<TOKEN>/setWebhook?url=https://<api-domain>/api/v1/telegram/webhook&secret_token=<WEBHOOK_SECRET>"`.

## Clients
- Android (VELTRIX OS / VELTRIX Admin): install the APK from the GitHub Actions artifact, enter the API URL (https) on first launch.
- Windows: run `VELTRIX-OS-Windows-<version>-Setup.exe`. The installer is not code-signed yet, so Windows SmartScreen shows a warning until an Authenticode certificate is configured.

## Release signing (not configured in the repository)
- Android release: provide `VELTRIX_KEYSTORE_*` environment variables in CI secrets (see `apps/android/app/build.gradle.kts`).
- Windows: Authenticode certificate + `signtool` step (to be added once a certificate exists).
