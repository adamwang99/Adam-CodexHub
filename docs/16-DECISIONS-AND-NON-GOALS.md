# 16 — Decisions & Non-Goals

## Confirmed product decisions

### D1
Product name: **Adam CodexHub**

### D2
Windows-first technology:

**C# + .NET 8 + WPF**

### D3
Hybrid architecture:

- Codex account login: direct/native
- API providers: local gateway where compatible

### D4
Provider list is extensible.

Users can add providers.

Remote registry can add/update definitions.

### D5
Model catalogs can update automatically.

Discovered does not mean enabled.

### D6
Different provider sessions use safe handoff instead of forced in-place conversion.

### D7
Returning to a previous provider should normally resume its prior session plus automatic state refresh.

### D8
Automatic sync of unfinished work and stale sessions is ON by default.

### D9
First-run provider/session acknowledgement is mandatory.

### D10
Approved brand direction:

simple 2D Adam CodexHub wordmark; Adam amber/charcoal identity.

## Non-goals for V1

- building a replacement IDE
- modifying undocumented Codex internal databases
- silently routing every prompt to arbitrary providers
- storing complete chat history inside Adam CodexHub
- cloud account system
- team collaboration
- billing reseller features
- provider credential marketplace
