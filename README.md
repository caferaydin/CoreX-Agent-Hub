# CoreX Agent Hub

Coding agent'ları çalıştıran, izole eden ve kanıtlayan **iç mühendislik kontrol düzlemi**.
Ticari bir ürün değildir; tek geliştirici iş istasyonunda, tek kullanıcı için çalışır.

Otoriter mimari doküman: [`docs/architecture/CoreX-Agent-Hub-Mimari-Plan-v1.1.1.md`](docs/architecture/CoreX-Agent-Hub-Mimari-Plan-v1.1.1.md)
(durum: **ARCHITECTURE ACCEPTED — DISCOVERY CLOSED**).
Kabul edilmiş kararlar: [`docs/adr/`](docs/adr/) (ADR-0001 … ADR-0017).

## Mevcut durum

| Faz | Kapsam | Durum |
|---|---|---|
| Faz 0 | Mimari kaydı, ADR'ler, format kararları | **TAMAM** |
| Faz 1 | Foundation + Domain + Persistence + CLI | **TAMAM** |
| Faz 2 | ProcessRunner (Job Object), FileSystemGuard | uygulanmadı |
| Faz 3 | Workspace / git / worktree | uygulanmadı |
| Faz 4 | Doğrulama motoru + kanıt deposu | uygulanmadı |
| Faz 5 | Orchestrator + Context Pack + ScriptedAgentRuntime (Loop Zero) | uygulanmadı |
| Faz 6 | ClaudeCodeRuntime | uygulanmadı |
| Faz 7 | İnceleme adımı + inceleme override kabulü | uygulanmadı |
| Faz 8 | CodexRuntime | uygulanmadı |
| Faz 9 | Minimal API + React UI | uygulanmadı |
| Faz 10 | Sertleştirme + WSL2 sert izolasyon + Kronos kapısı | uygulanmadı |

Faz 1'de **çalışma zamanı davranışı yoktur**: süreç başlatılmaz, git çağrılmaz, agent
çalıştırılmaz, dosya sistemi worktree'si açılmaz. Faz 1 kataloğu, görev modelini,
Ready kapısını, kalıcılığı ve CLI'ı kurar.

## Gereksinimler

- .NET SDK 10 (`global.json` ile 10.0.301 sabitlenmiştir)
- Git (yalnızca bu deponun kendi geliştirmesi için; Hub henüz git çağırmaz)

## Derleme

```
dotnet restore CoreX.Hub.slnx
dotnet build   CoreX.Hub.slnx -c Release
```

Uyarılar hata olarak derlenir (`TreatWarningsAsErrors`); uyarı bastırılmaz.

## Testler

```
dotnet test CoreX.Hub.slnx -c Release
```

| Test projesi | Kapsam |
|---|---|
| `CoreX.Hub.Domain.Tests` | Saf domain kuralları: organizasyon izolasyon değişmezi, TaskKey, kabul kriterleri, Ready kapısı, kabul override kümesi |
| `CoreX.Hub.Integration.Tests` | Gerçek SQLite: migration, WAL, kısıtlar, eşzamanlılık, seed determinizmi/idempotansı, organizasyon kapsamlama, CLI uçtan uca |
| `CoreX.Hub.ArchitectureTests` | Katman bağımlılık yönü, yasaklı kod yapıları (E.1 değişmezleri), domain sözlüğü |

Testler yalnızca geçici dizinlerde ve fixture verisiyle çalışır. Hiçbir test gerçek bir
repository'ye, gerçek şirket verisine veya kullanıcının veri dizinine dokunmaz.

## CLI

Çalıştırılabilir adı `hub`.

```
dotnet run --project src/CoreX.Hub.Cli -- --help
# veya derlenmiş hâli:
dotnet src/CoreX.Hub.Cli/bin/Release/net10.0/hub.dll --help
```

Faz 1 komutları:

```
hub org list
hub project list [--org <slug>]
hub repo list [--org <slug>] [--project <slug>]

hub task create --org <slug> --project <slug> [--repo <slug>]
                --objective <metin> [--key <TASKKEY>]
                [--allow <glob>]... [--forbid <glob>]...
                [--machine-criterion <ref>=<metin>]...
                [--human-criterion <metin>]...
                [--undeclared-criterion <metin>]...
                [--actor <ad>]
hub task show  <TASKKEY> [--org <slug>]
hub task ready <TASKKEY> [--org <slug>] [--actor <ad>]
```

Global seçenekler: `--data-dir <yol>`, `--config <yol>`, `--help`, `--version`.

Çıkış kodları: `0` başarı · `1` kullanım hatası · `2` işlem reddedildi ·
`3` Ready kapısı sağlanmadı · `70` beklenmeyen hata.

`run`, `accept`, `reject`, `evidence`, `workspace`, `doctor` komutları **bilinçli olarak
yoktur**; sonraki fazlara aittir.

### Kabul kriteri türleri (ADR-0015)

Her kriter doğrulama **yöntemini** beyan etmek zorundadır:

- `--machine-criterion <ref>=<metin>` — `<ref>`, repository'nin varsayılan verification
  profile'ındaki bir adım adına çözülmelidir.
- `--human-criterion <metin>` — geçerlidir ve hiçbir doğrulama komutu gerektirmez.
- `--undeclared-criterion <metin>` — yalnızca reddedilme davranışını denemek içindir;
  Ready kapısı bunu reddeder.

## Veri nerede duruyor

Çözüm sırası:

1. `--data-dir <yol>`
2. `COREX_HUB_DATA_DIR` ortam değişkeni
3. `%LOCALAPPDATA%\CoreXAgentHub`

Bu dizinde `hub.db` (SQLite, WAL modu) bulunur. Şema `dotnet ef` migration'ları ile
uygulanır; uygulama açılışta bekleyen migration'ları uygular. Büyük çıktılar (agent
stdout'u, diff'ler, komut çıktıları) veritabanına **konmayacaktır**; Faz 4'te içerik
adresli blob deposuna yazılacaktır (ADR-0013).

## `hub.config.yaml`

Katalog (Organization / Project / Repository / VerificationProfile / AgentProfile)
yapılandırmadan gelir. Uygulama her açılışta yapılandırmayı okur ve **deterministik,
idempotent** bir seed uygular:

- doğal anahtara göre ekler veya günceller,
- kimlikler doğal anahtardan türetilir (aynı yapılandırma iki boş veritabanında aynı
  satırları üretir),
- **hiçbir zaman silmez**; yapılandırmada olmayan kayıtlar uyarı olarak raporlanır,
- hiçbir değişiklik yoksa audit satırı bile yazmaz.

Ayrıştırma **fail-closed**'dır: bilinmeyen veya yanlış yazılmış bir anahtar sessizce yok
sayılmaz, yapılandırma yüklemesi hata verir ve CLI sıfırdan farklı bir kodla çıkar.
`requiresHardIsolaton: true` gibi bir yazım hatası, varsayılan `false` değerine düşüp
ADR-0017 kapısını sessizce açamaz. Hata mesajı dosyayı, satır/sütunu ve sorunlu anahtarı
adıyla bildirir; yapılandırma reddedildiğinde **hiçbir katalog satırı değişmez**.

Depodaki `hub.config.yaml` yalnızca **fixture** değerleri içerir: gerçek ürün
repository'si, gerçek Kronos verisi ve hiçbir sır içermez.

Yapılandırma dosyası aranma sırası: `--config` → veri dizini → çalışma dizininden
yukarı doğru.

### Kronos onboarding kapısı (ADR-0017)

`requiresHardIsolation: true` **ve** `isolationLevel: Logical` olan bir organizasyon
`Blocked` olarak yüklenir ve hiçbir execution başlatamaz. Bu bir belge vaadi değil,
kod değişmezidir ve birim testiyle kilitlenmiştir. Depodaki
`isolation-gate-fixture` organizasyonu bu davranışı görünür kılmak içindir; gerçek
Kronos verisi değildir.

## Migration'lar

```
dotnet ef migrations add <Ad> \
  --project src/CoreX.Hub.Infrastructure \
  --startup-project src/CoreX.Hub.Cli \
  --output-dir Persistence/Migrations
```

`dotnet ef migrations list` çıktısı "Pending" gösterir: design-time factory geçici bir
veritabanına bakar, gerçek veri dizinine değil. Bu beklenen davranıştır.

## Proje yapısı

```
src/CoreX.Hub.Domain          Saf domain. Hiçbir bağımlılığı yok.
src/CoreX.Hub.Application     Use case'ler, portlar, yapılandırma sözleşmesi.
src/CoreX.Hub.Infrastructure  EF Core + SQLite, YAML okuyucu, tek IHubStore uygulaması.
src/CoreX.Hub.Cli             Faz 1'in tek arayüzü ve composition root'u.
tests/                        Domain / Integration / Architecture testleri.
docs/architecture             Otoriter mimari doküman + tarihçe.
docs/adr                      ADR-0001 … ADR-0017.
```

Bağımlılık yönü tek yönlüdür ve `CoreX.Hub.ArchitectureTests` tarafından derlenmiş
assembly referansları üzerinden zorlanır.
