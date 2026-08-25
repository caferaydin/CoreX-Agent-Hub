# Faz 9A — Yerel operatör API'si ve SSE

Bu belge Phase 9A'nın sözleşmesidir. Faz 9B'nin React kabuğu buraya karşı yazılacak.

Host: `CoreX.Hub.Api` (`hub-api`). Bağlanma: **yalnızca loopback**. Kimlik doğrulama: **yok**
— ve bu ikisi bu sırayla okunmalıdır; ilki olmadan ikincisi savunulabilir değildir.

## 1. Composition root ayrıldı

Faz 9'a kadar composition root `CoreX.Hub.Cli`'ydi. İkinci bir adaptör gelince bu isim yanlış
hâle geldi: API ya wiring'i kopyalayacaktı ya da bir konsol çalıştırılabilirine bağlanacaktı.

```
Api  ──┐
       ├──> Composition ──> Application ──> Domain
Cli  ──┘
```

`src/CoreX.Hub.Composition/` — `HubSession`, `HubPaths`, `Orchestration/`, `Agents/`,
`Verification/`, `Workspace/`, `Context/`, `Gates/` (33 dosya, mekanik taşıma, mantık
değişikliği yok). `CoreX.Hub.Cli` artık yalnızca konsol adaptörüdür (`Program`, `HubCli`,
`ArgParser`, `Commands/`).

İki adaptör de **aynı** application authority'lerini kullanır. Guard bunu doğrular:
`One_project_composes_the_system_and_every_adapter_goes_through_it`.

## 2. Rota yüzeyi

Önek: `/api/v1`

| Metot | Rota | Ne yapar |
|---|---|---|
| GET | `/health` | API + veritabanı + yapılandırma. Sağlayıcıya **dokunmaz** |
| GET | `/organizations`, `/organizations/{slug}` | Katalog |
| GET | `/projects?organizationId=` | Katalog |
| GET | `/repositories?organizationId=&projectId=`, `/repositories/{id}` | Reviewer profili, verification profilleri, `localPathExists` |
| GET | `/agent-profiles?organizationSlug=` | Profiller + `runtimeDeclared` |
| GET | `/tasks?organizationId=&projectId=&state=&offset=&limit=` | Sayfalı, deterministik sıralı liste |
| GET | `/tasks/{taskKey}` | Kapsam, kriterler, ready gate, denemeler, kabul durumu |
| POST | `/tasks` | `TaskService.CreateAsync` |
| POST | `/tasks/{taskKey}/ready` | Ready gate |
| POST | `/tasks/{taskKey}/runs` | Yürütmeyi **başlatır ve döner** (202) |
| POST | `/tasks/{taskKey}/cancel` | Mevcut iptal otoritesini çağırır |
| GET | `/executions/{id}` | Commit'ler, adımlar, workspace, beklenmeyen commit |
| GET | `/executions/{id}/steps` | Normalize edilmiş zaman çizelgesi |
| GET | `/executions/{id}/verification` | Kalıcı komut sonuçları |
| GET | `/executions/{id}/review` | Kanonik bulgular + gate cevabı |
| GET | `/executions/{id}/evidence` | Kanıt envanteri |
| GET | `/executions/{id}/evidence/{evidenceId}/content` | Ham kanıt, sınırlı |
| GET | `/executions/{id}/diff` | `GitCommitDiff` artefaktı |
| GET | `/executions/{id}/events` | **SSE** |
| GET | `/tasks/{k}/attempts/{n}/acceptance` | Karar öncesi görünüm |
| POST | `/tasks/{k}/attempts/{n}/accept` | Olağan kabul |
| POST | `/tasks/{k}/attempts/{n}/accept-override` | **Ayrı rota**, ReviewBlocked override |
| POST | `/tasks/{k}/attempts/{n}/request-changes` | Geri gönderme |
| POST | `/tasks/{k}/attempts/{n}/reject` | Sonlandırma |

Sağlayıcıya özel rota **yoktur**: `/claude/*` yok, `/codex/*` yok.

## 3. API bir authority değildir

Her mutasyon ince bir çağrıdır:

| İşlem | Authority |
|---|---|
| task create | `TaskService.CreateAsync` |
| ready | `TaskService.MarkReadyAsync` (ready gate) |
| run | `ExecutionLaunchPlanner` + `ExecutionOrchestrator` |
| cancel | `TaskCancellationService.CancelAsync` |
| accept / override | `AcceptanceService.AcceptAsync` |
| request-changes / reject | `AcceptanceService` |
| review bloklar mı | `ReviewFindingGate` (ADR-0022) |

Guard'lar: endpoint dosyalarında aggregate transition çağrısı yok, `SaveChangesAsync` yok,
`DbContext` yok, `FindingSeverity` karşılaştırması yok.

`overrideApplicable` sunucuda hesaplanır ve **gönderilir** ki UI dürüst çizebilsin — ama
sunucu, istemcinin ne düşündüğünden bağımsız olarak reddeder. Test bunu ayrıca kanıtlar:
düz `accept` bir ReviewBlocked'ı almaz, `accept-override` bir Passed'ı almaz.

## 4. Çalıştırma semantiği (§19/§20)

HTTP isteği **yürütmeyi beklemez**. Döndüğü an, execution satırı zaten vardır.

`ExecutionLauncher`:

- `IHostedService`'tir; iş `_running` sözlüğünde tutulur ve kapanışta beklenir.
- `Task.Run` fire-and-forget **yoktur** (guard).
- Orchestrator'a verilen token `_lifetime`'a bağlıdır — **asla** `requestAborted`'a.
  `requestAborted` yalnızca kimliği beklemeyi ve session açmayı sınırlar.
- `MachineExecutionSlot` aynı dosya tanıtıcısıyla claim edilir: tarayıcıdan başlatılan bir
  koşu ile terminalden başlatılan bir koşu hâlâ aynı anda olamaz.
- Lease, heartbeat, `StaleExecutionReconciler`, `NamedExecutionCancellation`,
  `ProcessRunner` — hepsi Faz 5'inki, değiştirilmedi.

Kapanışta: iptal istenir (ADR-0023 dayanıklı sonlandırma), sınırlı süre beklenir, dolmazsa
bir sonraki süreç reconcile eder.

**Tarayıcı kapanması, SSE kopması, sekme yenilenmesi bir yürütmeyi iptal etmez.**

## 4b. İptal

`POST /api/v1/tasks/{taskKey}/cancel`

Gövde: `{ "actor": "...", "reason": "..." }` — başka hiçbir şey. Süreç kimliği, sinyal adı,
sağlayıcı, runtime veya izin modu **istekten seçilemez**; guard bunu doğrular.

**Hedef: TaskKey.** Mevcut CLI otoritesi de öyledir (`hub cancel <TASKKEY>`). "Bu görevi ve
varsa şu an koşan yürütmesini durdur" demektir. İkinci bir yorum icat edilmedi.

Otorite: `TaskCancellationService` — Faz 5'ten beri Application'da, sağlayıcı-nötr, dar bir
port (`IExecutionCancellationSignal`) arkasında. **API'de hiçbir iptal kuralı yoktur.**
Çıkarılacak bir şey yoktu; tek risk *inşanın* ikiye ayrılmasıydı ve o tek yere alındı
(`HubCancellation`). Sinyal de tek yerde kurulur: koşan tarafın dinlediği ile operatörün
istediği aynı adlandırılmış primitiftir.

### Üç sonuç, tek boolean değil

| `state` | Anlamı | HTTP |
|---|---|---|
| `Cancelled` | İş durdu, görev terminal | 200 |
| `CancellationRequested` | Durması istendi, süresinde durmadı; **görev olduğu gibi bırakıldı** | 200 |
| `Refused` | Hiçbir şey istenmedi, hiçbir şey değişmedi | 409 `cancellation_refused` |

`CancellationRequested`'ı `Cancelled` diye raporlamak, hâlâ yazıyor olabilecek bir süreç
hakkında iddiada bulunmak olurdu. İkisi ayrı tutulur.

### Reddedilme durumları

- Görev zaten terminal → *"the task is already X"*. Terminal durum **yeniden yazılmaz**,
  yürütme kazandığı outcome'u korur.
- Koşan yürütme var ama kimse dinlemiyor (host ölmüş) → reddedilir. Görev `Cancelled`
  yazılmaz; bir sonraki hub komutu reconcile eder.
- Platformda sinyal yok → reddedilir.

Tekrarlanan iptal: ilkinden sonra görev terminaldir, ikincisi olağan terminal reddidir.
İdempotency altyapısı **kurulmadı**.

### İstemcinin bağlantısı

İptal isteği otoriteye `CancellationToken.None` ile verilir. Canlı bir koşuyu durdurmak başka
bir sürece sorup durduğunu görmeyi bekler; isteği başlatan bağlantı bunu **geri alamaz**.
Sekmeyi kapatan bir tarayıcı yine de iptal etmiştir.

### SSE

İptal için ayrı bir kanal **eklenmedi**. Olağan sorgu ve durum akışı geçişi gösterir, çünkü
otorite onu kalıcı hâle getirmiştir — gösteren tek şey budur.

## 5. SSE

`GET /api/v1/executions/{id}/events`

Kaynak: kalıcı `OrchestrationEvents` + `Executions` + `ExecutionSteps`. **İkinci bir event
bus değildir**; sayfa yenilemesinin okuyacağı satırların aynısını okur. Yeni tablo yok,
migration yok.

Olay türleri: `snapshot`, `execution-event`, `complete`, `error`.

`snapshot` **her zaman ilk** gönderilir: geç katılan, aralık kaçıran veya cursor'suz
bağlanan bir istemci hiçbir zaman durumu olaylardan çıkarmak zorunda kalmaz.

**Reconnect:** `id:` alanı, yürütmenin kalıcı zaman çizelgesindeki **sırasal** konumdur
(1'den başlar). Tarayıcı `Last-Event-ID` başlığını kendiliğinden gönderir; `?after=` sorgu
parametresi de kabul edilir. Yeni bir sütun gerekmedi: çizelge append-only ve sırası
kararlıdır, dolayısıyla "on ikinciden sonrası" kesindir.

**Backpressure:** hiçbir şey çalışan yürütmeye abone olmaz. Akış kalıcı durumu sınırlı
aralıkla (1 sn) okur; her yazma zaman aşımlıdır. Okumayı bırakan bir tarayıcı bir socket'e
mal olur, orchestrator'a değil. `Channel` yoktur, sınırsız kuyruk yoktur.

**Otorite akış değil veritabanıdır.** Olay düşerse sayfa yenilemesi gerçeği yeniden kurar.

## 6. Hata zarfı

```json
{ "code": "acceptance_refused", "message": "...", "correlationId": "...", "details": ["..."] }
```

| Durum | Kod | HTTP |
|---|---|---|
| `AcceptanceException` | `acceptance_refused` | 409 |
| `TaskOperationException` | `task_refused` | 409 |
| `DomainException` | `domain_refused` | 409 |
| `HubConfigurationException` | `configuration_refused` | 409 |
| `ExecutionStartException` | `execution_refused` | 409 |
| `ArgumentException` | `invalid_request` | 400 |
| bulunamadı | `not_found` | 404 |
| beklenmeyen | `internal_error` | 500 |

500 hiçbir şey söylemez: stack trace yok, yol yok, exception mesajı yok — yalnızca
correlation id. Ayrıntı yerel log'dadır.

## 7. Wire sözleşmesi

- **Enum'lar string.** TypeScript istemci hiçbir zaman ordinal'e bağlanmaz.
- **Zaman damgaları UTC ISO-8601, `Z` ile biter.** `UtcInstantConverter` bunu hem REST hem
  SSE için uygular — bu tam olarak yakalanan bir kusurdu: REST `Z`, SSE `+03:00` veriyordu.
- **Null'lar yazılır, atlanmaz.** Bir alanın bazen yok bazen null olması, tek bir olgu için
  iki şekil demektir.
- DTO'lar domain aggregate'i veya EF tipi taşımaz (guard).
- Id'ler mevcut kararlı kimliklerdir; `TaskKey` (`CXH-...`) Guid'in yanında taşınır.
  SQLite rowid asla dışarı çıkmaz.

## 8. Yerel-öncelik güvenlik varsayımları

- Varsayılan bağlanma `http://127.0.0.1:5177`. `HubApiOptions.Bind()` loopback olmayan her
  adresi **reddeder** — `0.0.0.0`, `+`, `*`, LAN IP'si, alan adı. Port değiştirilebilir,
  arayüz değiştirilemez.
- CORS varsayılan olarak same-origin. `AllowAnyOrigin` yok. Adlandırılmış origin'ler yalnızca
  Development ortamında onurlandırılır.
- Kanıt yalnızca **kimlikle** adreslenir. Hiçbir rotada dosya yolu parametresi yoktur; bir
  artefakt onu sahiplenen yürütmeyle birlikte doğrulanır, dolayısıyla ödünç alınmış bir id
  başka bir yürütmeyi okuyamaz. Boyut tavanı 8 MB.
- İstek sözleşmeleri permission profile, sandbox, reviewer veya runtime seçemez (guard).
  `RunBody` yalnızca `Actor` + `AgentProfile` taşır.
- Repository'nin yerel yolu dışarı verilmez; yalnızca `localPathExists`.

## 9. Faz 9B–9E'ye kalanlar

- **9B:** React/TypeScript kabuğu — navigasyon, task listesi/detayı, execution timeline.
- **9C:** diff görüntüleyici, verification ve bulgu görünümleri.
- **9D:** kabul akışı; override düğmesi **yalnızca** `overrideApplicable` true iken ve
  boş olmayan gerekçe zorunlu (sunucu zaten reddediyor, UI dürüst olmalı).
- **9E:** uçtan uca UI kabulü + yetkili tam regresyon.

Ayrıca ertelenenler: OpenAPI (geliştirme için serbest, henüz eklenmedi), istemci kodu
üretimi, task iptal rotası (mevcut iptal semantiği CLI'da; API yüzeyi 9B+ ile gelecek).
