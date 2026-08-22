# ADR-0003 — Git worktree ile execution izolasyonu ve base-commit sabitleme

- **Durum:** Accepted
- **Tarih:** 2026-08-22
- **Kaynak:** mimari doküman § G.1, G.2

## Bağlam

Agent'ın geliştiricinin ana çalışma dizininde çalışması hem veri kaybı hem tekrar
üretilemezlik riski taşır. Ayrıca çalışmanın hangi kod durumundan başladığı kayıt altında
olmalıdır.

## Karar

Her execution için:

1. Hedef branch'in HEAD'i çözülüp `BaseCommitSha` olarak **sabitlenir**.
2. `git worktree add -b agent/<taskKey>/<attempt> <path> <BaseCommitSha>` ile
   **commit'ten** yeni bir worktree açılır (çalışma ağacından değil).
3. Tüm agent ve doğrulama işleri yalnızca bu worktree içinde yürür.

## Sonuçlar

- Geliştiricinin ana dizini kirli olsa bile görev başlayabilir.
- `.gitignore` kapsamındaki sırlar worktree'de hiç bulunmaz; izolasyon aynı zamanda
  bir sır sızıntısı önlemidir.
- Her execution tekrar üretilebilir bir başlangıç noktasına sahiptir.
- Submodule ve Git LFS içeren repolar MVP'de reddedilir.
