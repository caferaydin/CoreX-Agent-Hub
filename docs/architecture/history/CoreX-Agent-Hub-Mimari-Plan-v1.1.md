# CoreX Agent Hub — Mimari Plan (v1.1, denetim düzeltmeleri uygulanmış)

**Belge türü:** Mimari karar kaydı / uygulama öncesi plan — düzeltme turu
**Önceki sürüm:** `CoreX-Agent-Hub-Mimari-Plan-v1.md` (yön onaylandı)
**Durum:** Faz 1 uygulaması için hazır — bkz. Bölüm Y
**Tarih:** 2026-08-22

> Bu oturumda da **hiçbir kod yazılmadı, hiçbir proje iskelesi kurulmadı, hiçbir bağımlılık yüklenmedi, hiçbir veritabanı oluşturulmadı, hiçbir commit atılmadı.** Bu belge yalnızca v1'e denetçi düzeltmelerini uygular ve çapraz referansları tutarlı hâle getirir.

---

## 0. Doğrulanmış Ortam Gerçekleri

| Bileşen | Ölçülen değer | Plana etkisi |
|---|---|---|
| İşletim sistemi | Windows 11 Pro 10.0.26200 | Sandbox stratejisi Linux'tan farklı — bkz. K, L |
| .NET SDK | 10.0.301 | `net10.0` hedeflenir |
| Git | 2.54.0.windows.1 | `git worktree`, `--porcelain=v2` mevcut |
| Node.js | v24.16.0 | Codex CLI ve JS test araçları için hazır |
| WSL2 | Kurulu, varsayılan dağıtım Ubuntu | Kronos için gereken sert izolasyonun birinci adayı |
| Claude Code | **2.1.240, kurulu ve oturumu açık** | Faz 6 için hazır; ayrı auth altyapısı gerekmiyor |
| Codex CLI | **KURULU DEĞİL** | Codex'e dair her şey doğrulanmamış kabul edilir |

### 0.1 Claude Code CLI — bu makinede doğrulanan yetenekler

| Flag | Doğrulandı | Mimari kullanım |
|---|---|---|
| `-p, --print` | ✔ | Non-interactive çalıştırma |
| `--output-format json\|stream-json` | ✔ | Yapısal sonuç + canlı akış |
| `--input-format stream-json` | ✔ | İleride kontrollü çok turlu girdi |
| `--json-schema <schema>` | ✔ | Şema doğrulamalı yapısal çıktı — review bulguları için |
| `--permission-mode manual\|plan\|acceptEdits\|auto\|dontAsk\|bypassPermissions` | ✔ | İzin profili motoru |
| `--tools` / `--allowedTools` / `--disallowedTools` | ✔ | Araç seviyesinde teknik kısıt (`Bash(git *)` desenleri) |
| `--add-dir <dirs...>` | ✔ | Context Pack dizinini okunur kılma |
| `--safe-mode` | ✔ | CLAUDE.md, skills, plugins, hooks, MCP, custom commands devre dışı |
| `--setting-sources user,project,local` | ✔ | Repo içi `.claude/settings.json` etkisini kesme |
| `--strict-mcp-config` + `--mcp-config` | ✔ | MCP yüzeyini sıfırlama |
| `--system-prompt-file`, `--append-system-prompt-file` | ✔ (help'te gizli, hata mesajıyla teyit) | Context Pack enjeksiyon kanalı |
| `--session-id <uuid>` | ✔ | `ExecutionId` = sağlayıcı oturum kimliği |
| `--no-session-persistence` | ✔ | Oturum sızıntısını engelleme |
| `-r/--resume`, `-c/--continue`, `--fork-session` | ✔ | Faz 10 onarım turu için |
| `--model`, `--effort`, `--fallback-model` | ✔ | Runtime profil parametreleri |
| `--debug-file <path>` | ✔ | Teşhis kanıtı |
| `--max-budget-usd` | ✔ (flag mevcut) | **Bütçe kontrolü buna dayandırılmaz** (abonelik auth altında etkisi belirsiz). Bütçe: eşzamanlılık=1 + günlük çalıştırma sayacı + timeout |
| `claude setup-token` | ✔ | **Loop Zero ve Faz 6 için GEREKLİ DEĞİL.** Yalnızca ileride gözetimsiz/CI/uzak worker çalıştırma gerekirse değerlendirilir |
| `--bare` | ✔ | **KULLANILMAYACAK** — OAuth/keychain okumaz, `ANTHROPIC_API_KEY` dayatır, mevcut abonelik oturumunu kırar |
| `-w, --worktree` | ✔ | **KULLANILMAYACAK** — worktree sahipliği Hub'da |

---

## 0.5 v1 → v1.1 Denetim Düzeltmeleri

### 0.5.1 Uygulanan düzeltmeler

| # | Düzeltme | Etkilenen bölümler |
|---|---|---|
| **D1** | `hub run` asla `Accepted`'a geçemez. Çalıştırma `AwaitingAcceptance` ile biter; `hub accept` ayrı, açık, geri dönülemez karardır | B, E.1, P.1, P.5, S.2, S.4, T-Faz 5, X |
| **D2** | Her zaman başarısız olan `pre-commit` hook'u kaldırıldı. Push engelleme korunur ve güçlendirilir. Hub kendi korumasını atlatmak zorunda kalmaz | G.2, G.3, G.4, K.3, U (ADR-0005) |
| **D3** | Bloklayıcı sorular tek global listeden çıkarıldı, **bloke ettikleri faza** göre sınıflandırıldı. Faz 1–5'in dış bağımlılığı yok | V (tamamen yeniden yazıldı), T, Y |
| **D4** | Kabul kriterleri `MACHINE` / `HUMAN` olarak açıkça beyan edilir. "Doğrulanamayan kriter = tanımsız görev" iddiası **reddedildi** | B.1, D.2, D.3, I.1, I.5, J.4, P.5, S.2, U (ADR-0015) |
| **D5** | **Kronos Onboarding Güvenlik Kapısı** eklendi. Mantıksal izolasyon yalnızca Core X Logic pilotu için kabul edilir | B, D.1, K.4, L (yeni L.4), T-Faz 10, U (ADR-0017), V |
| **D6** | Başarısız/kısmi execution'lar için **Hub commit'i atılmaz**. Kanıt commit olmadan korunur. Commit yalnızca agent adımı başarılı + ScopeGuard + SecretGuard geçtikten sonra | D.3, G.4, G.6, I.4, I.5, P.2, Q, S.2, U (ADR-0016) |
| **D7** | Loop Zero kapsamı sabit tutuldu; D4 ve D6 mevcut bileşenlere yapılan düzeltmelerdir, yeni bileşen değildir | S.2, S.2.1 |

### 0.5.2 Değişen mimari kararlar (karar düzeyi)

| Karar | v1 | v1.1 | Sebep |
|---|---|---|---|
| Kabul geçişi | Loop Zero kabul kriteri "`Accepted` ile biter" | Çalıştırma `AwaitingAcceptance` ile biter; kabul ayrı komut | D1 |
| Worktree hook politikası | `pre-push` **ve** `pre-commit` daima başarısız | Yalnızca `pre-push` daima başarısız; `pre-commit` yok | D2 |
| Agent commit engelleme | Hook ile "engellenmiş" sayılıyordu | **Orkestrasyon/runtime politikası + tespit**; hook değil | D2 |
| Kabul kriteri modeli | Hepsi makinece doğrulanabilir olmalı | `MACHINE` \| `HUMAN` beyanı zorunlu; her ikisi de geçerli | D4 |
| Otomatik outcome girdisi | (örtük) doğrulama + review | **Yalnızca** makine kanıtı + yapılandırılmış review kapıları. Kriter metni ve agent beyanı outcome'a giremez | D4 |
| Commit politikası | Agent bitince Hub her hâlükârda commit atar | Commit koşullu: agent adımı `Succeeded` **ve** ScopeGuard **ve** SecretGuard geçmeli | D6 |
| Worktree temizliği | Kabul/ret sonrası kaldırılır | **Result commit yoksa worktree kaldırılamaz** (değişikliklerin tek kalıcı kopyası orada) | D6 tutarlılık sonucu |
| Kronos | "Mantıksal izolasyon, artık risk kabul" | Core X Logic için kabul; **Kronos sert izolasyon olmadan kaydedilemez** (kodla zorlanır) | D5 |
| Claude auth | `setup-token` vs API key kararı bloklayıcı | Mevcut açık abonelik oturumu kullanılır; API-key faturalandırma mimarisi **tasarlanmaz**; `setup-token` yalnızca gözetimsiz çalıştırma gerekirse | D3 |
| Soru sınıflandırması | Tek "uygulama öncesi bloklayıcı" listesi | Faz bazlı kapı tablosu | D3 |
| Yeni Execution outcome | — | `SecretGuardBlocked` eklendi | D6 |
| Yeni bulgu türü | — | `UnexpectedAgentCommit` (tespit, engelleme değil) | D2 |

### 0.5.3 Kendi tutarlılık denetimim — v1'de bulup düzelttiğim çelişkiler

Bunlar denetçinin listesinde yoktu; iç tutarlılık taraması sırasında bulundum:

| Bulgu | v1'deki hâli | v1.1'de |
|---|---|---|
| **Faz numarası kayması** | F.4 "Codex Faz 7", T "Faz 8 Codex". K.4/L.3 "sandbox Faz 9", T "Faz 10 sertleştirme". B satır 14 "onarım Faz 9", T "Faz 10". B satır 18 "UI Faz 8", T "Faz 9" | Tek kanonik faz listesi (T) ve tüm çapraz referanslar buna göre düzeltildi |
| **Commit'siz kanıtta diff eksikliği** | Diff `BaseCommitSha..HEAD` ile alınıyordu; commit yoksa bu boş döner ve **yeni (untracked) dosyalar `git diff`'te görünmez** | `git add -N .` + `git diff <base>` ile çalışma ağacı diff'i; ayrıca dosya envanteri |
| **Worktree silme ile kanıt kaybı** | "Kabul/ret sonrası worktree kaldırılır" — commit yoksa değişiklikler yok olur | Commit yoksa kaldırma yasak (G.6 değişmezi) |
| **Agent adımı başarısızken doğrulama** | "doğrulama yine de çalıştırılır (opsiyonel)" — belirsiz | Kesin kural: agent adımı `Succeeded` değilse Verify ve Review **atlanır** |
| **Aggregate/tablo sayısı** | "12 tablo, 3 aggregate" — sayım eksikti | 15 tablo, 3 aggregate (D bölümünde sayım açık) |
| **`OrchestrationEvent` modelde yoktu** | R'de vardı, D'de yoktu | D.3'e eklendi |
| **Ready gate ile kriter modeli çelişkisi** | "kabul kriteri doğrulama komutuna bağlanamıyorsa Ready olamaz" — D4 ile çelişir | Ready gate D4 modeline göre yeniden yazıldı |

---

## A. Yönetici Özeti — Sistemi Nasıl Anladım

### A.1 CoreX Agent Hub nedir

CoreX Agent Hub, **coding agent'ları çalıştıran bir kontrol düzlemidir (control plane).** Zekâ üretmez; zekâyı *sınırlar, besler, gözlemler ve kanıtlar.*

Üç işi yapar:

1. **İzolasyon** — Her agent çalışması, geliştiricinin çalışma dizinine değil, commit'i sabitlenmiş, tek kullanımlık bir git worktree'sine karşı yürür.
2. **Bağlam sahipliği** — Kurumsal hafıza agent'ın sohbet geçmişinde değil, platformda durur. Her çalıştırma için deterministik, hash'lenmiş bir *Context Pack* üretilir.
3. **Kanıt** — Agent'ın "yaptım, testler geçti" beyanı hiçbir şey ifade etmez. Build/test/diff/exit-code gerçeklerini **platform kendisi** üretir ve saklar.

### A.2 Hangi problemi çözüyor

- Bir agent çalışmasının **ne yaptığını doğrulamak**, çalışmanın kendisinden pahalı.
- Aynı işi **tekrar üretmek** mümkün değil (prompt nerede, hangi commit'ten başladı, hangi kural seti geçerliydi?).
- Organizasyonlar arası **sızıntı riski** insan disiplinine bağlı.
- Ürün sayısı arttıkça **bağlam yeniden anlatma maliyeti** doğrusal büyüyor.

### A.3 Ne DEĞİLDİR

Sohbet uygulaması değil · sanal çalışan simülasyonu değil · otonom yazılım fabrikası değil · SaaS değil · genel amaçlı workflow motoru değil · RAG platformu değil.

### A.4 İlk değerli sonuç

> **45 dakikalık bir kod incelemesini, 5 dakikalık bir kanıt incelemesine indirmek.**

Somut ifadesi: `base commit` + `unified diff` + `build/test exit code ve sayıları` + `kapsam ihlali var mı` + `bu bağlam paketinin hash'i` → tek ekranda, tekrar üretilebilir şekilde.

### A.5 Bu projenin en büyük varoluşsal riski

Hub, terminalde `claude` yazmaktan **daha kötü bir deneyim** üretirse ölür. Kazanma koşulu: *izolasyon + kanıt + tekrarlanabilirlik*. Her faz bu üçünden birine hizmet etmelidir.

---

## B. Mimari Değerlendirme — KEEP / CHANGE / DEFER / REJECT

| # | Öneri | Karar | Gerekçe |
|---|---|---|---|
| 1 | Local-first, tek makine, tek kullanıcı | **KEEP** | Dağıtık hiçbir gereksinim yok |
| 2 | Evidence-first (AI beyanı ≠ kanıt) | **KEEP** | Sistemin varlık sebebi |
| 3 | İnsan tek kabul makamı | **KEEP + güçlendirildi (D1)** | `hub run` asla kabul edemez; kabul ayrı ve geri dönülemez karar kaydıdır |
| 4 | Git worktree ile görev izolasyonu | **KEEP** | En yüksek getirili tekil karar |
| 5 | Worktree'lerin repo içinde `.agent-worktrees` altında olması | **CHANGE** | Repo kirlenmesi, Windows 260-karakter yolu, araç taraması. Kök repo dışında ve kısa: `D:\cx\` |
| 6 | Sağlayıcıyı role sabitlememek | **KEEP** | `AgentProfile` ile çözülür |
| 7 | `IAgentRuntime` soyutlaması | **KEEP, daraltılmış** | 3 üyeli ince arayüz; ikinci implementasyon `ScriptedRuntime` |
| 8 | İlk dikey dilimin Codex review içermesi | **CHANGE** | Codex kurulu değil; dilim üçe bölündü (S) |
| 9 | Context Pack'in dev bir prompt bloğu olması | **CHANGE** | Pack dosya olarak materyalize edilir, hash'lenir, `--system-prompt-file` ile verilir |
| 10 | `Policy` ayrı varlık/motor | **REJECT (MVP)** | Kural DSL'i 1 kullanıcı ve 2 rol için aşırı |
| 11 | `AgentRun`/`Verification`/`Review` ayrı üst düzey varlıklar | **CHANGE** | Tek `ExecutionStep` (tipli) + `Finding` + `EvidenceArtifact` |
| 12 | Agent'ın commit atması | **REJECT + netleştirildi (D2)** | Commit'i Hub atar. Ancak bu **hook ile engellenmiş değil**, runtime politikası + tespit ile sağlanır |
| 13 | Önerilen 11 durumlu Task state machine | **CHANGE** | Task kaba, Execution ayrıntılı; `Failed` Task'ta değil Execution'da (P) |
| 14 | Repair loop (otomatik onarım) | **DEFER → Faz 10** | MVP'de tek deneme + insana yükselt |
| 15 | Şifreli secret store | **REJECT (MVP)** | Saklanacak sır yok. Yerine: env allowlist + **minimal SecretGuard** (D6) — alt sistem değil, tek bir kapı kontrolü |
| 16 | SQLite | **KEEP** | WAL + tek yazıcı + blob'lar dışarıda |
| 17 | .NET / ASP.NET Core | **KEEP** | Süreç kontrolü + P/Invoke (Job Objects) için ideal |
| 18 | React + TypeScript UI | **KEEP, ERTELE → Faz 9** | İlk "UI" Hub'ın kendi CLI'ıdır |
| 19 | PostgreSQL / Redis / RabbitMQ / K8s | **REJECT** | Gereksinim yok |
| 20 | Vector DB / RAG | **REJECT (MVP)** | Bağlam = sürüm kontrollü markdown |
| 21 | Organizasyon izolasyonu birinci sınıf | **KEEP + KAPI EKLENDİ (D5)** | Mantıksal izolasyon **yalnızca Core X Logic pilotu** için kabul. Kronos, sert izolasyon doğrulanana kadar kaydedilemez (L.4) |
| 22 | Reviewer'ın yazamaması | **KEEP + güçlendir** | `--tools Read,Glob,Grep` + `--permission-mode plan` + atılabilir worktree + hash ile tespit |
| 23 | Maliyet/kullanım takibi | **DEFER → Faz 10** | Metadata ilk günden saklanır, rapor sonra |
| 24 | OpenTelemetry / event bus | **DEFER** | `Activity` ile korelasyon; exporter yok |
| 25 | Otonomi (agent-to-agent) | **REJECT** | Hedef değil, risk |
| 26 | "Doğrulanamayan kriter = tanımsız görev" | **REJECT (D4)** | Geçerli mühendislik/ürün kriterlerinin bir kısmı makinece doğrulanamaz. `MACHINE`/`HUMAN` beyanı modeli benimsendi |

### B.1 Varsayımlara itirazlar (D4 ile güncellendi)

**İtiraz 1 — Asıl darboğaz agent değil, görev tanımıdır.**
Non-interactive çalıştırma interaktiften *daha az yeteneklidir*: yol ortasında insan yönlendirmesi yok. Bu, görev tanımı kalitesini sistemin performans tavanı yapar. Erken yatırım "daha çok agent" değil, **iyi yapılandırılmış görev şablonu** olmalıdır.

**Ready gate (D4 ile düzeltilmiş hâli).** Bir Task `Draft`'tan çıkamaz, eğer:
- hedef repository yoksa,
- izinli/yasak dosya kapsamı (glob) yoksa,
- repository'ye bağlı bir verification profile yoksa,
- en az bir kabul kriteri yoksa,
- **herhangi bir kabul kriteri `VerificationKind` beyan etmiyorsa** (`Machine` veya `Human`),
- `Machine` beyanlı bir kriterin `VerificationRef`'i, seçili verification profile'daki bir adım adına çözülmüyorsa.

> v1'deki *"Doğrulanamayan kabul kriteri = tanımsız görev"* ifadesi **geri çekilmiştir.** Doğru ifade: *"Nasıl doğrulanacağı beyan edilmemiş kabul kriteri = tanımsız görev."* `Human` beyanı tamamen geçerli bir cevaptır.

Uyarı (hata değil): Hiç `Machine` kriteri olmayan bir görev, repository verification profile'ının ötesinde otomatik sinyal üretmez. Ready gate bunu engellemez, **uyarı olarak gösterir**.

**İtiraz 2 — "Kurumsal hafıza platformda" doğru, ama hafıza inşa etmeyin.**
v1'de kurumsal bağlam = organizasyon başına **sürüm kontrollü markdown** (`corex-context-<org>`). Hub derler, üretmez.

**İtiraz 3 — Bütçe kontrolü sağlayıcı flag'ine dayandırılamaz.**
Mevcut abonelik oturumu kullanıldığı için `--max-budget-usd`'nin etkili olacağı garanti değildir. Bütçe kontrolü Hub tarafında: `MaxConcurrentExecutions = 1`, günlük çalıştırma sayacı, üç seviyeli timeout.

**İtiraz 4 — Windows'ta "read-only enforcement" kolay değil.**
Araç allowlist'i in-process gerçek bir kısıttır; ancak OS seviyesinde agent kullanıcının yazabildiği her yere yazabilir. v1 cevabı: **atılabilir worktree + yazma tespiti** ("engelleme" yerine "önemsizleştirme"). Gerçek izolasyon Faz 10'da ve **Kronos için zorunludur** (L.4).

---

## C. Önerilen MVP Kapsamı

### C.1 KAPSAM İÇİ

1. Organizasyon / Proje / Repository kataloğu (yapılandırmadan seed).
2. Task oluşturma + Ready gate (D4 kriter modeliyle).
3. Execution yaşam döngüsü + tek yazıcı eşzamanlılık kontrolü.
4. **Workspace Manager**: base commit sabitleme, worktree + `agent/...` branch, worktree-yerel sertleştirme, koşullu Hub commit, temizlik, yetim/kilit kurtarma.
5. **Process Runner**: Job Object ile ağaç öldürme, üç seviyeli timeout, async stdout/stderr, UTF-8, ANSI temizleme, **kabuksuz** argv çalıştırma, env allowlist.
6. **Context Pack Builder**: deterministik dosya montajı + manifest + SHA-256.
7. **Agent Runtime**: `ScriptedAgentRuntime` (Faz 5) + `ClaudeCodeRuntime` (Faz 6).
8. **Verification Engine**: repo başına sıralı komut profili, exit code + çıktı yakalama, TRX/JUnit sayaç ayrıştırma, baseline önbelleği.
9. **Evidence Store**: içerik adresli blob + redaksiyon; commit'li ve **commit'siz** kanıt yolları.
10. **ScopeGuard**: değişen dosya yollarının izinli glob'lara uygunluğu (commit öncesi zorunlu kapı).
11. **SecretGuard (minimal)**: yalnızca eklenen diff satırlarında bilinen sır desenleri (commit öncesi zorunlu kapı).
12. **Review Step**: read-only profil, `--json-schema` ile şema doğrulamalı bulgular (Faz 7).
13. Human acceptance: `accept` / `request-changes` / `reject` + `Human` kriter verdictleri + audit.
14. Hub CLI (ilk arayüz).
15. Yapısal loglama + korelasyon.

### C.2 KAPSAM DIŞI (v1 MVP)

Policy DSL · workflow engine · otomatik onarım döngüsü · otomatik merge/push/deploy · React UI (Faz 9) · REST API (Faz 9) · maliyet raporlaması (Faz 10) · çoklu kullanıcı · kimlik doğrulama/yetkilendirme · vector DB / RAG · Codex (Faz 8) · çoklu repo görevleri · uzak worker · gözetimsiz/CI çalıştırma · container/WSL sandbox (Faz 10) · **Kronos onboarding (L.4 kapısı)** · MCP sunucu yönetimi · submodule / Git LFS · bildirimler · şifreli secret store · gelişmiş sır tarama.

---

## D. Domain Modeli

Prensip: **15 tablo, 3 aggregate.** Aggregate = tutarlılık sınırı.

### D.1 Aggregate: `Repository` (katalog — yapılandırma)

| # | Varlık | Sorumluluk | Yaşam döngüsü |
|---|---|---|---|
| 1 | `Organization` | İzolasyon kökü: slug, filesystem root, env allowlist, **`IsolationLevel: Logical\|Hard`**, **`RequiresHardIsolation: bool`**, `Status: Active\|Blocked` | Nadiren |
| 2 | `Project` | Ürün gruplaması | Nadiren |
| 3 | `Repository` | Yerel yol, default branch, protected branch listesi, verification profile ref, kapsam kuralları | Nadiren |
| 4 | `VerificationProfile` | Sıralı komut adımları (`StepsJson`) | Nadiren |
| 5 | `AgentProfile` | rol + provider + runtime + izin profili + model + limitler | Nadiren |

**D5 zorlaması:** Yapılandırma yüklenirken `RequiresHardIsolation = true` **ve** `IsolationLevel = Logical` olan bir organizasyon `Status = Blocked` olarak yüklenir ve **hiçbir execution başlatamaz**. Kronos bu bayrakla kaydedilir. Kapı bir belge vaadi değil, kod değişmezidir (bkz. L.4).

### D.2 Aggregate: `Task`

| # | Varlık / alan | Not |
|---|---|---|
| 6 | `Task` | `TaskKey` (`N360-014`), org/project/repository, `Objective`, `AllowedScope[]`, `ForbiddenScope[]`, `State`, `BlockReason?`, `Version` (optimistic concurrency) |
| 7 | `AcceptanceCriterion` | **(D4 — yeni)** `TaskId`, `Order`, `Text`, `VerificationKind: Machine\|Human`, `VerificationRef?` (Machine ise zorunlu: verification adım adı), `HumanCheckHint?` |

`Task` execution'ları içermez; ID ile referans verir.

### D.3 Aggregate: `Execution`

Bir görevin tek denemesi. Değişmez kimlik: `TaskId + AttemptNo`.

| # | Varlık / alan | Not |
|---|---|---|
| 8 | `Execution` | `BaseCommitSha`, `BranchName`, `WorktreePath`, `WorkspaceState`, `ContextPackHash`, `State`, `Outcome`, **`ResultCommitSha?` (D6: commit yoksa `null`)**, `PreviousExecutionId?`, `Lease` |
| 9 | `ExecutionStep` | Sıralı adım; tip: `PrepareWorkspace \| BuildContext \| Agent \| CaptureChanges \| Verify \| Review` |
| 10 | `CommandResult` | Verify adımının çocuğu: executable, argv, exitCode, süre, çıktı blob ref'leri |
| 11 | `Finding` | Review adımının çocuğu: severity, kategori, dosya, satır, başlık, gerekçe, confidence, outOfScope |
| 12 | `EvidenceArtifact` | İçerik adresli blob referansı: sha256, boyut, tür, redaksiyon sayısı |
| 13 | `AcceptanceDecision` | **Değişmez** karar kaydı: `Decision`, `Actor`, `Reason?`, `OverrideUsed`, **`HumanCriterionVerdictsJson` (D4)**, `DecidedAtUtc` |
| 14 | `AuditEvent` | Append-only: kim, ne, ne zaman, korelasyon |
| 15 | `OrchestrationEvent` | UI/CLI zaman çizelgesi için küçük satırlar (blob yok) |

### D.4 Value object'ler (tablo değil)

`GitRef` · `CommitSha` · `ScopeGlob` · `Severity` · `PermissionProfile` · `Duration` · `ContentHash` · `VerificationKind`.

### D.5 Bilinçli olarak modellenmeyenler

- `ContextPack` bir aggregate değil → içerik adresli **artifact**. Sürümleme = hash.
- `Policy` bir varlık değil → kod + repo yapılandırması.
- `Approval` bir aggregate değil → `AcceptanceDecision` kaydı.
- `AgentRun` ayrı varlık değil → `ExecutionStep(type=Agent)`.
- `Human` kriter verdictleri ayrı tablo değil → `AcceptanceDecision` üzerinde JSON (birlikte okunur, birlikte değişmez).

### D.6 Bounded context'ler (modüler monolit içinde)

`Catalog` → `Orchestration` → `Workspace` · `Runtime` · `Verification` · `Evidence` · `Audit`.
Bağımlılık yönü tek yönlü: `Orchestration` diğerlerini bilir, tersi değil.

---

## E. Bileşen Mimarisi

```
┌──────────────────────────────────────────────────────────────┐
│ Sunum                                                        │
│   CoreX.Hub.Cli   (ilk ve tek arayüz — Faz 1..8)             │
│   [Faz 9] CoreX.Hub.Api (Minimal API + SSE) + React SPA      │
└──────────────────────────┬───────────────────────────────────┘
                           │ komut / sorgu
┌──────────────────────────▼───────────────────────────────────┐
│ Application — CoreX.Hub.Application                          │
│   ExecutionOrchestrator (tek durum sahibi)                   │
│   TaskService · AcceptanceService · ExecutionQueue           │
│   ScopeGuard · SecretGuard · BudgetGuard                     │
└───┬───────────┬────────────┬───────────┬────────────┬────────┘
    │           │            │           │            │
┌───▼─────┐ ┌───▼──────┐ ┌───▼──────┐ ┌──▼───────┐ ┌──▼──────────┐
│Workspace│ │ Context  │ │  Agent   │ │ Verify   │ │ Evidence    │
│ Manager │ │ Builder  │ │ Runtimes │ │ Engine   │ │ Store       │
│ (git)   │ │          │ │          │ │          │ │(blob+redact)│
└───┬─────┘ └──────────┘ └────┬─────┘ └────┬─────┘ └──────┬──────┘
    │                         │            │              │
    └─────────┬───────────────┴────────────┘              │
              │                          desen kümesi ────┘
   ┌──────────▼───────────────────────────┐   (SecretGuard ile ortak)
   │ ProcessRunner  (Job Object, timeout, │
   │ stream tüketimi, env allowlist)      │  ← TEK süreç çıkış noktası
   └──────────┬───────────────────────────┘
              │
   ┌──────────▼────────────────────────────┐
   │ FileSystemGuard (kanonik yol, org kökü,│
   │ reparse-point/symlink kaçış denetimi)  │  ← TEK dosya sistemi çıkış noktası
   └────────────────────────────────────────┘

   Persistence: EF Core + SQLite (WAL)  |  Blob: içerik adresli dosya deposu
```

### E.1 Mimari değişmezler (CI'da test edilir)

1. **Süreç başlatan tek yer `ProcessRunner`'dır.** Kod tabanında başka `Process.Start` yok — mimari testle zorlanır.
2. **Dosya sistemi yolu çözen tek yer `FileSystemGuard`'dır.**
3. **Kabuk (shell) yoktur.** Her çalıştırma `executable + string[] argv`.
4. **Alt katmanlar Application'ı bilmez;** birbirlerini de bilmez.
5. **Agent runtime git bilmez.** Ona bir dizin ve bir prompt verilir.
6. **(D1) `Task.State = Accepted` yazan tek yer `AcceptanceService`'tir.** `ExecutionOrchestrator` içinde bu geçişe giden hiçbir kod yolu bulunmaz — mimari testle zorlanır.
7. **(D6) `git commit` çağıran tek yer `WorkspaceManager.CreateResultCommit`'tir** ve bu metot yalnızca ScopeGuard + SecretGuard geçtikten sonra çağrılabilir.
8. **(D2) Hub, kendi koruma mekanizmasını atlatan hiçbir git bayrağı kullanmaz** (`--no-verify`, `-c core.hooksPath=`, `--force` yasak) — mimari testle zorlanır.

---

## F. Agent Runtime Mimarisi

### F.1 Ortak soyutlama — kasıtlı olarak ince

```
IAgentRuntime
  Capabilities Describe()
  Task<AgentRunResult> RunAsync(AgentRunRequest req, IProgress<AgentEvent> sink, CancellationToken ct)
  Task<HealthResult> CheckHealthAsync(CancellationToken ct)
```

`AgentRunRequest`: `WorkingDirectory` · `SystemPromptFile` · `AppendSystemPromptFile` · `UserPrompt` · `PermissionProfile` · `ModelHint` · `EffortHint` · `StructuredOutputSchema?` · `StructuredOutputPath` · `EnvironmentAllowlist` · `SoftTimeout` · `IdleTimeout` · `HardTimeout` · `CorrelationId` · `SessionId?` · `ResumeSessionId?`

`AgentRunResult`: `Outcome (Completed/Failed/TimedOut/Cancelled/Crashed)` · `ExitCode` · `ProviderSessionId` · `StructuredOutput?` · `RawStdoutBlobRef` · `RawStderrBlobRef` · `UsageMetadata (serbest sözlük)` · `Duration`

`Capabilities`: `SupportsStructuredSchema` · `SupportsSessionResume` · `SupportsToolAllowlist` · `SupportsCostCap` · `SupportsStreaming` · `SupportsReadOnlyMode`

> Altın kural: **farkları saklama, ilan et.** Orchestrator eksik yeteneği telafi etmez; adımı `Unsupported` işaretler.

### F.2 `ScriptedAgentRuntime` — birinci implementasyon (Faz 5)

AI değil. Fixture patch uygular, sabit yapısal çıktı yazar, hata modlarını simüle eder: `apply-patch`, `no-op`, `out-of-scope-write`, `secret-write`, `hang`, `crash`, `partial-write`, `unexpected-commit`.

Neden ilk sırada: tüm pipeline'ı, durum makinesini, kanıt akışını, kapıları ve kurtarmayı **ağsız, AI'sız, CI'da deterministik** test edilebilir kılar; ayrıca `IAgentRuntime` üzerinde ikinci implementasyon baskısını erkenden yaratır.

### F.3 `ClaudeCodeRuntime` (Faz 6)

Yaklaşım: **CLI subprocess.** Gerekçe: .NET için resmî Claude Agent SDK yok (SDK Python/TypeScript); CLI zaten yapısal çıktı, şema doğrulama, izin modu, araç allowlist ve oturum kimliği sunuyor; süreç sınırı bize kalıyor.

**Kimlik doğrulama (D3):** Bu makinede **zaten açık olan Claude Code abonelik oturumu** kullanılır. Ayrı API-key faturalandırma mimarisi **tasarlanmaz**. `claude setup-token` yalnızca ileride gözetimsiz/CI/uzak worker çalıştırma gerekirse değerlendirilecek bir kaçış yoludur — Faz 6 için gerekli değildir. `--bare` kullanılmaz (API key dayatır).

Uygulayıcı (implementer) profili:

```
claude -p
  --output-format stream-json
  --session-id <execution-uuid>
  --no-session-persistence
  --safe-mode
  --setting-sources user
  --strict-mcp-config
  --system-prompt-file  <pack>/system.md
  --append-system-prompt-file <pack>/rules.md
  --tools Read,Edit,Write,Glob,Grep,Bash
  --disallowedTools "Bash(git commit*)" "Bash(git push*)" "Bash(git remote*)" "Bash(git config*)"
  --permission-mode acceptEdits
  --model <profil>  --effort <profil>
  --add-dir <context-pack-dir>
  --debug-file <evidence>/claude-debug.log
  # görev metni stdin'den
```

> `--disallowedTools` ile `git commit` engellemesi **runtime politikasıdır, güvenlik sınırı değildir** (D2). Agent yine de commit atabilirse bu bir hata değil, **tespit edilecek bir anomalidir** — bkz. G.4.

İnceleyici (reviewer) profili (Faz 7):

```
claude -p
  --output-format json
  --json-schema <findings-schema.json>
  --safe-mode --setting-sources user --strict-mcp-config
  --tools Read,Glob,Grep          # Edit/Write/Bash YOK
  --permission-mode plan
  --system-prompt-file <pack>/reviewer.md
```

**Faz 6 öncesi doğrulanacaklar** (uygulamayı bloklamaz, faz kapısıdır — bkz. V.2):
- `--safe-mode` + `--setting-sources user` altında mevcut abonelik oturumunun headless çalıştığı,
- `--json-schema` şema ihlali davranışı (exit code / hata mesajı),
- `stream-json` olay şemasının kararlılığı → **ham stdout her zaman blob olarak saklanır**, ayrıştırma başarısız olsa bile kanıt kaybolmaz.

### F.4 `CodexRuntime` (Faz 8)

**Durum: doğrulanmamış.** Codex CLI bu makinede kurulu değil. Bu nedenle **Faz 8**'e alınmıştır; MVP bağımlılığı değildir.

Doğrulanacaklar: non-interactive çalıştırma yüzeyi, yapısal çıktı biçimi, sandbox modları, onay politikası bayrakları, oturum devamı, kullanım metadata'sı, **Windows'ta native sandbox desteği**. Sandbox Windows'ta yoksa çalıştırma **WSL2 (mevcut)** üzerinden yapılır; bu Hub'a yol çevirimi (`D:\cx\w\x` ↔ `/mnt/d/cx/w/x`) sorumluluğu ekler.

Kimlik doğrulama: mevcut ChatGPT/Codex oturumu; ayrı API faturalandırma mimarisi kurulmaz.

**Ara çözüm (Faz 7):** İnceleyici ilk aşamada **farklı profilli ikinci bir Claude Code çalıştırması** olur — farklı model, sıfır paylaşılan oturum, salt-okunur araç seti, düşmanca system prompt. Sağlayıcı çeşitliliğinin faydası gerçektir ama Faz 7 bloklayıcısı değildir.

### F.5 Süreç yaşam döngüsü — Windows'a özgü kritik detaylar

1. **Süreç ağacı öldürme.** `Process.Kill(entireProcessTree: true)` torunlar için güvenilir değil. Çözüm: **Windows Job Object** — `CreateJobObject` + `JOBOBJECT_EXTENDED_LIMIT_INFORMATION` içinde `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE` + `AssignProcessToJobObject`. Ek kotalar: `ActiveProcessLimit`, `JobMemoryLimit`.
   *Yarış koşulu:* `Process.Start` ile job ataması arasında doğan çocuklar kaçabilir. Faz 2'de kabul edilir; **Faz 10**'da `CreateProcess` P/Invoke + `CREATE_SUSPENDED` → assign → `ResumeThread`.
2. **Stdout/stderr kilitlenmesi.** Her iki akış asenkron ve eşzamanlı tüketilir; `WaitForExit()` akışlar boşaltılmadan çağrılmaz.
3. **Kodlama.** UTF-8 zorlanır; ANSI escape dizileri kanıta yazılmadan temizlenir (ham hâli ayrı blob).
4. **`.cmd` / `.bat` tuzağı.** .NET, `UseShellExecute=false` ile batch shim'leri doğrudan çalıştıramaz. Tercih: gerçek çalıştırılabiliri çöz (`node <...>/npm-cli.js`). Zorunlu hâlde `cmd.exe /c` + argümanlar için katı beyaz liste regex'i.
5. **Üç seviyeli timeout:** *soft* (stdin kapat, nazikçe bekle) → *idle* (N dakikadır çıktı yok — asılı testin tek göstergesi) → *hard* (job kapat, ağacı öldür).
6. **Ortam değişkenleri: allowlist.** Boş ortamdan başla; yalnızca listelenenleri ekle. `GIT_TERMINAL_PROMPT=0`, `GIT_ASKPASS=<no-op>`, `NO_COLOR=1`, `CI=1` zorlanır.
7. **İptal:** stdin kapat → grace → job kapat. Kısmi değişiklikler worktree'de kalır ve **kanıt olarak yakalanır** (silinmez, commit edilmez — D6).

---

## G. Git / Worktree Stratejisi

### G.1 Yerleşim — repo dışında

```
D:\cx\                                  (Hub workspace kökü — kısa yol, bilinçli)
  corexlogic\                           (org kökü)
    nakliye360\
      N360-014-1\                       (taskKey-attempt)
  _fixtures\                            (Loop Zero fixture kökü — Faz 5)
```

Ek önlemler: Windows uzun yol desteği açık; yönetilen repolarda `git config core.longpaths true`; Defender'da `D:\cx` istisnası.

### G.2 Hazırlık akışı (D2 ile düzeltilmiş)

```
1. Repo geçerli mi?              git rev-parse --git-dir
2. Submodule / LFS var mı?       varsa MVP'de REDDET (açık hata)
3. Base commit sabitle:          git rev-parse <defaultBranch>   → BaseCommitSha
4. Branch adı üret:              agent/<taskKey>/<attempt>
5. Worktree oluştur:             git worktree add -b <branch> <path> <BaseCommitSha>
6. Worktree-yerel sertleştirme:
     git -C <wt> config core.hooksPath <hub-hooks-dir>
        └── hub-hooks-dir İÇERİĞİ:  pre-push   (daima exit 1)
            pre-commit YOK  ← D2
     git -C <wt> config remote.origin.pushurl "corex-hub://push-disabled"
     git -C <wt> config credential.helper ""
7. Kayıt: BaseCommitSha, branch, path, oluşturma zamanı
```

**Kritik nokta:** worktree bir **commit'ten** oluşturulur, çalışma ağacından değil. İki bedava kazanç:
- Geliştiricinin ana dizini kirliyse sorun değil.
- **`.gitignore`'daki `.env`, `appsettings.Development.json`, sertifikalar worktree'de hiç bulunmaz.** İzolasyon aynı anda bir sır sızıntısı önlemidir. (Kalan risk: repoya *commit edilmiş* sırlar — bkz. K.)

**Yan kazanç (D2 ile açıkça kayıt altına alındı):** `core.hooksPath` Hub dizinine yönlendirildiği için **repoya commit edilmiş git hook'ları da devre dışı kalır.** Bu, keyfî kod çalıştırma ve prompt-injection yüzeyini kapatan gerçek bir güvenlik kazancıdır.

### G.3 Korumalı branch'ler ve yayınlama engeli (D2)

Katman katman, güçlüden zayıfa:

| Katman | Mekanizma | Ne engeller |
|---|---|---|
| Yapısal | Git aynı branch'i iki worktree'de checkout etmez; her zaman yeni `agent/...` branch'i | Ana branch'in worktree'den bozulması |
| Doğrulama | `Repository.ProtectedBranches` + orchestrator kontrolü | Yanlış branch hedefi |
| **Hook** | Worktree-yerel `pre-push` **daima exit 1** | **Uzağa yayınlama** |
| Yapılandırma | `remote.origin.pushurl` yönlendirilemez değere ayarlı, credential helper boş | Uzağa yayınlama (ikinci kat) |
| Kod | Hub'da **push/merge/deploy kod yolu hiç yazılmaz** | Hub'ın kendisinden yayınlama |
| Runtime politikası | `--disallowedTools "Bash(git push*)" "Bash(git commit*)" …` | Agent'ın git kullanması (yumuşak) |

> **Açık ayrım (D2):** *Agent'ın commit atmasını engellemek* öncelikle bir **orkestrasyon/runtime politikasıdır** — yumuşaktır, atlatılabilir, ama **tespit edilebilir**. *Kazara uzağa yayınlamayı engellemek* ise **daha güçlü bir güvenlik gereksinimidir** ve üç bağımsız katmanla sağlanır. `pre-commit` hook'u kaldırılmıştır çünkü Hub'ın kendi sonucunu commit etmesi gerekir ve **Hub kendi korumasını atlatmak zorunda kalmamalıdır** (E.1 değişmez #8).

### G.4 Sonuç commit'i — koşullu (D6)

Agent adımı bittikten sonraki sıra:

```
1. Anomali tespiti:   git -C <wt> rev-parse HEAD  ==  BaseCommitSha ?
      HAYIR → EvidenceFinding: UnexpectedAgentCommit (bloklamaz, kayda geçer)
2. Değişiklik envanteri:
      git -C <wt> status --porcelain=v2 --untracked-files=all
      git -C <wt> add -N .           ← untracked dosyaların diff'te görünmesi için
      git -C <wt> diff <BaseCommitSha>          → çalışma ağacı diff'i (blob)
      dosya envanteri: yol, durum, boyut, sha256 (boyut sınırı üstü: yalnızca boyut)
3. ZORUNLU KAPILAR (sırayla):
      a) Agent adımı Outcome == Succeeded  ?      değilse → COMMIT YOK
      b) ScopeGuard:  değişen yollar ⊆ AllowedScope ?  değilse → COMMIT YOK
      c) SecretGuard: diff'in EKLENEN satırlarında bilinen sır deseni ? varsa → COMMIT YOK
4. Üç kapı da geçtiyse:
      git -C <wt> add -A
      git -C <wt> -c user.name="CoreX Agent Hub" \
                  -c user.email="agent@corex.local" \
                  commit -m "agent: <taskKey> attempt <n>"
      → ResultCommitSha kaydedilir
      → nihai diff:  git -C <wt> diff <BaseCommitSha>..HEAD   (blob)
```

Kapı sonuçları:

| Kapı başarısız | Execution Outcome | Commit | Worktree |
|---|---|---|---|
| (a) Agent adımı `Succeeded` değil | `Failed` / `TimedOut` / `Cancelled` / `Interrupted` | Yok | **Korunur** |
| (b) ScopeGuard | `ScopeViolation` | Yok | **Korunur** |
| (c) SecretGuard | `SecretGuardBlocked` | Yok | **Korunur** |

**Neden commit yok (D6):** commit kalıcı bir git nesnesi üretir; başarısız, kapsam dışı veya sır içeren bir sonucu kalıcılaştırmak geri alınması zor bir yan etkidir. `agent/...` branch'i gerçek repoda yaşamaya devam ettiği için, oraya yazılan bir sır ileride manuel bir push ile dışarı sızabilir. **Kanıt korunması commit gerektirmez.**

**SecretGuard kapsamı — kasıtlı olarak küçük (D6):**
- Yalnızca **eklenen diff satırları** taranır. Repoda zaten var olan sırlar bloklamaz (aksi hâlde her görev bloklanır).
- Desen kümesi **redaksiyon ile ortaktır** (tek kaynak, ikinci bir alt sistem yok): `sk-…`, `ghp_…`, `AKIA…`, JWT şekli, `-----BEGIN … PRIVATE KEY-----`, `Password=`/`Pwd=` içeren bağlantı dizeleri, `Authorization:` başlıkları.
- Entropi analizi, ML, baseline dosyası, vault, rotasyon **yok**.
- Operatör kaçış yolu: `hub commit-result <execution> --override --reason "..."` — audit'e yazılır.
- **Bu bir sır yönetim alt sistemi değildir**; commit kapısında ~30 satırlık bir kontroldür.

*Alternatif değerlendirildi:* SecretGuard'ı Faz 10'a ertelemek. **Reddedildi** — desen kümesi zaten Faz 4'te redaksiyon için yazılıyor; ertelemenin kazancı sıfıra yakın, riski (kalıcı git nesnesinde yeni sır) gerçek.

### G.5 Eşzamanlılık

- Farklı repolar paralel çalışabilir; `git worktree add/remove` aynı repo için **serileştirilir** (repo başına named mutex).
- Yönetilen repolarda `git config gc.auto 0`; bakım hiçbir execution aktif değilken planlı yapılır.
- MVP varsayılanı: `MaxConcurrentExecutions = 1`.

### G.6 Temizlik, yetim ve çökme kurtarma (D6 ile düzeltilmiş)

> **Değişmez (D6):** `ResultCommitSha == null` olan bir execution'ın worktree'si **otomatik kaldırılamaz.** Değişikliklerin tek kalıcı kopyası oradadır; kanıt blob'ları diff'i taşır ama dosyaları taşımaz.

| Durum | Commit | Worktree davranışı |
|---|---|---|
| Outcome `Passed`, insan `accept` etti | Var | `remove` edilir, branch **korunur**, `WorkspaceState=Released` |
| Outcome `Passed`, insan `reject`/`request-changes` etti | Var | `remove` edilir, branch korunur |
| Outcome `VerificationFailed` / `ReviewBlocked` | Var | `remove` edilir, branch korunur (değişiklikler commit'te güvende) |
| Outcome `ScopeViolation` / `SecretGuardBlocked` | **Yok** | **Korunur** — `hub workspace release <execution> --confirm` ile açıkça bırakılır |
| Outcome `Failed` / `TimedOut` / `Cancelled` / `Interrupted` | **Yok** | **Korunur** — aynı |
| `worktree remove` başarısız (dosya kilidi) | — | `dotnet build-server shutdown` → job kapat → 3 artan gecikmeli deneme → `Orphaned`; **iş akışı bloklanmaz** |
| Hub çöktü / makine restart | — | Açılışta reconciler: non-terminal execution'lar `Interrupted`; disk ↔ DB karşılaştırması; kayıtsız worktree'ler `Orphaned` raporlanır, **otomatik silinmez** |
| Bayat kilit | — | `git worktree prune` + açık operatör komutu |

Komutlar: `hub workspace reconcile`, `hub workspace release <execution> --confirm`, `hub workspace purge-orphans --confirm`. **Yıkıcı işlem daima açık bayrak ister.**

---

## H. Context (Bağlam) Mimarisi

### H.1 Kaynaklar ve öncelik

```
1. Task'a özgü talimat (objective, scope, kabul kriterleri + VerificationKind)
2. Repository kuralları     (context repo: repos/<repo>/*.md)
3. Project kuralları        (context repo: projects/<project>/*.md)
4. Organization kuralları   (context repo: org.md, conventions.md, adr/)
5. Hub global sözleşmesi    (Hub'da gömülü: rol, yasaklar, çıktı formatı)  ← DEĞİŞTİRİLEMEZ
6. Canlı git gerçekleri     (base commit, branch)
```

5 numaralı katman en altta değil, **üst katmanların ezemeyeceği** katmandır: Hub sözleşmesi ayrı bir `--system-prompt-file` içinde, proje kuralları `--append-system-prompt-file` içinde verilir.

### H.2 Depolama

Organizasyon başına **git reposu** (`corex-context-corexlogic`). Markdown. Vector DB yok, embedding yok, chunking yok.

### H.3 Materyalizasyon

```
<evidence>/<executionId>/context/
  system.md          ← Hub sözleşmesi
  rules.md           ← org + project + repo kuralları (birleştirilmiş)
  task.md            ← objective, scope, kabul kriterleri (MACHINE/HUMAN etiketli)
  repo-state.json    ← baseCommit, branch, defaultBranch
  decisions/         ← ilgili ADR kopyaları
  manifest.json      ← dosya başına sha256 + kaynak yol + sürüm
```

`--add-dir` ile okunur kılınır; `system.md`/`rules.md` prompt dosyası olarak geçirilir; `task.md` stdin'e verilir. `ContextPackHash` execution'a yazılır.

### H.4 Repo içi `CLAUDE.md` / `AGENTS.md`

**Güvenilmeyen girdidir.** Varsayılan: `--safe-mode` ile devre dışı. Bir repo kuralı gerçekten faydalıysa **context repoya açıkça alınır** — güven, sessiz varsayım değil, kayıtlı karar olur.

### H.5 Boyut kontrolü

- Sert bütçe: varsayılan **64 KB toplam metin**. Aşılırsa build başarısız — **sessiz kırpma yasak**.
- `decisions/` yalnızca kapsam/etiket filtresinden geçen ADR'leri içerir.
- Geçmiş görev kanıtı pack'e girmez.

---

## I. Doğrulama ve Kanıt Modeli

### I.1 Doğruluk kademeleri (D4 ile genişletildi)

| Kademe | İçerik | Güven | **Otomatik** outcome'u etkiler mi? | Nihai insan kabulünü etkiler mi? |
|---|---|---|---|---|
| **A — Makine gerçeği** | exit code, commit SHA, dosya hash'i, diff, süre, test sayaçları, ScopeGuard, SecretGuard | Otoriter | **Evet** | Evet |
| **B — İnceleyici bulgusu** | şema doğrulamalı `Finding[]` | Yapısal görüş | Evet — yalnızca yapılandırılmış kapı kurallarıyla | Evet |
| **C — Agent beyanı** | uygulayıcının serbest metin özeti | **Sıfır** | **Hayır — asla** | **Hayır — asla** |
| **D — İnsan değerlendirmesi** | `Human` kabul kriterlerine verilen verdictler | İnsan otoritesi | **Hayır** | **Evet — yalnızca burada** |

**(D4) Değişmez:** `Execution.Outcome` yalnızca Kademe A ve yapılandırılmış Kademe B kapılarından hesaplanır. Kabul kriteri metni, `Human` verdictleri ve agent beyanı outcome hesabına **giremez**. Kademe D yalnızca `hub accept` anında değerlendirilir.

### I.2 Kabul kriterlerinin kanıta bağlanması (D4)

| Kriter türü | Nasıl değerlendirilir | Nerede görünür |
|---|---|---|
| `Machine` (`VerificationRef = "test"`) | İlgili verification adımının sonucu doğrudan kriterin sonucu olur | Kanıt özetinde otomatik ✔/✘ |
| `Machine`, adım `required: false` ve FAIL | Outcome `Passed` kalabilir; kriter **karşılanmadı** olarak gösterilir | Kabul ekranında uyarı |
| `Human` | `hub accept` sırasında insan verdict verir: `Met` / `NotMet` / `NotApplicable` | `AcceptanceDecision.HumanCriterionVerdictsJson` |

Eşleme tek bir string referansıdır (adım adı). **Kural motoru yoktur.**

### I.3 Verification profile

```yaml
verification:
  workingDirectory: "."
  steps:
    - name: restore
      executable: dotnet
      args: ["restore"]
      timeoutSeconds: 300
      required: true
    - name: build
      executable: dotnet
      args: ["build", "--no-restore", "-warnaserror"]
      timeoutSeconds: 600
      required: true
    - name: test
      executable: dotnet
      args: ["test", "--no-build", "--logger", "trx;LogFileName=results.trx"]
      timeoutSeconds: 1800
      required: true
      resultParser: trx
```

- `executable` **allowlist**'ten gelir, yapılandırma zamanında mutlak yola çözülür.
- `args` bir dizidir; string birleştirme yok; kabuk yok.
- `resultParser`: `none | trx | junit`.
- **(Tutarlılık kuralı)** Agent adımı `Succeeded` değilse Verify ve Review **çalıştırılmaz**. Kısmi çalışmanın manuel doğrulanması istenirse bu Faz 10'da opsiyonel `hub verify <execution>` komutuyla ele alınır.

### I.4 Baseline

`(RepositoryId, BaseCommitSha, ProfileHash)` anahtarıyla önbelleklenen baseline çalıştırma. Kanıt karşılaştırmalı sunulur:

```
test: base 840 passed / 2 failed  →  after 842 passed / 0 failed   (İYİLEŞME)
```

### I.5 Kanıt deposu (D6 ile: commit'siz yol)

Blob'lar içerik adresli: `blobs/<sha256[0:2]>/<sha256>`, atomik yazım (temp + rename).

`EvidenceArtifact.kind`: `ContextPack | AgentStdout | AgentStderr | AgentStructuredResult | GitStatus | GitWorktreeDiff | GitCommitDiff | FileInventory | CommandStdout | CommandStderr | TestReport | ReviewFindings | DebugLog`.

**Commit'siz execution'da bile toplanan asgari kanıt (D6):**

| Kanıt | Kaynak |
|---|---|
| `GitStatus` | `git status --porcelain=v2 --untracked-files=all` |
| `GitWorktreeDiff` | `git add -N .` + `git diff <BaseCommitSha>` |
| `FileInventory` | yol · durum · boyut · sha256 (boyut sınırı üstü: yalnızca boyut) |
| Hash'ler | `BaseCommitSha`, `ContextPackHash`, diff blob sha256, dosya sha256'ları |
| `AgentStdout` / `AgentStderr` / `DebugLog` | ProcessRunner |
| Kapı sonucu | `ScopeViolation` / `SecretGuardBlocked` detayları (hangi yol / hangi desen — **sırrın kendisi değil**) |

`.gitignore` kapsamındaki dosyalar `git add -N` tarafından atlanır → **yok sayılan sırlar kanıta girmez.** Bu bilinçli bir tercihtir.

**Redaksiyon yazma anında yapılır**; redakte sayısı kaydedilir. Kalan risk açıkça kabul edilir: regex tabanlı redaksiyon eksiksiz değildir.

SQLite'a blob konmaz — yalnızca meta + hash.

### I.6 Kanıt özeti (insanın gördüğü şey — D1/D4/D6 ile güncellendi)

```
Task N360-014 · Execution #1
Base       9f3c1a2  (main)          Branch  agent/N360-014/1
Result     4b77e01                  Context 3af9…  (64 dosya, 41 KB)
Worktree   D:\cx\corexlogic\nakliye360\N360-014-1   (Active)

Implementation   Claude Code 2.1.240 · opus · effort=high · 6m12s
Gates            ScopeGuard PASS · SecretGuard PASS · UnexpectedAgentCommit: yok
Scope            12 dosya değişti

Verification
  restore  PASS   0   18s
  build    PASS   0   1m04s
  test     PASS   0   2m51s     842 passed / 0 failed  (baseline: 840/2)

Kabul kriterleri
  [MACHINE] "Tüm birim testleri geçer"        → verify:test      ✔
  [MACHINE] "Build uyarısız derlenir"          → verify:build     ✔
  [HUMAN]   "Fiyatlandırma mantığı iş kuralına uygun"   → insan değerlendirmesi bekliyor

Review           read-only profile · 3 bulgu
  Critical 0 · Major 0 · Minor 2 · Info 1

Agent claim      (bilgi amaçlı, kanıt değil)
  "Refactored shipment pricing; added 2 tests."

Execution Outcome   Passed
Task State          AwaitingAcceptance        ← hub run BURADA durur (D1)
Sonraki adım        hub accept N360-014#1     (veya request-changes / reject)
```

Commit'siz bir örnek:

```
Execution Outcome   ScopeViolation
Result commit       YOK  (D6 — kapı geçilmedi)
Worktree            KORUNDU — inceleme için erişilebilir
İhlal               src/Infrastructure/Migrations/*.cs  (AllowedScope dışında)
Task State          Ready   (yeniden denenebilir)
```

---

## J. İnceleme (Review) Modeli — Faz 7

### J.1 Salt-okunurluk nasıl gerçekten sağlanır

Güçlüden zayıfa:

1. **Atılabilir worktree.** İnceleyici, uygulayıcınınkinin ayrı bir kopyasında çalışır (aynı commit, farklı worktree); inceleme sonrası koşulsuz silinir.
2. **Değişiklik tespiti.** Öncesi/sonrası ağaç hash'i karşılaştırılır; fark varsa `ReviewerTamperDetected` üretilir ve inceleme **geçersiz** sayılır.
3. **Araç kısıtı.** `--tools Read,Glob,Grep`.
4. **İzin modu.** `--permission-mode plan`.
5. **Prompt.** En zayıf katman; yalnızca üsttekilerin üzerine eklenir.

> Not: İnceleyici worktree'si `ResultCommitSha` varsa o commit'ten oluşturulur. `ResultCommitSha == null` ise (D6) **inceleme çalıştırılmaz** — zaten Outcome terminal bir kapı hatasıdır.

### J.2 Diff-only mü, tüm repo mu?

**Diff-anchored, on-demand okuma.** İnceleyiciye unified diff + salt-okunur worktree verilir. Bulgular diff'e çıpalanır; değişmemiş dosyalara ait bulgular `outOfScope: true` ile işaretlenir ve **bloklamaz**.

### J.3 Bulgu şeması

```json
{
  "findings": [{
    "severity": "Critical|Major|Minor|Info",
    "category": "correctness|security|architecture|performance|test-coverage|convention",
    "file": "src/...", "line": 42,
    "title": "≤80 karakter",
    "rationale": "neden yanlış",
    "suggestedFix": "opsiyonel",
    "confidence": "high|medium|low",
    "outOfScope": false
  }],
  "summary": "≤500 karakter"
}
```

`--json-schema` bunu runtime'da zorlar. Şema ihlali → bir kez yeniden dene → yine ihlal → adım `Failed`, sonuç `ReviewUnusable`. **Serbest metin ayrıştırma yok.**

### J.4 Kapılar (D4/D6 ile güncellendi)

| Koşul | Sonuç | İnceleme çalışır mı? |
|---|---|---|
| Agent adımı `Succeeded` değil | `Failed`/`TimedOut`/`Cancelled`/`Interrupted` | Hayır |
| ScopeGuard başarısız | `ScopeViolation` | Hayır |
| SecretGuard başarısız | `SecretGuardBlocked` | Hayır |
| `required` doğrulama adımı FAIL | `VerificationFailed` | Hayır (zaman/maliyet israfı) |
| `Critical` bulgu ≥ 1 | `ReviewBlocked` | — |
| `Major` bulgu ≥ eşik (varsayılan 1) | `ReviewBlocked` (yapılandırılabilir) | — |
| Yalnızca Minor/Info | `Passed` | — |

**(D4)** Kabul kriterleri bu tabloya **girmez**. `Machine` kriterleri zaten doğrulama adımlarının bir görünümüdür; `Human` kriterleri yalnızca kabul anında değerlendirilir.

İnsan her zaman **gerekçe kaydederek** kapıyı geçebilir: `hub accept --override --reason "..."`. Override sessiz olamaz; audit'e yazılır.

---

## K. Güvenlik Tehdit Modeli (MVP)

### K.1 Varlıklar

Kaynak kodu · commit edilmiş sırlar · geliştiricinin dosya sistemi ve SSH/git kimlik bilgileri · bulut/DB kimlik bilgileri · sağlayıcı auth durumu · Hub veritabanı ve kanıt deposu · iş istasyonu bütünlüğü.

### K.2 Güven sınırları

```
[İnsan operatör]              ── güvenilir
   │
[Hub süreci]                  ── güvenilir
   ├── SINIR 1 ────────────────────────────────────────
   │   [Agent süreci]         ── YARI GÜVENİLİR
   │       girdisi güvenilmez, çıktısı güvenilmez,
   │       yetkisi kullanıcının yetkisi kadar
   ├── SINIR 2 ────────────────────────────────────────
   │   [Repo içeriği]         ── GÜVENİLMEZ VERİ
   ├── SINIR 3 ────────────────────────────────────────
   │   [Doğrulama komutları]  ── repo build script'leri = keyfî kod
   └── SINIR 4 ────────────────────────────────────────
       [Ağ / paket kayıtları] ── güvenilmez (postinstall)
```

**Dürüst tespit:** Sınır 1 v1'de OS tarafından zorlanmaz. Agent, geliştirici kullanıcısı olarak çalışır.

### K.3 Senaryolar ve önlemler (D2/D6 ile güncellendi)

| # | Senaryo | Olasılık | Etki | Önlem | Kalan risk |
|---|---|---|---|---|---|
| 1 | Repo içeriğinden prompt injection | Yüksek | Yüksek | `--safe-mode`, `--setting-sources user`, `--strict-mcp-config`, **`core.hooksPath` ile repo hook'ları devre dışı** | Kod yorumları hâlâ okunuyor — kabul |
| 2 | Doğrulama komutu üzerinden komut enjeksiyonu | Orta | Kritik | Kabuk yok, argv dizisi, executable allowlist | `cmd.exe` shim yolunda argüman beyaz listesi şart |
| 3 | Kimlik bilgisi sızıntısı (env) | Yüksek | Kritik | **Env allowlist**, `GIT_ASKPASS` no-op, `GIT_TERMINAL_PROMPT=0` | Sağlayıcı auth dosyaları diskte okunabilir |
| 4 | `.env` / sertifika okuma | Orta | Yüksek | Worktree commit'ten oluşur → gitignore'lu sırlar yok | Commit edilmiş sırlar mevcut |
| 5 | Yıkıcı komut / kazara push | Orta | Kritik | G.3'teki altı katman | Agent yine kullanıcı ev dizinine yazabilir |
| 6 | Bağımlılık yükleme script'i (postinstall) | Orta | Kritik | `npm ci --ignore-scripts` varsayılan | İstisna tanımlanırsa risk geri gelir |
| 7 | Path traversal / symlink-junction kaçışı | Düşük | Yüksek | `FileSystemGuard` | — |
| 8 | Organizasyonlar arası sızıntı | Orta | Yüksek | Org kökleri, DB scope, org başına env allowlist, **Kronos kapısı (L.4)** | Core X Logic içi: mantıksal |
| 9 | Kanıtta sır görünmesi | Yüksek | Orta | Yazma anında redaksiyon; `.gitignore` kapsamı kanıta girmez | Regex eksiksiz değil |
| 10 | **Yeni sırrın kalıcı git nesnesine yazılması** | Orta | Yüksek | **SecretGuard commit kapısı (D6)** | Desen kümesi sınırlı; override mümkün (audit'li) |
| 11 | Kötü niyetli üretilmiş kod (backdoor) | Düşük–Orta | Kritik | Otomatik merge/push yok, insan kabulü, bağımsız inceleme | İnsan gözden kaçırabilir |
| 12 | İnceleyicinin kodu değiştirmesi | Düşük | Orta | Atılabilir worktree + hash tespiti + araç kısıtı | — |
| 13 | Agent'ın Hub kanıtını değiştirmesi | Düşük | Yüksek | Kanıt deposu worktree dışında, `--add-dir` ile verilmez | Aynı kullanıcı → OS düzeyinde erişilebilir |
| 14 | **Agent'ın kendi commit'ini atması** | Orta | Düşük | Runtime politikası (`--disallowedTools`) + **tespit** (`UnexpectedAgentCommit`) | Engellenmiyor, kayda geçiyor — bilinçli tercih (D2) |
| 15 | Sonsuz döngü / kaynak tüketimi | Orta | Orta | Job Object kotaları, üç seviyeli timeout, eşzamanlılık=1, günlük bütçe | — |
| 16 | Sağlayıcı oturumunun kötüye kullanımı | Düşük | Kritik | Hub token saklamaz; mevcut oturum kullanılır | Agent aynı kullanıcı olarak oturuma erişebilir |

### K.4 v1 için açıkça kabul edilen artık riskler

1. Agent, geliştirici kullanıcısının tüm dosya sistemine erişebilir.
2. **Core X Logic içinde** organizasyon izolasyonu mantıksaldır; uzlaşmış bir agent'a karşı dayanıklı değildir. **Kronos bu koşulda onboard edilemez (L.4).**
3. Redaksiyon ve SecretGuard en iyi çabadır.
4. Repoya commit edilmiş mevcut sırlar agent'a görünür.
5. Agent'ın kendi commit'ini atması engellenmez, tespit edilir.

**Yükseltme tetikleyicileri** (Faz 10 sert izolasyon işini öne alır): Kronos onboarding talebi · güvenilmeyen/üçüncü taraf repo · ikinci bir insan kullanıcı · üretim kimlik bilgilerinin repo yakınında bulunması.

---

## L. Organizasyon İzolasyon Modeli

### L.1 Katman katman ne sağlanabilir

| Katman | v1 mekanizması | Gerçek zorlama? |
|---|---|---|
| Veritabanı | Her tabloda `OrganizationId`; EF Core global query filter + `OrgScope` ambient bağlamı | ✔ (uygulama içi) |
| Dosya sistemi | Org başına kök; `FileSystemGuard` kanonikleştirme + prefix + reparse-point | ✔ (uygulama içi) |
| Repository | `Repository.OrganizationId`; çapraz org çözümü hata | ✔ (uygulama içi) |
| Bağlam | Context repo org başına; pack builder org sınırını geçemez | ✔ (uygulama içi) |
| Ortam değişkenleri | Allowlist org başına | ✔ (uygulama içi) |
| Kanıt / log | Blob dizini ve log dosyaları org'a göre ayrı | ✔ (uygulama içi) |
| **Agent süreci** | Aynı Windows kullanıcısı | **✘ — zorlama yok** |
| **Sağlayıcı oturumu** | Ortak Claude Code auth durumu | **✘ — zorlama yok** |

### L.2 Dürüst özet

> Mevcut hâliyle organizasyon izolasyonu **kaza önleyicidir, saldırı önleyici değildir.** Yanlış repoya yazmayı, yanlış bağlamı derlemeyi, yanlış env'i sızdırmayı engeller. Uzlaşmış bir agent'ı engellemez.

### L.3 Sert izolasyon seçenekleri (Faz 10)

| Seçenek | Maliyet | İzolasyon | Not |
|---|---|---|---|
| **WSL2 dağıtımı (org başına)** | Düşük–Orta | İyi | **Tercih edilen ilk aday.** WSL zaten kurulu; yol çevirimi gerekir; Codex'in muhtemel Linux-sandbox ihtiyacıyla aynı yatırımı paylaşır |
| Org başına ayrı Windows yerel kullanıcısı + NTFS ACL | Orta | İyi | Sağlayıcı auth kullanıcı başına kurulmalı; `CreateProcessAsUser` gerekir |
| Org başına container (Docker Desktop) | Orta–Yüksek | Çok iyi | Ağ kontrolü kolay; Windows I/O performans bedeli |
| Ayrı fiziksel/sanal makine | Yüksek | En iyi | Gerçek gereksinim doğarsa |

### L.4 KRONOS ONBOARDING GÜVENLİK KAPISI (D5)

**Durum: Core X Logic pilotu için mantıksal izolasyon KABUL EDİLDİ. Kronos BLOKELİ.**

**Kural.** Core X Logic ve Kronos agent süreçleri **aynı OS güvenlik principal'ı** altında çalıştığı sürece Kronos'a ait hiçbir repository, bağlam, kimlik bilgisi veya şirket verisi platforma kaydedilemez.

**Kapıyı açmak için gerekenler (hepsi):**

| # | Koşul | Doğrulama kanıtı |
|---|---|---|
| G-K1 | Onaylı sert izolasyon yaklaşımlarından biri uygulanmış (birinci aday: **WSL2, org başına dağıtım**) | Çalışan `IAgentRuntime` üzerinden org başına ayrı OS principal'ı ile başarılı execution |
| G-K2 | Çapraz erişim negatif testi geçmiş | Kronos principal'ından Core X Logic org köküne okuma denemesi **başarısız** olmalı (ve tersi) |
| G-K3 | Sağlayıcı oturumu ayrımı çözülmüş | Kronos çalıştırmasının Core X Logic oturum/auth durumuna erişemediği gösterilmiş |
| G-K4 | Kanıt ve log ayrımı OS düzeyinde doğrulanmış | Kronos execution'ı Core X Logic blob deposunu okuyamaz |
| G-K5 | `Organization.IsolationLevel = Hard` olarak işaretlenmiş ve yapılandırma yükleyicisi bunu doğrulamış | `hub doctor` çıktısı |

**Kod ile zorlanır (belge vaadi değil):** Kronos organizasyonu `RequiresHardIsolation = true` bayrağıyla kaydedilir. Yapılandırma yükleyici `RequiresHardIsolation && IsolationLevel == Logical` gördüğünde organizasyonu `Status = Blocked` yapar; `ExecutionOrchestrator` bloke bir organizasyon için execution başlatmayı reddeder. Bu davranış Faz 1'de yazılan bir birim testiyle kilitlenir.

**Bu kapı neyi bloklamaz:** Faz 1–10 geliştirmesini, Core X Logic kullanımını, Loop Zero'yu, Claude/Codex entegrasyonunu, UI'ı. **Yalnızca Kronos onboarding'ini bloklar.**

---

## M. Kalıcılık Önerisi

### M.1 SQLite — uygun

Tek kullanıcı, tek süreç, yerel makine. PostgreSQL için gerekçe yok.

- **WAL** modu · `busy_timeout = 5000` · **tek yazıcı** (global yazma semaforu) · UTC ISO-8601 · EF Core 10 + açık migration'lar.
- Yedekleme: `VACUUM INTO` + blob deposu kopyası.

### M.2 Veri türüne göre ayrıştırma

| Veri | Nerede | Gerekçe |
|---|---|---|
| Katalog, Task, Kriter, Execution, Step, Finding, Decision, Audit, OrchestrationEvent | SQLite tabloları | Sorgulanır, küçük, ilişkisel |
| Agent stdout/stderr, komut çıktıları, debug log | **Blob** (DB'de yalnızca hash+boyut) | MB'lar |
| Unified diff (worktree veya commit) | Blob | Aynı |
| Context Pack | Blob + manifest | Değişmez, hash'lenebilir |
| Dosya envanteri (D6) | Blob (JSON) + özet sayaçlar tabloda | Büyüyebilir |
| Test raporu | Ham → blob; **ayrıştırılmış sayaçlar → tablo** | Sorgulanabilir metrik |
| Review bulguları | **Tablo** + ham JSON blob | Filtreleme gerekir |
| Agent beyanı | Blob + küçük özet alanı | Kanıt değil |

### M.3 Saklama

Blob'lar içerik adresli → silme referans sayımı ister → **MVP'de silme yok**. `hub evidence gc --older-than 90d` Faz 10'da.

---

## N. Teknoloji Önerisi

| Katman | Öneri | Gerekçe |
|---|---|---|
| Runtime | **.NET 10 (`net10.0`)**, C# | Makinede mevcut; süreç kontrolü ve P/Invoke için birinci sınıf |
| Host | Faz 1–8: **konsol host + `IHostedService`**. Faz 9: ASP.NET Core Minimal API + SSE | Ayrı kuyruk/worker altyapısı yok |
| ORM | **EF Core 10 + SQLite** | Migration disiplini |
| Loglama | **Serilog**, JSON dosya sink'i | Yapısal; sonradan OTel'e köprülenebilir |
| CLI | **`System.CommandLine`** | Hub'ın ilk arayüzü |
| Test | `xUnit` + `FluentAssertions` + fixture git repoları | Deterministik E2E |
| Mimari test | `NetArchTest` veya Roslyn analyzer | E.1 değişmezlerini CI'da zorlamak |
| UI (Faz 9) | **React + TypeScript + Vite** | Olgun diff bileşenleri (Monaco / react-diff-view) JS ekosisteminde; geliştirici zaten React+TS kullanıyor |
| Şema | JSON Schema | `--json-schema` ile doğrudan uyumlu |

**Reddedilen alternatifler:** PostgreSQL · Redis · RabbitMQ/Kafka · MediatR · Docker (altyapı olarak; izolasyon için Faz 10) · Node.js sidecar + Claude Agent SDK.

---

## O. Kaynak Ağacı Yapısı (öneri — oluşturulmadı)

```
corex-agent-hub/
├─ docs/
│  ├─ adr/                          ADR-0001 … ADR-0017
│  └─ architecture/                 bu belge + diyagramlar
├─ src/
│  ├─ CoreX.Hub.Domain/             Varlıklar, value object'ler, durum makineleri.
│  │                                BAĞIMLILIK YOK.
│  ├─ CoreX.Hub.Application/        ExecutionOrchestrator, TaskService,
│  │                                AcceptanceService, ScopeGuard, SecretGuard,
│  │                                BudgetGuard, portlar (arayüzler)
│  ├─ CoreX.Hub.Infrastructure/     EF Core DbContext, migration'lar, repository
│  │                                implementasyonları, blob deposu, redaksiyon
│  │                                (SecretGuard ile ortak desen kümesi)
│  ├─ CoreX.Hub.Execution/          ProcessRunner (Job Object interop),
│  │                                FileSystemGuard, env allowlist
│  ├─ CoreX.Hub.Workspace/          Git komutları, worktree yaşam döngüsü,
│  │                                koşullu result commit, reconciler, diff yakalama
│  ├─ CoreX.Hub.Context/            Context Pack derleyici + manifest/hash
│  ├─ CoreX.Hub.Agents/             Abstractions/  IAgentRuntime
│  │                                Scripted/     test double
│  │                                ClaudeCode/   CLI adapter (Faz 6)
│  │                                Codex/        (Faz 8)
│  ├─ CoreX.Hub.Verification/       Profil modeli, çalıştırıcı, TRX/JUnit ayrıştırıcı
│  ├─ CoreX.Hub.Cli/                İlk ve tek arayüz (Faz 1–8)
│  ├─ CoreX.Hub.Api/                [Faz 9] Minimal API + SSE
│  └─ CoreX.Hub.Web/                [Faz 9] React + Vite
├─ tests/
│  ├─ CoreX.Hub.Domain.Tests/
│  ├─ CoreX.Hub.Workspace.Tests/            gerçek git, geçici repolar
│  ├─ CoreX.Hub.Execution.Tests/            timeout/kill/hang senaryoları
│  ├─ CoreX.Hub.Verification.Tests/
│  ├─ CoreX.Hub.EndToEnd.Tests/             ScriptedAgentRuntime ile tam pipeline
│  └─ CoreX.Hub.ArchitectureTests/          E.1 değişmezleri
├─ fixtures/
│  └─ repos/                        Sentetik git repoları + hazır patch dosyaları
└─ hub.config.yaml                  Org/proje/repo/profil seed'i
```

`CoreX.Hub.Agents` tek proje olarak başlar; Codex ayrı bağımlılık gerektirdiğinde bölünür.

---

## P. Durum Makineleri

### P.1 Task (kaba) — D1 ile netleştirildi

```
Draft ──(Ready gate geçildi)───────────────► Ready
Ready ──(execution başladı)────────────────► Running
Running ──(execution Completed, Outcome=Passed)──────► AwaitingAcceptance
Running ──(execution Completed, Outcome≠Passed)──────► Ready
Running ──(execution Failed/TimedOut/Interrupted)────► Ready
AwaitingAcceptance ──(hub accept)──────────► Accepted        [TERMİNAL]
AwaitingAcceptance ──(request-changes)─────► Ready
AwaitingAcceptance ──(reject)──────────────► Rejected        [TERMİNAL]
terminal olmayan herhangi ──(cancel)───────► Cancelled       [TERMİNAL]
```

**(D1) Değişmezler:**
- `Accepted` geçişini yapan **tek** yol `hub accept` komutu → `AcceptanceService`'tir.
- `ExecutionOrchestrator`'ın `Accepted`'a giden hiçbir kod yolu yoktur (E.1 #6, mimari testle zorlanır).
- `Accepted` ve `Rejected` **terminaldir ve geri alınamaz.** Fikir değişirse yeni bir Task açılır. `AcceptanceDecision` değişmez bir kayıttır; güncellenmez, silinmez.

**Bilinçli sadeleştirme:** Task'ta `Failed` durumu yok — başarısızlık execution'a aittir. `Blocked` da yok; bloke görev `Ready`'dir ve `BlockReason` taşır.

### P.2 Execution (asıl makine) — D6 ile güncellendi

```
Created
 → PreparingWorkspace → BuildingContext → RunningAgent → CapturingChanges
 → Verifying → Reviewing → Completed
```

`Completed` bir `Outcome` taşır:

| Outcome | Anlamı | Result commit | Worktree |
|---|---|---|---|
| `Passed` | Tüm kapılar geçti | Var | Kabul/ret sonrası release |
| `VerificationFailed` | `required` doğrulama adımı FAIL | Var | Release edilebilir |
| `ReviewBlocked` | Bloklayıcı bulgu | Var | Release edilebilir |
| `ScopeViolation` | Kapsam dışı değişiklik | **Yok** | **Korunur** |
| `SecretGuardBlocked` | Eklenen satırlarda sır deseni | **Yok** | **Korunur** |

Terminal hata durumları (`Completed` değil): `Failed` · `TimedOut` · `Cancelled` · `Interrupted` — hepsinde **commit yok, worktree korunur**.

Akış kuralları:
- `RunningAgent` `Succeeded` değilse → `CapturingChanges` yalnızca **kanıt toplar**, commit atmaz → `Verifying` ve `Reviewing` **atlanır**.
- `CapturingChanges` kapısı başarısızsa (ScopeGuard/SecretGuard) → `Verifying`/`Reviewing` atlanır.
- `Verifying` FAIL → `Reviewing` atlanır.
- Tüm geçişler tek transaction'da yazılır ve `AuditEvent` üretir.

### P.3 ExecutionStep (mini)

```
Pending → Running → Succeeded | Failed | TimedOut | Cancelled | Skipped | Unsupported
```

`Skipped`: önceki kapı nedeniyle çalıştırılmadı (sebep kaydedilir).
`Unsupported`: runtime `Capabilities` gereği çalıştırılamadı. **Sessiz geçiş yok.**

### P.4 Verification / Review

Ayrı durum makinesi **yoktur**; bunlar `ExecutionStep`'tir.
- `VerificationOutcome`: her `required` adım `Succeeded` ise `Pass`, aksi hâlde `Fail`.
- `ReviewOutcome`: `Clean | NonBlockingFindings | Blocked | Unusable`.

### P.5 Kabul (D1 + D4)

`AcceptanceDecision` bir durum değil, **değişmez kayıttır**:

```
{
  ExecutionId, Decision: Accept|RequestChanges|Reject,
  Actor, Reason?, OverrideUsed: bool,
  HumanCriterionVerdicts: [ { CriterionId, Verdict: Met|NotMet|NotApplicable, Note? } ],
  DecidedAtUtc
}
```

Kurallar:
- `hub accept` çağrısı, Task `AwaitingAcceptance` değilse reddedilir.
- Task'ın **her** `Human` kriteri için bir verdict verilmeden `Accept` kaydedilemez.
- `Machine` kriterlerinin sonucu kanıttan otomatik doldurulur; insan bunları değiştiremez.
- Kabul; merge, push, release veya deployment **anlamına gelmez** (ADR-0014).

### P.6 Eşzamanlılık ve yarış koşulları

| Risk | Önlem |
|---|---|
| Aynı Task'a iki execution | Filtreli benzersiz indeks: Task başına terminal olmayan tek execution |
| Eşzamanlı Task güncellemesi | `Version` ile optimistic concurrency |
| Aynı repoda paralel `worktree add` | Repo başına named mutex |
| SQLite yazma çakışması | Global yazma semaforu + WAL + busy_timeout |
| Zombi execution | `Lease` (PID + host başlangıç zamanı + heartbeat); açılışta reconciler |
| İptal ile tamamlanma yarışı | Tek transaction; iptal yalnızca terminal olmayan durumdan kabul edilir |
| **Çift kabul** | `AcceptanceDecision` üzerinde `ExecutionId` benzersiz indeksi + Task durum kontrolü |

---

## Q. Hata ve Kurtarma Modeli (D6 ile güncellendi)

| Olay | Tespit | Davranış | Commit | Worktree |
|---|---|---|---|---|
| Claude süreci çöktü | Exit code | Adım `Failed`; **kısmi değişiklikler kanıtlanır** (status + `add -N` diff + envanter); Verify/Review atlanır | Yok | Korunur |
| Claude asıldı | `IdleTimeout` (varsayılan 10 dk) | Nazik kapatma → job kill → `TimedOut` | Yok | Korunur |
| Claude auth hatası | stream-json olayı / exit code | `Failed`, `AuthenticationRequired` sınıflandırması → **yeniden deneme yok**, insana yükselt | Yok | Korunur |
| Codex çöktü (Faz 8) | Aynı | İnceleme `Unusable`; doğrulama kanıtı geçerli kalır | Var (zaten atılmıştı) | Release edilebilir |
| Hub çöktü | Açılışta lease uyuşmazlığı | Execution → `Interrupted`; `hub workspace reconcile` önerilir | Yok | Korunur |
| Makine restart | Aynı | Aynı | Yok | Korunur |
| Test asıldı | `IdleTimeout` + `HardTimeout` | Job kill (tüm ağaç); adım `TimedOut`; doğrulama `Fail` → `VerificationFailed` | **Var** (commit doğrulamadan önce atılır) | Release edilebilir |
| Süreç hiç çıkmıyor | Job handle kapatma | Ağaç garantili ölür | — | — |
| Worktree kilitli kaldı | `remove` hatası | build-server shutdown → job kill → 3 artan gecikmeli deneme → `Orphaned`; akış bloklanmaz | — | `Orphaned` |
| Kapsam ihlali | ScopeGuard | `ScopeViolation`; Verify/Review atlanır | **Yok** | **Korunur** |
| Sır deseni | SecretGuard | `SecretGuardBlocked`; operatör `--override --reason` ile commit edebilir | **Yok** | **Korunur** |
| Agent kendi commit'ini attı | `HEAD != BaseCommitSha` | `UnexpectedAgentCommit` bulgusu; akış devam eder (bloklamaz) | Var (agent commit'i + Hub commit'i) | Normal |
| Context üretimi başarısız | Builder doğrulaması | Execution başlamadan `Failed`; **sessiz kırpma yok** | Yok | Oluşturulmadıysa yok |
| Doğrulama başarısız | Exit code | `VerificationFailed`; inceleme atlanır; Task `Ready`'ye döner | Var | Release edilebilir |
| İnceleyici bloklayıcı bulgu | Kapı kuralı | `ReviewBlocked`; insan override edebilir (gerekçe zorunlu) | Var | Release edilebilir |
| Şema doğrulaması başarısız | `--json-schema` | Bir kez yeniden dene → `ReviewUnusable` | Var | Release edilebilir |
| Disk doldu | Yazma hatası | `Failed`; blob yazımı atomik → yarım blob yok | Yok | Korunur |

**Genel ilkeler:**
1. *Hiçbir kurtarma yolu veri silmez.* Temizlik daima açık operatör komutudur.
2. *(D6)* **Commit yoksa worktree silinmez.**
3. *(D1)* Hiçbir kurtarma yolu Task'ı `Accepted` yapmaz.

---

## R. Gözlemlenebilirlik

1. **Yapısal loglar** — Serilog, `logs/hub-YYYYMMDD.jsonl`. Zorunlu alanlar: `timestamp`, `level`, `message`, `organizationId`, `taskKey`, `executionId`, `stepId`, `correlationId`.
2. **Korelasyon** — `ExecutionId` bir GUID'dir ve aynı zamanda Claude `--session-id` olarak verilir → Hub kaydı ↔ sağlayıcı oturumu tek atlamada eşleşir.
3. **`OrchestrationEvent` tablosu** — CLI/UI zaman çizelgesi için küçük satırlar. Blob yok.
4. **`Activity` / `ActivitySource`** — geçişler ve süreç çalıştırmaları etrafında. Exporter yok.
5. **Süre ölçümleri** — adım bazında; kanıt özetinde gösterilir.
6. **`hub doctor`** — git sürümü, `claude --version`, disk alanı, DB erişimi, yetim worktree sayısı, açık lease sayısı, **bloke organizasyonlar (L.4)**.

**Yapılmayacaklar:** metrik sunucusu, tracing backend, log toplama, Kafka, dashboard.

---

## S. İlk Uçtan Uca Dikey Dilim

### S.1 Neden dilim üçe bölündü

v1 önerisi doğru yöne bakıyordu ama iki doğrulanmamış AI bağımlılığını (Claude headless + Codex) ilk dilime koyuyordu. Bu, mimari riskin en yüksek olduğu yerde (süreç kontrolü, git izolasyonu, kanıt) hata ayıklamayı AI belirsizliğiyle karıştırır.

### S.2 DİLİM 1 — "Loop Zero" (AI YOK) — D7 ile kapsamı kilitli

```
hub task create → Ready gate (MACHINE/HUMAN kriter beyanı doğrulanır)
      ↓
Repository çöz + base commit sabitle
      ↓
Worktree + agent/<key>/1 branch + worktree-yerel sertleştirme (yalnızca pre-push)
      ↓
Context Pack derle + manifest + hash
      ↓
ScriptedAgentRuntime (fixture patch / hata modu)
      ↓
CaptureChanges: anomali tespiti → envanter → ScopeGuard → SecretGuard
      ↓                                         │
      ├── kapı başarısız → commit YOK, kanıt VAR, worktree KORUNUR → Completed(...)
      ↓
Hub result commit + commit diff
      ↓
Verification profile (dotnet restore/build/test)
      ↓
Kanıt sakla (blob + tablo)
      ↓
Task = AwaitingAcceptance          ← hub run BURADA DURUR (D1)
      ↓
hub accept <execution>  (HUMAN kriter verdictleri girilir)
      ↓
Task = Accepted  → worktree release
```

**Loop Zero'nun İÇİNDE olanlar (sabit — D7):**
fixture repository · `ScriptedAgentRuntime` · `ProcessRunner` · `FileSystemGuard` · git worktree izolasyonu · Context Pack · ScopeGuard · SecretGuard (commit kapısı) · Verification · Evidence (commit'li ve commit'siz) · deterministik orkestrasyon · **açık insan kabulü**.

**Loop Zero'nun DIŞINDA olanlar:**
Claude yok · Codex yok · React yok · REST API yok · WSL runtime yok · otonom onarım döngüsü yok · maliyet raporu yok · çoklu org paralelliği yok · gerçek repository yok · gerçek organizasyon envanteri yok.

> **D7 notu:** D4 (kabul kriteri modeli) bir alan + bir CLI sorusu + bir JSON kaydıdır; D6 (koşullu commit + SecretGuard) mevcut `CaptureChanges` adımına eklenen iki koşuldur ve desen kümesini redaksiyondan devralır. **Yeni bileşen eklenmemiştir.**

#### Kabul kriterleri (Loop Zero)

| # | Kriter |
|---|---|
| 1 | **(D1)** `hub run N360-DEMO` başarıyla tamamlandığında Task `AwaitingAcceptance` olur — **asla `Accepted` olmaz.** Ardından `hub accept N360-DEMO#1` Task'ı `Accepted` yapar. İki komut arasında Task durumu kalıcıdır (süreç yeniden başlatılsa bile) |
| 2 | **(D1)** `hub accept`, Task `AwaitingAcceptance` değilken çağrılırsa reddedilir; aynı execution için ikinci kez çağrılırsa reddedilir |
| 3 | **(D4)** Fixture task'ı 2 `MACHINE` + 1 `HUMAN` kriter taşır. `MACHINE` sonuçları kanıttan otomatik dolar; `HUMAN` kriterine verdict verilmeden `accept` tamamlanmaz |
| 4 | **(D4)** `VerificationKind` beyan edilmemiş bir kriterle Task `Ready`'ye geçemez; `Machine` kriterinin `VerificationRef`'i mevcut olmayan bir adıma işaret ederse Ready gate reddeder |
| 5 | Tüm akış **CI'da, ağsız, AI'sız, deterministik** geçer |
| 6 | Aynı base commit ile iki çalıştırma **aynı `ContextPackHash`** üretir |
| 7 | Fixture repoda test kırıkken → `VerificationFailed`; patch uygulanınca → `Passed`. Baseline karşılaştırması kanıtta görünür |
| 8 | **(D6)** `out-of-scope-write` modu → `ScopeViolation`, **result commit yok**, `GitWorktreeDiff` + `FileInventory` kanıtı var, **worktree korunur** |
| 9 | **(D6)** `secret-write` modu → `SecretGuardBlocked`, commit yok, kanıt var, worktree korunur; `--override --reason` ile commit atılabilir ve audit'e yazılır |
| 10 | **(D6)** `hang` modu → `IdleTimeout` tetiklenir, **süreç ağacı tamamen ölür** (kalan `dotnet`/`node` süreci yok), commit yok, worktree korunur |
| 11 | **(D6)** `crash` / `partial-write` modu → kısmi değişiklikler `GitWorktreeDiff` ve `FileInventory` olarak kanıtlanır; **untracked yeni dosyalar diff'te görünür** (`add -N` doğrulaması) |
| 12 | **(D2)** `unexpected-commit` modu → `UnexpectedAgentCommit` bulgusu üretilir, akış bloklanmaz |
| 13 | **(D2)** Worktree'de `git push` denemesi `pre-push` hook'u nedeniyle başarısız olur; **Hub'ın kendi commit'i hiçbir bypass bayrağı olmadan başarılı olur** |
| 14 | Çalışma ortasında Hub süreci `kill` edilir → yeniden başlatmada execution `Interrupted`, worktree korunur, `reconcile` doğru raporlar |
| 15 | Kanıt deposunda hiçbir blob DB içinde değil; her blob hash'iyle bulunabilir |
| 16 | **(D5)** `RequiresHardIsolation = true` + `IsolationLevel = Logical` olan bir organizasyon `Blocked` yüklenir ve execution başlatılamaz |

**Test stratejisi:** `fixtures/repos/dotnet-sample` — gerçek git reposu, kasıtlı kırık test, hazır `fix.patch`, kapsam dışı patch, sahte sır içeren patch. `ScriptedAgentRuntime` modları F.2'de listelenmiştir.

**Demo senaryosu:**
> `hub run N360-DEMO` → ~90 saniye → ekranda I.6'daki kanıt özeti → **Task `AwaitingAcceptance`** → `hub evidence show N360-DEMO#1 --diff` → `hub accept N360-DEMO#1` (HUMAN kriteri sorulur) → **`Accepted`**.

**Neden bu dilim mimariyi kanıtlar:** ProcessRunner, FileSystemGuard, worktree yaşam döngüsü, koşullu commit kapıları, durum makineleri, kanıt deposu (iki yol), kurtarma, kapsam ve sır denetimi, ve **kabul semantiği** — hepsi aynı anda ve tekrarlanabilir biçimde sınanmış olur.

### S.3 DİLİM 2 — Claude uygulayıcı (Faz 6)

`ScriptedAgentRuntime` yerine `ClaudeCodeRuntime`. Loop Zero'nun 16 kriteri geçerliliğini korur; ek olarak: `stream-json` ayrıştırma başarısız olsa bile ham stdout kanıtı kaybolmaz; `--session-id` = `ExecutionId` eşleşmesi doğrulanır; mevcut abonelik oturumu `--safe-mode --setting-sources user` altında çalışır.

### S.4 DİLİM 3 — Bağımsız inceleme (Faz 7)

Ayrı, atılabilir worktree (result commit'ten) + read-only profil + `--json-schema`. Kabul kriteri: inceleyici worktree'sinde yazma tespit edilirse inceleme `Unusable` olur; `ResultCommitSha == null` olan execution'da inceleme hiç çalıştırılmaz; bloklayıcı bulgu Task'ı `AwaitingAcceptance`'a **geçirmez**.

---

## T. Fazlı Uygulama Yol Haritası (kanonik faz listesi)

> Bu liste tek doğruluk kaynağıdır. Belgedeki tüm faz referansları buna uyar.

### Faz 0 — Kararlar (kod yok)
- **Amaç:** ADR'leri kaydetmek.
- **Çıktı:** `docs/adr/` içinde ADR-0001 … ADR-0017; `hub.config.yaml` şema taslağı; fixture kökü ve `TaskKey` formatı kararı.
- **Kabul:** Denetçi bu belgeyi ve ADR'leri onaylar.
- **Dış kapı:** Yok.
- **YAPILMAYACAK:** Proje oluşturma.

### Faz 1 — İskelet, domain, kalıcılık, CLI
- **Çıktı:** Solution; `Domain` + `Application` + `Infrastructure` + `Cli`; 15 tablo + migration'lar; `hub.config.yaml` seed'i; Ready gate (D4); `Organization` bloke etme mantığı (D5); `hub org/project/repo list`, `hub task create/show`.
- **Bağımlılık:** Faz 0.
- **Kabul:** Task oluşturulur; `VerificationKind` beyanı olmadan `Ready`'ye geçemez; `RequiresHardIsolation` bloke testi geçer; mimari testler (E.1 #1, #4, #6, #8) yeşil.
- **Dış kapı:** **Yok** (D3).
- **YAPILMAYACAK:** Süreç çalıştırma, git, AI, API, UI.

### Faz 2 — Execution çekirdeği (EN YÜKSEK RİSK)
- **Çıktı:** `ProcessRunner` (Job Object interop, üç seviyeli timeout, async stream tüketimi, env allowlist, UTF-8/ANSI), `FileSystemGuard`.
- **Kabul:** Torun süreç öldürme testi geçer; yüksek hacimli stdout+stderr deadlock testi geçer; allowlist dışı env değişkeni çocuk süreçte görünmez.
- **Dış kapı:** Yok.
- **YAPILMAYACAK:** Git, AI.

### Faz 3 — Workspace (git) yöneticisi
- **Çıktı:** Base commit sabitleme, worktree/branch, worktree-yerel sertleştirme (**yalnızca `pre-push`** — D2), anomali tespiti, çalışma ağacı diff'i (`add -N`), koşullu result commit iskeleti, reconciler, `hub workspace` komutları.
- **Kabul:** Kirli ana dizinle görev başlar; `.gitignore`'lu dosya worktree'de yok; `pre-push` push'u engeller; **Hub commit'i bypass bayrağı olmadan başarılı olur**; zorla kilitlenmiş worktree `Orphaned` olur ve akış bloklanmaz.
- **Dış kapı:** Yok.

### Faz 4 — Doğrulama motoru + kanıt deposu
- **Çıktı:** Profil modeli, allowlist'li kabuksuz çalıştırıcı, TRX/JUnit ayrıştırıcı, baseline önbelleği, içerik adresli blob deposu, atomik yazım, **redaksiyon desen kümesi (SecretGuard ile ortak)**.
- **Kabul:** Fixture repoda kırık→düzelmiş geçişi sayaçlarla kanıtlanır; sır test vektörleri redakte edilir; blob yazımı atomiktir.
- **Dış kapı:** Yok.

### Faz 5 — Orchestrator + Context Pack + ScriptedAgentRuntime → ★ DİLİM 1 (Loop Zero) ★
- **Çıktı:** `ExecutionOrchestrator`, durum makineleri (D1/D6), lease/heartbeat, ScopeGuard, **SecretGuard commit kapısı**, Context Pack derleyici + manifest, `ScriptedAgentRuntime` (8 mod), `hub run / accept / request-changes / reject / evidence show / commit-result --override`.
- **Kabul:** S.2'deki 16 kriter.
- **Dış kapı:** Yok.
- **YAPILMAYACAK:** AI çağrısı, inceleme, API, UI.

### Faz 6 — `ClaudeCodeRuntime` → ★ DİLİM 2 ★
- **Çıktı:** CLI adapter, hataya dayanıklı `stream-json` ayrıştırıcı, izin profili eşlemesi, kullanım metadata yakalama, `--session-id` korelasyonu.
- **Kabul:** Gerçek bir görev uçtan uca geçer ve `AwaitingAcceptance` ile durur; ayrıştırma hatası kanıt kaybına yol açmaz; timeout ve iptal gerçek süreçte doğrulanır.
- **Dış kapı:** **G6-1, G6-2, G6-3** (bkz. V.2).
- **YAPILMAYACAK:** Onarım döngüsü, çoklu tur.

### Faz 7 — İnceleme adımı (Claude read-only profil) → ★ DİLİM 3 ★
- **Çıktı:** Ayrı atılabilir worktree, read-only profil, `--json-schema` bulgu şeması, kurcalama tespiti, kapı kuralları, override + gerekçe.
- **Kabul:** Bulgular tabloya yazılır; Critical bulgu bloklar; override audit'e düşer; kurcalama senaryosu `Unusable` üretir.
- **Dış kapı:** Faz 6 tamamlanmış olmalı.

### Faz 8 — `CodexRuntime`
- **Çıktı:** Codex adapter; gerekirse WSL2 yol çevirimi; `Capabilities` ilanı; profil eşlemesi.
- **Kabul:** İnceleme adımı **yalnızca `AgentProfile` değiştirilerek** Codex'e geçer — orchestrator kodu değişmez. (F.1 soyutlamasının gerçek sınavı.)
- **Dış kapı:** **G8-1, G8-2** (bkz. V.2).

### Faz 9 — Minimal API + UI
- **Çıktı:** ASP.NET Core Minimal API + SSE (CLI ile eşdeğer yüzey); React+TS: proje/görev listesi, execution zaman çizelgesi, diff görüntüleyici, doğrulama sonuçları, bulgular, **kabul ekranı (HUMAN kriter checklist'i — D4)**.
- **Kabul:** Bir görev baştan sona yalnızca UI'dan yönetilebilir; kabul ekranı `HUMAN` verdictleri olmadan `accept` göndermez.
- **YAPILMAYACAK:** Avatar, animasyon, gamification, dashboard metrikleri.

### Faz 10 — Sertleştirme + Kronos kapısı
- **Çıktı:** Onarım döngüsü (max 1 tur, bütçe sınırlı) · maliyet/kullanım raporu · kanıt GC · gelişmiş redaksiyon · `CreateProcess` P/Invoke ile yarışsız job ataması · **WSL2 tabanlı sert organizasyon izolasyonu** · pre-flight sır tarama · günlük çalıştırma bütçesi · opsiyonel `hub verify <execution>`.
- **Kabul:** **L.4'teki G-K1…G-K5 koşulları karşılanır ve Kronos onboard edilebilir hâle gelir.** K.4'teki artık risklerden en az ikisi kapanır.

---

## U. Önerilen ADR'ler

| No | Başlık | Durum |
|---|---|---|
| ADR-0001 | Local-first, tek süreçli modüler monolit | v1'den |
| ADR-0002 | Kanıt otoritesi: makine gerçeği > inceleyici > agent beyanı; agent beyanı otomatik outcome'a giremez | v1'den, D4 ile netleşti |
| ADR-0003 | Git worktree ile execution izolasyonu ve base-commit sabitleme | v1'den |
| ADR-0004 | Worktree'ler repo dışında, Hub yönetimindeki kısa kökte | v1'den |
| ADR-0005 | **Sonuç commit'ini Hub atar; `pre-commit` hook'u yoktur; agent commit engeli runtime politikası + tespittir; uzağa yayınlama engeli altı katmanlıdır ve Hub kendi korumasını atlatmaz** | **D2 ile revize** |
| ADR-0006 | Tüm süreç çalıştırma tek `ProcessRunner` üzerinden, kabuk kullanılmadan | v1'den |
| ADR-0007 | Ortam değişkeni allowlist'i (denylist değil) | v1'den |
| ADR-0008 | `IAgentRuntime` ince tutulur; yetenekler gizlenmez, ilan edilir | v1'den |
| ADR-0009 | **Claude Code entegrasyonu CLI subprocess ile; mevcut abonelik oturumu kullanılır; API-key faturalandırma mimarisi tasarlanmaz; `setup-token` yalnızca gözetimsiz çalıştırma gerekirse** | **D3 ile revize** |
| ADR-0010 | Context Pack diskte materyalize edilir ve hash'lenir | v1'den |
| ADR-0011 | Repo içi agent yapılandırması ve git hook'ları varsayılan olarak devre dışı | v1'den, D2 ile genişledi |
| ADR-0012 | **Organizasyon izolasyonu Core X Logic pilotu için mantıksaldır; artık risk kabul edilmiştir** | **D5 ile daraltıldı** |
| ADR-0013 | SQLite + WAL; büyük çıktılar içerik adresli blob deposunda | v1'den |
| ADR-0014 | İnsan kabulü merge/push/deploy anlamına gelmez; kabul ayrı, açık ve geri alınamaz bir karar kaydıdır | v1'den, D1 ile güçlendi |
| **ADR-0015** | **Kabul kriterleri `MACHINE`/`HUMAN` beyanı taşır; otomatik outcome yalnızca makine kanıtı ve yapılandırılmış review kapılarından hesaplanır; kural motoru yoktur** | **YENİ (D4)** |
| **ADR-0016** | **Başarısız/kısmi/kapı-bloklu execution'lar için result commit atılmaz; kanıt commit olmadan korunur; commit'siz worktree otomatik silinemez; minimal SecretGuard commit kapısıdır** | **YENİ (D6)** |
| **ADR-0017** | **Kronos onboarding, sert OS-seviyesi izolasyon uygulanıp doğrulanana kadar kod ile bloke edilir (WSL2 birinci aday)** | **YENİ (D5)** |

MVP'de **yazılmayacak** ADR'ler: mesajlaşma altyapısı, çoklu kiracılık, yetkilendirme modeli, ölçeklendirme, vector store.

---

## V. Açık Sorular — Faz Bazlı Kapılar (D3 ile yeniden yapılandırıldı)

> v1'deki tek global "uygulama öncesi bloklayıcı" listesi kaldırıldı. Her soru, **gerçekten bloke ettiği faza** bağlanmıştır.

### V.1 Faz 1 öncesi gerçek bloklayıcılar

| # | Konu | Durum |
|---|---|---|
| — | **Yok.** | Faz 1–5 hiçbir dış bilinmeyene bağlı değildir. Loop Zero fixture repository, fixture organizasyon ve fixture kökleri kullanır. |

Faz 1'in tek ön koşulu **Faz 0 çıktısıdır** (ADR kaydı + iki küçük format kararı):

| Karar | Önerilen varsayılan | Not |
|---|---|---|
| Fixture/workspace kökü | `D:\cx\` (fixture'lar `D:\cx\_fixtures\`) | Kısa yol zorunluluğu (G.1); onay yeterli, araştırma gerekmez |
| `TaskKey` formatı | `<PROJE-KISALTMA>-<3 hane>` (`N360-014`) | Benzersizlik org+proje kapsamında |

### V.2 Faz kapıları

| Kapı | Bloke ettiği faz | Soru | Nasıl kapatılır |
|---|---|---|---|
| **G6-1** | Faz 6 | Mevcut Claude Code abonelik oturumu `--safe-mode --setting-sources user` altında headless (`-p`) çalışıyor mu? | Tek bir manuel çalıştırma. **API-key mimarisi tasarlanmayacak.** Çalışmazsa `setup-token` değerlendirilir |
| **G6-2** | Faz 6 | İlk gerçek repository hangisi; doğrulama komutları tam olarak neler; şu an testler geçiyor mu? | Repo seç, komutları elle çalıştır, süreleri ölç → verification profile yaz |
| **G6-3** | Faz 6 | Core X Logic organizasyon/proje/repository envanteri ve yerel yollar | `hub.config.yaml` doldur |
| **G7-1** | Faz 7 | Bloklayıcı `Major` bulgu eşiği kaç olacak? | Faz 7 sonunda gerçek bulgularla kalibre et |
| **G8-1** | Faz 8 | Codex hangi yüzeyle entegre olacak; non-interactive çalıştırma ve yapısal çıktı biçimi nedir? | Codex CLI kur, `--help` çıktısını Faz 0'daki Claude analizinin aynısıyla çıkar |
| **G8-2** | Faz 8 | Codex Windows'ta sandbox destekliyor mu; WSL2 gerekiyor mu; ChatGPT aboneliğiyle programatik kullanım uygun mu? | Kurulum sonrası doğrulama |
| **G10-1** | Faz 10 | Onarım döngüsünde maksimum tur ve bütçe | Faz 7 verisiyle kalibre |
| **G-K1…G-K5** | **Kronos onboarding** (geliştirmeyi bloklamaz) | L.4 tablosundaki beş koşul | Faz 10 çıktısı + negatif erişim testleri |

### V.3 Ertelenebilir sorular (hiçbir fazı bloklamaz)

| # | Soru | Ne zaman |
|---|---|---|
| N1 | İnceleme için sağlayıcı çeşitliliği ne kadar değerli (aynı sağlayıcı farklı model yeterli mi)? | Faz 7 sonunda ölç |
| N2 | Çoklu repo görevleri gerekli mi? | Gerçek bir görev talep edene kadar hayır |
| N3 | Kanıt saklama süresi ve GC politikası | Disk baskısı oluşunca |
| N4 | UI'da hangi diff bileşeni (Monaco vs react-diff-view) | Faz 9 |
| N5 | Maliyet/kullanım raporlamasının biçimi | Faz 10 |
| N6 | Uzak worker / ayrı execution makinesi | Ölçülmüş darboğaz olmadan hayır |
| N7 | Gözetimsiz/CI çalıştırma gerekecek mi (o zaman `setup-token`) | Talep doğduğunda |

---

## W. Aşırı Mühendislik Riskleri

En tehlikeliden başlayarak:

1. **Workflow/policy motoru inşa etmek.** *Uyarı:* Faz 9'a kadar pipeline **kodda sabit sıralı adımlardır**. Yapılandırılabilirlik ancak ikinci gerçek pipeline şekli ortaya çıkınca eklenir.
2. **Kabul kriteri modelini kural motoruna çevirmek (D4 sonrası yeni risk).** `MACHINE`/`HUMAN` beyanı bir string referansıdır. *Uyarı:* koşul ifadeleri, boolean birleştirme, kriter bağımlılık grafiği **yazılmayacak**.
3. **SecretGuard'ı sır yönetim platformuna büyütmek (D6 sonrası yeni risk).** *Uyarı:* desen kümesi redaksiyonla ortaktır ve orada kalır. Vault, rotasyon, entropi modeli, baseline dosyası yok.
4. **Soyutlamayı tek sağlayıcıya göre şekillendirmek.** *Uyarı:* `ScriptedAgentRuntime`'ı ilk yazın.
5. **Bağlam sistemini bilgi tabanına çevirmek.** *Uyarı:* Bağlam = insan yazımı markdown + git.
6. **Kanıt modelini aşırı normalleştirmek.** *Uyarı:* Blob + hash + küçük meta.
7. **UI'ı erken başlatmak.** *Uyarı:* Faz 9'dan önce tek satır React yok.
8. **Otonomi eklemek.** *Uyarı:* Kanıtı ve determinizmi öldürür.
9. **Çok kullanıcılı / yetkilendirme mimarisi hazırlamak.** *Uyarı:* `Actor` bir string olsun.
10. **Event sourcing / CQRS.** *Uyarı:* Append-only `AuditEvent` tablosu yeter.
11. **Genel amaçlı plugin sistemi.** *Uyarı:* İki runtime var, ikisi de derlenmiş projede.
12. **Maliyet takibini erken derinleştirmek.** *Uyarı:* Metadata sakla, raporu Faz 10'a bırak.
13. **Diff görüntüleyiciyi sıfırdan yazmak.** *Uyarı:* Olgun kütüphane kullan.
14. **Sandbox'ı mükemmelleştirmeye çalışmak.** *Uyarı:* Faz 10'da WSL2 ile tek hamlede gelir; Kronos kapısı dışında acele gerektirmez.

---

## X. Nihai Önerilen İnşa Sırası

```
 1. Faz 0: ADR-0001 … ADR-0017'yi yaz; fixture kökü ve TaskKey formatını onayla;
    bu belgeyi denetime ver.
 2. Solution ve proje iskeletini oluştur; katman bağımlılık yönünü mimari testle kilitle.
 3. Domain modelini yaz (15 tablo, 3 aggregate) + durum makineleri (D1/D6)
    + Ready gate (D4) + Organization bloke mantığı (D5).
 4. EF Core + SQLite (WAL) + ilk migration + hub.config.yaml seed'i.
 5. Hub CLI iskeleti: org/project/repo list, task create/show.
    → Faz 1 TAMAM
 6. ProcessRunner: Job Object interop, üç seviyeli timeout, async stream tüketimi,
    env allowlist, UTF-8/ANSI. "Torun süreç ölüyor mu" testini yaz ve geçir.
 7. FileSystemGuard: kanonik yol, org kökü, reparse-point kaçış denetimi.
    → Faz 2 TAMAM
 8. Workspace Manager: base commit sabitleme, worktree+branch,
    worktree-yerel sertleştirme (YALNIZCA pre-push), anomali tespiti,
    çalışma ağacı diff'i (add -N), reconciler.
    → Faz 3 TAMAM
 9. Evidence Store: içerik adresli blob, atomik yazım, redaksiyon desen kümesi.
10. Verification Engine: profil modeli, allowlist'li kabuksuz çalıştırıcı,
    TRX/JUnit ayrıştırıcı, baseline önbelleği.
    → Faz 4 TAMAM
11. Context Pack Builder: kaynak birleştirme, öncelik, bütçe kontrolü, manifest+hash.
12. ScopeGuard + SecretGuard: commit öncesi zorunlu kapılar; koşullu result commit.
13. ExecutionOrchestrator: durum makineleri, lease/heartbeat, adım sıralaması, audit.
14. ScriptedAgentRuntime (8 mod) + fixture repolar.
15. hub run / accept / request-changes / reject / evidence show / commit-result --override
    → ★ Faz 5 TAMAM — DİLİM 1 (Loop Zero): AI'sız uçtan uca kanıt + AÇIK İNSAN KABULÜ ★
16. G6-1/G6-2/G6-3 kapılarını kapat (tek manuel Claude çalıştırması + ilk repo seçimi
    + Core X Logic envanteri).
17. ClaudeCodeRuntime → ★ Faz 6 — DİLİM 2: ilk gerçek agent çalıştırması ★
18. Review adımı: ayrı atılabilir worktree, read-only profil, --json-schema bulgular,
    kurcalama tespiti, kapı kuralları → ★ Faz 7 — DİLİM 3 ★
19. G8-1/G8-2 kapılarını kapat; CodexRuntime — orchestrator'a dokunmadan.
    → Faz 8 TAMAM
20. Minimal API + SSE + React UI (kabul ekranında HUMAN kriter checklist'i).
    → Faz 9 TAMAM
21. Sertleştirme: onarım döngüsü, maliyet raporu, kanıt GC, CreateProcess P/Invoke,
    WSL2 sert izolasyon → G-K1…G-K5 → **Kronos onboarding kapısı açılır**.
    → Faz 10 TAMAM
```

**15. adım projenin gerçek doğum anıdır.** O noktada AI olmadan çalışan, deterministik, denetlenebilir bir mühendislik orkestrasyon motoru vardır ve AI eklemek yalnızca bir adaptör değiştirmektir.

---

## Y. Kapanış — Revizyon Özeti ve Faz 1 Kararı

### Y.1 Revizyon özeti

| Düzeltme | Uygulandı | Ana etki |
|---|---|---|
| D1 — Kabul semantiği | ✔ | `hub run` `AwaitingAcceptance`'ta durur; `Accepted` yalnızca `AcceptanceService`'ten; mimari testle kilitlendi |
| D2 — `pre-commit` çelişkisi | ✔ | `pre-commit` kaldırıldı; push engeli altı katmana çıkarıldı; agent commit'i **tespit** edilir; Hub bypass bayrağı kullanamaz |
| D3 — Soru sınıflandırması | ✔ | Global bloklayıcı liste kaldırıldı; faz kapıları tablosu (V.2); Faz 1–5 dış bağımlılıksız |
| D4 — Kabul kriteri modeli | ✔ | `MACHINE`/`HUMAN` beyanı; "doğrulanamayan kriter = tanımsız görev" geri çekildi; otomatik outcome yalnızca makine kanıtı + review kapıları |
| D5 — Kronos kapısı | ✔ | L.4 eklendi; kod ile zorlanan `Blocked` durumu; geliştirmeyi bloklamaz |
| D6 — Başarısız/kısmi commit politikası | ✔ | Koşullu commit; commit'siz kanıt yolu; **commit'siz worktree silinemez** değişmezi; minimal SecretGuard |
| D7 — Loop Zero küçük kalsın | ✔ | Kapsam listesi sabitlendi; D4/D6 yeni bileşen eklemedi |
| Çapraz referans uyumu | ✔ | Kanonik faz listesi (T); v1'deki 4 faz numarası kayması düzeltildi |
| İç tutarlılık taraması | ✔ | 7 ek çelişki bulundu ve düzeltildi (0.5.3) |

### Y.2 Faz 1 öncesi kalan gerçek bloklayıcılar

**Dış bilinmeyene bağlı bloklayıcı yoktur.**

Kalan tek ön koşul, tamamı bu oturumda üretilebilecek Faz 0 çıktısıdır:

1. ADR-0001 … ADR-0017'nin yazılıp kaydedilmesi (içerikleri bu belgede kararlaştırılmış durumda).
2. İki format onayı: workspace/fixture kökü (`D:\cx\` önerilir) ve `TaskKey` formatı (`N360-014` önerilir).

Bunlar araştırma değil, onay gerektirir.

### Y.3 Sonraki fazlara ertelenen bloklayıcılar

| Kapı | Bloke ettiği | Kısa açıklama |
|---|---|---|
| G6-1 | Faz 6 | Mevcut abonelik oturumunun `--safe-mode` altında headless çalıştığının teyidi |
| G6-2 | Faz 6 | İlk gerçek repository + verification profile |
| G6-3 | Faz 6 | Core X Logic envanteri ve yerel yollar |
| G7-1 | Faz 7 | `Major` bulgu eşiği kalibrasyonu |
| G8-1 / G8-2 | Faz 8 | Codex kurulumu, yüzeyi, Windows sandbox / WSL2 gereksinimi, kullanım uygunluğu |
| G10-1 | Faz 10 | Onarım turu ve bütçe sınırları |
| **G-K1 … G-K5** | **Yalnızca Kronos onboarding** | Sert OS-seviyesi izolasyon uygulanmış ve negatif testlerle doğrulanmış olmalı |

### Y.4 Faz 1 uygulaması başlayabilir mi?

> **EVET.**
>
> Faz 1 (iskelet, domain modeli, SQLite kalıcılığı, Ready gate, organizasyon bloke mantığı, CLI iskeleti) **hiçbir dış bilinmeyene bağlı değildir** ve Faz 0'ın ADR kaydı tamamlandığı anda başlayabilir.
>
> Faz 1–5 boyunca hiçbir AI sağlayıcısına, hiçbir gerçek repository'ye ve hiçbir gerçek organizasyon verisine ihtiyaç yoktur. İlk dış kapı (G6-1) yalnızca Faz 6'da devreye girer.
>
> **Kronos verisi hiçbir fazda platforma girmez** — L.4 kapısı açılana kadar bu bir kod değişmezidir.

---

## Ek — Denetçi İçin Hızlı Kontrol Listesi

| Soru | Cevap |
|---|---|
| `hub run` kabul edebilir mi? | Hayır — E.1 #6 mimari değişmezi, P.1, S.2 kriter #1–2 |
| Hub kendi korumasını atlatıyor mu? | Hayır — `pre-commit` yok; E.1 #8 bypass bayraklarını yasaklar |
| Başarısız execution commit atıyor mu? | Hayır — G.4 kapı tablosu, P.2, ADR-0016 |
| Commit'siz kanıt eksik mi? | Hayır — I.5 asgari kanıt tablosu (`add -N` diff + envanter + hash'ler) |
| Commit'siz worktree silinebilir mi? | Hayır — G.6 değişmezi |
| Kabul kriterleri makinece doğrulanmak zorunda mı? | Hayır — `MACHINE`/`HUMAN` beyanı yeterli (D4, ADR-0015) |
| Agent beyanı bir yerde outcome'a giriyor mu? | Hayır — I.1 Kademe C, hiçbir kademe geçişinde kullanılmaz |
| Kronos ne zaman onboard edilebilir? | L.4 G-K1…G-K5 karşılandığında; kod ile bloke (D.1, ADR-0017) |
| Faz 1'in dış bağımlılığı var mı? | Yok — V.1, Y.2 |
| Loop Zero büyüdü mü? | Hayır — S.2 kapsam listesi sabit; D4/D6 mevcut bileşenlere eklendi (D7) |
| İlk çalışan çıktı ne zaman? | X adım 15 (Faz 5 sonu) |
