# Next Session — Continuation Plan

1. `git fetch origin ccr-c065d702-4m28p8 && git status && git log --oneline -15`.
2. Read `DEVELOPMENT_CHECKPOINT.md`, `TASKS.md`, `BUILD_STATUS.md`.
3. Environment: `export DOTNET_ROOT=/opt/dotnet PATH=/opt/dotnet:$PATH`; `service postgresql start`.
4. Re-verify: backend integration tests, desktop unit tests, `dotnet build apps/windows/Veltrix.Desktop`, Control Center `npm run lint && npm run typecheck && npm test && npm run build`, Android `scripts/gradle-with-backoff.sh ./gradlew assembleDebug` in `apps/android` and `apps/admin-android`.
5. Continue the first ⬜/🟡 row in `TASKS.md`. Do not redo ✅ rows.
6. Check GitHub Actions runs on the branch (GitHub MCP `actions_list`) and fix red jobs.
