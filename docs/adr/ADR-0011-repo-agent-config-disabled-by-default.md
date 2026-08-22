# ADR-0011 — Repo içi agent yapılandırması ve git hook'ları varsayılan olarak devre dışı

- **Durum:** Accepted
- **Tarih:** 2026-08-22
- **Kaynak:** mimari doküman § H.4, G.2, K.3 #1

## Bağlam

Repo içeriği (README, kod yorumları, `CLAUDE.md`, `AGENTS.md`, `.claude/settings.json`,
git hook'ları) **güvenilmeyen veridir**. Bunların agent davranışını sessizce yönlendirmesi
bir prompt-injection ve keyfî kod çalıştırma yüzeyidir.

## Karar

1. Agent çalıştırmalarında repo kaynaklı yapılandırma devre dışı bırakılır:
   `--safe-mode`, `--setting-sources user`, `--strict-mcp-config`.
2. Worktree'de `core.hooksPath` Hub'ın sahibi olduğu bir dizine yönlendirilir; böylece
   **repoya commit edilmiş git hook'ları da devre dışı kalır**.
3. Bir repo kuralı gerçekten faydalıysa **context repoya açıkça alınır**; güven sessiz
   bir varsayım değil, git'te görünen kayıtlı bir karar olur.

## Sonuçlar

- Agent yalnızca Hub'ın verdiği bağlamı görür.
- Repo hook'larının devre dışı kalması aynı zamanda doğrulama determinizmini artırır
  (lint gibi işler doğrulama profilinde açıkça tanımlanır).
- Kod yorumlarının okunmaya devam etmesi kabul edilen bir artık risktir.
