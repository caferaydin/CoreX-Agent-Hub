# Faz 7 — Bağımsız review

Bu belge, bağımsız review adımının **ne olduğunu**, hangi ölçümlerin onu bu hâle
getirdiğini ve G7-1 eşik kalibrasyonunun sonucunu kaydeder.

Karar kaydı: [ADR-0022](adr/ADR-0022-bagimsiz-review-kanit-uretir.md).

## 1. Sınır

Review bir **commit'i** inceler.

Girdi, sonuç commit'i zaten atılmış ve kendisinden istenen makine doğrulamasını zaten
geçmiş bir execution'dır. Çıktı, yapılandırılmış bulgular ve bunlardan **Hub'ın** türettiği
bir sonuçtur.

Review'ün yapamadıkları, dikkatli olduğu için değil, tuttuğu portlarda karşılıkları
**olmadığı** için yapılamaz:

| Yapmaz | Neden yapamaz |
|---|---|
| Onarım / kod değişikliği | Reviewer `ReadOnly` profille koşar; runtime bunu `Read, Glob, Grep` + `plan` iznine çevirir |
| Commit / merge / push | Application katmanında git portu yoktur; snapshot komutları Workspace modülünündür |
| Süreç başlatma | Reviewer, kabul edilmiş agent süreç sınırından geçer; `Process.Start` yoktur |
| Kapsam belirleme | `outOfScope` reviewer'ın iddiasıdır; cevabı Hub, değişen yollardan türetir |
| Sonuç belirleme | Şemada "verdict" alanı yoktur; kapı `ReviewFindingGate`'tir |

## 2. Faz 7A'da ölçülenler

**Ö1 — Biten sağlayıcı, tamamlanmış review demek değildir.**
Gerçek sağlayıcı sıfır çıkış koduyla bitip kendini başarılı ilan ederken **hiç**
yapılandırılmış çıktı döndürmeyebilir. Şema hatası dışarıdan başarıya benzer. Ayırt eden
tek şey `structured_output` alanının varlığıdır.

*Sonucu:* "çıktı yok" asla "bulgu yok" değildir → `ReviewUnusable`.

**Ö2 — Sağlayıcı kendi araç yüzeyine bir araç ekler.**
`--json-schema` verildiğinde `system/init` olayı, istenen listeye ek olarak sağlayıcının
kendi `StructuredOutput` aracını bildirir.

*Sonucu:* beklenen etkin yüzey `istenen + StructuredOutput` olarak hesaplanır. Bu bir ihlal
değil, ilan edilmiş bir gerçektir; adapter onu bu şekilde doğrular.

**Ö3 — Reviewer izin modu.**
`ReadOnly` profil `--permission-mode plan` ve `--tools Read,Glob,Grep` üretir. Init olayı
her ikisini de geri bildirir ve adapter uyuşmazlıkta koşumu reddeder.

## 3. Akış

```
Verify(Passed) → Reviewing
  snapshot(ResultCommitSha)            tek kullanımlık, agent worktree'si DEĞİL
  inspect(before)                      HEAD == ResultCommitSha ve temiz olmalı
  ── deneme 1..2 ───────────────────────────────────────────────
     reviewer(prompt = Hub sözleşmesi + saklanmış diff, schema)
     inspect(after) → tamper? → ReviewUnusable, yeniden deneme YOK
     process outcome != Completed → ReviewUnusable, yeniden deneme YOK
     belge okunamadı → yalnızca burada bir kez yeniden dene
     belge geçerli → Judge
  ──────────────────────────────────────────────────────────────
  release(snapshot)                    artık kalırsa raporlanır, sonucu değiştirmez
```

`Judge`, her bulgunun yolunu Hub'ın kendi kuralıyla doğrular, `outOfScope`'u değişen
yollardan **yeniden hesaplar**, uyuşmazlığı kaydeder ve `ReviewFindingGate`'e sorar.

Yargılanamayan bir yol (`../..`, mutlak yol) zararsız bir kapsam-dışı gözlem değildir:
belgenin tamamı `ReviewUnusable` olur.

## 4. Sonuçlar

| Sonuç | Adım | Görev | İnsan kabulü |
|---|---|---|---|
| `Passed` | `Succeeded` | `AwaitingAcceptance` | Düz kabul |
| `ReviewBlocked` | `Succeeded` | `Ready` | Yalnızca override (ADR-0014) |
| `ReviewUnusable` | `Failed` / `TimedOut` / `Cancelled` | `Ready` | Hiçbiri |

`ReviewBlocked` **çalışan** bir review'dür: işini yapmıştır, bu yüzden adımı `Succeeded`
sayılır. `ReviewUnusable` review'ün **yokluğudur** ve hiçbir kabul yolundan geçmez.

## 5. Kanıt

Reviewer'ın her denemesi `AgentStdout` / `AgentStderr` / `AgentStructuredResult` olarak
saklanır ve **Review adımına** bağlanır. Implementer'ın aynı türdeki kanıtı `Agent`
adımındadır; ikisini ayıran şey `ExecutionStepId`'dir.

Hub'ın doğruladığı hâl — kanonik belge — `ReviewFindings` olarak saklanır. Sonraki bir
okuyucunun güvenmesi gereken kopya budur; sağlayıcının kendi belgesi ham kanıt olarak
yanında durur.

Bulgular `Finding` satırları olarak, review adımına bağlı, sonucu yazmadan **önce**
kaydedilir. "Bulgusu olmayan `ReviewBlocked`" bu yüzden olası değildir.

## 6. Yapılandırma

```yaml
agentProfiles:
  - name: claude-reviewer
    role: Reviewer
    provider: ClaudeCode
    runtimeKey: claude-code
    permissionProfile: ReadOnly
    model: claude-opus-5
    effort: high

repositories:
  - slug: corex-agent-hub
    reviewerProfile: claude-reviewer
```

Composition, execution satırı **var olmadan önce** şunları reddeder: bildirilmemiş profil,
yanlış rol, yazabilen izin profili, model/effort eksikliği, bildirilmemiş runtime anahtarı,
`ReadOnly` veya şema yeteneği ilan etmeyen runtime.

`reviewerProfile` bildirilmemişse repository review edilmiyordur; adım `Skipped` +
`ReviewNotConfigured` olur ve bu bir geçiş değil, bir beyandır.

Faz 8'de reviewer'ı Codex'e almak bu profili değiştirmektir; kodda değişiklik gerekmez.

## 7. G7-1 — eşik kalibrasyonu

Ölçüm tek seferlik ve sınırlıydı: **bir** gerçek Claude review'ü, Hub'ın kendi
repository'sindeki gerçek bir commit üzerinde, üretim review yolunun tamamından geçerek.

### Koşum

| | |
|---|---|
| İncelenen commit | `0ef77e6` — "fix: terminalize early execution cancellation" (STAB-01) |
| Değişim boyutu | 6 dosya, +705 / −7 satır; 808 satırlık unified diff |
| Profil | `claude-reviewer` — `ReadOnly`, `claude-opus-5`, effort `high` |
| Runtime beyanı | `SupportsReadOnlyMode=true`, `SupportsStructuredSchema=true` |
| Snapshot | `ResultCommitSha`'ya sabitlenmiş tek kullanımlık worktree |
| Süre | 476 s (soft 900 s / idle 1200 s / hard 1800 s) |
| Deneme | **1** — ilk denemede geçerli yapılandırılmış belge geldi |
| Yapılandırılmış çıktı | 5 722 bayt; stdout 586 KB, stderr 0 bayt |
| Snapshot artığı | yok |

### Sonuç

```
verdict   : Clean
findings  : 4  (kapsam içi 4, kapsam dışı 0)
severity  : Critical 0 · Major 0 · Minor 3 · Info 1
threshold : MajorThreshold = 1  →  bloklamadı
scope     : 0 uyuşmazlık
```

| # | Severity | Kategori | Güven | Kapsam içi |
|---|---|---|---|---|
| 1 | Minor | correctness | medium | evet |
| 2 | Minor | test-coverage | medium | evet |
| 3 | Minor | test-coverage | high | evet |
| 4 | Info | convention | high | evet |

### Kalibrasyonun söylediği

**Eşik 1'de kalır.**

Kalibrasyonun tek sorusu şuydu: eşik 1 iken, **sağlam** bir değişim yanlışlıkla bloklanır
mı? Ölçüm hayır diyor. Reviewer'ın söyleyecek dört şeyi vardı ve hiçbirini bloklayıcı
saymadı; severity'yi şişirmedi. Şişiren bir reviewer, eşiği 1 olan bir kapıda temiz her
değişimi `ReviewBlocked` yapardı — ölçülen davranış bu değil.

Ters yön de aynı ölçümle desteklenir: eşiği yükseltmek, gerçek bir Major bulguyu tek
başına insana götürmemek demek olurdu. `ReviewBlocked` zaten bir insanın override
edebileceği **tek** sonuçtur (ADR-0014); tek bir gerçek Major'ın bir insanın bakmasını
gerektirmesi maliyetli değil, doğru olandır.

### Yan ölçümler

**Kapsam.** Reviewer'ın dört `outOfScope` iddiasının dördü de Hub'ın değişen yollardan
türettiği cevapla uyuştu (0 uyuşmazlık). Alan yine de reviewer'a bırakılmaz: uyuşma bir
ölçümdür, garanti değil.

**Anchor kalitesi.** Dört bulgunun dördü de `dosya:satır` çözümlenebilir ve gerekçeleri
gerçekten okunmuş koda atıf yapıyor — biri, diffte olmayan `Finish.cs` ve
`Execution.Lifecycle.cs` satırlarına, yani snapshot'tan okunmuş dosyalara.

**Şema yolu.** İlk denemede geçerli belge geldi; yeniden deneme yolu bu koşumda
tetiklenmedi. Ö1'in ("çıktı yok, bulgu yok değildir") pahalı yolu bu yüzden burada
ölçülmedi; deterministik testlerle kanıtlanır.

**Bağımsızlık.** Reviewer, incelediği commit'te eklenen iki architecture guard'ın mevcut
guard'ları tekrarladığını kendi başına buldu — kimse söylemeden. Bu, review'ün
"onaylayan" değil "bakan" bir şey olduğunun ölçüsüdür.

### Kalibrasyonun kapsamadıkları

Bu **bir** ölçümdür. Şunları kanıtlamaz ve kanıtladığı iddia edilmez:

- Bloklayan bir review'ün gerçek reviewer ile nasıl göründüğü (bu koşumda Major çıkmadı).
- Reviewer'ın enjekte edilmiş bir talimata canlı tepkisi. Hub tarafı deterministik olarak
  kanıtlıdır — diff içindeki talimat hiçbir kararı değiştiremez — ama modelin kendi
  direnci burada ölçülmedi.
- Yeniden deneme ve tamper yollarının canlı davranışı.
- Bu ölçüm bir üretim implementation task'ı **değildi**; uçtan uca gerçek görev koşumu
  Faz 7C'dir.

Ham reviewer transkripti (586 KB) repository'ye **girmez**; gerçek koşumlarda kanıt
deposunda, Review adımına bağlı olarak saklanır.
