# CoreX Agent Hub — Mimari Plan (v1.1.1, final errata)

**Belge türü:** Mimari karar kaydı — dondurma öncesi son düzeltme turu
**Soy zinciri:** v1 (yön onaylandı) → v1.1 (denetim düzeltmeleri) → **v1.1.1 (final errata)**
**Durum:** Mimari dondurmaya hazır — bkz. Bölüm Y
**Tarih:** 2026-08-22

> Bu oturumda da **hiçbir kod yazılmadı, hiçbir proje iskelesi kurulmadı, hiçbir bağımlılık yüklenmedi, hiçbir veritabanı oluşturulmadı, hiçbir commit atılmadı.**

---

## 0. Doğrulanmış Ortam Gerçekleri

| Bileşen | Ölçülen değer | Plana etkisi |
|---|---|---|
| İşletim sistemi | Windows 11 Pro 10.0.26200 | Sandbox stratejisi Linux'tan farklı — K, L |
| .NET SDK | 10.0.301 | `net10.0` hedeflenir |
| Git | 2.54.0.windows.1 | `git worktree`, `--porcelain=v2` mevcut |
| Node.js | v24.16.0 | Codex CLI ve JS test araçları için hazır |
| WSL2 | Kurulu, varsayılan Ubuntu | Kronos için gereken sert izolasyonun birinci adayı |
| Claude Code | **2.1.240, kurulu ve oturumu açık** | Faz 6 için hazır; ayrı auth altyapısı gerekmiyor |
| Codex CLI | **KURULU DEĞİL** | Codex'e dair her şey doğrulanmamış |

### 0.1 Claude Code CLI — doğrulanan yetenekler

| Flag | Mimari kullanım |
|---|---|
| `-p, --print` | Non-interactive çalıştırma |
| `--output-format json\|stream-json` | Yapısal sonuç + canlı akış |
| `--json-schema <schema>` | Şema doğrulamalı çıktı — review bulguları |
| `--permission-mode manual\|plan\|acceptEdits\|auto\|dontAsk\|bypassPermissions` | İzin profili motoru |
| `--tools` / `--allowedTools` / `--disallowedTools` | Araç seviyesinde teknik kısıt |
| `--add-dir` | Context Pack dizinini okunur kılma |
| `--safe-mode` | CLAUDE.md, skills, plugins, hooks, MCP devre dışı |
| `--setting-sources user` | Repo içi `.claude/settings.json` etkisini kesme |
| `--strict-mcp-config` | MCP yüzeyini sıfırlama |
| `--system-prompt-file`, `--append-system-prompt-file` | Context Pack enjeksiyon kanalı |
| `--session-id <uuid>` | `ExecutionId` = sağlayıcı oturum kimliği |
| `--no-session-persistence` | Oturum sızıntısını engelleme |
| `-r/--resume`, `--fork-session` | Faz 10 onarım turu |
| `--model`, `--effort`, `--fallback-model` | Runtime profil parametreleri |
| `--debug-file` | Teşhis kanıtı |
| `--max-budget-usd` | **Bütçe buna dayandırılmaz** — eşzamanlılık=1 + günlük sayaç + timeout |
| `claude setup-token` | Loop Zero/Faz 6 için **gerekli değil**; yalnızca gözetimsiz/CI ihtiyacında |
| `--bare` | **KULLANILMAYACAK** — API key dayatır, abonelik oturumunu kırar |
| `-w, --worktree` | **KULLANILMAYACAK** — worktree sahipliği Hub'da |

### 0.2 Onaylanmış Faz 0 format kararları

| Karar | Değer | Not |
|---|---|---|
| **Workspace kökü** | `D:\cx\` | Windows yol uzunluğu için kısa kök zorunluluğu (G.1) |
| **Fixture kökü** | `D:\cx\_fixtures\` | Loop Zero |
| **`TaskKey` formatı** | `<PROJECT>-<3 hane>` | Örnek: `N360-014`, `KRN-007` |
| **Loop Zero fixture TaskKey** | **`HUB-001`** | Fixture örnekleri hiçbir gerçek ürüne bağlanmaz |

Bu kararlar **onaylanmıştır**; artık açık soru değildir.

---

## 0.5 v1 → v1.1 Düzeltmeleri (soy zinciri kaydı)

| # | Düzeltme | Özet |
|---|---|---|
| D1 | Kabul semantiği | `hub run` asla `Accepted`'a geçemez; kabul ayrı, açık, geri dönülemez karar |
| D2 | `pre-commit` çelişkisi | Daima-başarısız `pre-commit` kaldırıldı; push engeli çok katmanlı; Hub kendi korumasını atlatmaz |
| D3 | Soru sınıflandırması | Global bloklayıcı liste yerine faz kapıları; Faz 1–5 dış bağımlılıksız |
| D4 | Kabul kriteri modeli | `MACHINE`/`HUMAN` beyanı; "doğrulanamayan kriter = tanımsız görev" reddedildi |
| D5 | Kronos kapısı | Sert izolasyon olmadan Kronos onboard edilemez; kod ile zorlanır |
| D6 | Başarısız/kısmi commit | Koşullu commit; commit'siz kanıt; commit'siz worktree silinemez; minimal SecretGuard |
| D7 | Loop Zero küçük kalsın | Kapsam sabitlendi |

---

## 0.6 v1.1 → v1.1.1 Final Errata

### 0.6.1 E1 — Beklenmeyen agent commit'i artık BLOKLAYICIDIR

**Sorun.** v1.1, `HEAD != BaseCommitSha` durumunu yalnızca bir bulgu (`UnexpectedAgentCommit`) olarak kaydediyor ve akışı sürdürüyordu. Bu, commit-kapısı mimarisiyle çelişir: SecretGuard'ın Hub commit'inden **önce** çalışmasının tek sebebi, güvensiz üretilmiş içeriğin kalıcı git nesnesine dönüşmesini engellemektir. Agent kendi commit'ini attıysa **bu garanti zaten delinmiştir**; akışı sürdürmek, delinmiş bir garantinin üzerine doğrulama ve inceleme inşa etmek olur.

**Yeni semantik.** Agent adımından sonra `HEAD` ile `BaseCommitSha` karşılaştırılır. Farklıysa:

| Yapılır | Yapılmaz |
|---|---|
| Tüm kanıt toplanır | Hub result commit'i **atılmaz** |
| Commit grafiği ve beklenmeyen commit SHA'ları kaydedilir | Doğrulama **çalıştırılmaz** |
| `git status` kaydedilir | İnceleme **çalıştırılmaz** |
| `BaseCommitSha`'dan diff kaydedilir | Agent commit'i **asla** reset/rewrite/silme ile "düzeltilmez" |
| Minimal sır deseni taraması **teşhis amaçlı** çalıştırılır ve sayısı kaydedilir | Worktree **silinmez** |

Sonuç: **`Outcome = RuntimePolicyViolation`**, detay `UnexpectedAgentCommit`. Worktree korunur. Temizlik açık bir insan işidir.

> **Politika motoru değildir.** `RuntimePolicyViolation` tek bir sabit kontrolün (`HEAD == BaseCommitSha`) sonucudur. Kural DSL'i, koşul ifadesi veya genişletilebilir politika kümesi yoktur.

### 0.6.2 E2 — İnceleme override'ı / kabul semantiği

**Sorun.** v1.1'de `ReviewBlocked` Task'ı `Ready`'ye döndürüyordu, ancak J.4 `hub accept --override` ile inceleme kapısının aşılabileceğini yazıyordu. `hub accept` ise `AwaitingAcceptance` şartı arıyordu. Bu durum makinesi düzeyinde uygulanamaz bir çelişkiydi.

**Yeni semantik — iki ayrı kabul yolu:**

| Yol | Ön koşullar | Sonuç |
|---|---|---|
| **Normal kabul** `hub accept` | `Task == AwaitingAcceptance` **ve** son Execution `Outcome == Passed` | Tüm `HUMAN` verdictleri alınır → `AcceptanceDecision` (`OverrideUsed=false`) → `Accepted` |
| **İnceleme override kabulü** `hub accept --override --reason "..."` | `Task == Ready` **ve** son Execution `Completed(ReviewBlocked)` **ve** altı ön koşul (aşağıda) | Tüm `HUMAN` verdictleri alınır → `AcceptanceDecision` (`OverrideUsed=true`, `Reason` zorunlu) → `Accepted` |

Override'ın altı ön koşulu (hepsi zorunlu):
1. Uygulama adımı `Succeeded`,
2. ScopeGuard geçti,
3. SecretGuard geçti,
4. `required` makine doğrulaması geçti,
5. `ResultCommitSha` mevcut,
6. İnceleme **kullanılabilirdi** ve geçerli bloklayıcı bulgu(lar) üretti (`ReviewOutcome == Blocked`, en az bir bloklayıcı `Finding`).

**Değişmezler:**
- `Execution.Outcome` **`Passed`'e yeniden yazılmaz** — `ReviewBlocked` olarak kalır.
- Tüm bulgular korunur.
- Tek, değişmez `AcceptanceDecision` yazılır; `AuditEvent` üretilir.
- Task doğrudan `Ready → Accepted` geçer.

> Override'ın anlamı: *"İnsan geçerli bir bloklayıcı inceleyici görüşünü gördü ve sorumluluğu açıkça üstlendi."* **"İnceleyici yanılmıştı" anlamına gelmez.**

**Override'ın KESİNLİKLE kullanılamayacağı outcome'lar (kapalı küme, varsayılan reddet):**
`VerificationFailed` · `ScopeViolation` · `SecretGuardBlocked` · `RuntimePolicyViolation` · `ReviewUnusable` · `Failed` · `TimedOut` · `Cancelled` · `Interrupted` · `Passed` (aşılacak kapı yok).

> Makine gerçeği ve runtime bütünlüğü hataları, bir kabul override'ı ile başarılı işe dönüştürülemez.

### 0.6.3 E1/E2'nin mekanik olarak zorunlu kıldığı düzeltmeler

| # | Bulgu | Düzeltme |
|---|---|---|
| **M1** | `hub commit-result <execution> --override` komutu, `SecretGuardBlocked` bir execution'a commit attırıyordu. E2 bu outcome'un kabule taşınmasını yasakladığı için komutun hiçbir çıkışı kalmadı; ayrıca SecretGuard'ın varlık sebebini tek bayrakla iptal ediyordu | **Komut kaldırıldı.** Worktree zaten korunuyor; insan gerekirse worktree içinde elle çalışır. Böylece `--override` terimi tüm sistemde **tek bir anlam** taşır: inceleme kapısı kabul override'ı |
| **M2** | `ReviewUnusable` bir `ReviewOutcome` idi ama karşılık gelen bir `Execution.Outcome` değeri yoktu; E2 bunu override yasak listesine koyunca boşluk görünür oldu | `Execution.Outcome` enum'una **`ReviewUnusable`** eklendi |
| **M3** | `CaptureChanges` adımında kanıt toplama ile kapı değerlendirme sırası karışıktı (v1.1 G.4 önce anomali, sonra envanter) | Kesin sıra: **önce kanıt topla, sonra kapıları sırayla değerlendir** (G.4) |
| **M4** | Agent adımı başarısız **ve** `HEAD != Base` ise hangi outcome kazanır belirsizdi | Öncelik kuralı: **agent adımı terminal hatası outcome'u belirler**; beklenmeyen commit ek teşhis alanı olarak kaydedilir. Davranış (commit yok, doğrulama/inceleme yok, worktree korunur) her iki hâlde aynıdır |
| **M5** | Commit'siz outcome sayısı arttıkça korunan worktree birikimi görünmez kalıyordu | `hub run`, aynı Task'ın önceki denemelerinden korunan worktree varsa **uyarı satırı** basar. (Tek satır çıktı; alt sistem değil) |
| **M6** | `--override` J.4'te "her zaman kullanılabilir" gibi yazılmıştı | J.4 daraltıldı; kapalı küme + varsayılan reddet |

### 0.6.4 Değişen mimari kararlar (karar düzeyi)

| Karar | v1.1 | v1.1.1 | Sebep |
|---|---|---|---|
| Beklenmeyen agent commit'i | Bulgu; akış sürer | **`RuntimePolicyViolation` — bloklayıcı**; commit/doğrulama/inceleme yok | E1 |
| Agent commit'inin temizliği | (tanımsız) | Hub **asla** reset/rewrite/silme yapmaz; insan işi | E1 |
| Kabul override'ı | J.4'te belirsiz, uygulanamaz | Yalnızca `ReviewBlocked`; `Ready → Accepted` kenarı; kapalı küme | E2 |
| `Execution.Outcome` override sonrası | (ima: `Passed`) | **Değişmez, `ReviewBlocked` kalır** | E2 |
| `hub commit-result --override` | Vardı | **Kaldırıldı** | M1 |
| `ReviewUnusable` | Yalnızca `ReviewOutcome` | Ayrıca bir `Execution.Outcome` | M2 |
| Faz 0 formatları | Öneri | **Onaylandı** (`D:\cx\`, `<PROJECT>-<3 hane>`, fixture `HUB-001`) | Faz 0 |

---

## A. Yönetici Özeti

### A.1 CoreX Agent Hub nedir

**Coding agent'ları çalıştıran bir kontrol düzlemi (control plane).** Zekâ üretmez; zekâyı *sınırlar, besler, gözlemler ve kanıtlar.*

1. **İzolasyon** — Her agent çalışması, commit'i sabitlenmiş, tek kullanımlık bir git worktree'sine karşı yürür.
2. **Bağlam sahipliği** — Kurumsal hafıza platformda durur; her çalıştırma için deterministik, hash'lenmiş bir *Context Pack* üretilir.
3. **Kanıt** — Agent beyanı hiçbir şey ifade etmez. Build/test/diff/exit-code gerçeklerini **platform kendisi** üretir ve saklar.

### A.2 Hangi problemi çözüyor

Doğrulama maliyeti işin kendisinden pahalı · tekrar üretilebilirlik yok · organizasyonlar arası sızıntı riski insan disiplinine bağlı · bağlam anlatma maliyeti ürün sayısıyla doğrusal büyüyor.

### A.3 Ne DEĞİLDİR

Sohbet uygulaması değil · sanal çalışan simülasyonu değil · otonom yazılım fabrikası değil · SaaS değil · genel amaçlı workflow motoru değil · RAG platformu değil.

> **Değişiklik (ADR-0028, 2026-10-22):** "otonom yazılım fabrikası değil" geçerliliğini korur. Repository kapsamlı, varsayılanı kapalı bir otopilot eklendi: yalnızca hiç denenmemiş `Ready` görevleri sırayla başlatır ve yalnızca kanıtın her kriteri cevapladığı (insan kriteri olmayan, her makine kriteri `Met`, review gerçekten çalışmış ve temiz) işi politika kararı olarak kabul eder. Ana dala ekleme, yeniden deneme ve override insanda kalır.

### A.4 İlk değerli sonuç

> **45 dakikalık bir kod incelemesini, 5 dakikalık bir kanıt incelemesine indirmek.**

`base commit` + `unified diff` + `build/test exit code ve sayıları` + `kapı sonuçları` + `bağlam paketi hash'i` → tek ekranda, tekrar üretilebilir.

### A.5 En büyük varoluşsal risk

Hub, terminalde `claude` yazmaktan daha kötü bir deneyim üretirse ölür. Kazanma koşulu: *izolasyon + kanıt + tekrarlanabilirlik*.

---

## B. Mimari Değerlendirme — KEEP / CHANGE / DEFER / REJECT

| # | Öneri | Karar | Gerekçe |
|---|---|---|---|
| 1 | Local-first, tek makine, tek kullanıcı | **KEEP** | Dağıtık gereksinim yok |
| 2 | Evidence-first | **KEEP** | Varlık sebebi |
| 3 | İnsan tek kabul makamı | **KEEP + iki yollu (E2)** | Normal kabul + yalnızca `ReviewBlocked` için override |
| 4 | Git worktree ile görev izolasyonu | **KEEP** | En yüksek getirili tekil karar |
| 5 | Worktree'ler repo içinde | **CHANGE** | Kök repo dışında ve kısa: **`D:\cx\`** (onaylandı) |
| 6 | Sağlayıcıyı role sabitlememek | **KEEP** | `AgentProfile` |
| 7 | `IAgentRuntime` soyutlaması | **KEEP, daraltılmış** | 3 üyeli arayüz; ikinci implementasyon `ScriptedRuntime` |
| 8 | İlk dilimin Codex review içermesi | **CHANGE** | Dilim üçe bölündü (S) |
| 9 | Context Pack'in prompt bloğu olması | **CHANGE** | Diskte materyalize, hash'li |
| 10 | `Policy` ayrı varlık/motor | **REJECT** | E1'in getirdiği `RuntimePolicyViolation` da tek sabit kontroldür, motor değil |
| 11 | `AgentRun`/`Verification`/`Review` ayrı varlıklar | **CHANGE** | Tek `ExecutionStep` (tipli) |
| 12 | Agent'ın commit atması | **REJECT + BLOKLAYICI (E1)** | Tespit yetmez; `RuntimePolicyViolation` ile akış durur |
| 13 | 11 durumlu Task state machine | **CHANGE** | Task kaba, Execution ayrıntılı (P) |
| 14 | Repair loop | **DEFER → Faz 10** | MVP'de tek deneme |
| 15 | Şifreli secret store | **REJECT (MVP)** | Env allowlist + minimal SecretGuard kapısı |
| 16 | SQLite | **KEEP** | WAL + tek yazıcı + blob'lar dışarıda |
| 17 | .NET / ASP.NET Core | **KEEP** | Süreç kontrolü + P/Invoke |
| 18 | React + TypeScript UI | **KEEP, ERTELE → Faz 9** | İlk arayüz CLI |
| 19 | PostgreSQL / Redis / RabbitMQ / K8s | **REJECT** | Gereksinim yok |
| 20 | Vector DB / RAG | **REJECT (MVP)** | Bağlam = sürüm kontrollü markdown |
| 21 | Organizasyon izolasyonu | **KEEP + KRONOS KAPISI** | Mantıksal izolasyon yalnızca Core X Logic pilotu için (L.4) |
| 22 | Reviewer'ın yazamaması | **KEEP + güçlendir** | Araç kısıtı + atılabilir worktree + hash tespiti |
| 23 | Maliyet takibi | **DEFER → Faz 10** | Metadata saklanır |
| 24 | OTel / event bus | **DEFER** | `Activity` ile korelasyon |
| 25 | Otonomi (agent-to-agent) | **REJECT** | Risk |
| 26 | "Doğrulanamayan kriter = tanımsız görev" | **REJECT** | `MACHINE`/`HUMAN` beyanı modeli |

### B.1 Varsayımlara itirazlar

**İtiraz 1 — Asıl darboğaz görev tanımıdır.**

**Ready gate.** Bir Task `Draft`'tan çıkamaz, eğer: hedef repository yoksa · kapsam glob'ları yoksa · verification profile yoksa · en az bir kabul kriteri yoksa · **herhangi bir kriter `VerificationKind` beyan etmiyorsa** · `Machine` kriterin `VerificationRef`'i mevcut bir adım adına çözülmüyorsa.

> *"Nasıl doğrulanacağı beyan edilmemiş kabul kriteri = tanımsız görev."* `Human` beyanı tamamen geçerli bir cevaptır.

Uyarı (hata değil): Hiç `Machine` kriteri olmayan görev, verification profile'ının ötesinde otomatik sinyal üretmez.

**İtiraz 2 — Kurumsal hafıza platformda, ama hafıza inşa etmeyin.** Bağlam = organizasyon başına sürüm kontrollü markdown.

**İtiraz 3 — Bütçe sağlayıcı flag'ine dayandırılamaz.** `MaxConcurrentExecutions = 1`, günlük çalıştırma sayacı, üç seviyeli timeout.

**İtiraz 4 — Windows'ta read-only enforcement kolay değil.** Atılabilir worktree + yazma tespiti. Gerçek izolasyon Faz 10 ve **Kronos için zorunlu** (L.4).

---

## C. MVP Kapsamı

### C.1 KAPSAM İÇİ

1. Organizasyon / Proje / Repository kataloğu.
2. Task + Ready gate (`MACHINE`/`HUMAN`).
3. Execution yaşam döngüsü + tek yazıcı eşzamanlılık.
4. **Workspace Manager**: base commit sabitleme, worktree + `agent/...` branch, worktree-yerel sertleştirme, **koşullu** Hub commit, temizlik, yetim/kilit kurtarma.
5. **Process Runner**: Job Object ağaç öldürme, üç seviyeli timeout, async stream tüketimi, UTF-8/ANSI, kabuksuz argv, env allowlist.
6. **Context Pack Builder**: deterministik montaj + manifest + SHA-256.
7. **Agent Runtime**: `ScriptedAgentRuntime` (Faz 5) + `ClaudeCodeRuntime` (Faz 6).
8. **Verification Engine**: sıralı komut profili, exit code, TRX/JUnit sayaç, baseline önbelleği.
9. **Evidence Store**: içerik adresli blob + redaksiyon; commit'li ve commit'siz yollar.
10. **Kapılar**: RuntimePolicyGate (E1) · ScopeGuard · SecretGuard.
11. **Review Step** (Faz 7): read-only profil, `--json-schema`.
12. **Kabul**: normal (`AwaitingAcceptance`) + inceleme override'ı (`Ready`, yalnızca `ReviewBlocked`) + `HUMAN` verdictleri + audit.
13. Hub CLI.
14. Yapısal loglama + korelasyon.

### C.2 KAPSAM DIŞI (v1 MVP)

Policy DSL/motoru · workflow engine · otomatik onarım · otomatik merge/push/deploy · React UI (Faz 9) · REST API (Faz 9) · maliyet raporu (Faz 10) · çoklu kullanıcı · authz · vector DB · Codex (Faz 8) · çoklu repo görevleri · uzak worker · gözetimsiz/CI çalıştırma · WSL sandbox (Faz 10) · **Kronos onboarding (L.4)** · MCP yönetimi · submodule/LFS · bildirimler · şifreli secret store · gelişmiş sır tarama · **Hub tarafından otomatik git geçmişi onarımı (E1: kalıcı yasak)**.

---

## D. Domain Modeli

**15 tablo, 3 aggregate.**

### D.1 Aggregate: `Repository` (katalog)

| # | Varlık | Sorumluluk |
|---|---|---|
| 1 | `Organization` | slug, filesystem root, env allowlist, **`IsolationLevel: Logical\|Hard`**, **`RequiresHardIsolation`**, `Status: Active\|Blocked` |
| 2 | `Project` | Ürün gruplaması |
| 3 | `Repository` | Yerel yol, default branch, protected branch'ler, verification profile ref, kapsam kuralları |
| 4 | `VerificationProfile` | Sıralı komut adımları (`StepsJson`) |
| 5 | `AgentProfile` | rol + provider + runtime + izin profili + model + limitler |

**Kronos zorlaması (D5):** `RequiresHardIsolation && IsolationLevel == Logical` → `Status = Blocked`; bloke organizasyon execution başlatamaz. Kod değişmezi, belge vaadi değil (L.4).

### D.2 Aggregate: `Task`

| # | Varlık | Alanlar |
|---|---|---|
| 6 | `Task` | `TaskKey` (**`<PROJECT>-<3 hane>`**), org/project/repository, `Objective`, `AllowedScope[]`, `ForbiddenScope[]`, `State`, `BlockReason?`, `Version` |
| 7 | `AcceptanceCriterion` | `TaskId`, `Order`, `Text`, `VerificationKind: Machine\|Human`, `VerificationRef?`, `HumanCheckHint?` |

### D.3 Aggregate: `Execution`

| # | Varlık | Alanlar |
|---|---|---|
| 8 | `Execution` | `AttemptNo`, `BaseCommitSha`, `BranchName`, `WorktreePath`, `WorkspaceState`, `ContextPackHash`, `State`, **`Outcome`**, `ResultCommitSha?`, **`UnexpectedCommitShas?`**, **`UnexpectedAgentCommitDetected: bool`**, `PreviousExecutionId?`, `Lease` |
| 9 | `ExecutionStep` | `PrepareWorkspace \| BuildContext \| Agent \| CaptureChanges \| Verify \| Review` |
| 10 | `CommandResult` | executable, argv, exitCode, süre, çıktı blob ref'leri |
| 11 | `Finding` | severity, kategori, dosya, satır, başlık, gerekçe, confidence, outOfScope |
| 12 | `EvidenceArtifact` | sha256, boyut, tür, redaksiyon sayısı |
| 13 | `AcceptanceDecision` | **Değişmez**: `ExecutionId`, `Decision`, `Actor`, `Reason?`, **`OverrideUsed`**, `HumanCriterionVerdictsJson`, `DecidedAtUtc` |
| 14 | `AuditEvent` | Append-only |
| 15 | `OrchestrationEvent` | Zaman çizelgesi satırları (blob yok) |

> **(E2)** İnceleme override'ı için **yeni tablo yoktur.** `AcceptanceDecision` zaten `OverrideUsed`, `Reason`, `HumanCriterionVerdicts`, `Actor`, `DecidedAtUtc` taşır.

### D.4 Value object'ler

`GitRef` · `CommitSha` · `ScopeGlob` · `Severity` · `PermissionProfile` · `Duration` · `ContentHash` · `VerificationKind`.

### D.5 Modellenmeyenler

`ContextPack` → içerik adresli artifact · `Policy` → kod + repo yapılandırması · `Approval` → `AcceptanceDecision` · `AgentRun` → `ExecutionStep(type=Agent)` · `Human` verdictleri → `AcceptanceDecision` üzerinde JSON.

### D.6 Bounded context'ler

`Catalog` → `Orchestration` → `Workspace` · `Runtime` · `Verification` · `Evidence` · `Audit`. Tek yönlü bağımlılık.

---

## E. Bileşen Mimarisi

```
┌──────────────────────────────────────────────────────────────┐
│ Sunum:  CoreX.Hub.Cli (Faz 1–8) · [Faz 9] Api + React SPA    │
└──────────────────────────┬───────────────────────────────────┘
┌──────────────────────────▼───────────────────────────────────┐
│ Application — ExecutionOrchestrator (tek durum sahibi)       │
│   TaskService · AcceptanceService · ExecutionQueue           │
│   RuntimePolicyGate(E1) · ScopeGuard · SecretGuard · Budget  │
└───┬───────────┬────────────┬───────────┬────────────┬────────┘
┌───▼─────┐ ┌───▼──────┐ ┌───▼──────┐ ┌──▼───────┐ ┌──▼──────────┐
│Workspace│ │ Context  │ │  Agent   │ │ Verify   │ │ Evidence    │
│ Manager │ │ Builder  │ │ Runtimes │ │ Engine   │ │(blob+redact)│
└───┬─────┘ └──────────┘ └────┬─────┘ └────┬─────┘ └──────┬──────┘
    └─────────┬───────────────┴────────────┘  desen kümesi─┘
   ┌──────────▼───────────────────────────┐  (SecretGuard ortak)
   │ ProcessRunner (Job Object, timeout,  │  ← TEK süreç çıkışı
   │ stream tüketimi, env allowlist)      │
   └──────────┬───────────────────────────┘
   ┌──────────▼────────────────────────────┐
   │ FileSystemGuard (kanonik yol, org kökü,│ ← TEK dosya sistemi çıkışı
   │ reparse-point kaçış denetimi)          │
   └────────────────────────────────────────┘
   Persistence: EF Core + SQLite (WAL) | Blob: içerik adresli depo
```

### E.1 Mimari değişmezler (CI'da test edilir)

| # | Değişmez |
|---|---|
| 1 | Süreç başlatan tek yer `ProcessRunner`'dır — başka `Process.Start` yok |
| 2 | Dosya sistemi yolu çözen tek yer `FileSystemGuard`'dır |
| 3 | **Kabuk yoktur** — her çalıştırma `executable + string[] argv` |
| 4 | Alt katmanlar Application'ı ve birbirini bilmez |
| 5 | Agent runtime git bilmez |
| 6 | **(D1)** `Task.State = Accepted` yazan tek yer `AcceptanceService`'tir; `ExecutionOrchestrator`'da o geçişe kod yolu yoktur |
| 7 | **(D6)** `git commit` çağıran tek yer `WorkspaceManager.CreateResultCommit`'tir; yalnızca üç kapı geçtikten sonra çağrılabilir |
| 8 | **(D2)** Hub kendi korumasını atlatan git bayrağı kullanmaz: `--no-verify`, `-c core.hooksPath=`, `--force` yasak |
| 9 | **(E1)** Hub, agent'ın oluşturduğu commit'i **asla** onarmaz/siler: `git reset`, `git branch -f`, `git update-ref`, `git commit --amend`, `git rebase`, `git push --force` çağrısı kod tabanında bulunmaz |
| 10 | **(E2)** Kabul override'ı **kapalı kümedir**: yalnızca `Outcome == ReviewBlocked`. Uygulama `switch` + açık `default: deny` şeklindedir; birim testi tüm diğer outcome değerlerinin reddedildiğini doğrular (enum büyüse bile varsayılan reddet) |
| 11 | **(E2)** `AcceptanceService` hiçbir zaman `Execution.Outcome` yazmaz |

---

## F. Agent Runtime Mimarisi

### F.1 Ortak soyutlama

```
IAgentRuntime
  Capabilities Describe()
  Task<AgentRunResult> RunAsync(AgentRunRequest req, IProgress<AgentEvent> sink, CancellationToken ct)
  Task<HealthResult> CheckHealthAsync(CancellationToken ct)
```

`AgentRunRequest`: `WorkingDirectory` · `SystemPromptFile` · `AppendSystemPromptFile` · `UserPrompt` · `PermissionProfile` · `ModelHint` · `EffortHint` · `StructuredOutputSchema?` · `StructuredOutputPath` · `EnvironmentAllowlist` · `SoftTimeout` · `IdleTimeout` · `HardTimeout` · `CorrelationId` · `SessionId?` · `ResumeSessionId?`

`AgentRunResult`: `Outcome (Completed/Failed/TimedOut/Cancelled/Crashed)` · `ExitCode` · `ProviderSessionId` · `StructuredOutput?` · `RawStdoutBlobRef` · `RawStderrBlobRef` · `UsageMetadata` · `Duration`

`Capabilities`: `SupportsStructuredSchema` · `SupportsSessionResume` · `SupportsToolAllowlist` · `SupportsCostCap` · `SupportsStreaming` · `SupportsReadOnlyMode`

> **Farkları saklama, ilan et.** Eksik yetenek telafi edilmez; adım `Unsupported` işaretlenir.

### F.2 `ScriptedAgentRuntime` (Faz 5)

Modlar: `apply-patch` · `no-op` · `out-of-scope-write` · `secret-write` · `hang` · `crash` · `partial-write` · **`unexpected-commit`** (E1 ile artık `RuntimePolicyViolation` üretmesi beklenir).

Neden ilk sırada: pipeline'ı, durum makinelerini, üç kapıyı, kanıt akışını ve kurtarmayı **ağsız, AI'sız, CI'da deterministik** sınanabilir kılar; `IAgentRuntime` üzerinde ikinci implementasyon baskısını erken yaratır.

### F.3 `ClaudeCodeRuntime` (Faz 6)

**CLI subprocess.** .NET için resmî Claude Agent SDK yok; CLI zaten yapısal çıktı, şema doğrulama, izin modu, araç allowlist ve oturum kimliği sunuyor.

**Kimlik doğrulama (D3):** Makinede **zaten açık olan abonelik oturumu** kullanılır. API-key faturalandırma mimarisi tasarlanmaz. `setup-token` yalnızca gözetimsiz/CI/uzak worker gerekirse. `--bare` kullanılmaz.

Uygulayıcı profili:

```
claude -p
  --output-format stream-json  --session-id <execution-uuid>  --no-session-persistence
  --safe-mode  --setting-sources user  --strict-mcp-config
  --system-prompt-file <pack>/system.md
  --append-system-prompt-file <pack>/rules.md
  --tools Read,Edit,Write,Glob,Grep,Bash
  --disallowedTools "Bash(git commit*)" "Bash(git push*)" "Bash(git remote*)" "Bash(git config*)"
  --permission-mode acceptEdits  --model <profil>  --effort <profil>
  --add-dir <context-pack-dir>  --debug-file <evidence>/claude-debug.log
```

> **(E1)** `--disallowedTools` ile `git commit` engellemesi bir **runtime politikasıdır, güvenlik sınırı değildir.** Atlatılırsa sonuç bir bulgu değil, **bloklayıcı outcome**'dur (`RuntimePolicyViolation`).

İnceleyici profili (Faz 7):

```
claude -p --output-format json --json-schema <findings-schema.json>
  --safe-mode --setting-sources user --strict-mcp-config
  --tools Read,Glob,Grep          # Edit/Write/Bash YOK
  --permission-mode plan
  --system-prompt-file <pack>/reviewer.md
```

**Faz 6 kapıları:** G6-1, G6-2, G6-3 (V.2). Ham stdout her zaman blob olarak saklanır; ayrıştırma başarısız olsa bile kanıt kaybolmaz.

### F.4 `CodexRuntime` (Faz 8)

Codex CLI kurulu değil → Faz 8. Doğrulanacaklar: non-interactive yüzey, yapısal çıktı, sandbox modları, onay bayrakları, oturum devamı, usage metadata, **Windows'ta native sandbox**. Yoksa WSL2 üzerinden + yol çevirimi (`D:\cx\w\x` ↔ `/mnt/d/cx/w/x`). Kimlik: mevcut ChatGPT/Codex oturumu.

**Ara çözüm (Faz 7):** İnceleyici, farklı profilli ikinci bir Claude Code çalıştırması olur.

### F.5 Süreç yaşam döngüsü — Windows'a özgü kritik detaylar

1. **Süreç ağacı öldürme:** Windows **Job Object** — `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE` + `AssignProcessToJobObject`; kotalar `ActiveProcessLimit`, `JobMemoryLimit`. Yarış koşulu Faz 10'da `CreateProcess` + `CREATE_SUSPENDED` ile kapatılır.
2. **Stdout/stderr kilitlenmesi:** her iki akış asenkron ve eşzamanlı tüketilir.
3. **Kodlama:** UTF-8 zorlanır; ANSI temizlenir (ham hâli ayrı blob).
4. **`.cmd`/`.bat` tuzağı:** gerçek çalıştırılabiliri çöz (`node <...>/npm-cli.js`); zorunlu hâlde `cmd.exe /c` + katı argüman beyaz listesi.
5. **Üç seviyeli timeout:** *soft* → *idle* (asılı testin tek göstergesi) → *hard* (job kapat).
6. **Env allowlist:** boş ortamdan başla; `GIT_TERMINAL_PROMPT=0`, `GIT_ASKPASS=<no-op>`, `NO_COLOR=1`, `CI=1`.
7. **İptal:** stdin kapat → grace → job kapat. Kısmi değişiklikler kanıtlanır, commit edilmez.

---

## G. Git / Worktree Stratejisi

### G.1 Yerleşim

```
D:\cx\                                  ← onaylanmış workspace kökü
  corexlogic\
    nakliye360\
      N360-014-1\                       (taskKey-attempt)
  _fixtures\
    hub-sample\
      HUB-001-1\                        ← Loop Zero fixture
```

Windows uzun yol desteği açık; yönetilen repolarda `git config core.longpaths true`; Defender'da `D:\cx` istisnası.

### G.2 Hazırlık akışı

```
1. git rev-parse --git-dir                      (repo geçerli mi)
2. Submodule / LFS varsa MVP'de REDDET
3. git rev-parse <defaultBranch>                → BaseCommitSha
4. Branch adı: agent/<taskKey>/<attempt>
5. git worktree add -b <branch> <path> <BaseCommitSha>
6. Worktree-yerel sertleştirme:
     git -C <wt> config core.hooksPath <hub-hooks-dir>
        └── İÇERİK: pre-push (daima exit 1).  pre-commit YOK (D2)
     git -C <wt> config remote.origin.pushurl "corex-hub://push-disabled"
     git -C <wt> config credential.helper ""
7. Kayıt: BaseCommitSha, branch, path, zaman
```

**Kritik:** worktree bir **commit'ten** oluşturulur. İki bedava kazanç: kirli ana dizin sorun değil; `.gitignore`'daki `.env`/sertifikalar worktree'de hiç bulunmaz.

**Yan kazanç:** `core.hooksPath` Hub dizinine yönlendirildiği için **repoya commit edilmiş git hook'ları da devre dışı kalır** — keyfî kod çalıştırma yüzeyini kapatır.

### G.3 Korumalı branch'ler ve yayınlama engeli

| Katman | Mekanizma | Ne engeller |
|---|---|---|
| Yapısal | Git aynı branch'i iki worktree'de checkout etmez; daima yeni `agent/...` | Ana branch'in bozulması |
| Doğrulama | `Repository.ProtectedBranches` + orchestrator kontrolü | Yanlış branch hedefi |
| **Hook** | Worktree-yerel `pre-push` daima exit 1 | **Uzağa yayınlama** |
| Yapılandırma | `remote.origin.pushurl` yönlendirilemez; credential helper boş | Uzağa yayınlama (2. kat) |
| Kod | Hub'da push/merge/deploy kod yolu **hiç yazılmaz** | Hub'dan yayınlama |
| Runtime politikası | `--disallowedTools "Bash(git push*)" "Bash(git commit*)" …` | Agent'ın git kullanması (yumuşak) |
| **Sonuç kapısı (E1)** | `HEAD != BaseCommitSha` → `RuntimePolicyViolation` | Politika ihlalinin **sessizce sürmesi** |

> **Açık ayrım (D2 + E1):** *Agent'ın commit atmasını engellemek* bir **runtime politikasıdır** — yumuşaktır ve atlatılabilir; ama atlatıldığında **akış durur** (E1). *Kazara uzağa yayınlamayı engellemek* daha güçlü bir güvenlik gereksinimidir ve beş bağımsız katmanla sağlanır. `pre-commit` hook'u yoktur çünkü **Hub kendi korumasını atlatmak zorunda kalmamalıdır** (E.1 #8).

### G.4 `CaptureChanges` adımı — kanıt önce, sonra kapılar (E1 + M3)

```
ADIM 1 — KANIT (her yolda çalışır, hiçbir koşula bağlı değil)
  git -C <wt> rev-parse HEAD
  git -C <wt> status --porcelain=v2 --untracked-files=all         → GitStatus
  git -C <wt> log --format=%H%x09%an%x09%ad%x09%s <Base>..HEAD    → GitCommitGraph
  git -C <wt> add -N .                       (untracked'ler diff'te görünsün)
  git -C <wt> diff <BaseCommitSha>                                 → GitWorktreeDiff
  dosya envanteri: yol · durum · boyut · sha256                    → FileInventory
  minimal sır deseni taraması (teşhis)                             → SecretPatternHits: n

ADIM 2 — KAPILAR (sırayla; ilk başarısız olan outcome'u belirler)
  K0  Agent adımı Outcome == Succeeded ?
        HAYIR → Failed / TimedOut / Cancelled / Interrupted
                (M4: HEAD != Base ise ek olarak UnexpectedAgentCommitDetected = true)
  K1  HEAD == BaseCommitSha ?                                        ← E1
        HAYIR → RuntimePolicyViolation  (detail: UnexpectedAgentCommit)
                UnexpectedCommitShas kaydedilir
  K2  ScopeGuard: değişen yollar ⊆ AllowedScope ?
        HAYIR → ScopeViolation
  K3  SecretGuard: diff'in EKLENEN satırlarında sır deseni ?
        VARSA → SecretGuardBlocked

ADIM 3 — COMMIT (yalnızca K0–K3 hepsi geçtiyse)
  git -C <wt> add -A
  git -C <wt> -c user.name="CoreX Agent Hub" \
              -c user.email="agent@corex.local" \
              commit -m "agent: <taskKey> attempt <n>"
  → ResultCommitSha
  → git -C <wt> diff <BaseCommitSha>..HEAD                          → GitCommitDiff
```

Kapı sonuçları:

| Kapı | Execution Outcome | Result commit | Doğrulama | İnceleme | Worktree |
|---|---|---|---|---|---|
| K0 başarısız | `Failed` / `TimedOut` / `Cancelled` / `Interrupted` | **Yok** | Hayır | Hayır | **Korunur** |
| **K1 başarısız (E1)** | **`RuntimePolicyViolation`** | **Yok** | **Hayır** | **Hayır** | **Korunur** |
| K2 başarısız | `ScopeViolation` | **Yok** | Hayır | Hayır | **Korunur** |
| K3 başarısız | `SecretGuardBlocked` | **Yok** | Hayır | Hayır | **Korunur** |
| Hepsi geçti | (devam) | **Var** | Evet | Evet (doğrulama geçerse) | Kabul/ret sonrası release |

**Neden K1 bloklayıcı (E1):** SecretGuard'ın commit'ten önce çalışmasının tek amacı güvensiz içeriğin kalıcı git nesnesine dönüşmesini engellemektir. Agent kendi commit'ini attıysa bu garanti **zaten delinmiştir**; üzerine doğrulama ve inceleme inşa etmek, delinmiş bir temele bina yapmaktır.

**(E1) Kalıcı yasak:** Hub, agent'ın oluşturduğu commit'i **asla** `reset`, `branch -f`, `update-ref`, `amend`, `rebase` veya `push --force` ile "düzeltmez". Kanıt önce; worktree korunur; temizlik açık bir insan işidir (E.1 #9).

**SecretGuard kapsamı — kasıtlı olarak küçük:**
- Yalnızca **eklenen diff satırları**; repoda zaten var olan sırlar bloklamaz.
- Desen kümesi **redaksiyon ile ortaktır** (tek kaynak): `sk-…`, `ghp_…`, `AKIA…`, JWT şekli, `-----BEGIN … PRIVATE KEY-----`, `Password=`/`Pwd=` bağlantı dizeleri, `Authorization:` başlıkları.
- Entropi analizi, ML, baseline dosyası, vault, rotasyon **yok**.
- **(M1)** `hub commit-result --override` komutu **yoktur**. `SecretGuardBlocked` bir execution ne commit'e ne kabule taşınabilir. Worktree korunduğu için hiçbir çalışma kaybolmaz; insan gerekirse worktree içinde elle çalışır.

### G.5 Eşzamanlılık

Farklı repolar paralel; `git worktree add/remove` repo başına serileştirilir (named mutex). Yönetilen repolarda `gc.auto 0`; bakım execution yokken. MVP: `MaxConcurrentExecutions = 1`.

### G.6 Temizlik, yetim ve çökme kurtarma

> **Değişmez (D6):** `ResultCommitSha == null` olan bir execution'ın worktree'si **otomatik kaldırılamaz.**

| Outcome / durum | Commit | Worktree |
|---|---|---|
| `Passed` + kabul/ret verildi | Var | `remove`, branch korunur, `Released` |
| `VerificationFailed` | Var | Release edilebilir |
| `ReviewBlocked` (override ile kabul edilse de edilmese de) | Var | Release edilebilir |
| `ReviewUnusable` (M2) | Var | Release edilebilir |
| `ScopeViolation` | **Yok** | **Korunur** — `hub workspace release <execution> --confirm` |
| `SecretGuardBlocked` | **Yok** | **Korunur** — aynı |
| **`RuntimePolicyViolation` (E1)** | **Yok** | **Korunur** — aynı; agent commit'i branch'te durur, dokunulmaz |
| `Failed` / `TimedOut` / `Cancelled` / `Interrupted` | **Yok** | **Korunur** |
| `worktree remove` başarısız | — | `dotnet build-server shutdown` → job kill → 3 artan gecikmeli deneme → `Orphaned`; akış bloklanmaz |
| Hub çöktü / restart | — | Reconciler: non-terminal → `Interrupted`; disk ↔ DB karşılaştırması; kayıtsız worktree'ler `Orphaned` raporlanır, **silinmez** |

Komutlar: `hub workspace reconcile` · `hub workspace release <execution> --confirm` · `hub workspace purge-orphans --confirm`. **Yıkıcı işlem daima açık bayrak ister.**
**(M5)** `hub run`, aynı Task'ın önceki denemelerinden korunan worktree varsa uyarı satırı basar.

---

## H. Context Mimarisi

### H.1 Kaynaklar ve öncelik

```
1. Task talimatı (objective, scope, kriterler + VerificationKind)
2. Repository kuralları     (context repo)
3. Project kuralları        (context repo)
4. Organization kuralları   (context repo)
5. Hub global sözleşmesi    (gömülü)                ← DEĞİŞTİRİLEMEZ
6. Canlı git gerçekleri     (base commit, branch)
```

Katman 5 üst katmanlarca ezilemez: Hub sözleşmesi `--system-prompt-file`, proje kuralları `--append-system-prompt-file`.

### H.2 Depolama

Organizasyon başına git reposu (`corex-context-corexlogic`). Markdown. Vector DB/embedding/chunking yok.

### H.3 Materyalizasyon

```
<evidence>/<executionId>/context/
  system.md · rules.md · task.md · repo-state.json · decisions/ · manifest.json
```

`--add-dir` ile okunur kılınır; `ContextPackHash` execution'a yazılır.

### H.4 Repo içi `CLAUDE.md` / `AGENTS.md`

**Güvenilmeyen girdidir.** `--safe-mode` ile devre dışı. Faydalı bir repo kuralı varsa **context repoya açıkça alınır** — güven kayıtlı bir karar olur.

### H.5 Boyut kontrolü

Sert bütçe: **64 KB**. Aşılırsa build başarısız — **sessiz kırpma yasak**. `decisions/` kapsam/etiket filtresinden geçer. Geçmiş görev kanıtı pack'e girmez.

---

## I. Doğrulama ve Kanıt Modeli

### I.1 Doğruluk kademeleri

| Kademe | İçerik | Güven | **Otomatik** outcome'u etkiler mi? | Nihai insan kabulünü etkiler mi? |
|---|---|---|---|---|
| **A — Makine gerçeği** | exit code, SHA'lar, dosya hash'i, diff, süre, test sayaçları, K0–K3 kapı sonuçları | Otoriter | **Evet** | Evet |
| **B — İnceleyici bulgusu** | şema doğrulamalı `Finding[]` | Yapısal görüş | Evet — yalnızca yapılandırılmış kapı kurallarıyla | Evet |
| **C — Agent beyanı** | serbest metin özeti | **Sıfır** | **Hayır — asla** | **Hayır — asla** |
| **D — İnsan değerlendirmesi** | `Human` kriter verdictleri **ve inceleme override kararı (E2)** | İnsan otoritesi | **Hayır** | **Evet — yalnızca burada** |

**Değişmez:** `Execution.Outcome` yalnızca Kademe A ve yapılandırılmış Kademe B kapılarından hesaplanır. **(E2)** İnceleme override'ı bir Kademe D eylemidir; Kademe A/B kayıtlarını değiştirmez, `Execution.Outcome`'u yeniden yazmaz.

### I.2 Kabul kriterlerinin kanıta bağlanması

| Kriter türü | Nasıl değerlendirilir | Nerede görünür |
|---|---|---|
| `Machine` (`VerificationRef = "test"`) | İlgili doğrulama adımının sonucu kriterin sonucudur | Kanıt özetinde otomatik ✔/✘ |
| `Machine`, adım `required: false` ve FAIL | Outcome `Passed` kalabilir; kriter karşılanmadı gösterilir | Kabul ekranında uyarı |
| `Human` | Kabul anında insan verdict verir: `Met`/`NotMet`/`NotApplicable` | `AcceptanceDecision.HumanCriterionVerdictsJson` |

Eşleme tek bir string referansıdır. **Kural motoru yoktur.**

### I.3 Verification profile

```yaml
verification:
  workingDirectory: "."
  steps:
    - { name: restore, executable: dotnet, args: ["restore"], timeoutSeconds: 300, required: true }
    - { name: build,   executable: dotnet, args: ["build","--no-restore","-warnaserror"], timeoutSeconds: 600, required: true }
    - { name: test,    executable: dotnet, args: ["test","--no-build","--logger","trx;LogFileName=results.trx"],
        timeoutSeconds: 1800, required: true, resultParser: trx }
```

- `executable` allowlist'ten gelir, mutlak yola çözülür. `args` dizidir; kabuk yok.
- **Doğrulama uygunluğu:** Verification **yalnızca** K0–K3 kapılarının tamamı geçtiyse ve `ResultCommitSha` varsa çalışır. Kısmi çalışmanın manuel doğrulanması Faz 10'da opsiyonel `hub verify <execution>` ile ele alınır.

### I.4 Baseline

`(RepositoryId, BaseCommitSha, ProfileHash)` önbelleği:

```
test: base 840 passed / 2 failed  →  after 842 passed / 0 failed   (İYİLEŞME)
```

### I.5 Kanıt deposu

Blob'lar içerik adresli `blobs/<sha256[0:2]>/<sha256>`, atomik yazım.

`EvidenceArtifact.kind`: `ContextPack | AgentStdout | AgentStderr | AgentStructuredResult | GitStatus | **GitCommitGraph** | GitWorktreeDiff | GitCommitDiff | FileInventory | CommandStdout | CommandStderr | TestReport | ReviewFindings | DebugLog`.

**Commit'siz execution'da bile toplanan asgari kanıt:**

| Kanıt | Kaynak |
|---|---|
| `GitStatus` | `git status --porcelain=v2 --untracked-files=all` |
| **`GitCommitGraph` (E1)** | `git log --format=… <Base>..HEAD` + `UnexpectedCommitShas` |
| `GitWorktreeDiff` | `git add -N .` + `git diff <BaseCommitSha>` |
| `FileInventory` | yol · durum · boyut · sha256 |
| Hash'ler | `BaseCommitSha`, `ContextPackHash`, diff blob sha256 |
| `AgentStdout`/`AgentStderr`/`DebugLog` | ProcessRunner |
| Kapı sonucu | Hangi kapı, hangi yol/desen (**sırrın kendisi değil**), `SecretPatternHits` |

`.gitignore` kapsamındaki dosyalar `git add -N` tarafından atlanır → yok sayılan sırlar kanıta girmez. Redaksiyon yazma anında yapılır. SQLite'a blob konmaz.

### I.6 Kanıt özeti — üç örnek

**(a) Loop Zero, başarılı** *(Faz 6'da "Implementation" satırı `Claude Code 2.1.240 · opus · effort=high` olur)*

```
Task HUB-001 · Execution #1
Base       9f3c1a2  (main)          Branch  agent/HUB-001/1
Result     4b77e01                  Context 3af9…  (18 dosya, 22 KB)
Worktree   D:\cx\_fixtures\hub-sample\HUB-001-1   (Active)

Implementation   ScriptedAgentRuntime (apply-patch) · 1.4s
Kapılar          K0 Agent PASS · K1 RuntimePolicy PASS · K2 Scope PASS · K3 Secret PASS
Scope            4 dosya değişti

Verification
  restore  PASS 0  18s
  build    PASS 0  41s
  test     PASS 0  12s     12 passed / 0 failed  (baseline: 11/1)

Kabul kriterleri
  [MACHINE] "Tüm birim testleri geçer"   → verify:test   ✔
  [MACHINE] "Build uyarısız derlenir"     → verify:build  ✔
  [HUMAN]   "Değişiklik fixture senaryosuna uygun"  → insan değerlendirmesi bekliyor

Execution Outcome   Passed
Task State          AwaitingAcceptance      ← hub run BURADA durur
Sonraki adım        hub accept HUB-001#1
```

**(b) Beklenmeyen agent commit'i (E1)**

```
Task HUB-001 · Execution #2
Base       9f3c1a2                  Branch  agent/HUB-001/2
Result commit       YOK
Kapılar             K0 PASS · K1 RuntimePolicy **FAIL** · K2 çalıştırılmadı · K3 çalıştırılmadı
Beklenmeyen commit  1 adet: 7ce4d90 "wip: agent commit"
Teşhis              SecretPatternHits: 0   (yalnızca teşhis, outcome'u değiştirmez)
Doğrulama           ÇALIŞTIRILMADI
İnceleme            ÇALIŞTIRILMADI
Worktree            KORUNDU — agent commit'ine dokunulmadı
Execution Outcome   RuntimePolicyViolation  (UnexpectedAgentCommit)
Task State          Ready
Kabul override      KULLANILAMAZ
```

**(c) İnceleme bloklu, insan override ile kabul etti (E2)**

```
Task N360-014 · Execution #3
Result     4b77e01                  Kapılar  K0–K3 hepsi PASS
Verification  build PASS · test PASS (842/0)
Review        Critical 1 · Major 0 · Minor 2      ReviewOutcome: Blocked (kullanılabilir)

Execution Outcome   ReviewBlocked        ← DEĞİŞTİRİLMEDİ
Task State          Accepted
AcceptanceDecision  OverrideUsed = true
                    Reason: "Critical bulgu bilinen legacy davranış; ayrı görev açıldı N360-021"
                    HumanCriterionVerdicts: [ {c3: Met} ]
                    Actor: cafer   ·   2026-08-22T14:20:11Z
Bulgular            KORUNDU (silinmedi, bastırılmadı)
```

---

## J. İnceleme (Review) Modeli — Faz 7

### J.1 Salt-okunurluk

1. **Atılabilir worktree** (result commit'ten oluşturulur), inceleme sonrası koşulsuz silinir.
2. **Değişiklik tespiti** — öncesi/sonrası ağaç hash'i; fark varsa `ReviewerTamperDetected` → inceleme **geçersiz** (`ReviewUnusable`).
3. **Araç kısıtı** — `--tools Read,Glob,Grep`.
4. **İzin modu** — `--permission-mode plan`.
5. **Prompt** — en zayıf katman, yalnızca üsttekilerin üzerine.

> İnceleme **yalnızca** `ResultCommitSha` varsa çalıştırılır. Commit yoksa (K0–K3 kapılarından biri başarısız) outcome zaten terminaldir.

### J.2 Diff-anchored, on-demand okuma

Unified diff + salt-okunur worktree. Bulgular diff'e çıpalanır; değişmemiş dosya bulguları `outOfScope: true` ile işaretlenir ve **bloklamaz**.

### J.3 Bulgu şeması

```json
{
  "findings": [{
    "severity": "Critical|Major|Minor|Info",
    "category": "correctness|security|architecture|performance|test-coverage|convention",
    "file": "src/...", "line": 42,
    "title": "≤80 karakter", "rationale": "neden yanlış",
    "suggestedFix": "opsiyonel", "confidence": "high|medium|low",
    "outOfScope": false
  }],
  "summary": "≤500 karakter"
}
```

`--json-schema` runtime'da zorlar. Şema ihlali → bir kez yeniden dene → yine ihlal → Review adımı `Failed`, **`Execution.Outcome = ReviewUnusable`** (M2). Serbest metin ayrıştırma yok.

### J.4 Kapılar ve override (E2 ile daraltıldı)

| Koşul | Execution Outcome | İnceleme çalışır mı? | Kabul override'ı? |
|---|---|---|---|
| K0 başarısız | `Failed`/`TimedOut`/`Cancelled`/`Interrupted` | Hayır | **HAYIR** |
| K1 başarısız (E1) | `RuntimePolicyViolation` | Hayır | **HAYIR** |
| K2 başarısız | `ScopeViolation` | Hayır | **HAYIR** |
| K3 başarısız | `SecretGuardBlocked` | Hayır | **HAYIR** |
| `required` doğrulama FAIL | `VerificationFailed` | Hayır | **HAYIR** |
| Şema/kurcalama sorunu | `ReviewUnusable` | Çalıştı ama kullanılamaz | **HAYIR** |
| `Critical` ≥ 1 | `ReviewBlocked` | Evet | **EVET — tek istisna** |
| `Major` ≥ eşik (varsayılan 1) | `ReviewBlocked` | Evet | **EVET — tek istisna** |
| Yalnızca Minor/Info | `Passed` | Evet | Gereksiz (aşılacak kapı yok) → reddedilir |

**(E2) Override yalnızca `ReviewBlocked` içindir.** Kullanımı: `hub accept --override --reason "<zorunlu>"`. Altı ön koşul P.5'te; sessiz override yoktur; `AuditEvent` zorunludur.

**(D4)** Kabul kriterleri bu tabloya girmez: `Machine` kriterleri doğrulama adımlarının görünümüdür, `Human` kriterleri yalnızca kabul anında değerlendirilir.

---

## K. Güvenlik Tehdit Modeli (MVP)

### K.1 Varlıklar

Kaynak kodu · commit edilmiş sırlar · geliştiricinin dosya sistemi ve SSH/git kimlik bilgileri · bulut/DB kimlik bilgileri · sağlayıcı auth durumu · Hub DB ve kanıt deposu · iş istasyonu bütünlüğü.

### K.2 Güven sınırları

```
[İnsan operatör] ── güvenilir
[Hub süreci]     ── güvenilir
 ├ SINIR 1 ── [Agent süreci]        YARI GÜVENİLİR (yetkisi = kullanıcının yetkisi)
 ├ SINIR 2 ── [Repo içeriği]        GÜVENİLMEZ VERİ
 ├ SINIR 3 ── [Doğrulama komutları] repo build script'leri = keyfî kod
 └ SINIR 4 ── [Ağ / paket kayıtları] güvenilmez (postinstall)
```

**Dürüst tespit:** Sınır 1 OS tarafından zorlanmaz; agent geliştirici kullanıcısı olarak çalışır.

### K.3 Senaryolar ve önlemler

| # | Senaryo | Olasılık | Etki | Önlem | Kalan risk |
|---|---|---|---|---|---|
| 1 | Repo içeriğinden prompt injection | Yüksek | Yüksek | `--safe-mode`, `--setting-sources user`, `--strict-mcp-config`, `core.hooksPath` ile repo hook'ları kapalı | Kod yorumları okunuyor — kabul |
| 2 | Doğrulama komutu üzerinden enjeksiyon | Orta | Kritik | Kabuk yok, argv dizisi, executable allowlist | `cmd.exe` shim yolunda beyaz liste şart |
| 3 | Kimlik bilgisi sızıntısı (env) | Yüksek | Kritik | **Env allowlist**, `GIT_ASKPASS` no-op | Sağlayıcı auth dosyaları diskte okunabilir |
| 4 | `.env` / sertifika okuma | Orta | Yüksek | Worktree commit'ten → gitignore'lu sırlar yok | Commit edilmiş sırlar mevcut |
| 5 | Yıkıcı komut / kazara push | Orta | Kritik | G.3'teki beş katman | Agent ev dizinine yazabilir |
| 6 | Bağımlılık postinstall script'i | Orta | Kritik | `npm ci --ignore-scripts` varsayılan | İstisna tanımlanırsa geri gelir |
| 7 | Path traversal / symlink kaçışı | Düşük | Yüksek | `FileSystemGuard` | — |
| 8 | Organizasyonlar arası sızıntı | Orta | Yüksek | Org kökleri, DB scope, env allowlist, **Kronos kapısı (L.4)** | Core X Logic içi: mantıksal |
| 9 | Kanıtta sır görünmesi | Yüksek | Orta | Yazma anında redaksiyon; gitignore kapsamı kanıta girmez | Regex eksiksiz değil |
| 10 | Yeni sırrın kalıcı git nesnesine yazılması | Orta | Yüksek | **SecretGuard commit kapısı (K3)** — **artık bypass bayrağı yok (M1)** | Desen kümesi sınırlı |
| 11 | Kötü niyetli üretilmiş kod (backdoor) | Düşük–Orta | Kritik | Otomatik merge/push yok, insan kabulü, bağımsız inceleme | İnsan gözden kaçırabilir |
| 12 | İnceleyicinin kodu değiştirmesi | Düşük | Orta | Atılabilir worktree + hash tespiti + araç kısıtı | — |
| 13 | Agent'ın Hub kanıtını değiştirmesi | Düşük | Yüksek | Kanıt deposu worktree dışında | Aynı kullanıcı → OS düzeyinde erişilebilir |
| 14 | **Agent'ın kendi commit'ini atması (E1)** | Orta | **Yüksek** — SecretGuard garantisini deler | **Bloklayıcı `RuntimePolicyViolation`**: commit yok, doğrulama yok, inceleme yok, kabul override'ı yok, worktree korunur | Agent commit'i branch'te kalır; **temizlik insan işidir** (Hub asla dokunmaz) |
| 15 | **Kabul override'ının kötüye kullanımı (E2)** | Orta | Orta | Kapalı küme (yalnızca `ReviewBlocked`), zorunlu gerekçe, `OverrideUsed` işareti, audit, bulguların korunması, `Outcome` değiştirilemez | İnsan yanlış karar verebilir — kayıt altında |
| 16 | Sonsuz döngü / kaynak tüketimi | Orta | Orta | Job Object kotaları, üç seviyeli timeout, eşzamanlılık=1, günlük bütçe | — |
| 17 | Sağlayıcı oturumunun kötüye kullanımı | Düşük | Kritik | Hub token saklamaz | Agent aynı kullanıcı olarak erişebilir |

### K.4 Açıkça kabul edilen artık riskler

1. Agent, geliştirici kullanıcısının tüm dosya sistemine erişebilir.
2. **Core X Logic içinde** izolasyon mantıksaldır. **Kronos bu koşulda onboard edilemez (L.4).**
3. Redaksiyon ve SecretGuard en iyi çabadır.
4. Repoya commit edilmiş mevcut sırlar agent'a görünür.
5. **(E1 ile revize)** Agent'ın kendi commit'ini atması **engellenmez ama akışı durdurur**; oluşan commit worktree branch'inde kalır ve temizliği insana aittir — Hub geçmişi asla onarmaz.
6. **(E2)** İnceleme override'ı insan yargısına dayanır; teknik olarak `ReviewBlocked` dışına genişletilemez.

**Yükseltme tetikleyicileri:** Kronos onboarding talebi · güvenilmeyen/üçüncü taraf repo · ikinci insan kullanıcı · üretim kimlik bilgilerinin repo yakınında bulunması.

---

## L. Organizasyon İzolasyon Modeli

### L.1 Katman katman

| Katman | Mekanizma | Gerçek zorlama? |
|---|---|---|
| Veritabanı | `OrganizationId` + EF Core global query filter + `OrgScope` | ✔ (uygulama içi) |
| Dosya sistemi | Org başına kök; `FileSystemGuard` | ✔ (uygulama içi) |
| Repository | `Repository.OrganizationId`; çapraz çözüm hata | ✔ (uygulama içi) |
| Bağlam | Context repo org başına | ✔ (uygulama içi) |
| Ortam değişkenleri | Allowlist org başına | ✔ (uygulama içi) |
| Kanıt / log | Org'a göre ayrı | ✔ (uygulama içi) |
| **Agent süreci** | Aynı Windows kullanıcısı | **✘** |
| **Sağlayıcı oturumu** | Ortak Claude Code auth | **✘** |

### L.2 Dürüst özet

> Mevcut hâliyle organizasyon izolasyonu **kaza önleyicidir, saldırı önleyici değildir.**

### L.3 Sert izolasyon seçenekleri (Faz 10)

| Seçenek | Maliyet | İzolasyon | Not |
|---|---|---|---|
| **WSL2 (org başına dağıtım)** | Düşük–Orta | İyi | **Birinci aday.** Zaten kurulu; Codex'in Linux-sandbox ihtiyacıyla aynı yatırım |
| Ayrı Windows yerel kullanıcısı + NTFS ACL | Orta | İyi | `CreateProcessAsUser`; auth kullanıcı başına |
| Container (Docker Desktop) | Orta–Yüksek | Çok iyi | Windows I/O bedeli |
| Ayrı makine | Yüksek | En iyi | Gerçek gereksinimde |

### L.4 KRONOS ONBOARDING GÜVENLİK KAPISI

**Durum: Core X Logic pilotu için mantıksal izolasyon KABUL. Kronos BLOKELİ.**

**Kural.** Core X Logic ve Kronos agent süreçleri **aynı OS güvenlik principal'ı** altında çalıştığı sürece Kronos'a ait hiçbir repository, bağlam, kimlik bilgisi veya şirket verisi platforma kaydedilemez.

| # | Koşul | Doğrulama kanıtı |
|---|---|---|
| G-K1 | Sert izolasyon uygulanmış (birinci aday **WSL2**, org başına) | Org başına ayrı OS principal'ı ile başarılı execution |
| G-K2 | Çapraz erişim negatif testi geçmiş | Kronos principal'ından Core X Logic köküne okuma **başarısız** (ve tersi) |
| G-K3 | Sağlayıcı oturumu ayrımı çözülmüş | Kronos çalıştırması Core X Logic auth durumuna erişemiyor |
| G-K4 | Kanıt/log ayrımı OS düzeyinde doğrulanmış | Kronos execution'ı Core X Logic blob deposunu okuyamıyor |
| G-K5 | `IsolationLevel = Hard` işaretli ve yükleyici doğrulamış | `hub doctor` çıktısı |

**Kod ile zorlanır:** Kronos `RequiresHardIsolation = true` ile kaydedilir; yükleyici `Logical` görürse `Status = Blocked` yapar; orchestrator bloke org için execution reddeder. Faz 1'de birim testiyle kilitlenir.

**Bloklamadıkları:** Faz 1–10 geliştirmesi, Core X Logic kullanımı, Loop Zero, Claude/Codex entegrasyonu, UI. **Yalnızca Kronos onboarding'ini bloklar.**

---

## M. Kalıcılık

### M.1 SQLite

WAL · `busy_timeout = 5000` · tek yazıcı (global semafor) · UTC ISO-8601 · EF Core 10 + açık migration'lar · yedekleme `VACUUM INTO` + blob kopyası.

### M.2 Veri türüne göre ayrıştırma

| Veri | Nerede |
|---|---|
| Katalog, Task, Kriter, Execution, Step, Finding, Decision, Audit, OrchestrationEvent | SQLite tabloları |
| Agent stdout/stderr, komut çıktıları, debug log | Blob (DB'de hash+boyut) |
| Diff (worktree / commit), commit grafiği | Blob |
| Context Pack | Blob + manifest |
| Dosya envanteri | Blob (JSON) + özet sayaçlar tabloda |
| Test raporu | Ham → blob; ayrıştırılmış sayaçlar → tablo |
| Review bulguları | Tablo + ham JSON blob |
| Agent beyanı | Blob + küçük özet alanı |

### M.3 Saklama

İçerik adresli → silme referans sayımı ister → **MVP'de silme yok**. `hub evidence gc` Faz 10.

---

## N. Teknoloji Önerisi

| Katman | Öneri | Gerekçe |
|---|---|---|
| Runtime | **.NET 10 (`net10.0`)**, C# | Mevcut; süreç kontrolü + P/Invoke |
| Host | Faz 1–8 konsol host + `IHostedService`; Faz 9 Minimal API + SSE | Kuyruk/worker altyapısı yok |
| ORM | **EF Core 10 + SQLite** | Migration disiplini |
| Loglama | **Serilog** JSON dosya sink | Yapısal |
| CLI | **`System.CommandLine`** | İlk arayüz |
| Test | `xUnit` + `FluentAssertions` + fixture git repoları | Deterministik E2E |
| Mimari test | `NetArchTest` / Roslyn analyzer | E.1 değişmezleri (#1, #4, #6, #8, #9, #10, #11) |
| UI (Faz 9) | **React + TypeScript + Vite** | Olgun diff bileşenleri JS ekosisteminde |
| Şema | JSON Schema | `--json-schema` uyumlu |

**Reddedilenler:** PostgreSQL · Redis · RabbitMQ/Kafka · MediatR · Docker (altyapı olarak) · Node.js sidecar + Agent SDK.

---

## O. Kaynak Ağacı (öneri — oluşturulmadı)

```
corex-agent-hub/
├─ docs/adr/                        ADR-0001 … ADR-0017
├─ docs/architecture/               bu belge
├─ src/
│  ├─ CoreX.Hub.Domain/             Varlıklar, VO'lar, durum makineleri. BAĞIMLILIK YOK.
│  ├─ CoreX.Hub.Application/        ExecutionOrchestrator, TaskService, AcceptanceService,
│  │                                RuntimePolicyGate, ScopeGuard, SecretGuard, BudgetGuard
│  ├─ CoreX.Hub.Infrastructure/     EF Core, migration'lar, blob deposu, redaksiyon
│  │                                (SecretGuard ile ortak desen kümesi)
│  ├─ CoreX.Hub.Execution/          ProcessRunner (Job Object interop), FileSystemGuard
│  ├─ CoreX.Hub.Workspace/          Git, worktree yaşam döngüsü, koşullu result commit,
│  │                                reconciler, diff/commit-graph yakalama
│  ├─ CoreX.Hub.Context/            Context Pack derleyici + manifest/hash
│  ├─ CoreX.Hub.Agents/             Abstractions/ · Scripted/ · ClaudeCode/(Faz 6) · Codex/(Faz 8)
│  ├─ CoreX.Hub.Verification/       Profil, çalıştırıcı, TRX/JUnit ayrıştırıcı
│  ├─ CoreX.Hub.Cli/                İlk ve tek arayüz (Faz 1–8)
│  ├─ CoreX.Hub.Api/                [Faz 9]
│  └─ CoreX.Hub.Web/                [Faz 9]
├─ tests/
│  ├─ …Domain.Tests / …Workspace.Tests / …Execution.Tests
│  ├─ …Verification.Tests / …EndToEnd.Tests
│  └─ …ArchitectureTests/           E.1 değişmezleri
├─ fixtures/repos/hub-sample/       Loop Zero fixture (HUB-001)
└─ hub.config.yaml
```

---

## P. Durum Makineleri

### P.1 Task (E2 ile güncellendi)

```
Draft ──(Ready gate)──────────────────────────────────► Ready
Ready ──(execution başladı)───────────────────────────► Running
Running ──(Completed, Outcome == Passed)──────────────► AwaitingAcceptance
Running ──(Completed, Outcome != Passed)──────────────► Ready
Running ──(Failed/TimedOut/Cancelled/Interrupted)─────► Ready
AwaitingAcceptance ──(hub accept)─────────────────────► Accepted   [TERMİNAL]
AwaitingAcceptance ──(request-changes)────────────────► Ready
AwaitingAcceptance ──(reject)─────────────────────────► Rejected   [TERMİNAL]
Ready ──(hub accept --override --reason, YALNIZCA     ─► Accepted   [TERMİNAL]
        son Execution == Completed(ReviewBlocked)
        ve altı ön koşul sağlanıyorsa)                            ← E2
terminal olmayan herhangi ──(cancel)──────────────────► Cancelled  [TERMİNAL]
```

**Değişmezler:**
- `Accepted` geçişini yapan **tek** yol `AcceptanceService`'tir (E.1 #6).
- `Accepted` / `Rejected` **terminal ve geri alınamaz.** `AcceptanceDecision` değişmezdir.
- **(E2)** `Ready → Accepted` kenarı **yalnızca** override yolundan geçilir ve **yalnızca** son Execution `Completed(ReviewBlocked)` ise. Diğer tüm `Ready` durumlarında bu kenar yoktur.
- Task'ta `Failed` durumu yok (başarısızlık execution'a ait). `Blocked` yok (`Ready` + `BlockReason`).
- Bir Task `Accepted` iken son Execution'ın `Outcome`'u `ReviewBlocked` olabilir — bu **beklenen ve kayıtlı** bir durumdur.

### P.2 Execution (E1/M2 ile güncellendi)

```
Created → PreparingWorkspace → BuildingContext → RunningAgent
        → CapturingChanges → Verifying → Reviewing → Completed
```

`Completed` outcome'ları:

| Outcome | Anlam | Result commit | Kabul override'ı |
|---|---|---|---|
| `Passed` | Tüm kapılar + doğrulama + inceleme temiz | Var | Gereksiz → reddedilir |
| `VerificationFailed` | `required` doğrulama adımı FAIL | Var | **Hayır** |
| `ReviewBlocked` | Geçerli bloklayıcı bulgu | Var | **EVET — tek istisna** |
| `ReviewUnusable` (M2) | Şema ihlali / kurcalama → inceleme kullanılamaz | Var | **Hayır** |
| `ScopeViolation` | K2 | **Yok** | **Hayır** |
| `SecretGuardBlocked` | K3 | **Yok** | **Hayır** |
| **`RuntimePolicyViolation`** (E1) | K1 — `UnexpectedAgentCommit` | **Yok** | **Hayır** |

Terminal hata durumları (`Completed` değil): `Failed` · `TimedOut` · `Cancelled` · `Interrupted` — hepsinde commit yok, worktree korunur, kabul override'ı yok.

Akış kuralları:
- K0 (agent adımı `Succeeded` değil) → `CapturingChanges` yalnızca kanıt toplar → `Verifying`/`Reviewing` **atlanır**. **(M4)** `HEAD != Base` ise ek olarak `UnexpectedAgentCommitDetected = true` yazılır; outcome K0 tarafından belirlenir.
- **K1 başarısız (E1)** → commit yok → `Verifying`/`Reviewing` **atlanır** → `RuntimePolicyViolation`.
- K2/K3 başarısız → commit yok → `Verifying`/`Reviewing` **atlanır**.
- `Verifying` FAIL → `Reviewing` atlanır → `VerificationFailed`.
- Tüm geçişler tek transaction; her geçiş `AuditEvent` üretir.

### P.3 ExecutionStep

```
Pending → Running → Succeeded | Failed | TimedOut | Cancelled | Skipped | Unsupported
```

`Skipped`: önceki kapı nedeniyle çalıştırılmadı (sebep kaydedilir). `Unsupported`: `Capabilities` gereği. **Sessiz geçiş yok.**

### P.4 Verification / Review

Ayrı durum makinesi yok; `ExecutionStep`'tir.
- `VerificationOutcome`: her `required` adım `Succeeded` ise `Pass`, aksi `Fail`.
- `ReviewOutcome`: `Clean | NonBlockingFindings | Blocked | Unusable`.
- Eşleme: `Blocked` → `Execution.Outcome = ReviewBlocked`; `Unusable` → `ReviewUnusable` (M2).

### P.5 Kabul — iki yol (E2)

`AcceptanceDecision` değişmez kayıttır:

```
{
  ExecutionId, Decision: Accept|RequestChanges|Reject,
  Actor, Reason?, OverrideUsed: bool,
  HumanCriterionVerdicts: [ { CriterionId, Verdict: Met|NotMet|NotApplicable, Note? } ],
  DecidedAtUtc
}
```

**`AcceptanceService` değişmezleri:**

| # | Kural |
|---|---|
| I-A1 | **Normal kabul:** `Task.State == AwaitingAcceptance` **ve** `LatestExecution.Outcome == Passed`. `--override` verilirse **reddedilir** (aşılacak kapı yok) |
| I-A2 | **Override kabulü:** `Task.State == Ready` **ve** `LatestExecution.State == Completed` **ve** `LatestExecution.Outcome == ReviewBlocked` **ve** altı ön koşul **ve** `Reason` boş değil |
| I-A3 | Altı ön koşul: (1) uygulama adımı `Succeeded`, (2) ScopeGuard geçti, (3) SecretGuard geçti, (4) `required` doğrulama geçti, (5) `ResultCommitSha != null`, (6) `ReviewOutcome == Blocked` ve ≥1 bloklayıcı `Finding` |
| I-A4 | **Kapalı küme:** `switch (outcome) { case ReviewBlocked: allow; default: deny; }` — enum büyüse bile varsayılan reddet (E.1 #10) |
| I-A5 | Her iki yolda da Task'ın **tüm** `Human` kriterleri için verdict zorunlu |
| I-A6 | `Machine` kriter sonuçları kanıttan otomatik doldurulur; insan değiştiremez |
| I-A7 | `AcceptanceService` **`Execution.Outcome` yazmaz** (E.1 #11). Override sonrası outcome `ReviewBlocked` kalır |
| I-A8 | Bulgular korunur; bastırılmaz, silinmez, "çözüldü" işaretlenmez |
| I-A9 | Execution başına tek `AcceptanceDecision` (benzersiz indeks) |
| I-A10 | Her kabul kararı `AuditEvent` üretir; override kararında `Reason` audit'e yazılır |
| I-A11 | `LatestExecution` = Task'ın en yüksek `AttemptNo`'lu execution'ı. Daha yeni bir execution varsa eski `ReviewBlocked` override edilemez |

Kabul; merge, push, release veya deployment **anlamına gelmez** (ADR-0014).

**Görevi terk etmek:** Bir `Ready` görev artık istenmiyorsa `hub cancel` kullanılır (`Cancelled`, terminal). `reject` yalnızca `AwaitingAcceptance`'tan çağrılır.

### P.6 Eşzamanlılık

| Risk | Önlem |
|---|---|
| Aynı Task'a iki execution | Filtreli benzersiz indeks: Task başına terminal olmayan tek execution |
| Eşzamanlı Task güncellemesi | `Version` optimistic concurrency |
| Paralel `worktree add` | Repo başına named mutex |
| SQLite yazma çakışması | Global yazma semaforu + WAL + busy_timeout |
| Zombi execution | `Lease` (PID + host başlangıç zamanı + heartbeat) + açılış reconciler |
| İptal / tamamlanma yarışı | Tek transaction |
| Çift kabul | `AcceptanceDecision.ExecutionId` benzersiz indeksi + Task durum kontrolü |
| **Override sırasında yeni execution başlaması** | I-A11 (`LatestExecution` kontrolü) + Task `Running` iken override reddedilir |

---

## Q. Hata ve Kurtarma Modeli

| Olay | Tespit | Davranış | Commit | Worktree | Override? |
|---|---|---|---|---|---|
| Claude süreci çöktü | Exit code | Adım `Failed`; kısmi değişiklikler kanıtlanır; Verify/Review atlanır | Yok | Korunur | Hayır |
| Claude asıldı | `IdleTimeout` (10 dk) | Nazik kapatma → job kill → `TimedOut` | Yok | Korunur | Hayır |
| Claude auth hatası | Olay/exit code | `Failed` + `AuthenticationRequired`; **yeniden deneme yok**, insana yükselt | Yok | Korunur | Hayır |
| **Agent kendi commit'ini attı (E1)** | `HEAD != BaseCommitSha` (K1) | Kanıt + commit grafiği + teşhis sır taraması; **doğrulama ve inceleme çalıştırılmaz**; Hub geçmişe **dokunmaz** | **Yok** | **Korunur** | **Hayır** |
| Agent hem commit attı hem çöktü (M4) | K0 + K1 | Outcome K0'dan gelir; `UnexpectedAgentCommitDetected = true` kaydedilir; davranış aynı | Yok | Korunur | Hayır |
| Kapsam ihlali | K2 | `ScopeViolation`; Verify/Review atlanır | Yok | Korunur | Hayır |
| Sır deseni | K3 | `SecretGuardBlocked`; **bypass komutu yok (M1)**; insan worktree'de elle çalışır veya görevi yeniden çalıştırır | Yok | Korunur | Hayır |
| Test asıldı | `IdleTimeout` + `HardTimeout` | Job kill; adım `TimedOut`; doğrulama `Fail` → `VerificationFailed` | **Var** | Release edilebilir | Hayır |
| Doğrulama başarısız | Exit code | `VerificationFailed`; inceleme atlanır; Task `Ready` | Var | Release edilebilir | Hayır |
| İnceleyici bloklayıcı bulgu | Kapı kuralı | `ReviewBlocked`; Task `Ready`; **insan `hub accept --override --reason` ile kabul edebilir** | Var | Release edilebilir | **EVET** |
| Şema ihlali / kurcalama | `--json-schema` / hash | Bir kez yeniden dene → `ReviewUnusable`; Task `Ready`; yeniden deneme insan kararı | Var | Release edilebilir | Hayır |
| Codex çöktü (Faz 8) | Exit code | `ReviewUnusable` | Var | Release edilebilir | Hayır |
| Hub çöktü / makine restart | Lease uyuşmazlığı | `Interrupted`; `hub workspace reconcile` | Yok | Korunur | Hayır |
| Worktree kilitli kaldı | `remove` hatası | build-server shutdown → job kill → 3 deneme → `Orphaned`; akış bloklanmaz | — | `Orphaned` | — |
| Context üretimi başarısız | Builder doğrulaması | Execution başlamadan `Failed`; **sessiz kırpma yok** | Yok | Yoksa yok | Hayır |
| Disk doldu | Yazma hatası | `Failed`; blob yazımı atomik → yarım blob yok | Yok | Korunur | Hayır |

**Genel ilkeler:**
1. Hiçbir kurtarma yolu veri silmez; temizlik daima açık operatör komutudur.
2. **(D6)** Commit yoksa worktree silinmez.
3. **(D1)** Hiçbir kurtarma yolu Task'ı `Accepted` yapmaz.
4. **(E1)** Hiçbir kurtarma yolu git geçmişini onarmaz/yeniden yazmaz.
5. **(E2)** Hiçbir kurtarma yolu `Execution.Outcome`'u değiştirmez.

---

## R. Gözlemlenebilirlik

1. **Yapısal loglar** — Serilog `logs/hub-YYYYMMDD.jsonl`; zorunlu alanlar `timestamp`, `level`, `message`, `organizationId`, `taskKey`, `executionId`, `stepId`, `correlationId`.
2. **Korelasyon** — `ExecutionId` = Claude `--session-id`.
3. **`OrchestrationEvent`** — zaman çizelgesi satırları, blob yok.
4. **`Activity` / `ActivitySource`** — exporter yok.
5. **Süre ölçümleri** — adım bazında.
6. **`hub doctor`** — git sürümü, `claude --version`, disk, DB, yetim worktree sayısı, **korunan (commit'siz) worktree sayısı**, açık lease, bloke organizasyonlar (L.4).

**Yapılmayacaklar:** metrik sunucusu, tracing backend, log toplama, Kafka, dashboard.

---

## S. İlk Uçtan Uca Dikey Dilim

### S.1 Neden üçe bölündü

v1, iki doğrulanmamış AI bağımlılığını (Claude headless + Codex) ilk dilime koyuyordu; bu, mimari riskin en yüksek olduğu yerde hata ayıklamayı AI belirsizliğiyle karıştırır.

### S.2 DİLİM 1 — "Loop Zero" (AI YOK) — kapsam kilitli (D7)

```
hub task create → Ready gate (MACHINE/HUMAN beyanı doğrulanır)
      ↓
Repository çöz + base commit sabitle
      ↓
Worktree + agent/HUB-001/1 + sertleştirme (yalnızca pre-push)
      ↓
Context Pack + manifest + hash
      ↓
ScriptedAgentRuntime (8 mod)
      ↓
CaptureChanges:  KANIT  →  K0 → K1(E1) → K2 → K3
      ↓                     │    │        │    │
      │                     └────┴────────┴────┴──► commit YOK · kanıt VAR
      │                                             worktree KORUNUR · Completed(...)
      ↓ (hepsi geçti)
Hub result commit + commit diff
      ↓
Verification profile
      ↓
Kanıt sakla
      ↓
Task = AwaitingAcceptance          ← hub run BURADA DURUR (D1)
      ↓
hub accept HUB-001#1   (HUMAN kriter verdictleri girilir)
      ↓
Task = Accepted → worktree release
```

**Loop Zero'nun İÇİNDE (sabit):** fixture repository (`HUB-001`) · `ScriptedAgentRuntime` · `ProcessRunner` · `FileSystemGuard` · git worktree izolasyonu · Context Pack · **K1 RuntimePolicyGate** · ScopeGuard · SecretGuard · Verification · Evidence (commit'li ve commit'siz) · deterministik orkestrasyon · **açık insan kabulü**.

**Loop Zero'nun DIŞINDA:** Claude · Codex · React · REST API · WSL runtime · otonom onarım · maliyet raporu · **inceleme adımı ve inceleme override'ı** (Faz 7) · çoklu org paralelliği · gerçek repository · gerçek organizasyon envanteri.

> **D7 notu:** E1, `CaptureChanges` içindeki mevcut anomali kontrolünün sonucunu bulgudan outcome'a çevirir — yeni bileşen değildir. E2'nin Loop Zero'ya tek etkisi bir **negatif testtir** (kriter #17).

#### Loop Zero kabul kriterleri

| # | Kriter |
|---|---|
| 1 | **(D1)** `hub run HUB-001` başarıyla tamamlandığında Task `AwaitingAcceptance` olur — **asla `Accepted` olmaz.** Ardından `hub accept HUB-001#1` Task'ı `Accepted` yapar. İki komut arasında durum kalıcıdır (süreç yeniden başlasa bile) |
| 2 | **(D1)** `hub accept`, Task `AwaitingAcceptance` değilken reddedilir; aynı execution için ikinci kez çağrılırsa reddedilir |
| 3 | **(D4)** Fixture task'ı 2 `MACHINE` + 1 `HUMAN` kriter taşır; `MACHINE` sonuçları kanıttan otomatik dolar; `HUMAN` verdict'i verilmeden `accept` tamamlanmaz |
| 4 | **(D4)** `VerificationKind` beyan edilmemiş kriterle Task `Ready`'ye geçemez; `Machine` kriterinin `VerificationRef`'i olmayan bir adıma işaret ederse reddedilir |
| 5 | Tüm akış **CI'da, ağsız, AI'sız, deterministik** geçer |
| 6 | Aynı base commit ile iki çalıştırma **aynı `ContextPackHash`** üretir |
| 7 | Fixture repoda test kırıkken → `VerificationFailed`; patch uygulanınca → `Passed`; baseline karşılaştırması kanıtta görünür |
| 8 | **(D6)** `out-of-scope-write` → `ScopeViolation`, commit yok, `GitWorktreeDiff` + `FileInventory` var, worktree korunur |
| 9 | **(D6+M1)** `secret-write` → `SecretGuardBlocked`, commit yok, kanıt var, worktree korunur; **hiçbir Hub komutu bu execution'ı commit'e veya kabule taşıyamaz** (`hub commit-result` komutu mevcut değildir) |
| 10 | **(D6)** `hang` → `IdleTimeout`, **süreç ağacı tamamen ölür** (kalan `dotnet`/`node` süreci yok), commit yok, worktree korunur |
| 11 | **(D6)** `crash` / `partial-write` → kısmi değişiklikler `GitWorktreeDiff` ve `FileInventory` olarak kanıtlanır; **untracked yeni dosyalar diff'te görünür** (`add -N` doğrulaması) |
| 12 | **(E1)** `unexpected-commit` → `Outcome = RuntimePolicyViolation`; **result commit yok**, `GitCommitGraph` ve `UnexpectedCommitShas` kaydedilmiş, **doğrulama ve inceleme adımları `Skipped`**, worktree korunur |
| 13 | **(E1)** `unexpected-commit` sonrası agent'ın oluşturduğu commit **hâlâ branch'te durur**; Hub hiçbir `reset`/`branch -f`/`update-ref`/`amend` çağrısı yapmamıştır (mimari test + git reflog kontrolü) |
| 14 | **(M4)** `crash` + `unexpected-commit` birleşimi → outcome `Failed`, `UnexpectedAgentCommitDetected = true`; commit yok, worktree korunur |
| 15 | **(D2)** Worktree'de `git push` denemesi `pre-push` nedeniyle başarısız olur; **Hub'ın kendi commit'i hiçbir bypass bayrağı olmadan başarılı olur** |
| 16 | Hub süreci çalışma ortasında `kill` edilir → yeniden başlatmada `Interrupted`, worktree korunur, `reconcile` doğru raporlar |
| 17 | **(E2 negatif test)** `ScopeViolation`, `SecretGuardBlocked`, `RuntimePolicyViolation`, `Failed`, `TimedOut`, `Interrupted` ve `Passed` outcome'larına `hub accept --override --reason "x"` çağrısı **reddedilir**; `AcceptanceDecision` yazılmaz; Task durumu değişmez |
| 18 | Kanıt deposunda hiçbir blob DB içinde değil; her blob hash'iyle bulunabilir |
| 19 | **(D5)** `RequiresHardIsolation = true` + `IsolationLevel = Logical` organizasyon `Blocked` yüklenir ve execution başlatamaz |

**Test stratejisi:** `fixtures/repos/hub-sample` — gerçek git reposu, kasıtlı kırık test, `fix.patch`, kapsam dışı patch, sahte sır içeren patch, commit atan script modu.

**Demo senaryosu:**
> `hub run HUB-001` → ~90 s → I.6(a) kanıt özeti → **Task `AwaitingAcceptance`** → `hub evidence show HUB-001#1 --diff` → `hub accept HUB-001#1` (HUMAN kriteri sorulur) → **`Accepted`**.

### S.3 DİLİM 2 — Claude uygulayıcı (Faz 6)

`ScriptedAgentRuntime` yerine `ClaudeCodeRuntime`. Loop Zero'nun 19 kriteri geçerliliğini korur; ek olarak: `stream-json` ayrıştırma hatası kanıt kaybına yol açmaz; `--session-id` = `ExecutionId`; mevcut abonelik oturumu `--safe-mode --setting-sources user` altında çalışır; **gerçek bir agent `git commit` denemesi `RuntimePolicyViolation` üretir** (E1'in gerçek runtime ile doğrulanması).

### S.4 DİLİM 3 — Bağımsız inceleme (Faz 7)

Ayrı, atılabilir worktree (result commit'ten) + read-only profil + `--json-schema`.

Kabul kriterleri:
1. İnceleyici worktree'sinde yazma tespit edilirse `ReviewUnusable` üretilir.
2. `ResultCommitSha == null` olan execution'da inceleme **hiç çalıştırılmaz**.
3. Bloklayıcı bulgu Task'ı `AwaitingAcceptance`'a **geçirmez**; Task `Ready` olur.
4. **(E2)** `hub accept --override --reason "..."` yalnızca `ReviewBlocked` execution'da başarılı olur; `Reason` boşsa reddedilir; `HUMAN` verdictleri istenir; `AcceptanceDecision.OverrideUsed = true` yazılır; **`Execution.Outcome` `ReviewBlocked` kalır**; bulgular korunur; `AuditEvent` yazılır; Task `Accepted` olur.
5. **(E2)** `ReviewUnusable` execution'da aynı komut **reddedilir**.

---

## T. Fazlı Uygulama Yol Haritası (kanonik faz listesi)

### Faz 0 — Kararlar (kod yok)
- **Çıktı:** ADR-0001 … ADR-0017; `hub.config.yaml` şema taslağı; **format kararları onaylandı** (`D:\cx\`, `<PROJECT>-<3 hane>`, fixture `HUB-001`).
- **Kabul:** Denetçi bu belgeyi ve ADR'leri onaylar → **mimari dondurma**.
- **Dış kapı:** Yok. **YAPILMAYACAK:** Proje oluşturma.

### Faz 1 — İskelet, domain, kalıcılık, CLI
- **Çıktı:** Solution; `Domain`+`Application`+`Infrastructure`+`Cli`; 15 tablo + migration'lar; seed; Ready gate (D4); organizasyon bloke mantığı (D5); `hub org/project/repo list`, `hub task create/show`.
- **Kabul:** `VerificationKind` beyanı olmadan `Ready`'ye geçilemez; `RequiresHardIsolation` bloke testi geçer; mimari testler (E.1 #1, #4, #6, #8, #9, #10, #11) yeşil.
- **Dış kapı:** **Yok.** **YAPILMAYACAK:** Süreç çalıştırma, git, AI, API, UI.

### Faz 2 — Execution çekirdeği (EN YÜKSEK RİSK)
- **Çıktı:** `ProcessRunner` (Job Object, üç seviyeli timeout, async stream, env allowlist, UTF-8/ANSI), `FileSystemGuard`.
- **Kabul:** Torun süreç öldürme testi; yüksek hacimli stdout+stderr deadlock testi; allowlist dışı env değişkeni çocukta görünmez.

### Faz 3 — Workspace (git)
- **Çıktı:** Base commit sabitleme, worktree/branch, sertleştirme (**yalnızca `pre-push`**), **commit grafiği yakalama**, çalışma ağacı diff'i (`add -N`), koşullu result commit iskeleti, reconciler, `hub workspace` komutları.
- **Kabul:** Kirli ana dizinle görev başlar; `.gitignore`'lu dosya worktree'de yok; `pre-push` engeller; **Hub commit'i bypass bayrağı olmadan başarılı**; `RuntimePolicyViolation` yolunda hiçbir tarih onarım komutu çağrılmaz (mimari test); kilitli worktree `Orphaned` olur ve akış bloklanmaz.

### Faz 4 — Doğrulama motoru + kanıt deposu
- **Çıktı:** Profil modeli, allowlist'li kabuksuz çalıştırıcı, TRX/JUnit ayrıştırıcı, baseline önbelleği, içerik adresli blob deposu, atomik yazım, **redaksiyon desen kümesi (SecretGuard ile ortak)**.
- **Kabul:** Kırık→düzelmiş geçişi sayaçlarla; sır test vektörleri redakte edilir; blob yazımı atomik.

### Faz 5 — Orchestrator + Context Pack + ScriptedAgentRuntime → ★ DİLİM 1 (Loop Zero) ★
- **Çıktı:** `ExecutionOrchestrator`, durum makineleri (D1/D6/E1/E2), lease/heartbeat, **RuntimePolicyGate (K1)**, ScopeGuard (K2), SecretGuard (K3), Context Pack derleyici, `ScriptedAgentRuntime` (8 mod), `AcceptanceService` (I-A1…I-A11), `hub run / accept [--override --reason] / request-changes / reject / cancel / evidence show`.
- **Kabul:** S.2'deki 19 kriter.
- **Dış kapı:** Yok. **YAPILMAYACAK:** AI çağrısı, inceleme adımı, API, UI.

### Faz 6 — `ClaudeCodeRuntime` → ★ DİLİM 2 ★
- **Çıktı:** CLI adapter, hataya dayanıklı `stream-json` ayrıştırıcı, izin profili eşlemesi, usage metadata, `--session-id` korelasyonu.
- **Kabul:** Gerçek görev uçtan uca geçer ve `AwaitingAcceptance` ile durur; ayrıştırma hatası kanıt kaybetmez; timeout/iptal gerçek süreçte doğrulanır; gerçek agent commit denemesi `RuntimePolicyViolation` üretir.
- **Dış kapı:** **G6-1, G6-2, G6-3.** **YAPILMAYACAK:** Onarım döngüsü.

### Faz 7 — İnceleme adımı → ★ DİLİM 3 ★
- **Çıktı:** Ayrı atılabilir worktree, read-only profil, `--json-schema`, kurcalama tespiti, `ReviewBlocked`/`ReviewUnusable` eşlemesi, **inceleme override kabul yolu (E2)**.
- **Kabul:** S.4'teki 5 kriter.
- **Dış kapı:** Faz 6 tamam. **G7-1** (`Major` eşiği kalibrasyonu).

### Faz 8 — `CodexRuntime`
- **Çıktı:** Codex adapter; gerekirse WSL2 yol çevirimi; `Capabilities` ilanı.
- **Kabul:** İnceleme adımı **yalnızca `AgentProfile` değiştirilerek** Codex'e geçer — orchestrator kodu değişmez.
- **Dış kapı:** **G8-1, G8-2.**

### Faz 9 — Minimal API + UI
- **Çıktı:** Minimal API + SSE; React+TS: proje/görev listesi, execution zaman çizelgesi, diff görüntüleyici, doğrulama sonuçları, bulgular, **kabul ekranı** (`HUMAN` checklist + **override için ayrı, gerekçe zorunlu akış**).
- **Kabul:** Bir görev baştan sona yalnızca UI'dan yönetilebilir; override akışı gerekçesiz gönderilemez ve yalnızca `ReviewBlocked` execution'da görünür.

### Faz 10 — Sertleştirme + Kronos kapısı
- **Çıktı:** Onarım döngüsü (max 1 tur) · maliyet raporu · kanıt GC · gelişmiş redaksiyon · `CreateProcess` P/Invoke ile yarışsız job ataması · **WSL2 sert organizasyon izolasyonu** · pre-flight sır tarama · günlük çalıştırma bütçesi · opsiyonel `hub verify <execution>`.
- **Kabul:** **L.4'teki G-K1…G-K5 karşılanır ve Kronos onboard edilebilir hâle gelir.** K.4'teki artık risklerden en az ikisi kapanır.

---

## U. ADR'ler

| No | Başlık | Durum |
|---|---|---|
| ADR-0001 | Local-first, tek süreçli modüler monolit | v1 |
| ADR-0002 | Kanıt otoritesi: makine gerçeği > inceleyici > agent beyanı; agent beyanı otomatik outcome'a giremez | v1 / D4 |
| ADR-0003 | Git worktree ile execution izolasyonu ve base-commit sabitleme | v1 |
| ADR-0004 | Worktree'ler repo dışında, kısa Hub kökünde (**`D:\cx\`**) | v1 / Faz 0 |
| ADR-0005 | **Sonuç commit'ini Hub atar; `pre-commit` hook'u yoktur; agent commit'i bir runtime politikası ihlalidir ve tespit edildiğinde akışı bloklar (`RuntimePolicyViolation`); Hub agent'ın oluşturduğu git geçmişini asla onarmaz/silmez; uzağa yayınlama engeli beş katmanlıdır ve Hub kendi korumasını atlatmaz** | **E1 ile revize** |
| ADR-0006 | Tüm süreç çalıştırma tek `ProcessRunner` üzerinden, kabuksuz | v1 |
| ADR-0007 | Ortam değişkeni allowlist'i (denylist değil) | v1 |
| ADR-0008 | `IAgentRuntime` ince tutulur; yetenekler ilan edilir | v1 |
| ADR-0009 | Claude Code entegrasyonu CLI subprocess ile; mevcut abonelik oturumu; API-key faturalandırma tasarlanmaz | D3 |
| ADR-0010 | Context Pack diskte materyalize edilir ve hash'lenir | v1 |
| ADR-0011 | Repo içi agent yapılandırması ve git hook'ları varsayılan devre dışı | v1 / D2 |
| ADR-0012 | Organizasyon izolasyonu Core X Logic pilotu için mantıksaldır | D5 |
| ADR-0013 | SQLite + WAL; büyük çıktılar içerik adresli blob deposunda | v1 |
| ADR-0014 | **İnsan kabulü merge/push/deploy anlamına gelmez; kabul ayrı, açık ve geri alınamaz bir karar kaydıdır; tek override istisnası `ReviewBlocked`'tır ve override `Execution.Outcome`'u yeniden yazmaz, bulguları silmez** | **E2 ile revize** |
| ADR-0015 | **Kabul kriterleri `MACHINE`/`HUMAN` beyanı taşır; otomatik outcome yalnızca makine kanıtı ve yapılandırılmış review kapılarından hesaplanır; `HUMAN` verdictleri her iki kabul yolunda da zorunludur; kural motoru yoktur** | **E2 ile revize** |
| ADR-0016 | **Başarısız/kısmi/kapı-bloklu execution'lar (`Failed`, `TimedOut`, `Cancelled`, `Interrupted`, `RuntimePolicyViolation`, `ScopeViolation`, `SecretGuardBlocked`) için result commit atılmaz; kanıt commit olmadan korunur; commit'siz worktree otomatik silinemez; SecretGuard'ın bypass komutu yoktur** | **E1/M1/M2 ile revize** |
| ADR-0017 | Kronos onboarding, sert OS-seviyesi izolasyon uygulanıp doğrulanana kadar kod ile bloke edilir (WSL2 birinci aday) | D5 |

**Yazılmayacak ADR'ler:** mesajlaşma altyapısı, çoklu kiracılık, yetkilendirme modeli, ölçeklendirme, vector store.

---

## V. Faz Bazlı Kapılar

### V.1 Faz 1 öncesi gerçek bloklayıcılar

| # | Konu | Durum |
|---|---|---|
| — | **Yok.** | Faz 1–5 hiçbir dış bilinmeyene bağlı değildir. Loop Zero fixture repository (`HUB-001`), fixture organizasyon ve fixture kökü (`D:\cx\_fixtures\`) kullanır. Format kararları §0.2'de onaylanmıştır. |

Faz 1'in tek ön koşulu **Faz 0 çıktısıdır**: ADR-0001…0017'nin kaydı.

### V.2 Faz kapıları

| Kapı | Bloke ettiği | Soru | Nasıl kapatılır |
|---|---|---|---|
| **G6-1** | Faz 6 | Mevcut abonelik oturumu `--safe-mode --setting-sources user` altında headless (`-p`) çalışıyor mu? | Tek manuel çalıştırma. API-key mimarisi tasarlanmayacak |
| **G6-2** | Faz 6 | İlk gerçek repository; doğrulama komutları; testler şu an geçiyor mu? | Repo seç, komutları elle çalıştır, verification profile yaz |
| **G6-3** | Faz 6 | Core X Logic organizasyon/proje/repository envanteri ve yerel yollar | `hub.config.yaml` doldur |
| **G7-1** | Faz 7 | Bloklayıcı `Major` bulgu eşiği | Gerçek bulgularla kalibre |
| **G8-1** | Faz 8 | Codex non-interactive yüzeyi ve yapısal çıktı biçimi | Codex CLI kur, `--help` analizini yap |
| **G8-2** | Faz 8 | Codex Windows sandbox / WSL2 gereksinimi; abonelikle programatik kullanım uygunluğu | Kurulum sonrası doğrulama |
| **G10-1** | Faz 10 | Onarım turu maksimumu ve bütçe | Faz 7 verisiyle kalibre |
| **G-K1…G-K5** | **Yalnızca Kronos onboarding** | L.4'teki beş koşul | Faz 10 çıktısı + negatif erişim testleri |

### V.3 Ertelenebilir sorular

| # | Soru | Ne zaman |
|---|---|---|
| N1 | İnceleme için sağlayıcı çeşitliliği ne kadar değerli? | Faz 7 sonunda ölç |
| N2 | Çoklu repo görevleri | Gerçek talep doğana kadar hayır |
| N3 | Kanıt saklama süresi ve GC | Disk baskısı oluşunca |
| N4 | UI diff bileşeni (Monaco vs react-diff-view) | Faz 9 |
| N5 | Maliyet raporu biçimi | Faz 10 |
| N6 | Uzak worker | Ölçülmüş darboğaz olmadan hayır |
| N7 | Gözetimsiz/CI çalıştırma (o zaman `setup-token`) | Talep doğduğunda |

---

## W. Aşırı Mühendislik Riskleri

1. **Workflow/policy motoru inşa etmek.** *Uyarı:* Faz 9'a kadar pipeline **kodda sabit sıralı adımlardır**.
2. **`RuntimePolicyViolation`'ı bir politika motoruna büyütmek (E1 sonrası yeni risk).** *Uyarı:* K1 tek bir sabit karşılaştırmadır (`HEAD == BaseCommitSha`). İkinci bir "politika" eklemek isteniyorsa önce bunun bir kapı mı yoksa bir kural motoru mu olduğuna karar verin; motor **yazılmayacak**.
3. **Kabul override'ını genelleştirmek (E2 sonrası yeni risk).** *Uyarı:* Override kapalı bir kümedir ve tek üyesi vardır. "Sadece bu sefer `VerificationFailed`'ı da geçelim" talebi mimarinin reddidir; testle engellenir (E.1 #10).
4. **Kabul kriteri modelini kural motoruna çevirmek.** *Uyarı:* `MACHINE`/`HUMAN` bir string referansıdır; koşul ifadesi, boolean birleştirme, bağımlılık grafiği yazılmayacak.
5. **SecretGuard'ı sır yönetim platformuna büyütmek.** *Uyarı:* Desen kümesi redaksiyonla ortaktır ve orada kalır.
6. **Soyutlamayı tek sağlayıcıya göre şekillendirmek.** *Uyarı:* `ScriptedAgentRuntime`'ı ilk yazın.
7. **Bağlam sistemini bilgi tabanına çevirmek.** *Uyarı:* Bağlam = markdown + git.
8. **Kanıt modelini aşırı normalleştirmek.** *Uyarı:* Blob + hash + küçük meta.
9. **UI'ı erken başlatmak.** *Uyarı:* Faz 9'dan önce tek satır React yok.
10. **Otonomi eklemek.** *Uyarı:* Kanıtı ve determinizmi öldürür. *ADR-0028:* kanıtı ve determinizmi değiştirmeyen dar biçimi (sıra + kanıtla sınırlı politika kabulü) eklendi; uyarı onarım döngüsü, agent beyanına dayalı karar ve otomatik entegrasyon için aynen geçerlidir.
11. **Çok kullanıcılı / yetkilendirme mimarisi.** *Uyarı:* `Actor` bir string.
12. **Event sourcing / CQRS.** *Uyarı:* Append-only `AuditEvent` yeter.
13. **Genel amaçlı plugin sistemi.** *Uyarı:* İki runtime, ikisi de derlenmiş projede.
14. **Maliyet takibini erken derinleştirmek.** *Uyarı:* Metadata sakla, rapor Faz 10.
15. **Diff görüntüleyiciyi sıfırdan yazmak.** *Uyarı:* Olgun kütüphane.
16. **Sandbox'ı mükemmelleştirmeye çalışmak.** *Uyarı:* Faz 10'da WSL2 ile gelir.
17. **Agent'ın bıraktığı git karmaşasını Hub'a otomatik temizletmek (E1 sonrası yeni risk).** *Uyarı:* "Nasılsa `git reset --hard` atıveririz" en tehlikeli kolaylıktır; kanıtı yok eder. E.1 #9 bunu kalıcı olarak yasaklar.

---

## X. Nihai İnşa Sırası

```
 1. Faz 0: ADR-0001 … ADR-0017'yi yaz; §0.2 format kararlarını kaydet; belgeyi dondur.
 2. Solution ve proje iskeleti; katman bağımlılık yönünü mimari testle kilitle.
 3. Domain: 15 tablo, 3 aggregate + durum makineleri (D1/D6/E1/E2)
    + Ready gate (D4) + organizasyon bloke mantığı (D5).
 4. EF Core + SQLite (WAL) + ilk migration + hub.config.yaml seed'i.
 5. Hub CLI iskeleti: org/project/repo list, task create/show.
    → Faz 1 TAMAM
 6. ProcessRunner: Job Object, üç seviyeli timeout, async stream, env allowlist.
    "Torun süreç ölüyor mu" testini geçir.
 7. FileSystemGuard: kanonik yol, org kökü, reparse-point denetimi.
    → Faz 2 TAMAM
 8. Workspace Manager: base commit sabitleme, worktree+branch, sertleştirme
    (YALNIZCA pre-push), commit grafiği + çalışma ağacı diff'i (add -N), reconciler.
    Mimari test: git tarih onarım komutları kod tabanında yok (E.1 #9).
    → Faz 3 TAMAM
 9. Evidence Store: içerik adresli blob, atomik yazım, redaksiyon desen kümesi.
10. Verification Engine: profil, allowlist'li kabuksuz çalıştırıcı, TRX/JUnit, baseline.
    → Faz 4 TAMAM
11. Context Pack Builder: montaj, öncelik, bütçe, manifest+hash.
12. Kapılar: K1 RuntimePolicyGate (E1) → K2 ScopeGuard → K3 SecretGuard;
    koşullu result commit.
13. ExecutionOrchestrator: durum makineleri, lease/heartbeat, adım sıralaması, audit.
14. AcceptanceService: I-A1…I-A11 (normal + override yolları, kapalı küme).
15. ScriptedAgentRuntime (8 mod) + fixture repo (HUB-001).
16. hub run / accept [--override --reason] / request-changes / reject / cancel /
    evidence show / workspace *
    → ★ Faz 5 TAMAM — DİLİM 1 (Loop Zero) ★
17. G6-1/G6-2/G6-3 kapılarını kapat.
18. ClaudeCodeRuntime → ★ Faz 6 — DİLİM 2 ★
19. İnceleme adımı + inceleme override kabul yolu → ★ Faz 7 — DİLİM 3 ★
20. G8-1/G8-2; CodexRuntime — orchestrator'a dokunmadan. → Faz 8
21. Minimal API + SSE + React UI (kabul ekranı + ayrı override akışı). → Faz 9
22. Sertleştirme + WSL2 sert izolasyon → G-K1…G-K5 → **Kronos kapısı açılır**. → Faz 10
```

**16. adım projenin doğum anıdır.**

---

## Y. Kapanış — Final Errata Doğrulaması

### Y.1 Revizyon özeti

| Değişiklik | Uygulandı | Ana etki |
|---|---|---|
| **E1** — Beklenmeyen agent commit'i bloklayıcı | ✔ | Yeni outcome `RuntimePolicyViolation`; commit/doğrulama/inceleme yok; worktree korunur; Hub git geçmişini asla onarmaz (E.1 #9) |
| **E2** — İnceleme override'ı / kabul semantiği | ✔ | `Ready → Accepted` kenarı yalnızca `ReviewBlocked` için; `Outcome` yeniden yazılmaz; kapalı küme + varsayılan reddet (E.1 #10) |
| **M1** — `hub commit-result --override` kaldırıldı | ✔ | `--override` tüm sistemde tek anlam taşır; SecretGuard'ın bypass'ı yok |
| **M2** — `ReviewUnusable` bir `Execution.Outcome` oldu | ✔ | Override yasak listesi boşluksuz |
| **M3** — `CaptureChanges` sırası netleşti | ✔ | Önce kanıt, sonra K0→K1→K2→K3 |
| **M4** — K0/K1 çakışması öncelik kuralına bağlandı | ✔ | Outcome K0'dan; `UnexpectedAgentCommitDetected` teşhis alanı |
| **M5** — Korunan worktree uyarısı | ✔ | `hub run` tek satır uyarı |
| **M6** — J.4 override daraltıldı | ✔ | Kapalı küme tablosu |
| **Faz 0 formatları** | ✔ | `D:\cx\` · `<PROJECT>-<3 hane>` · fixture `HUB-001` (§0.2) |
| Çapraz referans denetimi | ✔ | 14 maddelik tutarlılık taraması (Y.2) |

### Y.2 Belge düzeyi tutarlılık denetimi (14 madde)

| # | Alan | Sonuç |
|---|---|---|
| 1 | Task durum geçişleri | P.1 tek kaynak; `Ready → Accepted` kenarı yalnızca override; B/S/T/X buna uyumlu |
| 2 | Execution outcome'ları | P.2 tek kaynak; 7 `Completed` outcome + 4 terminal hata; G.4, G.6, J.4, Q, S.2 aynı listeyi kullanıyor |
| 3 | Commit yaşam döngüsü | G.4 ADIM 3 tek commit noktası; E.1 #7 ile kilitli; ADR-0016 ile uyumlu |
| 4 | Kapı sıralaması | Her yerde **K0 → K1 → K2 → K3**; G.4, P.2, J.4, S.2, Q tutarlı |
| 5 | `UnexpectedAgentCommit` davranışı | Bloklayıcı: G.3, G.4, G.6, K.3 #14, K.4 #5, P.2, Q, S.2 #12–14, F.2, F.3, ADR-0005/0016 |
| 6 | Doğrulama uygunluğu | I.3 + P.2: yalnızca K0–K3 geçtiyse ve `ResultCommitSha` varsa |
| 7 | İnceleme uygunluğu | J.1 + J.4 + P.2: yalnızca `ResultCommitSha` varsa ve doğrulama geçtiyse |
| 8 | Normal kabul | P.5 I-A1; S.2 #1–3; I.6(a) |
| 9 | İnceleme override kabulü | P.5 I-A2/I-A3; J.4; S.4 #4; I.6(c); ADR-0014 |
| 10 | Worktree release kuralları | G.6 tablosu tek kaynak; `ResultCommitSha == null` → korunur; Q ile birebir |
| 11 | Loop Zero kabul kriterleri | S.2: 19 kriter; E1 için #12–14, E2 negatif testi için #17, M1 için #9 |
| 12 | Hata/kurtarma | Q tablosuna "Override?" sütunu eklendi; beş genel ilke |
| 13 | ADR ifadeleri | ADR-0005, 0014, 0015, 0016 revize; 0004'e `D:\cx\` işlendi; yeni ADR açılmadı (kapsam büyümesin) |
| 14 | Faz çapraz referansları | Kanonik liste T; F.4→Faz 8, L.3→Faz 10, W#9→Faz 9, V.2 kapıları, X adımları hizalı |

### Y.3 Denetçinin istediği açık beyanlar

**1. E1 her yerde tutarlı biçimde bloklayıcı mı?**
> **EVET.** `RuntimePolicyViolation` şu 12 yerde aynı semantikle geçiyor: G.3 (katman tablosu), G.4 (K1 kapısı + sonuç tablosu), G.6 (worktree korunur), I.5 (`GitCommitGraph` kanıtı), I.6(b) (örnek), J.4 (inceleme çalışmaz + override yok), K.3 #14, K.4 #5, P.2 (outcome tablosu + akış kuralları), Q (iki satır), S.2 (#12, #13, #14), ADR-0005 ve ADR-0016. Hiçbir yerde "yalnızca bulgu" ifadesi kalmadı. Hub'ın git geçmişini onarması E.1 #9 ile kalıcı olarak yasaklandı ve S.2 #13 ile test edilir.

**2. `ReviewBlocked` override'ı durum makinesi altında artık uygulanabilir mi?**
> **EVET.** P.1'e açık bir `Ready → Accepted` kenarı eklendi; koşulu son Execution'ın `Completed(ReviewBlocked)` olması ve P.5 I-A3'teki altı ön koşulun sağlanmasıdır. `AcceptanceService` I-A1/I-A2 ile iki yolu ayırır. Yeni tablo eklenmedi — `AcceptanceDecision` (`OverrideUsed`, `Reason`, `HumanCriterionVerdicts`, `Actor`, `DecidedAtUtc`) yeniden kullanıldı. `Execution.Outcome` değişmez (E.1 #11), bulgular korunur (I-A8).

**3. Başka bir execution outcome'u kabul override'ı ile geçilebilir mi?**
> **HAYIR.** Kapalı küme: yalnızca `ReviewBlocked`. Uygulama `switch` + açık `default: deny`; birim testi `VerificationFailed`, `ScopeViolation`, `SecretGuardBlocked`, `RuntimePolicyViolation`, `ReviewUnusable`, `Failed`, `TimedOut`, `Cancelled`, `Interrupted` ve `Passed` için reddi doğrular (E.1 #10, S.2 #17). Enum ileride büyürse varsayılan davranış reddetmektir. Ayrıca SecretGuard'ın eski bypass komutu tamamen kaldırıldı (M1).

**4. Faz 1 hâlâ bloksuz mu?**
> **EVET.** V.1: Faz 1–5 için dış bilinmeyen yok. Format kararları §0.2'de onaylandı. Tek ön koşul Faz 0'ın ADR kaydıdır. İlk dış kapı G6-1, yalnızca Faz 6'da devreye girer. E1 ve E2 hiçbir yeni dış bağımlılık getirmedi; ikisi de Faz 5'te yazılacak mevcut bileşenlerin içinde yer alır.

**5. Mimari dondurulabilir mi?**
> **EVET.** İki denetim çelişkisi (E1, E2) kapatıldı; bunların mekanik olarak açtığı altı boşluk (M1–M6) kapatıldı; 14 maddelik belge düzeyi tutarlılık taraması temiz sonuç verdi; kapsam büyümedi (yeni tablo yok, yeni ADR yok, yeni bileşen yok; bir CLI komutu **kaldırıldı**).
>
> Açık kalan kapılar (G6-*, G7-1, G8-*, G10-1, G-K*) **mimari sorular değildir** — ortam doğrulaması ve kalibrasyondur; ait oldukları fazda kapanır ve mimariyi değiştirmez.
>
> **ARCHITECTURE FREEZE / DISCOVERY CLOSED önerilir. Faz 0 ADR kaydı tamamlandığında Faz 1 uygulaması başlayabilir.**

---

## Ek — Denetçi Hızlı Kontrol Listesi

| Soru | Cevap |
|---|---|
| `hub run` kabul edebilir mi? | Hayır — E.1 #6, P.1, S.2 #1–2 |
| Hub kendi korumasını atlatıyor mu? | Hayır — `pre-commit` yok; E.1 #8 |
| Hub agent'ın commit'ini siliyor/onarıyor mu? | Hayır — E.1 #9, S.2 #13, ADR-0005 |
| Beklenmeyen agent commit'i akışı durduruyor mu? | Evet — `RuntimePolicyViolation`, K1 (E1) |
| Başarısız execution commit atıyor mu? | Hayır — G.4 kapı tablosu, P.2, ADR-0016 |
| Commit'siz kanıt eksik mi? | Hayır — I.5 asgari kanıt tablosu (`add -N` diff + envanter + commit grafiği + hash'ler) |
| Commit'siz worktree silinebilir mi? | Hayır — G.6 değişmezi |
| SecretGuard'ın bypass'ı var mı? | Hayır — M1 ile kaldırıldı |
| Kabul override'ı hangi outcome'lar için? | Yalnızca `ReviewBlocked` — E.1 #10, P.5 I-A2/I-A4, S.2 #17 |
| Override `Execution.Outcome`'u değiştiriyor mu? | Hayır — E.1 #11, I-A7, I.6(c) |
| Kabul kriterleri makinece doğrulanmak zorunda mı? | Hayır — `MACHINE`/`HUMAN` beyanı (ADR-0015) |
| Agent beyanı outcome'a giriyor mu? | Hayır — I.1 Kademe C |
| Kronos ne zaman onboard edilebilir? | L.4 G-K1…G-K5 karşılandığında; kod ile bloke (ADR-0017) |
| Faz 1'in dış bağımlılığı var mı? | Yok — V.1, Y.3 #4 |
| Loop Zero büyüdü mü? | Hayır — S.2 kapsam listesi sabit; E1 mevcut kontrolün sonucunu değiştirdi, E2 tek negatif test ekledi |
| İlk çalışan çıktı ne zaman? | X adım 16 (Faz 5 sonu) |
