# CoreX Agent Hub — Mimari Keşif ve Uygulama Planı (v1)

**Belge türü:** Mimari karar önerisi / uygulama öncesi plan
**Durum:** Denetime hazır (implementation başlamadı)
**Tarih:** 2026-08-22
**Hedef okuyucu:** Bağımsız kıdemli mimar denetçisi

> Bu oturumda **hiçbir kod yazılmadı, hiçbir proje iskelesi kurulmadı, hiçbir bağımlılık yüklenmedi, hiçbir veritabanı oluşturulmadı, hiçbir commit atılmadı.** Aşağıdaki içerik yalnızca analiz, karar önerisi ve plandır.

---

## 0. Doğrulanmış Ortam Gerçekleri

Plan varsayım üzerine değil, bu makinede ölçülmüş gerçekler üzerine kurulmuştur. Denetçi bunları tekrar doğrulayabilir.

| Bileşen | Ölçülen değer | Plana etkisi |
|---|---|---|
| İşletim sistemi | Windows 11 Pro 10.0.26200 | Sandbox stratejisi Linux'tan farklı — bkz. K ve L |
| .NET SDK | 10.0.301 | `net10.0` hedeflenebilir |
| Git | 2.54.0.windows.1 | `git worktree`, `--porcelain=v2` mevcut |
| Node.js | v24.16.0 | Codex CLI ve JS test araçları için hazır |
| WSL2 | Kurulu, varsayılan dağıtım Ubuntu | Gerçek OS-seviyesi izolasyon için mevcut kaçış yolu |
| Claude Code | **2.1.240, kurulu** | Headless entegrasyon bugün mümkün |
| Codex CLI | **KURULU DEĞİL** (`codex: command not found`) | Codex tüm iddiaları doğrulanmamış sayılmalı |

### 0.1 Claude Code CLI — bu makinede doğrulanan yetenekler

`claude --help` (2.1.240) çıktısından teyit edilenler. Bunlar mimarinin taşıyıcı unsurlarıdır:

| Flag | Doğrulandı | Mimari kullanım |
|---|---|---|
| `-p, --print` | ✔ | Non-interactive çalıştırma |
| `--output-format json\|stream-json` | ✔ | Yapısal sonuç + canlı akış |
| `--input-format stream-json` | ✔ | İleride kontrollü çok turlu girdi |
| `--json-schema <schema>` | ✔ | **Şema doğrulamalı yapısal çıktı** — review findings için kritik |
| `--permission-mode manual\|plan\|acceptEdits\|auto\|dontAsk\|bypassPermissions` | ✔ | İzin profili motoru |
| `--tools <list>` / `--allowedTools` / `--disallowedTools` | ✔ | **Araç seviyesinde teknik kısıt** (`Bash(git *)` gibi desenler) |
| `--add-dir <dirs...>` | ✔ | Dosya sistemi erişim genişletme (varsayılan cwd kapsamı) |
| `--safe-mode` | ✔ | **CLAUDE.md, skills, plugins, hooks, MCP, custom commands devre dışı** — repo kaynaklı prompt injection'a karşı birinci savunma |
| `--setting-sources user,project,local` | ✔ | Repo içi `.claude/settings.json` etkisini kesme |
| `--strict-mcp-config` + `--mcp-config` | ✔ | MCP yüzeyini sıfırlama |
| `--system-prompt-file`, `--append-system-prompt-file` | ✔ (help'te gizli, hata mesajıyla teyit edildi) | **Context Pack enjeksiyon kanalı** |
| `--session-id <uuid>` | ✔ | Korelasyon ID'sini biz atarız — audit için ideal |
| `--no-session-persistence` | ✔ | Oturum sızıntısını engelleme |
| `-r/--resume`, `-c/--continue`, `--fork-session` | ✔ | Onarım turu için oturum devamı |
| `--max-budget-usd` | ✔ (flag var) | Maliyet tavanı — *abonelik auth altındaki davranışı doğrulanmalı* |
| `--model`, `--effort low..max`, `--fallback-model` | ✔ | Runtime profil parametreleri |
| `--debug-file <path>` | ✔ | Teşhis kanıtı |
| `claude setup-token` | ✔ | **Abonelik ile uzun ömürlü headless token** — ayrı API faturalandırması gerekmeyebilir |
| `--bare` | ✔ | **KULLANMAYIN**: OAuth/keychain okumaz, yalnızca `ANTHROPIC_API_KEY` ister — abonelik akışını kırar |
| `-w, --worktree` | ✔ | **Kullanılmayacak**: worktree sahipliği Hub'da kalmalı |

Bu tablo tek başına şu sonucu üretir: **izin modeli, yapısal çıktı ve prompt-injection savunmasının önemli kısmı Claude Code tarafında zaten teknik olarak mevcut.** Bunları prompt ile yeniden icat etmeye gerek yok.

---

## A. Yönetici Özeti — Sistemi Nasıl Anladım

### A.1 CoreX Agent Hub nedir

CoreX Agent Hub, **coding agent'ları çalıştıran bir kontrol düzlemidir (control plane).** Zekâ üretmez; zekâyı *sınırlar, besler, gözlemler ve kanıtlar.*

Üç işi yapar:

1. **İzolasyon** — Her agent çalışması, geliştiricinin çalışma dizinine değil, taahhüt (commit) sabitlenmiş, tek kullanımlık bir git worktree'sine karşı yürür.
2. **Bağlam sahipliği** — Kurumsal hafıza agent'ın sohbet geçmişinde değil, platformda durur. Her çalıştırma için deterministik, hash'lenmiş bir *Context Pack* üretilir.
3. **Kanıt** — Agent'ın "yaptım, testler geçti" beyanı hiçbir şey ifade etmez. Build/test/diff/exit-code gerçeklerini **platform kendisi** üretir ve saklar.

### A.2 Hangi problemi çözüyor

Bugünkü problem "agent yeterince akıllı değil" değil. Problem şu:

- Bir agent çalışmasının **ne yaptığını doğrulamak**, çalışmanın kendisinden pahalı.
- Aynı işi **tekrar üretmek** mümkün değil (prompt nerede, hangi commit'ten başladı, hangi kural seti geçerliydi?).
- Organizasyonlar arası **sızıntı riski** insan disiplinine bağlı.
- Ürün sayısı arttıkça **bağlam yeniden anlatma maliyeti** doğrusal büyüyor.

Hub bu dördünü mekanikleştirir.

### A.3 Ne DEĞİLDİR

- Bir sohbet uygulaması değildir.
- Bir "yapay zekâ ofisi / sanal çalışan simülasyonu" değildir.
- Bir otonom yazılım fabrikası değildir — insan onayı kaldırılamaz bir kapıdır.
- Bir SaaS ürünü değildir (v1'de tek kullanıcı, tek makine).
- Genel amaçlı bir workflow motoru değildir.
- Bir RAG platformu değildir.

### A.4 İlk değerli sonuç (first valuable outcome)

> **45 dakikalık bir kod incelemesini, 5 dakikalık bir kanıt incelemesine indirmek.**

Somut ifadesi: bir görev için Hub bana şunu verebilmeli —
`base commit` + `unified diff` + `build/test exit code'ları ve sayıları` + `kapsam ihlali var mı` + `bu paketin hash'i` → tek ekranda, tekrar üretilebilir şekilde.

Bu sağlanana kadar UI, çoklu agent, otonomi ve maliyet takibi **erken optimizasyondur**.

### A.5 Bu projenin en büyük varoluşsal riski

Hub, terminalde `claude` yazmaktan **daha kötü bir deneyim** üretirse ölür. Kazanma koşulu tek: *izolasyon + kanıt + tekrarlanabilirlik*. Hub bu üçünü sunmuyorsa yazılmamalıdır. Yol haritasındaki her faz bu üçünden birine hizmet etmelidir.

---

## B. Mimari Değerlendirme — KEEP / CHANGE / DEFER / REJECT

| # | Öneri | Karar | Gerekçe |
|---|---|---|---|
| 1 | Local-first, tek makine, tek kullanıcı | **KEEP** | Doğru. Dağıtık hiçbir gereksinim yok. |
| 2 | Evidence-first (AI beyanı ≠ kanıt) | **KEEP** | Sistemin varlık sebebi bu. |
| 3 | İnsan tek kabul makamı | **KEEP** | Doğru ve pazarlık dışı. |
| 4 | Git worktree ile görev izolasyonu | **KEEP** | En yüksek getirili tekil karar. |
| 5 | Worktree'lerin repo içinde `.agent-worktrees` altında olması | **CHANGE** | Repo kirlenmesi, `.gitignore` bağımlılığı, Windows 260-karakter yol sınırı, araçların taraması. Worktree kökü **repo dışında, kısa bir yolda**, Hub yönetiminde olmalı (`D:\cx\w\...`). |
| 6 | Sağlayıcıyı role sabitlememek (Claude≠implementer) | **KEEP** | Ucuz ve doğru; `AgentProfile` ile çözülür. |
| 7 | `IAgentRuntime` soyutlaması | **KEEP, ama daraltılmış** | 5 üyeli ince bir arayüz. Tek sağlayıcı varken "evrensel adapter" yazmak tuzak. İkinci implementasyon **ScriptedRuntime (test double)** olmalı — asıl soyutlama baskısını o üretir. |
| 8 | İlk dikey dilimin Codex review içermesi | **CHANGE** | Codex bu makinede **kurulu değil**; ilk dilim doğrulanamayan bir bağımlılığa yaslanmamalı. Dilim üçe bölünmeli (bkz. S). |
| 9 | Context Pack'in dev bir prompt bloğu olması | **CHANGE** | Pack **dosya olarak materyalize edilmeli**, hash'lenmeli, `--system-prompt-file` / `--append-system-prompt-file` ile verilmeli. Prompt blobu ne hash'lenir ne denetlenir. |
| 10 | `Policy` ayrı bir varlık/motor | **REJECT (MVP)** | Kural DSL'i ve policy engine, 1 kullanıcı ve 2 rol için aşırı. Policy = kod + repo başına yapılandırma. |
| 11 | `AgentRun`, `Verification`, `Review` ayrı üst düzey varlıklar | **CHANGE** | Tek `ExecutionStep` (tipli) + `Finding` + `EvidenceArtifact`. Tek zaman çizelgesi, tek retry semantiği, tek durum makinesi. |
| 12 | Agent'ın commit atması | **REJECT** | Commit'i **Hub** atar, sabit kimlikle. Deterministik diff, gürültüsüz tarih, kaza ile push yok. |
| 13 | Önerilen 11 durumlu Task state machine | **CHANGE** | İki makine karışmış. Task kaba, Execution ayrıntılı olmalı; `Failed` Task'ta değil Execution'da yaşamalı (bkz. P). |
| 14 | Repair loop (otomatik onarım) | **DEFER** | Faz 9. MVP'de tek deneme + insana yükselt. |
| 15 | Secret store (şifreli kimlik deposu) | **DEFER** | MVP'de saklanacak sır **yok**: sağlayıcılar kendi auth'unu tutuyor. Asıl iş sırrı *saklamak* değil, **ortam değişkenlerini agent'a sızdırmamak** (allowlist). |
| 16 | SQLite | **KEEP** | Doğru. WAL + tek yazıcı + blob'lar dışarıda. |
| 17 | .NET / ASP.NET Core | **KEEP** | Ekosistem uyumu + süreç kontrolü + P/Invoke (Job Objects) için ideal. |
| 18 | React + TypeScript UI | **KEEP, ama ERTELE** | UI Faz 8. İlk "UI" Hub'ın kendi CLI'ıdır. |
| 19 | PostgreSQL / Redis / RabbitMQ / K8s | **REJECT** | Hiçbir gereksinim yok. |
| 20 | Vector DB / RAG | **REJECT (MVP)** | Bağlam kaynağı, sürüm kontrollü markdown. Embedding'e ihtiyaç kanıtlanmadı. |
| 21 | Organizasyon izolasyonu birinci sınıf | **KEEP, ama dürüstlükle** | v1'de **mantıksal** izolasyon (aynı OS kullanıcısı). Bu gerçek bir güvenlik sınırı değildir; artık risk açıkça kabul edilmelidir (bkz. L). |
| 22 | Reviewer'ın yazamaması | **KEEP + güçlendir** | Prompt yetmez; `--tools Read,Grep,Glob` + `--permission-mode plan` + **tek kullanımlık worktree** (yazsa bile atılır) + hash ile tespit. |
| 23 | Maliyet/kullanım takibi | **DEFER (ama alan aç)** | `--output-format json` metadata'sını ilk günden sakla, rapor sonra. |
| 24 | OpenTelemetry / event bus | **DEFER** | `System.Diagnostics.Activity` ile korelasyon; exporter yok. |
| 25 | Otonomi (agent-to-agent) | **REJECT** | Hedef değil, risk. |

### B.1 Varsayımlarınıza itirazlar

**İtiraz 1 — Asıl darboğaz agent değil, görev tanımı olacak.**
Non-interactive çalıştırma, interaktif çalıştırmadan *daha az yeteneklidir*: yol ortasında insan yönlendirmesi yok. Bu, görev tanımının kalitesini sistemin performans tavanı yapar. Bu yüzden Hub'ın erken yatırımı "daha çok agent" değil, **makinece kontrol edilebilir görev şablonu** olmalıdır.

Somut kural önerisi — bir Task `Draft`'tan çıkamaz, eğer:
- hedef repository yoksa,
- izinli/yasak dosya kapsamı (glob) yoksa,
- en az bir verification profile yoksa,
- kabul kriteri bir doğrulama komutuna bağlanamıyorsa.

*Doğrulanamayan kabul kriteri = tanımsız görev.*

**İtiraz 2 — "Kurumsal hafıza platformda" doğru, ama hafıza inşa etmeyin.**
v1'de kurumsal bağlam = organizasyon başına **sürüm kontrollü markdown dosyaları** (`corex-context-<org>` reposu). Hub bunları *derler*, üretmez. Bilgi tabanı yazmaya kalkışmak projeyi öldürür.

**İtiraz 3 — Abonelik ile programatik çalıştırmanın sınırları test edilmeli.**
`claude setup-token` abonelik için resmî headless yolu sunuyor. Yine de otomatik çağrı hacmi rate limit'e çarpar. Hub, ilk günden **eşzamanlı çalıştırma limiti (varsayılan 1)** ve **günlük çalıştırma bütçesi** taşımalı. Ayrıca aboneliğin otomasyon kullanım şartları uygulama öncesi teyit edilmelidir (V.1'de BLOCKING).

**İtiraz 4 — Windows'ta "read-only enforcement" kolay değil.**
Claude Code'un araç allowlist'i in-process gerçek bir kısıttır; ancak OS seviyesinde agent hâlâ kullanıcının yazabildiği her yere yazabilir (uzlaşma hâlinde). Gerçek çözüm ayrı OS kullanıcısı / WSL2 / container. v1 için doğru cevap: **atılabilir worktree + yazma tespiti**, yani "engelleme" yerine "önemsizleştirme".

---

## C. Önerilen MVP Kapsamı

### C.1 KAPSAM İÇİ

1. Organizasyon / Proje / Repository kataloğu (yapılandırma dosyasından seed edilir).
2. Task oluşturma ve `Ready` kapısı doğrulaması.
3. Execution yaşam döngüsü + tek yazıcı eşzamanlılık kontrolü.
4. **Workspace Manager**: base commit sabitleme, worktree + `agent/...` branch oluşturma, temizlik, kilit/yetim kurtarma.
5. **Process Runner**: Windows Job Object ile ağaç öldürme, iki seviyeli timeout, boşta-kalma timeout'u, asenkron stdout/stderr tüketimi, UTF-8, ANSI temizleme, **kabuk kullanmadan** argv ile çalıştırma.
6. **Context Pack Builder**: deterministik dosya montajı + SHA-256.
7. **Agent Runtime**: `ScriptedAgentRuntime` (test) + `ClaudeCodeRuntime`.
8. **Verification Engine**: repo başına sıralı komut profili, exit code + çıktı yakalama, TRX/JUnit sayaç ayrıştırma (opsiyonel).
9. **Evidence Store**: içerik adresli blob deposu + redaksiyon (secret scrubbing).
10. **Scope Guard**: diff dosya yollarının izinli glob'lara uygunluğunun makinece denetimi.
11. **Review Step**: read-only profil ile bağımsız inceleyici, `--json-schema` ile şema doğrulamalı bulgular.
12. Human acceptance (`accept` / `request-changes` / `reject`) + audit kaydı.
13. Hub CLI (ilk arayüz) + minimal REST API.
14. Yapısal loglama + korelasyon ID'leri.

### C.2 KAPSAM DIŞI (v1)

Policy DSL · workflow engine · otomatik onarım döngüsü · otomatik merge/push/deploy · React UI · maliyet raporlaması · çoklu kullanıcı · kimlik doğrulama/yetkilendirme · vector DB / RAG · Codex entegrasyonu (Faz 7) · çoklu repo görevleri · uzak worker · container/WSL sandbox (Faz 9) · OS seviyesi organizasyon izolasyonu (Faz 9) · MCP sunucu yönetimi · submodule / Git LFS desteği · Slack/bildirim · şifreli secret store.

---

## D. Domain Modeli

Prensip: **12 tablo, 3 aggregate.** Aggregate = tutarlılık sınırı, "önemli şey" değil.

### D.1 Aggregate: `Repository` (katalog)

| Varlık | Sorumluluk | İlişki | Yaşam döngüsü | MVP |
|---|---|---|---|---|
| `Organization` | İzolasyon kökü; slug, filesystem root, env allowlist | 1..* Project | Nadiren değişir | ✔ |
| `Project` | Ürün gruplaması (Nakliye360, ERP…) | Org'a ait, 1..* Repository | Nadiren | ✔ |
| `Repository` | Yerel yol, default branch, protected branch listesi, verification profile ref, kapsam kuralları | Project'e ait | Nadiren | ✔ |
| `VerificationProfile` | Sıralı komut adımları tanımı | Repository'e ait (ya da paylaşılan) | Nadiren | ✔ |
| `AgentProfile` | rol + provider + runtime + izin profili + model + limitler | Org'a ait | Nadiren | ✔ |

Bu beşi **yapılandırmadır**, iş akışı değil. Bir `hub.config.yaml`'den seed edilip DB'ye yazılır. UI'dan CRUD'a MVP'de gerek yok.

### D.2 Aggregate: `Task`

| Alan | Not |
|---|---|
| `TaskKey` | İnsan okunur, benzersiz: `N360-014` |
| `OrganizationId`, `ProjectId`, `RepositoryId` | Tek repo hedefi (çoklu repo ertelendi) |
| `Objective` | Ne yapılacak (serbest metin) |
| `AllowedScope[]`, `ForbiddenScope[]` | Glob; **makinece denetlenir** |
| `AcceptanceCriteria[]` | Her biri ideal olarak bir verification adımına bağlı |
| `State` | Draft / Ready / Running / AwaitingAcceptance / Accepted / Rejected / Cancelled |
| `Version` | Optimistic concurrency |

`Task` **execution'ları içermez**; onlara ID ile referans verir. Neden: bir execution onlarca MB kanıt üretir; aynı aggregate içine koymak her yüklemede felaket olur.

### D.3 Aggregate: `Execution`

Bir görevin **tek denemesi**. Değişmez (immutable) kimlik: `TaskId + AttemptNo`.

| Alan | Not |
|---|---|
| `BaseCommitSha` | **Tekrar üretilebilirliğin çıpası** |
| `BranchName`, `WorktreePath`, `WorkspaceState` | Active / Released / Orphaned |
| `ContextPackHash` | Hangi bağlamla çalıştı |
| `State`, `Outcome` | bkz. P |
| `PreviousExecutionId?` | Onarım turu zinciri |
| `Lease` | HostProcessId + HostStartedAtUtc + HeartbeatUtc → çökme tespiti |

Alt varlıklar:

| Varlık | Sorumluluk | MVP |
|---|---|---|
| `ExecutionStep` | Sıralı adım; tip: `PrepareWorkspace \| BuildContext \| Agent \| CaptureChanges \| Verify \| Review`; her biri kendi mini durum makinesine sahip | ✔ |
| `CommandResult` | Verify adımının çocuğu: executable, argv, exitCode, süre, çıktı blob ref'leri | ✔ |
| `Finding` | Review adımının çocuğu: severity, kategori, dosya, satır, başlık, gerekçe, confidence | ✔ |
| `EvidenceArtifact` | Herhangi bir adıma bağlı içerik adresli blob referansı (sha256, boyut, tür, redaksiyon sayısı) | ✔ |
| `AcceptanceDecision` | Execution'a bağlı insan kararı: Accept / RequestChanges / Reject + aktör + not + zaman | ✔ |
| `AuditEvent` | Append-only olay kaydı (kim, ne, ne zaman, hangi korelasyon) | ✔ |

### D.4 Value object'ler (tablo değil)

`GitRef`, `CommitSha`, `ScopeGlob`, `Severity`, `PermissionProfile`, `Duration`, `ContentHash`.

### D.5 Bilinçli olarak **modellenmeyenler**

- `ContextPack` bir aggregate değildir → içerik adresli bir **artifact**'tır. Sürümleme = hash.
- `Policy` bir varlık değildir → kod + repo yapılandırması.
- `Approval` bir aggregate değildir → Execution üzerinde bir karar kaydı.
- `AgentRun` ayrı bir varlık değildir → `ExecutionStep(type=Agent)`.

### D.6 Bounded context'ler (modüler monolit içinde)

`Catalog` → `Orchestration` → `Workspace` · `Runtime` · `Verification` · `Evidence` · `Audit`.
Bunlar ayrı servis değil, ayrı **namespace ve proje** sınırlarıdır. Bağımlılık yönü tek yönlü olmalı; `Orchestration` diğerlerini bilir, tersi değil.

---

## E. Bileşen Mimarisi

```
┌──────────────────────────────────────────────────────────────┐
│ Sunum                                                        │
│   CoreX.Hub.Cli   (ilk arayüz)                               │
│   CoreX.Hub.Api   (ASP.NET Core minimal API + SSE)           │
│   [Faz 8] React SPA — Api tarafından statik servis edilir    │
└──────────────────────────┬───────────────────────────────────┘
                           │ komut / sorgu
┌──────────────────────────▼───────────────────────────────────┐
│ Application — CoreX.Hub.Application                          │
│   ExecutionOrchestrator (tek gerçek durum sahibi)            │
│   TaskService · AcceptanceService · ExecutionQueue           │
│   ScopeGuard · BudgetGuard                                   │
└───┬───────────┬────────────┬───────────┬────────────┬────────┘
    │           │            │           │            │
┌───▼─────┐ ┌───▼──────┐ ┌───▼──────┐ ┌──▼───────┐ ┌──▼────────┐
│Workspace│ │ Context  │ │  Agent   │ │ Verify   │ │ Evidence  │
│ Manager │ │ Builder  │ │ Runtimes │ │ Engine   │ │  Store    │
│(git)    │ │          │ │          │ │          │ │(blob+redact)│
└───┬─────┘ └──────────┘ └────┬─────┘ └────┬─────┘ └───────────┘
    │                         │            │
    └─────────┬───────────────┴────────────┘
              │
   ┌──────────▼───────────────────────────┐
   │ ProcessRunner  (Job Object, timeout, │
   │ stream tüketimi, env allowlist)      │  ← TEK süreç çıkış noktası
   └──────────┬───────────────────────────┘
              │
   ┌──────────▼───────────────────────────┐
   │ FileSystemGuard (kanonik yol, org kökü,│
   │ reparse-point/symlink kaçış denetimi) │  ← TEK dosya sistemi çıkış noktası
   └───────────────────────────────────────┘

   Persistence: EF Core + SQLite (WAL)  |  Blob: içerik adresli dosya deposu
```

### E.1 Bağlayıcı kurallar (mimari değişmezler)

1. **Süreç başlatan tek yer `ProcessRunner`'dır.** Kod tabanının başka hiçbir yerinde `Process.Start` çağrısı bulunmayacak. Bir mimari test (`ArchUnitNET` veya basit bir Roslyn analyzer) bunu CI'da zorunlu kılmalı.
2. **Dosya sistemi yolu üreten/çözen tek yer `FileSystemGuard`'dır.**
3. **Kabuk (shell) yoktur.** Her çalıştırma `executable + string[] argv` şeklindedir. Komut enjeksiyonu bu kararla kaynağında ölür.
4. **Alt katmanlar Application'ı bilmez.** `Workspace`, `Runtime`, `Verification`, `Evidence` birbirini de bilmez; yalnızca Application onları koordine eder.
5. **Agent runtime'ı git bilmez.** Ona hazır bir dizin ve bir prompt verilir; branch/commit kararları Hub'ındır.

---

## F. Agent Runtime Mimarisi

### F.1 Ortak soyutlama — kasıtlı olarak ince

```
IAgentRuntime
  Capabilities  Describe()
  Task<AgentRunResult> RunAsync(AgentRunRequest req, IProgress<AgentEvent> sink, CancellationToken ct)
```

`AgentRunRequest` (sağlayıcıdan bağımsız):
`WorkingDirectory` · `SystemPromptFile` · `AppendSystemPromptFile` · `UserPrompt` · `PermissionProfile` · `ModelHint` · `EffortHint` · `StructuredOutputSchema?` · `StructuredOutputPath` · `EnvironmentAllowlist` · `SoftTimeout` · `HardTimeout` · `IdleTimeout` · `CorrelationId` · `SessionId?` · `ResumeSessionId?`

`AgentRunResult`:
`Outcome (Completed/Failed/TimedOut/Cancelled/Crashed)` · `ExitCode` · `ProviderSessionId` · `StructuredOutput?` · `RawStdoutBlobRef` · `RawStderrBlobRef` · `UsageMetadata (serbest sözlük)` · `Duration`

`Capabilities` (sağlayıcı farklarını *gizlemek yerine ilan etmek* için):
`SupportsStructuredSchema` · `SupportsSessionResume` · `SupportsToolAllowlist` · `SupportsCostCap` · `SupportsStreaming` · `SupportsReadOnlyMode`

> Soyutlamanın altın kuralı: **farkları saklama, ilan et.** Orchestrator bir yeteneği yoksa telafi etmez; adımı `Unsupported` olarak işaretler. "Evrensel adapter" yazma dürtüsüne bu şekilde direnilir.

### F.2 `ScriptedAgentRuntime` — birinci implementasyon

AI değil. Bir fixture patch'i uygular, sabit yapısal çıktı yazar, opsiyonel olarak hata/timeout/çökme simüle eder.

Neden ilk sırada: **tüm pipeline'ı, durum makinesini, kanıt akışını ve kurtarmayı ağ/AI olmadan, CI'da deterministik test edilebilir hale getirir.** Ayrıca `IAgentRuntime` üzerinde ikinci implementasyon baskısını erkenden yaratır — soyutlamayı tek sağlayıcıya göre şekillendirmeyi engeller.

### F.3 `ClaudeCodeRuntime`

Yaklaşım: **CLI subprocess.** Gerekçe:
- .NET için **resmî bir Claude Agent SDK yoktur** (SDK Python ve TypeScript'tir). Sadece SDK için Node/Python sidecar taşımak, kazandırdığından fazla operasyonel yük getirir.
- CLI zaten yapısal çıktı, şema doğrulama, izin modu, araç allowlist ve oturum kimliği sunuyor (bkz. §0.1).
- Süreç sınırı, iptal ve timeout kontrolünü *bize* verir.

Uygulayıcı (implementer) profili — çağrı iskeleti:

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
  --disallowedTools "Bash(git push*)" "Bash(git commit*)" ...
  --permission-mode acceptEdits
  --model <profil>  --effort <profil>
  --add-dir <context-pack-dir>
  --debug-file <evidence>/claude-debug.log
  # prompt stdin'den
```

İnceleyici (reviewer) profili — farkı önemli:

```
claude -p
  --output-format json
  --json-schema <findings-schema.json>
  --safe-mode --setting-sources user --strict-mcp-config
  --tools Read,Glob,Grep          # Edit/Write/Bash YOK
  --permission-mode plan
  --system-prompt-file <pack>/reviewer.md
```

**Doğrulanması gerekenler (uygulama öncesi):**
- `--safe-mode` + `--setting-sources user` altında abonelik auth'unun headless çalıştığı (`claude setup-token` ile).
- `--json-schema` şema ihlalinde davranışı (exit code mu, hata mesajı mı).
- `--max-budget-usd`'nin abonelik auth altında etkili olup olmadığı.
- `stream-json` olay şemasının sürüm kararlılığı → **ham stdout her zaman blob olarak saklanmalı**, ayrıştırma başarısız olsa bile kanıt kaybolmasın.

### F.4 `CodexRuntime`

**Durum: doğrulanmamış.** Codex CLI bu makinede kurulu değil. Bu nedenle:

- Faz 7'ye alınmıştır; MVP bağımlılığı değildir.
- Doğrulanması gerekenler: non-interactive çalıştırma yüzeyi (`codex exec` benzeri), yapısal çıktı biçimi, sandbox modları (`read-only` / `workspace-write`), onay politikası flag'leri, oturum devamı, kullanım metadata'sı, **Windows üzerinde native sandbox desteğinin olup olmadığı**.
- Kuvvetli beklenti: Codex'in sandbox uygulaması Windows'ta zayıf/desteklenmiyor olabilir; bu durumda çalıştırma **WSL2 (Ubuntu, mevcut)** üzerinden yapılır. Bu, Hub'a *yol çevirimi* (`D:\cx\w\x` ↔ `/mnt/d/cx/w/x`) sorumluluğu ekler — küçük ama gerçek bir iş kalemi.
- Kimlik doğrulama: mevcut ChatGPT/Codex oturumu kullanılmalı; ayrı API faturalandırma mimarisi **kurulmamalı** (doğrulanana kadar).

**Ara çözüm önerisi (Faz 6):** İnceleyici, ilk aşamada **farklı profilli ikinci bir Claude Code çalıştırması** olsun — farklı model, sıfır paylaşılan oturum, salt-okunur araç seti, düşmanca (adversarial) system prompt. Sağlayıcı çeşitliliğinin bağımsızlık faydası gerçektir ama Faz 1 bloklayıcısı değildir. Böylece review pipeline'ı Codex'ten önce çalışır hale gelir ve Codex geldiğinde sadece runtime değişir.

### F.5 Süreç yaşam döngüsü — Windows'a özgü kritik detaylar

Bunlar planlanmazsa haftalar yer:

1. **Süreç ağacı öldürme.** `Process.Kill(entireProcessTree: true)` torunlar için güvenilir değildir. Çözüm: **Windows Job Object** — `CreateJobObject` + `JOBOBJECT_EXTENDED_LIMIT_INFORMATION` içinde `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE` + `AssignProcessToJobObject`. Job handle kapanınca tüm ağaç ölür. Ek olarak `ActiveProcessLimit` ve `JobMemoryLimit` kotaları konabilir.
   *Yarış koşulu:* `Process.Start` ile job'a atama arasında doğan çocuklar kaçabilir. Faz 1'de kabul edilebilir; sertleştirme fazında `CreateProcess` P/Invoke + `CREATE_SUSPENDED` → assign → `ResumeThread`.
2. **Stdout/stderr kilitlenmesi.** Her iki akış da **asenkron ve eşzamanlı** tüketilmeli; `WaitForExit()` akışlar boşaltılmadan çağrılmamalı. Klasik deadlock kaynağı.
3. **Kodlama.** UTF-8 zorla; ANSI escape dizilerini kanıta yazmadan önce temizle (ham hâli ayrı blob olarak sakla).
4. **`.cmd` / `.bat` tuzağı.** .NET, `UseShellExecute=false` ile `npm.cmd` gibi batch shim'leri doğrudan çalıştıramaz. İki seçenek: (a) gerçek çalıştırılabiliri çöz (`node <...>/npm-cli.js`), (b) `cmd.exe /c` kullan — ama o zaman cmd argümanları yeniden ayrıştırır ve `& | < > ^ %` üzerinden enjeksiyon geri gelir. **Öneri:** (a) tercih; (b) zorunluysa argümanlar katı bir beyaz liste regex'inden geçmeli.
5. **Üç seviyeli timeout:** *soft* (stdin kapat, nazikçe bekle) → *idle* (N dakikadır çıktı yok — asılı testin tek göstergesi budur) → *hard* (job kapat, ağacı öldür).
6. **Ortam değişkenleri: allowlist, denylist değil.** Boş bir ortamdan başla; yalnızca açıkça listelenenleri ekle (`PATH`, `SystemRoot`, `TEMP`, `USERPROFILE`, sağlayıcı auth için gereken minimum). `GIT_TERMINAL_PROMPT=0`, `GIT_ASKPASS=<no-op>`, `NO_COLOR=1`, `CI=1` zorla. Bu tek karar, kimlik bilgisi sızıntısı yüzeyini büyük ölçüde kapatır.
7. **İptal (cancellation):** stdin kapat → grace → job kapat. Kısmi değişiklikler worktree'de kalır ve **kanıt olarak yakalanır** (silinmez).

---

## G. Git / Worktree Stratejisi

### G.1 Yerleşim — repo dışında

```
D:\cx\                                  (Hub workspace kökü — KISA yol, bilinçli)
  corexlogic\                           (org kökü)
    nakliye360\
      N360-014-1\                       (taskKey-attempt)
  kronos\
    erp\
      KRN-007-1\
```

Repo içinde `.agent-worktrees` **kullanılmamalı**: `.gitignore` bağımlılığı, IDE/test runner'ların tarama alanına girmesi, `node_modules` ile birleşince Windows 260 karakter yol sınırı, yedekleme/AV taraması.

Ek önlemler: Windows uzun yol desteği açık; yönetilen repolarda `git config core.longpaths true`; Defender'da `D:\cx` için istisna (performans).

### G.2 Hazırlık akışı

```
1. Repo geçerli mi?              git rev-parse --git-dir
2. Submodule / LFS var mı?       varsa MVP'de REDDET (açık hata mesajıyla)
3. Base commit sabitle:          git rev-parse <defaultBranch>   → BaseCommitSha
4. Branch adı üret:              agent/<taskKey>/<attempt>       (çakışma imkânsız)
5. Worktree oluştur:             git worktree add -b <branch> <path> <BaseCommitSha>
6. Worktree-yerel sertleştirme:
     git -C <wt> config core.hooksPath <hub-hooks>   # pre-push ve pre-commit her zaman exit 1
     git -C <wt> config remote.origin.pushurl <geçersiz>
     credential.helper temizle
7. Kayıt: BaseCommitSha, branch, path, worktree oluşturma zamanı
```

**Kritik ve sıkça atlanan nokta:** worktree bir **commit'ten** oluşturulur, çalışma ağacından değil. Bu iki şeyi bedavaya çözer:
- Geliştiricinin ana dizini kirliyse (dirty) sorun değil — görev yine başlayabilir.
- **`.gitignore`'daki `.env`, `appsettings.Development.json`, sertifikalar vb. worktree'de hiç bulunmaz.** Yani izolasyon, aynı anda bir sır sızıntısı önlemidir. (Kalan risk: repoya *commit edilmiş* sırlar — bkz. K.)

### G.3 Korumalı branch'ler

Yapısal olarak zaten korunuyor: git aynı branch'i iki worktree'de checkout etmeye izin vermez ve biz her zaman yeni bir `agent/...` branch'i açarız. Buna ek olarak:
- `Repository.ProtectedBranches` listesi + orchestrator'da açık kontrol,
- worktree-yerel `pre-commit`/`pre-push` hook'ları (her zaman başarısız),
- push/merge/deploy için **hiç kod yolu yazılmaz** (var olmayan özellik, en güvenli özelliktir).

### G.4 Commit politikası — commit'i Hub atar

Agent commit atmaz. Adım sırası:

```
Agent biter
  → git -C <wt> status --porcelain=v2       (değişiklik envanteri)
  → ScopeGuard: değişen yollar AllowedScope içinde mi?   (ihlal = kanıt bulgusu)
  → git -C <wt> add -A
  → git -C <wt> -c user.name="CoreX Agent Hub" \
        -c user.email="agent@corex.local" commit -m "agent: <taskKey> attempt <n>"
  → git -C <wt> diff <BaseCommitSha>..HEAD   → unified diff blob (kanıt)
  → ResultCommitSha kaydedilir
```

Faydaları: deterministik diff, sabit ve ayırt edilebilir yazar kimliği, agent-üretimi commit mesajı gürültüsü yok, kaza ile push imkânsız.

### G.5 Eşzamanlılık

- Farklı repolar paralel çalışabilir.
- `git worktree add/remove` aynı repo için **serileştirilir** (repo başına named mutex) — `.git` içinde kısa süreli kilit alır.
- Yönetilen repolarda `git config gc.auto 0`; bakım (`git gc`, `git worktree prune`) hiçbir execution aktif değilken Hub tarafından planlı yapılır.
- MVP varsayılanı: `MaxConcurrentExecutions = 1`. Gerekçe: rate limit, CPU, ve hata ayıklama kolaylığı. Yapılandırılabilir.

### G.6 Temizlik, yetim ve çökme kurtarma

| Durum | Davranış |
|---|---|
| Normal bitiş, kabul edildi | Diff ve commit zaten kanıt; worktree `remove`, branch **korunur** (silmek geri dönülemez) |
| Normal bitiş, reddedildi | Aynı; branch korunur, `WorkspaceState=Released` |
| `worktree remove` başarısız (dosya kilidi) | `dotnet build-server shutdown` → job kapat → 3 kez artan gecikmeyle yeniden dene → hâlâ olmuyorsa `WorkspaceState=Orphaned`, execution **yine de tamamlanır** (temizlik iş akışını bloklamaz) |
| Hub çöktü / makine yeniden başladı | Açılışta reconciler: non-terminal execution'lar `Interrupted`; disk taraması + `git worktree list --porcelain` ile DB karşılaştırması; kayıtsız worktree'ler `Orphaned` olarak raporlanır — **otomatik silinmez** |
| Bayat kilit (`.git/worktrees/<x>/locked`) | `git worktree prune` + açık operatör komutu; sessiz zorla silme yok |
| Kısmi değişiklikler | Silinmez. Yakalanır, commit edilir, kanıt olur. |

Temizlik için ayrı bir CLI komutu: `hub workspace reconcile [--purge-orphans]`. Yıkıcı işlem daima açık bayrak ister.

---

## H. Context (Bağlam) Mimarisi

### H.1 Kaynaklar ve öncelik

Yüksekten düşüğe (çakışmada üstteki kazanır):

```
1. Task'a özgü talimat (objective, scope, acceptance criteria)
2. Repository kuralları        (context repo: repos/<repo>/*.md)
3. Project kuralları           (context repo: projects/<project>/*.md)
4. Organization kuralları      (context repo: org.md, conventions.md, adr/)
5. Hub global kuralları        (Hub içinde gömülü: güvenlik/çıktı sözleşmesi)
6. Canlı git gerçekleri        (base commit, branch, değişen dosya yok)
```

Not: 5 numaralı katman (çıktı sözleşmesi, yasaklı eylemler) **en altta değil, değiştirilemez** olmalıdır — üst katmanlar onu ezemez. Uygulama: Hub kuralları ayrı bir `--system-prompt-file` içinde, proje kuralları `--append-system-prompt-file` içinde.

### H.2 Depolama

- Bağlam kaynağı: organizasyon başına **git reposu** (`corex-context-corexlogic`, `corex-context-kronos`). Markdown. Sürüm kontrolü zaten var, review süreci zaten var, diff zaten var.
- Hub bu repoyu okur, **derler**, üretmez.
- Vector DB yok, embedding yok, chunking yok. Gerekçe: v1'de bağlam hacmi insan tarafından yazılmış birkaç yüz KB'dir; alaka seçimi *dizin yapısıyla* çözülür, benzerlik aramasıyla değil.

### H.3 Materyalizasyon (kritik tasarım kararı)

Context Pack, prompt string'i değil, **diskte bir dizindir**:

```
<evidence>/<executionId>/context/
  system.md          ← Hub sözleşmesi: rol, yasaklar, çıktı formatı
  rules.md           ← org + project + repo kuralları (birleştirilmiş)
  task.md            ← objective, scope, acceptance criteria
  repo-state.json    ← baseCommit, branch, defaultBranch, dosya sayısı
  decisions/         ← ilgili ADR'lerin kopyası
  manifest.json      ← her dosya için sha256 + kaynak dosya yolu + sürüm
```

Bu dizin `--add-dir` ile agent'a okunur kılınır; `system.md` ve `rules.md` prompt dosyası olarak geçirilir; `task.md` içeriği stdin'e verilir.

Faydaları:
- **Hash'lenebilir** → `ContextPackHash` execution kaydına yazılır → tekrar üretilebilirlik.
- **Denetlenebilir** → denetçi tam olarak neyin verildiğini okuyabilir.
- **Sağlayıcıdan bağımsız** → Codex de aynı dizini okur.
- `manifest.json` sayesinde "hangi kural sürümü geçerliydi" sorusu cevaplanır.

### H.4 Repo içi `CLAUDE.md` / `AGENTS.md` sorunu

Bunlar **güvenilmeyen girdidir** (prompt injection yüzeyi). Politika:

- Varsayılan: `--safe-mode` ile devre dışı. Agent yalnızca Hub'ın verdiği bağlamı görür.
- Bir repo kuralları gerçekten faydalıysa, o dosya **context repoya açıkça alınır** (insan kararı, git'te görünür). Güven, sessiz bir varsayım değil, kayıtlı bir karar olur.

### H.5 Boyut kontrolü

- Pack üzerinde sert bir bütçe: varsayılan **64 KB toplam metin**. Aşılırsa build başarısız — sessiz kırpma **yasak**.
- `decisions/` yalnızca görevin `AllowedScope`'una veya etiketlerine göre filtrelenmiş ADR'leri içerir (basit etiket eşleşmesi, semantik arama değil).
- Geçmiş görev kanıtı pack'e **girmez**; yalnızca insan tarafından işaretlenmiş "kabul edilmiş kararlar" girer. Aksi hâlde pack çöp toplayıcıya döner.

---

## I. Doğrulama ve Kanıt Modeli

### I.1 Üç kademeli doğruluk — asla karıştırılmaz

| Kademe | İçerik | Güven | Durum makinesini etkileyebilir mi? |
|---|---|---|---|
| **A — Makine gerçeği** | exit code, commit SHA, dosya hash'i, unified diff, süre, test sayaçları, kapsam ihlali | Otoriter | **Evet** |
| **B — İnceleyici bulgusu** | şema doğrulamalı `Finding[]` | Yapısal görüş | Evet, ama yalnızca eşik kurallarıyla |
| **C — Agent beyanı** | uygulayıcının serbest metin özeti, "testler geçti" | Sıfır | **Hayır — asla** |

Bu ayrım şema seviyesinde zorlanmalı: `AgentClaim` kaydının hiçbir alanı `Execution.Outcome` hesabına girmez. Denetçi bu kuralı kodda tek bir yerde doğrulayabilmelidir.

### I.2 Verification profile

```yaml
# repository yapılandırması
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

Kurallar:
- `executable` **allowlist**'ten gelir (repo başına: `dotnet`, `git`, `node`, `npm`, `pnpm`…), yapılandırma zamanında mutlak yola çözülür.
- `args` bir dizidir; asla string birleştirme yok; asla kabuk yok.
- `resultParser`: `none | trx | junit`. Test sayaçlarını yapısal kanıta çevirir.

### I.3 Baseline problemi (sıkça atlanır)

Repo zaten kırıksa, kırık test agent'ın suçu değildir. Çözüm: `(RepositoryId, BaseCommitSha, ProfileHash)` anahtarıyla önbelleklenen bir **baseline çalıştırma**. Kanıt karşılaştırmalı sunulur:

```
test: base 840 passed / 2 failed  →  after 842 passed / 0 failed   (İYİLEŞME)
```

Bu, sistemin en ikna edici tek çıktısıdır ve maliyeti düşüktür (commit başına bir kez).

### I.4 Kanıt deposu

- Blob'lar içerik adresli: `blobs/<sha256[0:2]>/<sha256>`. Aynı çıktı tekrar üretilirse tekrar yazılmaz.
- `EvidenceArtifact` satırı: `{ id, executionId, stepId, kind, sha256, sizeBytes, mediaType, redactionCount, createdAt }`.
- `kind`: `ContextPack | AgentStdout | AgentStderr | AgentStructuredResult | GitStatus | GitDiff | CommandStdout | CommandStderr | TestReport | ReviewFindings | DebugLog`.
- **Redaksiyon yazma anında yapılır.** Desenler: `sk-…`, `ghp_…`, `AKIA…`, JWT şekli, `-----BEGIN … PRIVATE KEY-----`, `Password=`/`Pwd=` içeren bağlantı dizeleri, `Authorization:` başlıkları, yüksek entropili uzun token'lar. Redakte sayısı kaydedilir. **Kalan risk açıkça kabul edilir:** regex tabanlı redaksiyon eksiksiz değildir.
- SQLite'a blob konmaz. Sadece meta + hash.

### I.5 Kanıt özeti (insanın gördüğü şey)

```
Task N360-014 · Execution #1
Base       9f3c1a2  (main)          Branch  agent/N360-014/1
Result     4b77e01                  Context 3af9…  (64 dosya, 41 KB)

Implementation   Claude Code 2.1.240 · opus · effort=high · 6m12s
Scope            12 dosya değişti · ihlal: YOK

Verification
  restore  PASS   0   18s
  build    PASS   0   1m04s
  test     PASS   0   2m51s     842 passed / 0 failed  (baseline: 840/2)

Review           read-only profile · 3 bulgu
  Critical 0 · Major 0 · Minor 2 · Info 1

Agent claim      (bilgi amaçlı, kanıt değil)
  "Refactored shipment pricing; added 2 tests."

Status  READY_FOR_HUMAN_ACCEPTANCE
```

---

## J. İnceleme (Review) Modeli

### J.1 Salt-okunurluk nasıl gerçekten sağlanır

Katmanlı savunma, güçlüden zayıfa:

1. **Atılabilir worktree.** İnceleyici, uygulayıcınınkinin *ayrı bir kopyasında* çalışır (aynı commit, farklı worktree). O kopya inceleme sonrası koşulsuz silinir. Yazsa bile hiçbir şeye ulaşmaz.
2. **Değişiklik tespiti.** İnceleme öncesi ve sonrası worktree ağacı hash'lenir (`git status --porcelain=v2` + dosya hash'leri). Fark varsa `ReviewerTamperDetected` kanıt bulgusu üretilir ve inceleme **geçersiz** sayılır.
3. **Araç kısıtı.** `--tools Read,Glob,Grep` — Edit/Write/Bash tanımlı değil. Bu in-process gerçek bir kısıttır.
4. **İzin modu.** `--permission-mode plan`.
5. **Prompt.** En zayıf katman; yalnızca yukarıdakilerin üstüne eklenir, onların yerine geçmez.

### J.2 Diff-only mü, tüm repo mu?

Her ikisi de yanlış uçtur:
- Yalnızca diff → mimari ihlaller görünmez ("bu katman bu servisi çağırmamalı").
- Tüm repo serbest gezinti → gürültü, maliyet, ve kapsam dışı bulgular.

**Öneri: diff-anchored, on-demand okuma.** İnceleyiciye unified diff + salt-okunur worktree verilir. Bulgular diff'e çıpalanmak zorundadır; değişmemiş dosyalara ait bulgular şemada `outOfScope: true` ile işaretlenir ve **bloklamaz** (ayrı bir "gözlem" listesine düşer).

### J.3 Bulgu şeması (şema doğrulamalı)

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

Claude Code'un `--json-schema` bayrağı bunu runtime'da zorlar. Şema ihlali → **bir kez** şema hatasıyla yeniden dene → yine ihlal → adım `Failed`, sonuç `ReviewUnusable`. Serbest metin ayrıştırma yapılmaz.

### J.4 Kapılar (gates)

| Koşul | Sonuç |
|---|---|
| Herhangi bir doğrulama adımı `required` ve FAIL | `VerificationFailed` — inceleme **hiç çalıştırılmaz** (para ve zaman israfı) |
| Kapsam ihlali | `ScopeViolation` — bloklayıcı |
| `Critical` bulgu ≥ 1 | `ReviewBlocked` |
| `Major` bulgu ≥ eşik (varsayılan 1) | `ReviewBlocked` (yapılandırılabilir) |
| Yalnızca Minor/Info | `ReadyForAcceptance` (bulgular gösterilir) |

İnsan her zaman **gerekçe kaydederek** kapıyı geçebilir (`hub accept --override --reason "..."`). Override sessiz olamaz; audit'e yazılır.

---

## K. Güvenlik Tehdit Modeli (MVP)

### K.1 Varlıklar

Kaynak kodu (iki organizasyon) · commit edilmiş sırlar · geliştiricinin dosya sistemi ve SSH/git kimlik bilgileri · bulut/DB kimlik bilgileri (env veya profillerde) · sağlayıcı auth token'ları · Hub veritabanı ve kanıt deposu · geliştirici iş istasyonunun bütünlüğü.

### K.2 Güven sınırları

```
[İnsan operatör]              ── güvenilir
   │
[Hub süreci]                  ── güvenilir (bizim kodumuz)
   ├── SINIR 1 ──────────────────────────────────────────────
   │   [Agent süreci]         ── YARI GÜVENİLİR
   │       girdisi güvenilmez, çıktısı güvenilmez,
   │       yetkisi kullanıcının yetkisi kadar
   ├── SINIR 2 ──────────────────────────────────────────────
   │   [Repo içeriği]         ── GÜVENİLMEZ VERİ (kod, README, CLAUDE.md, issue metni)
   ├── SINIR 3 ──────────────────────────────────────────────
   │   [Doğrulama komutları]  ── repo'nun build script'leri = keyfî kod çalıştırma
   └── SINIR 4 ──────────────────────────────────────────────
       [Ağ / paket kayıtları] ── güvenilmez (postinstall script'leri)
```

**Dürüst tespit:** Sınır 1 v1'de OS tarafından zorlanan bir sınır değildir. Agent, geliştirici kullanıcısı olarak çalışır ve o kullanıcının okuyabildiği her şeyi okuyabilir.

### K.3 Senaryolar ve önlemler

| # | Senaryo | Olasılık | Etki | Önlem (v1) | Kalan risk |
|---|---|---|---|---|---|
| 1 | Repo içeriğinden prompt injection (`CLAUDE.md`, README, kod yorumu) | Yüksek | Yüksek | `--safe-mode`, `--setting-sources user`, `--strict-mcp-config`; bağlam yalnızca Hub'dan | Kod yorumları hâlâ okunuyor — kabul edilir |
| 2 | Doğrulama komutu üzerinden komut enjeksiyonu | Orta | Kritik | Kabuk yok, argv dizisi, executable allowlist | `cmd.exe` shim yolu kullanılırsa argüman beyaz listesi şart |
| 3 | Kimlik bilgisi sızıntısı (env üzerinden) | Yüksek | Kritik | **Env allowlist** (boştan başla), `GIT_ASKPASS` no-op, `GIT_TERMINAL_PROMPT=0` | Sağlayıcı auth dosyaları diskte okunabilir kalır |
| 4 | `.env` / sertifika okuma | Orta | Yüksek | Worktree commit'ten oluşur → gitignore'lu sırlar yok | **Commit edilmiş** sırlar mevcut → pre-flight secret tarama önerilir |
| 5 | Yıkıcı komut (`rm -rf`, `git push --force`) | Orta | Kritik | `--disallowedTools` desenleri, worktree izolasyonu, pre-push/pre-commit hook'ları, push kod yolunun **hiç yazılmamış olması** | Agent yine de kullanıcı ev dizinine yazabilir |
| 6 | Bağımlılık yükleme script'i (postinstall) | Orta | Kritik | `npm ci --ignore-scripts` varsayılan; istisna açıkça yapılandırılır | Devre dışı bırakılırsa risk geri gelir |
| 7 | Path traversal / symlink-junction kaçışı | Düşük | Yüksek | `FileSystemGuard`: `GetFullPath` + org kökü prefix kontrolü + reparse-point tespiti | — |
| 8 | Organizasyonlar arası sızıntı | Orta | Yüksek | Org kökleri, DB'de org scope, org başına env allowlist, pack üretiminde org filtresi | **Mantıksal** — OS zorlaması yok (bkz. L) |
| 9 | Kanıtta sır görünmesi (log/diff) | Yüksek | Orta | Yazma anında redaksiyon | Regex eksiksiz değil |
| 10 | Kötü niyetli üretilmiş kod (backdoor) | Düşük–Orta | Kritik | Otomatik merge/push yok, insan kabulü, bağımsız inceleme | İnsan gözden kaçırabilir |
| 11 | İnceleyicinin kodu değiştirmesi | Düşük | Orta | Atılabilir worktree + hash ile tespit + araç kısıtı | — |
| 12 | Agent'ın kendi Hub kanıtını değiştirmesi | Düşük | Yüksek | Kanıt deposu worktree dışında; agent'a `--add-dir` ile verilmez | Aynı kullanıcı → OS düzeyinde erişilebilir |
| 13 | Sonsuz döngü / kaynak tüketimi | Orta | Orta | Job Object kotaları, üç seviyeli timeout, eşzamanlılık=1, günlük bütçe | — |
| 14 | Sağlayıcı token'ının çalınması | Düşük | Kritik | Hub token saklamaz; sağlayıcının kendi deposu kullanılır | Agent aynı kullanıcı olarak o depoyu okuyabilir |

### K.4 v1 için açıkça kabul edilen artık riskler

1. Agent, geliştirici kullanıcısının tüm dosya sistemine erişebilir (worktree dışına yazabilir).
2. Organizasyon izolasyonu mantıksaldır; kötü niyetli/uzlaşmış bir agent'a karşı dayanıklı değildir.
3. Redaksiyon en iyi çabadır.
4. Repoya commit edilmiş sırlar agent'a görünür.

**Yükseltme tetikleyicileri** (bunlardan biri gerçekleşirse Faz 9 sandbox işi öne alınır): güvenilmeyen/üçüncü taraf bir repo işlenecekse; Hub'ı ikinci bir insan kullanacaksa; bir organizasyon üretim kimlik bilgilerini repo yakınında tutuyorsa.

---

## L. Organizasyon İzolasyon Modeli

### L.1 Katman katman ne sağlanabilir

| Katman | v1 mekanizması | Gerçek zorlama? |
|---|---|---|
| Veritabanı | Her tabloda `OrganizationId`; EF Core **global query filter** + `OrgScope` ambient bağlamı; org'suz sorgu derlenemez/çalışmaz | ✔ (uygulama içi) |
| Dosya sistemi | Org başına kök (`D:\cx\corexlogic`, `D:\cx\kronos`); `FileSystemGuard` kanonikleştirme + prefix + reparse-point kontrolü | ✔ (uygulama içi) |
| Repository | `Repository.OrganizationId`; farklı org'a ait repo çözümü hata verir | ✔ (uygulama içi) |
| Bağlam | Context repo org başına; pack builder org sınırını geçemez | ✔ (uygulama içi) |
| Ortam değişkenleri | Allowlist **org başına** tanımlanır | ✔ (uygulama içi) |
| Kanıt / log | Blob dizini ve log dosyaları org'a göre ayrılır | ✔ (uygulama içi) |
| **Agent süreci** | Aynı Windows kullanıcısı | **✘ — zorlama yok** |
| **Sağlayıcı oturumu** | Ortak Claude Code auth durumu | **✘ — zorlama yok** |

### L.2 Dürüst özet

> v1'de organizasyon izolasyonu **kaza önleyicidir, saldırı önleyici değildir.** Yanlış repoya yazmayı, yanlış bağlamı derlemeyi, yanlış env'i sızdırmayı engeller. Uzlaşmış bir agent'ı engellemez.

### L.3 Gerçek izolasyon için yükseltme yolu (Faz 9, seçenekler)

| Seçenek | Maliyet | İzolasyon | Not |
|---|---|---|---|
| Org başına ayrı Windows yerel kullanıcısı + NTFS ACL | Orta | İyi | Sağlayıcı auth'un kullanıcı başına kurulması gerekir; Hub `CreateProcessAsUser` kullanmalı |
| Org başına WSL2 dağıtımı | Düşük–Orta | İyi | **WSL zaten kurulu**; yol çevirimi gerekir; Linux sandbox mekanizmaları erişilir hâle gelir |
| Org başına container (Docker Desktop) | Orta–Yüksek | Çok iyi | Repo bind-mount; ağ kontrolü kolay; Windows'ta I/O performansı bedeli |
| Org başına ayrı fiziksel/sanal makine | Yüksek | En iyi | Gerçek gereksinim doğarsa |

Öneri: **WSL2** en iyi maliyet/fayda oranını sunuyor ve Codex'in muhtemel Linux-sandbox gereksinimiyle aynı yatırımı paylaşıyor. Faz 9'da ilk denenecek seçenek budur.

---

## M. Kalıcılık (Persistence) Önerisi

### M.1 SQLite değerlendirmesi — uygun

Tek kullanıcı, tek süreç, yerel makine, düşük yazma hacmi. PostgreSQL için hiçbir gerekçe yok.

Yapılandırma:
- **WAL** modu (okuma/yazma çakışmasını azaltır).
- `busy_timeout = 5000`.
- **Tek yazıcı**: uygulama içinde global bir yazma semaforu. SQLite'ın tek-yazıcı gerçeğiyle savaşmak yerine kabullenmek.
- Zaman damgaları UTC, ISO-8601 metin.
- EF Core 10 + açık migration'lar (`__EFMigrationsHistory` ile).
- Yedekleme: `VACUUM INTO` ile tutarlı anlık görüntü; blob deposu ayrıca kopyalanır.

### M.2 Veri türüne göre ayrıştırma

| Veri | Nerede | Gerekçe |
|---|---|---|
| Katalog, Task, Execution, Step, Finding, Decision, Audit | SQLite tabloları | Sorgulanır, küçük, ilişkisel |
| Agent stdout/stderr, komut çıktıları, debug log | **Blob deposu** (`blobs/<aa>/<sha256>`), DB'de yalnızca hash+boyut | MB'lar; DB'yi şişirir; içerik adresleme tekrarı önler |
| Unified diff | Blob | Aynı |
| Context Pack | Blob (dizin tar/zip veya dosya başına blob + manifest) | Hash'lenebilir, değişmez |
| Test raporu (TRX/JUnit) | Ham → blob; **ayrıştırılmış sayaçlar → tablo** | Sorgulanabilir metrik + tam kanıt |
| Yapısal review bulguları | **Tablo** (`Finding`) + ham JSON blob | Filtreleme/raporlama gerekir |
| Yapısal agent sonucu (claim) | Blob + küçük özet alanı | Kanıt değil, sorgulanmasına gerek yok |

### M.3 Saklama (retention)

- Blob'lar içerik adresli olduğu için silme referans sayımı ister → MVP'de **silme yok**.
- `hub evidence gc --older-than 90d` komutu Faz 9'da; o zamana kadar disk kullanımı raporlanır.

---

## N. Teknoloji Önerisi

| Katman | Öneri | Karar gerekçesi |
|---|---|---|
| Runtime | **.NET 10 (`net10.0`)**, C# | Makinede 10.0.301 var; ekosistem uyumu; süreç kontrolü ve P/Invoke (Job Objects) için birinci sınıf destek |
| Host | **ASP.NET Core Minimal API**, tek proses, `IHostedService` ile execution worker | Ayrı bir kuyruk/worker altyapısına gerek yok |
| ORM | **EF Core 10 + SQLite** | Migration disiplini; Dapper'a düşmeyi gerektirecek performans senaryosu yok |
| Doğrulama | `FluentValidation` (opsiyonel) veya elle | Küçük tutulmalı |
| Loglama | **Serilog**, JSON dosya sink'i, günlük rolling | Yapısal; sonradan OTel'e köprülenebilir |
| CLI | **`System.CommandLine`** | Hub'ın ilk arayüzü |
| Test | `xUnit` + `FluentAssertions` + fixture git repoları | Deterministik E2E için `ScriptedAgentRuntime` |
| Mimari test | `NetArchTest` / basit Roslyn analyzer | `Process.Start` yasağını CI'da zorlamak için |
| UI (Faz 8) | **React + TypeScript + Vite**, ASP.NET Core tarafından statik servis | Karar gerekçesi aşağıda |
| Şema | JSON Schema (review bulguları) | Claude Code `--json-schema` ile doğrudan uyumlu |

### N.1 UI: React mi Blazor mu?

Tek kullanıcı, localhost, canlı log akışı → Blazor Server daha az hareketli parça sunar. **Ama belirleyici gereksinim diff görselleştirmesidir.** Monaco / `react-diff-view` gibi olgun diff bileşenleri JS ekosistemindedir; Blazor'da bunları sarmalamak net bir kayıptır. Ayrıca geliştirici zaten React+TS kullanıyor.

**Karar: React + TypeScript.** Ancak Faz 8'e kadar hiç yazılmaz; API `text/event-stream` (SSE) ile ilerleme yayınlar — WebSocket/SignalR'a MVP'de gerek yok.

### N.2 Reddedilen alternatifler

PostgreSQL (gereksiz) · Redis (durum tek süreçte) · RabbitMQ/Kafka (kuyruk = SQLite tablosu + `IHostedService`) · MediatR (dolaylılık katmanı, fayda yok) · Docker (Faz 9'da izolasyon için, altyapı için değil) · Node.js sidecar + Claude Agent SDK (CLI yeterli; ikinci runtime taşımaya değmez).

---

## O. Kaynak Ağacı Yapısı (öneri — oluşturulmadı)

```
corex-agent-hub/
├─ docs/
│  ├─ adr/                          ADR-0001.md …
│  └─ architecture/                 bu belge + diyagramlar
├─ src/
│  ├─ CoreX.Hub.Domain/             Varlıklar, value object'ler, durum makineleri,
│  │                                domain kuralları. BAĞIMLILIK YOK.
│  ├─ CoreX.Hub.Application/        ExecutionOrchestrator, servisler, portlar
│  │                                (arayüzler), ScopeGuard, BudgetGuard
│  ├─ CoreX.Hub.Infrastructure/     EF Core DbContext, migration'lar, repository
│  │                                implementasyonları, blob deposu, redaksiyon
│  ├─ CoreX.Hub.Execution/          ProcessRunner (Job Object interop),
│  │                                FileSystemGuard, env allowlist
│  │                                → TÜM süreç ve dosya sistemi çıkışı burada
│  ├─ CoreX.Hub.Workspace/          Git komutları, worktree yaşam döngüsü,
│  │                                reconciler, diff yakalama
│  ├─ CoreX.Hub.Context/            Context Pack derleyici + manifest/hash
│  ├─ CoreX.Hub.Agents/             Abstractions/  IAgentRuntime, sözleşmeler
│  │                                Scripted/     test double
│  │                                ClaudeCode/   CLI adapter + stream-json ayrıştırıcı
│  │                                Codex/        [Faz 7]
│  ├─ CoreX.Hub.Verification/       Profil modeli, çalıştırıcı, TRX/JUnit ayrıştırıcı
│  ├─ CoreX.Hub.Api/                Minimal API, SSE, composition root
│  ├─ CoreX.Hub.Cli/                İlk arayüz: task/exec/evidence/accept komutları
│  └─ CoreX.Hub.Web/                [Faz 8] React + Vite
├─ tests/
│  ├─ CoreX.Hub.Domain.Tests/
│  ├─ CoreX.Hub.Workspace.Tests/            gerçek git, geçici repolar
│  ├─ CoreX.Hub.Execution.Tests/            timeout/kill/hang senaryoları
│  ├─ CoreX.Hub.Verification.Tests/
│  ├─ CoreX.Hub.EndToEnd.Tests/             ScriptedAgentRuntime ile tam pipeline
│  └─ CoreX.Hub.ArchitectureTests/          Process.Start yasağı, katman yönü
├─ fixtures/
│  └─ repos/                        Sentetik git repoları (dotnet-sample, node-sample)
│                                   + hazır patch dosyaları
└─ hub.config.yaml                  Org/proje/repo/profil seed'i (gitignore'lu örnek)
```

`CoreX.Hub.Agents` tek proje olarak başlar (klasörlerle ayrılmış). Codex geldiğinde ve ayrı bağımlılıklar gerektiğinde bölünür — daha önce değil.

---

## P. Durum Makineleri

### P.1 Task (kaba)

```
Draft ──(readiness gate geçildi)──► Ready
Ready ──(execution başladı)───────► Running
Running ──(execution tamamlandı, outcome=Passed)──► AwaitingAcceptance
Running ──(execution Failed/TimedOut/Interrupted)─► Ready      (yeniden denenebilir)
AwaitingAcceptance ──(accept)─────► Accepted        [TERMİNAL]
AwaitingAcceptance ──(request-changes)──► Ready
AwaitingAcceptance ──(reject)─────► Rejected        [TERMİNAL]
herhangi (terminal olmayan) ──(cancel)──► Cancelled [TERMİNAL]
```

**Bilinçli sadeleştirme:** Task'ta `Failed` durumu **yok**. Başarısızlık execution'a aittir; görev hâlâ yapılabilir bir iştir. Bu, "başarısız görevleri temizleme" iş akışını gereksiz kılar. `Blocked` de yok — bloke bir görev `Ready`'dir ve `BlockReason` alanı taşır.

### P.2 Execution (asıl makine)

```
Created
 → PreparingWorkspace → BuildingContext → RunningAgent → CapturingChanges
 → Verifying → Reviewing → Completed
```

`Completed` bir `Outcome` taşır: `Passed | VerificationFailed | ReviewBlocked | ScopeViolation`.
Terminal hata durumları: `Failed` (beklenmeyen hata), `TimedOut`, `Cancelled`, `Interrupted` (host çöktü).

Kurallar:
- `Verifying` FAIL → `Reviewing` **atlanır** → `Completed(VerificationFailed)`.
- `CapturingChanges` kapsam ihlali bulursa → `Completed(ScopeViolation)`; inceleme atlanır.
- Durum geçişleri tek bir `ExecutionOrchestrator` içinde, tek transaction'da yazılır.
- Her geçiş `AuditEvent` üretir.

### P.3 ExecutionStep (mini)

```
Pending → Running → Succeeded | Failed | TimedOut | Cancelled | Skipped | Unsupported
```

`Unsupported`: runtime `Capabilities` gereği adım çalıştırılamadı (örn. şema desteği yok). Sessiz geçiş yok.

### P.4 Verification / Review

Ayrı durum makineleri **yoktur**; bunlar `ExecutionStep`'tir. Sonuçları veri olarak taşınır:
- `VerificationOutcome`: her `required` adım Succeeded ise `Pass`, aksi hâlde `Fail`.
- `ReviewOutcome`: `Clean | NonBlockingFindings | Blocked | Unusable`.

### P.5 Onay

`AcceptanceDecision` bir durum değil, **değişmez bir kayıttır**: `{ ExecutionId, Decision, Actor, Reason?, OverrideUsed, DecidedAtUtc }`. Task durumu bu kayda tepki verir.

### P.6 Eşzamanlılık ve yarış koşulları

| Risk | Önlem |
|---|---|
| Aynı Task'a iki execution | Filtreli benzersiz indeks: `TaskId` başına terminal olmayan tek execution |
| Eşzamanlı Task güncellemesi | `Version` alanıyla optimistic concurrency; `WHERE Version = @v` |
| Aynı repoda paralel `worktree add` | Repo başına named mutex |
| SQLite yazma çakışması | Global yazma semaforu + WAL + busy_timeout |
| Zombi execution (host öldü) | `Lease` (PID + host başlangıç zamanı + heartbeat); açılışta reconciler |
| İptal ile tamamlanma yarışı | Durum geçişi tek transaction; iptal yalnızca terminal olmayan durumdan kabul edilir |

---

## Q. Hata ve Kurtarma Modeli

| Olay | Tespit | Davranış | Kanıt |
|---|---|---|---|
| Claude süreci çöktü | Sıfırdan farklı exit code, kısa süre | Adım `Failed`; **kısmi değişiklikler yakalanır**, silinmez; doğrulama yine de çalıştırılır (opsiyonel) | stdout/stderr blob'ları, exit code |
| Claude asıldı (çıktı yok) | `IdleTimeout` (varsayılan 10 dk) | Nazik kapatma → job kill → adım `TimedOut` | Kısmi çıktı + timeout kaydı |
| Claude ağ/auth hatası | stream-json hata olayı veya exit code | Adım `Failed`, `AuthenticationRequired` sınıflandırması → **yeniden deneme yok**, insana yükselt | Hata blob'u |
| Codex çöktü (Faz 7) | Aynı | İnceleme `Unusable`; doğrulama kanıtı geçerli kalır; insan yine kabul edebilir | Aynı |
| Hub çöktü | Açılışta lease uyuşmazlığı | Execution → `Interrupted`; worktree korunur; `hub workspace reconcile` önerilir | Son yazılmış adımlar |
| Makine yeniden başladı | Aynı | Aynı; hiçbir şey otomatik silinmez | — |
| Test asıldı | `IdleTimeout` + `HardTimeout` | Job kill (tüm ağaç); adım `TimedOut`; doğrulama `Fail` | Kısmi test çıktısı |
| Süreç hiç çıkmıyor | Job Object handle kapatma | Ağaç garantili ölür | Kill kaydı |
| Worktree kilitli kaldı | `worktree remove` hatası | build-server shutdown → job kill → 3 artan gecikmeli deneme → `Orphaned` (iş akışı bloklanmaz) | Hata çıktısı |
| Kısmi değişiklikler var | Her zaman beklenir | Yakala, commit et, kanıt yap. Silme **asla otomatik değil** | Diff |
| Context üretimi başarısız (kural dosyası yok, bütçe aşıldı) | Builder doğrulaması | Execution başlamadan `Failed`; **sessiz kırpma yok** | Builder hata raporu |
| Doğrulama başarısız | Exit code | `Completed(VerificationFailed)`; inceleme atlanır; Task `Ready`'ye döner | Tam komut kanıtı |
| İnceleyici bloklayıcı bulgu üretti | Kapı kuralı | `Completed(ReviewBlocked)`; insan override edebilir (gerekçe zorunlu) | Bulgular + karar |
| Şema doğrulaması başarısız | `--json-schema` | Bir kez yeniden dene → `ReviewUnusable` | Ham çıktı |
| Disk doldu | Yazma hatası | Execution `Failed`; blob yazımı atomik (temp + rename) → yarım blob yok | — |

**Genel ilke:** *Hiçbir kurtarma yolu veri silmez.* Temizlik daima açık operatör komutudur.

---

## R. Gözlemlenebilirlik

Minimum yeterli set:

1. **Yapısal loglar** — Serilog, `logs/hub-YYYYMMDD.jsonl`. Her kayıt zorunlu alanlar: `timestamp`, `level`, `message`, `organizationId`, `taskKey`, `executionId`, `stepId`, `correlationId`.
2. **Korelasyon** — `ExecutionId` bir GUID'dir ve aynı zamanda Claude `--session-id` olarak verilir. Böylece Hub kaydı ↔ sağlayıcı oturumu tek atlamada eşleşir. (Küçük ama çok değerli bir tasarım kararı.)
3. **`OrchestrationEvent` tablosu** — UI/CLI zaman çizelgesi için küçük satırlar (`executionId`, `at`, `type`, `message`). Blob yok. Log dosyasının yerine geçmez, tamamlar.
4. **`Activity` / `ActivitySource`** — geçişler ve süreç çalıştırmaları etrafında. Exporter **yok**; ileride OTel bağlamak refactor gerektirmesin diye.
5. **Süre ölçümleri** — adım bazında `startedAt/endedAt`; kanıt özetinde gösterilir.
6. **Sağlık komutu** — `hub doctor`: git sürümü, `claude --version`, disk alanı, DB erişimi, yetim worktree sayısı, açık lease sayısı.

**Yapılmayacaklar:** metrik sunucusu, tracing backend, log toplama, Kafka, dashboard.

---

## S. İlk Uçtan Uca Dikey Dilim

### S.1 Önerilen değişiklik

Sizin önerdiğiniz dilim doğru yöne bakıyor ama **iki doğrulanmamış AI bağımlılığını** (Claude headless + Codex) ilk dilime koyuyor. Bu, mimari riskin en yüksek olduğu yerde (süreç kontrolü, git izolasyonu, kanıt) hata ayıklamayı AI belirsizliğiyle karıştırır.

**Öneri: dilimi üçe böl.** İlk dilim AI içermez.

### S.2 Dilim 1 — "Loop Zero" (AI YOK)

```
hub task create → Ready gate
      ↓
Repository çöz + base commit sabitle
      ↓
Worktree + agent/<key>/1 branch
      ↓
Context Pack derle + hash
      ↓
ScriptedAgentRuntime  (fixture patch uygular)
      ↓
git status + ScopeGuard + Hub commit + diff yakala
      ↓
Verification profile çalıştır (dotnet restore/build/test)
      ↓
Kanıt sakla (blob + tablo)
      ↓
AwaitingAcceptance
      ↓
hub accept  → Accepted → worktree release
```

**Kabul kriterleri:**
1. `hub run N360-DEMO` uçtan uca tek komutla çalışır ve `Accepted` ile biter.
2. Tüm akış **CI'da, ağsız, AI'sız, deterministik** olarak geçer.
3. Aynı base commit ile iki çalıştırma **aynı `ContextPackHash`** üretir.
4. Fixture repo'da test kırıkken → `Completed(VerificationFailed)`; patch uygulanınca → `Passed`. Baseline karşılaştırması kanıtta görünür.
5. Kapsam ihlali senaryosu (`AllowedScope` dışına yazan patch) → `Completed(ScopeViolation)`, inceleme atlanır.
6. Çalışma ortasında Hub süreci `kill` edilir → yeniden başlatmada execution `Interrupted`, worktree korunur, `reconcile` doğru raporlar.
7. `ScriptedAgentRuntime` "asılma" modunda → `IdleTimeout` tetiklenir, **süreç ağacı tamamen ölür** (kalan `dotnet`/`node` süreci yok — test bunu doğrular).
8. Kanıt deposunda hiçbir blob DB içinde değil; her blob hash'iyle bulunabilir.
9. `git worktree list` çıktısı ile DB'nin uyumu doğrulanır.

**Non-goal'lar:** AI yok, inceleme yok, UI yok, Codex yok, onarım döngüsü yok, maliyet yok, çoklu org paralelliği yok.

**Test stratejisi:** `tests/fixtures/repos/dotnet-sample` — gerçek bir git reposu, kasıtlı kırık bir test, hazır bir `fix.patch`. `ScriptedAgentRuntime` modları: `apply-patch`, `no-op`, `out-of-scope-write`, `hang`, `crash`, `partial-write`.

**Demo senaryosu (insana gösterilen):**
> `hub run N360-DEMO` → 90 saniye → ekranda I.5'teki kanıt özeti → `hub accept N360-DEMO#1` → `Accepted`. Ardından `hub evidence show N360-DEMO#1 --diff` ile tam diff.

**Neden bu dilim mimariyi kanıtlar:** ProcessRunner, FileSystemGuard, worktree yaşam döngüsü, durum makinesi, kanıt deposu, kurtarma ve kapsam denetimi — hepsi *aynı anda* ve *tekrarlanabilir* biçimde sınanmış olur. Bunlar Hub'ın gerçek değer katmanıdır.

### S.3 Dilim 2 — Claude uygulayıcı

`ScriptedAgentRuntime` yerine `ClaudeCodeRuntime`. Kabul kriteri: aynı 9 madde geçerliliğini korur; ek olarak `stream-json` ayrıştırma başarısız olsa bile ham stdout kanıtı kaybolmaz, ve `--session-id` = `ExecutionId` eşleşmesi doğrulanır.

### S.4 Dilim 3 — Bağımsız inceleme

Ayrı, atılabilir worktree + read-only profil + `--json-schema`. Kabul kriteri: inceleyici worktree'sinde yazma tespit edilirse inceleme `Unusable` olur (test edilmiş senaryo).

---

## T. Fazlı Uygulama Yol Haritası

Sıralama ilkesi: **en yüksek teknik riski ve en yüksek değeri en öne al.** Risk burada AI değil; süreç kontrolü ve git izolasyonudur.

### Faz 0 — Kararlar (kod yok)
- **Amaç:** ADR'leri yazmak, V bölümündeki bloklayıcı soruları kapatmak.
- **Çıktı:** `docs/adr/` içinde ADR-0001…0012; `hub.config.yaml` şeması taslağı; ilk hedef repo ve verification profili belirlenmiş.
- **Kabul:** Denetçi bu belgeyi ve ADR'leri onaylar.
- **Yapılmayacak:** Hiçbir proje oluşturma.

### Faz 1 — İskelet, domain, kalıcılık, CLI
- **Amaç:** Konuşulabilir bir çekirdek.
- **Çıktı:** Solution; `Domain` + `Application` + `Infrastructure` + `Cli`; EF Core migration'ları; `hub.config.yaml` seed'i; `hub org list`, `hub task create/show`, Ready gate doğrulaması.
- **Bağımlılık:** Faz 0.
- **Kabul:** Task oluşturulabilir; eksik alanla `Ready`'ye geçemez; mimari testler yeşil.
- **Yapılmayacak:** Süreç çalıştırma, git, AI, API, UI.

### Faz 2 — Execution çekirdeği (EN YÜKSEK RİSK)
- **Amaç:** Süreç ve dosya sistemi çıkışını güvenli ve kanıtlanabilir kılmak.
- **Çıktı:** `ProcessRunner` (Job Object interop, üç seviyeli timeout, async stream tüketimi, env allowlist, UTF-8/ANSI), `FileSystemGuard` (kanonikleştirme, org kökü, reparse-point).
- **Kabul:** "Torun süreç öldürme" testi geçer — Job kapatıldığında hiçbir alt süreç hayatta kalmaz. Deadlock testi (yüksek hacimli stdout+stderr) geçer. Env allowlist testi: allowlist dışı değişken çocuk süreçte **görünmez**.
- **Yapılmayacak:** Git, AI.

### Faz 3 — Workspace (git) yöneticisi
- **Çıktı:** base commit sabitleme, worktree/branch yaşam döngüsü, worktree-yerel sertleştirme (hooks, pushurl), diff yakalama, Hub commit, reconciler, `hub workspace` komutları.
- **Kabul:** Kirli ana dizinle görev başlar; `.gitignore`'lu dosya worktree'de yok; `pre-push` hook'u push'u engeller (test edilir); zorla kilitlenmiş worktree `Orphaned` olur ve akış bloklanmaz.
- **Yapılmayacak:** AI, doğrulama.

### Faz 4 — Doğrulama motoru + kanıt deposu
- **Çıktı:** Profil modeli, komut çalıştırıcı (allowlist + argv), TRX/JUnit ayrıştırıcı, baseline önbelleği, içerik adresli blob deposu, redaksiyon.
- **Kabul:** Fixture repoda kırık→düzelmiş geçişi sayaçlarla kanıtlanır; sırlar redakte edilir (test vektörleriyle); blob yazımı atomiktir.

### Faz 5 — Orchestrator + Context Pack + `ScriptedAgentRuntime` → **DİLİM 1 TAMAM**
- **Çıktı:** `ExecutionOrchestrator`, durum makineleri, lease/heartbeat, ScopeGuard, Context Pack derleyici + manifest, `ScriptedAgentRuntime`, `hub run/accept/evidence` komutları.
- **Kabul:** S.2'deki 9 madde.
- **Yapılmayacak:** AI çağrısı, inceleme, UI.

### Faz 6 — `ClaudeCodeRuntime` → **DİLİM 2**
- **Bağımlılık:** V.1'deki auth sorusunun kapanmış olması.
- **Çıktı:** CLI adapter, `stream-json` ayrıştırıcı (hataya dayanıklı), izin profili eşlemesi, kullanım metadata yakalama, `--session-id` korelasyonu.
- **Kabul:** Gerçek bir görev uçtan uca geçer; ayrıştırma hatası kanıt kaybına yol açmaz; timeout ve iptal gerçek süreçte doğrulanır.
- **Yapılmayacak:** Onarım döngüsü, çoklu tur.

### Faz 7 — İnceleme adımı (Claude read-only profil) → **DİLİM 3**
- **Çıktı:** Ayrı atılabilir worktree, read-only profil, `--json-schema` ile bulgu şeması, kurcalama tespiti, kapı kuralları, override + gerekçe.
- **Kabul:** Bulgular tabloya yazılır; Critical bulgu bloklar; override audit'e düşer; kurcalama senaryosu `Unusable` üretir.

### Faz 8 — Codex runtime
- **Bağımlılık:** Codex CLI kurulumu + V.2'deki doğrulamalar.
- **Çıktı:** `CodexRuntime`, gerekirse WSL2 yol çevirimi, `Capabilities` ilanı, profil eşlemesi.
- **Kabul:** İnceleme adımı yalnızca `AgentProfile` değiştirilerek Codex'e geçer — orchestrator kodu **değişmez**. (Bu, F.1'deki soyutlamanın gerçek sınavıdır.)

### Faz 9 — Minimal UI
- **Çıktı:** React+TS: proje/görev listesi, execution zaman çizelgesi (SSE), diff görüntüleyici, doğrulama sonuçları, bulgular, kabul/ret düğmeleri.
- **Kabul:** Bir görev baştan sona **yalnızca UI'dan** yönetilebilir.
- **Yapılmayacak:** Avatar, animasyon, gamification, dashboard metrikleri.

### Faz 10 — Sertleştirme
- **Çıktı:** Onarım döngüsü (max 1 tur, bütçe sınırlı), maliyet/kullanım raporu, kanıt GC, gelişmiş redaksiyon, `CreateProcess` P/Invoke ile yarışsız job ataması, **WSL2 tabanlı organizasyon izolasyonu**, pre-flight secret tarama, günlük çalıştırma bütçesi.
- **Kabul:** K.4'teki artık risklerden en az ikisi kapanır.

---

## U. Önerilen ADR'ler

Yalnızca gerçekten geri döndürülmesi pahalı kararlar:

| No | Başlık | Neden ADR |
|---|---|---|
| ADR-0001 | Local-first, tek süreçli modüler monolit | Tüm altyapı kararlarının kökü |
| ADR-0002 | Kanıt otoritesi: makine gerçeği > inceleyici > agent beyanı | Sistemin varlık sebebi; ihlali sessizce olur |
| ADR-0003 | Git worktree ile execution izolasyonu ve base-commit sabitleme | Tekrar üretilebilirliğin temeli |
| ADR-0004 | Worktree'ler repo dışında, Hub yönetimindeki kısa kökte | Windows yolu/temizlik sonuçları geri dönülemez |
| ADR-0005 | Commit'i Hub atar, agent atmaz; push/merge kod yolu yoktur | Güvenlik ve tarih temizliği |
| ADR-0006 | Tüm süreç çalıştırma tek `ProcessRunner` üzerinden, kabuk kullanılmadan | Komut enjeksiyonunu kaynağında keser |
| ADR-0007 | Ortam değişkeni allowlist'i (denylist değil) | Kimlik bilgisi sızıntısının ana önlemi |
| ADR-0008 | `IAgentRuntime` ince tutulur; yetenekler gizlenmez, ilan edilir | Yanlış soyutlama en pahalı hatadır |
| ADR-0009 | Claude Code entegrasyonu CLI subprocess ile (SDK sidecar değil) | .NET için resmî Agent SDK yok |
| ADR-0010 | Context Pack diskte materyalize edilir ve hash'lenir | Denetlenebilirlik ve tekrar üretilebilirlik |
| ADR-0011 | Repo içi agent yapılandırması (`CLAUDE.md`, hooks, MCP) varsayılan olarak devre dışı | Prompt injection güven sınırı |
| ADR-0012 | v1 organizasyon izolasyonu mantıksaldır; artık risk kabul edilmiştir | Yanlış güvenlik iddiasını önler |
| ADR-0013 | SQLite + WAL; büyük çıktılar içerik adresli blob deposunda | Şema evrimini etkiler |
| ADR-0014 | İnsan kabulü merge/push/deploy anlamına gelmez | Ürün semantiği; sonradan değiştirmek tehlikeli |

MVP'de **yazılmayacak** ADR'ler: mesajlaşma altyapısı, çoklu kiracılık (multi-tenancy), yetkilendirme modeli, ölçeklendirme, vector store. Karar gerektiren bir problem henüz yok.

---

## V. Açık Sorular

### V.1 UYGULAMA ÖNCESİ BLOKLAYICI

| # | Soru | Neden bloklayıcı | Nasıl kapatılır |
|---|---|---|---|
| B1 | Headless Claude çalıştırmaları hangi kimlikle olacak: `claude setup-token` (abonelik) mi, `ANTHROPIC_API_KEY` mi? Aboneliğin otomatik/programatik kullanım şartları buna izin veriyor mu? | Faz 6'nın tümü ve maliyet modeli buna bağlı. Yanlış cevap projeyi yeniden yazdırır. | Token kur, `--safe-mode --setting-sources user` ile tek bir headless çalıştırma yap; güncel Claude Code dokümanı ve abonelik şartlarını teyit et |
| B2 | Bu Hub'ın çalıştıracağı **ilk repo** hangisi ve doğrulama komutları tam olarak neler? Şu anda testleri geçiyor mu? | Dilim 1'in fixture'ı ve baseline mantığı buna göre şekillenir | Repo seç, komutları elle çalıştır, süreleri ölç |
| B3 | Organizasyon kökleri ve repo envanteri (hangi repo hangi org/proje altında, yerel yollar) | `FileSystemGuard` ve seed yapılandırması bunsuz yazılamaz | `hub.config.yaml` taslağını doldur |
| B4 | "v1'de organizasyon izolasyonu mantıksaldır, OS zorlaması yoktur" kabul ediliyor mu? | Hayırsa Faz 10'daki WSL2 işi Faz 2'ye çekilmeli ve takvim değişir | Açık onay |
| B5 | Kabul kriterlerinin **makinece doğrulanabilir** olması zorunlu kılınıyor mu? | Ready gate'in en önemli kuralı | Açık onay |

### V.2 ERTELENEBİLİR (bloklayıcı değil)

| # | Soru | Ne zaman gerekir |
|---|---|---|
| N1 | Codex hangi yüzeyle entegre olacak (CLI / App Server / API) ve Windows'ta sandbox var mı? | Faz 8 başında |
| N2 | Codex'in ChatGPT aboneliğiyle programatik kullanımı uygun mu? | Faz 8 başında |
| N3 | İnceleme için sağlayıcı çeşitliliği ne kadar değerli? (aynı sağlayıcı farklı model yeterli mi) | Faz 7 sonunda ölç |
| N4 | Onarım döngüsünde maksimum tur sayısı ve bütçe | Faz 10 |
| N5 | Çoklu repo görevleri gerekli mi? | Gerçek bir görev talep edene kadar hayır |
| N6 | Kanıt saklama süresi ve GC politikası | Disk baskısı oluşunca |
| N7 | UI'da hangi diff bileşeni (Monaco vs react-diff-view) | Faz 9 |
| N8 | Maliyet/kullanım raporlamasının biçimi | Faz 10 |
| N9 | Uzak worker / ayrı execution makinesi | Gerçek bir darboğaz ölçülene kadar hayır |

---

## W. Aşırı Mühendislik Riskleri

Bu projenin gereksiz karmaşıklaşacağı **en olası** yerler, en tehlikeliden başlayarak:

1. **Workflow/policy motoru inşa etmek.** "İleride pipeline'lar yapılandırılabilir olsun" düşüncesi kolayca bir DSL, bir kural değerlendiricisi ve bir grafik editörüne dönüşür. **Uyarı:** Faz 8'e kadar pipeline **kodda sabit sıralı adımlardır**. Yapılandırılabilirlik ancak ikinci gerçek pipeline şekli ortaya çıktığında eklenir.
2. **Soyutlamayı tek sağlayıcıya göre şekillendirmek.** `IAgentRuntime`, Claude Code'un flag'lerinin birebir aynası olursa Codex'e uymaz. **Uyarı:** `ScriptedAgentRuntime`'ı ilk yazın; iki farklı implementasyon soyutlamayı dürüst tutar.
3. **Bağlam sistemini bilgi tabanına çevirmek.** Embedding, chunking, alaka skorlaması, otomatik özetleme… **Uyarı:** Bağlam = insan yazımı markdown + git. Vector DB'yi ancak "pack bütçesi yetmiyor ve manuel seçim işe yaramıyor" ölçülünce tartışın.
4. **Kanıt modelini aşırı normalleştirmek.** Her log satırını tabloya koyma isteği. **Uyarı:** Blob + hash + küçük meta. Sorgulanmayan şeyi tablolaştırmayın.
5. **UI'ı erken başlatmak.** UI, olmayan bir motoru güzel gösterir ve motorun eksikliklerini gizler. **Uyarı:** Faz 9'dan önce tek satır React yok.
6. **Otonomi eklemek.** "Agent'lar birbirine yazsa daha akıllı olur." **Uyarı:** Bu, kanıtı ve determinizmi öldürür. Sistemin değeri ters yönde.
7. **Çok kullanıcılı / yetkilendirme mimarisi hazırlamak.** Tek kullanıcı için `IPrincipal` soyutlaması, rol tabloları, JWT. **Uyarı:** `Actor` alanı bir string olsun; yeter.
8. **Event sourcing / CQRS.** Audit gereksinimi bunu davet eder. **Uyarı:** Append-only bir `AuditEvent` tablosu tüm ihtiyacı karşılar.
9. **Genel amaçlı plugin sistemi.** "Yeni runtime'lar plugin olarak yüklensin." **Uyarı:** İki runtime var; ikisi de derlenmiş projede.
10. **Maliyet takibini erken derinleştirmek.** **Uyarı:** Metadata'yı sakla, raporu Faz 10'a bırak.
11. **Diff görüntüleyiciyi sıfırdan yazmak.** **Uyarı:** Olgun bir kütüphane kullanın.
12. **Sandbox'ı mükemmelleştirmeye çalışmak.** Windows'ta tam sandbox uzun bir kuyu. **Uyarı:** v1'de "atılabilir worktree + tespit" yaklaşımı yeterlidir; gerçek izolasyon WSL2 ile tek hamlede gelir.

---

## X. Nihai Önerilen İnşa Sırası

Boş bir dizinden ilk faydalı CoreX Agent Hub çalıştırmasına:

```
 1. B1–B5 bloklayıcı sorularını kapat (özellikle Claude headless auth ve ilk hedef repo).
 2. ADR-0001 … ADR-0014'ü yaz; bu belgeyi denetime ver ve onayla.
 3. Solution ve proje iskeletini oluştur; katman bağımlılık yönünü mimari testle kilitle.
 4. Domain modelini yaz (12 tablo, 3 aggregate) + durum makineleri + Ready gate kuralı.
 5. EF Core + SQLite (WAL) + ilk migration + `hub.config.yaml` seed'i.
 6. Hub CLI'ın iskeleti: `org/project/repo list`, `task create/show`.
 7. ProcessRunner: Job Object interop, üç seviyeli timeout, async stream tüketimi,
    env allowlist, UTF-8/ANSI. → "torun süreç ölüyor mu" testini yaz ve geçir.
 8. FileSystemGuard: kanonik yol, org kökü, reparse-point kaçış denetimi.
 9. Workspace Manager: base commit sabitleme, worktree+branch, worktree-yerel
    sertleştirme (hooks/pushurl), Hub commit, diff yakalama, reconciler.
10. Evidence Store: içerik adresli blob, atomik yazım, redaksiyon; `EvidenceArtifact`.
11. Verification Engine: profil modeli, allowlist'li çalıştırıcı, TRX/JUnit ayrıştırıcı,
    baseline önbelleği.
12. Context Pack Builder: kaynak birleştirme, öncelik sırası, bütçe kontrolü, manifest+hash.
13. ScopeGuard: diff yollarının izinli glob'lara uygunluk denetimi.
14. ExecutionOrchestrator: durum makinesi, lease/heartbeat, adım sıralaması, audit.
15. ScriptedAgentRuntime + fixture repolar + hata modları (hang/crash/out-of-scope).
16. `hub run / accept / evidence show` → ★ DİLİM 1 TAMAM (AI'sız uçtan uca kanıt) ★
17. ClaudeCodeRuntime (CLI adapter, stream-json, izin profilleri, session korelasyonu)
    → ★ DİLİM 2: ilk gerçek agent çalıştırması ★
18. Review adımı: ayrı atılabilir worktree, read-only profil, --json-schema bulgular,
    kurcalama tespiti, kapı kuralları, override+gerekçe → ★ DİLİM 3 ★
19. Minimal REST API + SSE (CLI ile eşdeğer yüzey).
20. CodexRuntime (kurulum + doğrulama sonrası) — orchestrator'a dokunmadan.
21. React UI.
22. Sertleştirme: onarım döngüsü, WSL2 organizasyon izolasyonu, maliyet raporu,
    kanıt GC, gelişmiş redaksiyon.
```

**16. adım projenin gerçek doğum anıdır.** O noktada elinizde AI olmadan çalışan, deterministik, denetlenebilir bir mühendislik orkestrasyon motoru olur — ve AI eklemek yalnızca bir adaptör değiştirmek olur. Sıralamanın tersi (önce AI, sonra altyapı) bu projenin başarısızlığa uğramasının en olası yoludur.

---

## Ek — Denetçi İçin Hızlı Kontrol Listesi

| Soru | Bu belgedeki cevap |
|---|---|
| MVP gereğinden büyük mü? | Hayır — ilk dilim AI bile içermiyor (S.2) |
| Bilinmeyenler dürüstçe işaretlenmiş mi? | Evet — Codex kurulu değil (§0), B1–B5 bloklayıcı (V.1) |
| Güvenlik iddiaları abartılı mı? | Hayır — org izolasyonunun mantıksal olduğu açıkça yazıldı (L.2, K.4) |
| Sağlayıcı değiştirilebilirliği gerçek mi test edilebilir mi? | Faz 8 kabul kriteri tam olarak bunu ölçüyor |
| Aşırı mühendislik nerede bekleniyor? | 12 madde, uyarılarıyla (W) |
| İlk çalışan çıktı ne zaman? | 16. adım (X) |

