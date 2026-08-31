# Baseline Checklist

Before adding features:

- [ ] `dotnet restore` succeeds
- [ ] solution builds on Windows with .NET 8
- [ ] tests run
- [ ] first-run acknowledgement appears and cannot be skipped
- [ ] Codex Account is the default provider
- [ ] provider presets load
- [ ] DPAPI secret test passes under CurrentUser
- [ ] SQLite database initializes
- [ ] `codexhub providers` lists Codex Account + presets
- [ ] `codexhub refresh <project>` creates `.adam-codexhub/CURRENT_STATE.md`
- [ ] gateway binds only to `127.0.0.1`
- [ ] no secrets appear in logs
- [ ] backup/restore tests are added before production config switching
