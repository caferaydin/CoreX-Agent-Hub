# ADR-0016 — Bloklu/başarısız execution'lar için result commit atılmaz

- **Durum:** Accepted
- **Tarih:** 2026-08-22
- **Kaynak:** mimari doküman § G.4, G.6, I.5, P.2 (v1.1.1 final errata E1/M1/M2)

## Bağlam

Commit kalıcı bir git nesnesi üretir. Başarısız, kapsam dışı, sır içeren veya runtime
politikası ihlal edilmiş bir sonucu kalıcılaştırmak geri alınması zor bir yan etkidir:
`agent/...` branch'i gerçek repoda yaşamaya devam eder ve ileride manuel bir push ile
dışarı sızabilir. Öte yandan kanıtın korunması commit gerektirmez.

## Karar

1. Sonuç commit'i **yalnızca** dört kapı geçtiğinde atılır:
   K0 agent adımı `Succeeded`, K1 `HEAD == BaseCommitSha`, K2 ScopeGuard, K3 SecretGuard.
2. Şu outcome'larda **commit atılmaz**: `Failed`, `TimedOut`, `Cancelled`, `Interrupted`,
   `RuntimePolicyViolation`, `ScopeViolation`, `SecretGuardBlocked`.
3. Commit'siz yolda da asgari kanıt toplanır: `git status`, commit grafiği,
   `git add -N .` + `git diff <base>` ile çalışma ağacı diff'i, dosya envanteri, hash'ler,
   stdout/stderr, kapı sonucu.
4. **`ResultCommitSha == null` olan bir execution'ın worktree'si otomatik kaldırılamaz**;
   değişikliklerin tek kalıcı kopyası oradadır. Bırakma açık operatör komutu gerektirir.
5. SecretGuard'ın **bypass komutu yoktur.** (`hub commit-result --override` kaldırılmıştır.)
6. `ReviewUnusable` ayrı bir `Execution.Outcome` değeridir; şema ihlali veya inceleyici
   kurcalaması bu değere eşlenir.

## Sonuçlar

- Hiçbir kurtarma yolu veri silmez; temizlik daima açık operatör komutudur.
- `git add -N .` olmadan yeni (untracked) dosyalar diff'te görünmez; bu adım zorunludur.
- `.gitignore` kapsamı `add -N` tarafından atlandığı için yok sayılan sırlar kanıta girmez.
