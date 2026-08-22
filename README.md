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
| Faz 2 | ProcessRunner (Job Object), FileSystemGuard | **TAMAM** |
| Faz 3 | Workspace / git / worktree | **TAMAM** |
| Faz 4 | Doğrulama motoru + kanıt deposu | **TAMAM** |
| Faz 5 | Orchestrator + Context Pack + ScriptedAgentRuntime (Loop Zero) | uygulanmadı |
| Faz 6 | ClaudeCodeRuntime | uygulanmadı |
| Faz 7 | İnceleme adımı + inceleme override kabulü | uygulanmadı |
| Faz 8 | CodexRuntime | uygulanmadı |
| Faz 9 | Minimal API + React UI | uygulanmadı |
| Faz 10 | Sertleştirme + WSL2 sert izolasyon + Kronos kapısı | uygulanmadı |

Faz 1 kataloğu, görev modelini, Ready kapısını, kalıcılığı ve CLI'ı kurar.

Faz 2 **yerel çalıştırma ilkellerini** ekler: tek süreç başlatma sınırı (`ProcessRunner`)
ve yönetilen dosya sistemi güven sınırı (`FileSystemGuard`).

Faz 3 **git/worktree izolasyonunu** ekler: repository ön kontrolü, base-commit sabitleme,
izole agent worktree'si, worktree'ye özgü sertleştirme, kanıt toplama ve güvenli
release/reconcile. Hâlâ agent çalıştırılmaz, doğrulama komutu yürütülmez, Context Pack
üretilmez ve orchestration pipeline yoktur.

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
| `CoreX.Hub.Execution.Tests` | Gerçek süreçler: exit code, akış tahliyesi, UTF-8/ANSI, ortam izolasyonu, soft/idle/hard timeout, iptal, süreç ağacı ölümü, yol güven sınırı (gerçek NTFS junction ile) |
| `CoreX.Hub.Workspace.Tests` | Gerçek git: repo ön kontrolü, commit sabitleme, kirli kaynak izolasyonu, repo hook'unun etkisizleştirilmesi, iki katmanlı push engeli, kanıt toplama, beklenmeyen commit tespiti, release/orphan/reconcile, ikincil süreç yürütme (filtre/textconv/fsmonitor/imzalama) kontrol ve sertleştirme testleri |
| `CoreX.Hub.Verification.Tests` | Gerçek süreçler + gerçek dosya sistemi: adım sırası, zorunlu/isteğe bağlı semantiği, timeout, iptal, allowlist reddi, ortam izolasyonu, yol/rapor kaçış reddi, TRX/JUnit ayrıştırma, XXE/DTD reddi, profil hash'i, baseline önbelleği, kırık→düzeltilmiş uçtan uca, blob deposu (atomiklik/dedupe/eşzamanlılık) ve redaksiyon |
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

Bu dizinin altında:

```
hub.db                              SQLite, WAL modu (yalnızca meta veri)
evidence/blobs/<sha[0:2]>/<sha>     içerik adresli kanıt blobları
cache/verification/<anahtar>.json   doğrulama baseline önbelleği
```

Şema `dotnet ef` migration'ları ile uygulanır; uygulama açılışta bekleyen migration'ları
uygular. Büyük çıktılar (agent stdout'u, diff'ler, komut çıktıları, test raporları)
veritabanına **konmaz**; içerik adresli blob deposuna yazılır (ADR-0013). Veritabanı
yalnızca `sha256`, boyut, tür ve redaksiyon sayısını tutar. Kanıt ve önbellek her zaman
worktree'lerin **dışındadır**, böylece bir agent kendi kanıtını sıradan repository
işlemleriyle değiştiremez.

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

Aynı gerekçeyle **yinelenen YAML anahtarları** da reddedilir. YAML normalde son yazılanı
kazandırır; bu, etkin yapılandırmayı belge sırasına bağlar ve
`requiresHardIsolation: true` satırının hemen ardından gelen bir `false` satırının
ADR-0017 kapısını sessizce açmasına yol açabilirdi.

Ayrıştırıcı davranış matrisi:

| Girdi | Sonuç |
|---|---|
| Bilinmeyen / yanlış yazılmış anahtar | REDDET |
| Yinelenen anahtar | REDDET |
| Geçersiz enum veya değer | REDDET |
| Geçerli yapılandırma | KABUL |

Depodaki `hub.config.yaml` yalnızca **fixture** değerleri içerir: gerçek ürün
repository'si, gerçek Kronos verisi ve hiçbir sır içermez.

### Doğrulama ayarları

`verification.executables` mantıksal çalıştırılabilir adını mutlak bir yola bağlar. Bu
katalog değildir: veritabanına yazılmaz, yalnızca çalışma zamanında okunur.

```yaml
verification:
  executables:
    dotnet: 'C:\Program Files\dotnet\dotnet.exe'
```

Bir doğrulama adımı yalnızca burada geçen bir adı kullanabilir. Parser bildiren adım
ayrıca `resultPath` bildirmek zorundadır:

```yaml
- name: test
  executable: dotnet
  args: ['test', '--no-build', '--logger', 'trx;LogFileName=results.trx',
         '--results-directory', 'results']
  timeoutSeconds: 1800
  required: true
  resultParser: trx
  resultPath: 'results/results.trx'
```

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
src/CoreX.Hub.Execution       Faz 2 çalıştırma ilkelleri. Hiçbir CoreX projesine ve
                              hiçbir paket'e bağımlı değil.
src/CoreX.Hub.Workspace       Faz 3 git/worktree izolasyonu. Execution'a bağımlı DEĞİL;
                              git ve yol güveni dar port'lar üzerinden gelir.
src/CoreX.Hub.Verification    Faz 4 doğrulama motoru, TRX/JUnit ayrıştırıcıları, profil
                              hash'i, baseline karşılaştırması. Yalnızca Domain'e bağımlı;
                              süreç, yol, kanıt ve önbellek dar port'lardan gelir.
src/CoreX.Hub.Cli             Faz 1'in tek arayüzü ve composition root'u.
tests/                        Domain / Integration / Execution / Workspace /
                              Verification / Architecture testleri + corex-fixture
                              (deterministik test yardımcı exe'si).
docs/architecture             Otoriter mimari doküman + tarihçe.
docs/adr                      ADR-0001 … ADR-0019.
```

Bağımlılık yönü tek yönlüdür ve `CoreX.Hub.ArchitectureTests` tarafından derlenmiş
assembly referansları üzerinden zorlanır.

## Çalıştırma ilkelleri (Faz 2)

`ProcessRunner` üretim kodunda **işletim sistemi alt süreci oluşturan tek yerdir**
(ADR-0006). Kabuk kullanmaz: çalıştırılabilir dosya + argüman vektörü.

Kabuk yasağı iki katmanda zorlanır. **Kaynak katmanı:** üretim kodunda hiçbir kabuk
başlatma yapısı bulunmaz (mimari testler). **Çalışma zamanı katmanı:** çağıran
çalıştırılabiliri seçtiği için `ShellExecutablePolicy`, `cmd`/`powershell`/`pwsh`/`bash`/`sh`
yorumlayıcılarını ve `.cmd`/`.bat`/`.ps1`/`.psm1`/`.sh`/`.vbs`/`.js` script girdilerini
**süreç oluşturulmadan önce** reddeder. Karşılaştırma Windows semantiğine uygun olarak
büyük/küçük harf duyarsızdır ve dosya sistemine dokunmaz, dolayısıyla kurulu olmayan bir
yorumlayıcı da doğru sınıflandırılır. Her alt süreç bir
Windows Job Object'e (`JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`) atanır, böylece çalıştırma
sonlandığında **tüm süreç ağacı** ölür. stdout ve stderr baştan itibaren eşzamanlı
tahliye edilir; yakalama tavanı belleği sınırlar ama boru hattı tüketimi asla durmaz.

Üç açık son tarih vardır: `SoftTimeout` (stdin kapatılır, sınırlı süre tanınır),
`IdleTimeout` (iki akışta da hareket yok) ve `HardTimeout` (mutlak tavan; çıktı ile
ertelenemez).

Çocuk ortam değişkenleri **boş bir sözlükten** başlar (ADR-0007); yalnızca adıyla
listelenenler kopyalanır, ardından Hub'ın zorunlu değerleri (`GIT_TERMINAL_PROMPT=0`,
`NO_COLOR=1`, `CI=1`, etkisiz `GIT_ASKPASS`) yazılır ve bunlar çağıran tarafından
ezilemez.

`FileSystemGuard` yönetilen çalıştırma yollarının güven sınırıdır: kanonikleştirme,
segment duyarlı kök içinde kalma denetimi ve gerçek reparse-point (symlink/junction)
kaçış tespiti. Sanal dosya sistemi ya da `File.*` sarmalayıcısı değildir.

**Kabul edilen artık risk:** `Process.Start` ile `AssignProcessToJobObject` arasında
küçük bir yarış penceresi vardır. Bu pencerede oluşturulan bir torun süreç job'dan
kaçabilir. Dondurulmuş mimari bunu Faz 2 için kabul eder; Faz 10 askıya alınmış süreç
oluşturmayla kapatır.

## Git / worktree izolasyonu (Faz 3)

`WorkspaceManager` her execution için, **sabitlenmiş bir commit'ten** `agent/<taskKey>/<attempt>`
branch'i üzerinde ayrı bir git worktree'si açar. Worktree kaynak reponun dışındadır, bu
yüzden geliştiricinin kirli çalışma ağacı, izlenmeyen dosyaları ve `.gitignore`'lu sırları
agent'a hiç ulaşmaz.

**Güvenlik açıklaması (Faz 3):** `git worktree add` komutunun kendisi `post-checkout`
hook'unu çalıştırır. Kendi `core.hooksPath`'ini ayarlayan bir repo, bu yüzden **ilk
checkout sırasında** kod çalıştırabilir — sonradan yapılacak sertleştirme çok geç kalır.
Bu davranış git 2.54.0.windows.1 üzerinde birebir üretildi. Bu nedenle Hub'ın hook
yapılandırması **ilk checkout yapabilen komutta zaten etkilidir**: komut kapsamlı
`GIT_CONFIG_COUNT` / `GIT_CONFIG_KEY_n` / `GIT_CONFIG_VALUE_n` ortamı kullanılır.
E.1 #8 komut satırındaki `-c core.hooksPath` yapısını bir atlatma mekanizması olarak
yasakladığı için o yol kullanılmaz.

Kalıcı sertleştirme worktree kapsamlıdır (`extensions.worktreeConfig` + `git config
--worktree`), böylece geliştiricinin ana worktree davranışı değişmez. Etkin ayarlar:
Hub'a ait `core.hooksPath`, yönlendirilemez `remote.origin.pushurl`, etkisiz
`credential.helper`, `core.longpaths=true`, `gc.auto=0`. **`pre-commit` hook'u yoktur** —
Hub kendi sonuç commit'ini hiçbir atlatma bayrağı olmadan atabilmelidir.

Yayınlama iki bağımsız katmanla engellenir: Hub'a ait `pre-push` hook'u ve
yönlendirilemez push hedefi. Üretim kodunda **push/merge/pull/fetch kod yolu yoktur**;
`git commit` yalnızca `WorkspaceManager.CreateResultCommit` içinde çağrılır. Beklenmeyen
bir agent commit'i tespit edilir ve SHA'sı kaydedilir, ama **asla onarılmaz**: reset,
rebase, amend, `branch -f` ve `update-ref` kod tabanında yoktur.

Sonuç commit'i olmayan bir workspace otomatik olarak bırakılamaz; kaldırma başarısız
olursa workspace `Orphaned` olarak **raporlanır**, zorla silinmez.

### Git'in ikincil süreç yürütmesi

`ProcessRunner` yalnızca `git.exe` başlatır, ama git'in kendisi repo tarafından
yapılandırılan başka programları çalıştırabilir. Hook'lar bunun tek yüzeyi değildir:
temizleme/bulaştırma (clean/smudge) ve `process` filtreleri, `diff.external`,
`diff.<sürücü>.textconv`, `core.fsmonitor` ve commit imzalama da program çağırır.
Bunların her biri gerçek git üzerinde önce **kontrol testleriyle kanıtlandı**
(`GitHelperExposureControlTests`), sonra kapatıldı.

İki farklı savunma vardır, çünkü bu yüzeylerin hepsi aynı şekilde etkisizleştirilemez:

- **Reddedilenler.** Repo yerel yapılandırmasında `filter.*`, `include.*` veya
  `includeIf.*` varsa repository kullanılamaz sayılır (`UnsafeGitConfiguration`).
  Filtre sürücüleri komut kapsamında güvenilir biçimde kapatılamaz, `include`
  yönlendirmeleri ise incelenen yapılandırmanın dışına çıkabilir. Yapılandırma
  `--no-includes` ile okunur, böylece dahil edilen dosyalar taramayı atlatamaz.
  İzlenen `.gitattributes` dosyaları da (yalnızca kök değil, **tüm alt dizinler**)
  `filter=lfs` ve özel filtre bildirimleri için taranır.
- **Etkisizleştirilenler.** `core.fsmonitor=false` ve `commit.gpgSign=false`
  yönetilen çalışma zamanı yapılandırmasının parçasıdır; kanıt toplayan her `diff`
  çağrısı `--no-ext-diff --no-textconv` ile yapılır.

Yapılandırma iki adım arasında değişebileceği için güvenlik kontrolü, yardımcı
program çalıştırabilecek her işlemden **hemen önce** tekrarlanır: kanıt toplamadan
ve `git add -A`'dan önce. Güvensiz bir repo bulunduğunda hiçbir şey onarılmaz —
yapılandırma silinmez, üzerine yazılmaz; durum raporlanır ve işlem durur.

**Garantinin sınırı:** bu, "git yapılandırması hiçbir zaman hiçbir şey çalıştıramaz"
demek değildir. Kanıtlanan şey dardır: yukarıda sayılan yüzeyler ya reddedilir ya da
yönetilen komutlarda etkisizdir ve bu, kaynak taramasıyla değil, gerçek git üzerinde
çalışan **davranışsal işaret (marker) testleriyle** doğrulanır. Yeni bir git sürümü
yeni bir yürütme yüzeyi eklerse aynı yöntem tekrar uygulanmalıdır.

## Açık denetim kapıları

| Kapı | Kapsam | Ne zaman kapanmalı |
|---|---|---|
| **G6-0 Agent-Direct Git Hardening** | Bir agent'ın kendi worktree'sinde doğrudan çalıştırdığı git komutları Hub'ın komut kapsamlı sertleştirme ortamını taşımaz. Kalıcı worktree yapılandırması hook'ları, push hedefini ve credential helper'ı kapatır, ama `core.fsmonitor` ile `commit.gpgSign`'ı içermez. | Gerçek `ClaudeCodeRuntime` devreye girmeden **Faz 6'dan önce**. Faz 4'ü ve Faz 5'i bloke etmez; bugün hiçbir agent git çalıştırmıyor. |

## Doğrulama ve kanıt (Faz 4)

`VerificationEngine` bir doğrulama profilini **sıralı** çalıştırır: bildirilen sırayla,
bağımlılık grafiği olmadan, paralellik olmadan. Bu düşük seviyeli bir ilkeldir —
doğrulamanın *ne zaman* yapılabileceğine (K0..K3 kapıları, `ResultCommitSha` koşulu)
karar vermez; o Faz 5'in işidir.

**Ne çalışabilir.** Adımın `executable` alanı mantıksal bir addır ve
`hub.config.yaml` → `verification.executables` eşlemesinden çözülür (ADR-0018). Eşlemede
olmayan ad süreç hiç başlatılmadan reddedilir. `PATH` araması yoktur, `where.exe` veya
`Get-Command` çalıştırılmaz. Allowlist'te olmak da yetmez: Faz 2 kabuk yasağı yürürlüktedir.
Her süreç yine tek `ProcessRunner` üzerinden başlar — job object, üç kademeli timeout,
sınırlı çıktı yakalama ve boş temelli ortam allowlist'i aynen geçerlidir.

**Zorunlu ve isteğe bağlı adımlar.** `required: true` bir adım başarısız olursa doğrulama
başarısızdır ve sonraki adımlar deterministik olarak `Skipped` işaretlenir. `required: false`
bir adım başarısız olursa **başarısız kalır** ve kanıtta görünür, ama zorunlu sonucu
çevirmez. Bir isteğe bağlı başarısızlık hiçbir zaman `Passed` diye yeniden yazılmaz.

**Raporlar.** Parser bildiren adım `resultPath` de bildirmek zorundadır; repository'de
rapor aranmaz. XML, DTD kapalı, resolver `null` ve entity genişletmesi sıfır olarak okunur:
XXE reddedilir, ağa çıkılmaz. Rapor için pratik bir üst sınır vardır (16 MB); aşan rapor
belleğe alınmadan `ParseFailed` olur. Tanınmayan bir TRX sonucu **asla** başarı sayılmaz —
hata olarak sayılır ve teşhis metnine yazılır.

**Baseline.** Önbellek anahtarı tam olarak `RepositoryId` + `BaseCommitSha` + `ProfileHash`
üçlüsüdür (ADR-0019); worktree'lerin dışında, atomik yazılan küçük bir JSON dosyasıdır.
Karşılaştırma kanıttır, izin değildir: baseline daha kötü olsa bile güncel zorunlu adım
başarısızsa doğrulama başarısızdır.

**Kanıt deposu.** Bloblar içerik adreslidir (`blobs/<sha256[0:2]>/<sha256>`), Hub'a ait veri
dizini altındadır — kaynak repo, agent worktree'si veya context repo içinde **değildir**.
Yazım atomiktir: geçici dosya, `flushToDisk`, sonra tek bir rename. Aynı içeriği yazan iki
eşzamanlı yazıcı tek bloba yakınsar. Silme, GC ve referans sayımı **yoktur** (Faz 10).

**Redaksiyon yazma zamanında olur.** Ham metin süreç çalışırken bellekte bulunabilir, ama
kalıcı depoya **hiçbir zaman** ham hâliyle verilmez: metin önce ortak sır kataloğundan
geçer, sonra hash'lenir ve yazılır. Yani SHA-256 saklanan (redakte edilmiş) baytların
hash'idir; ham sırrın hash'i hiç oluşmaz. Rapor sayaçları redaksiyondan **önce** ham
rapordan okunur, böylece redaksiyon sayıları değiştiremez. Meta veride yalnızca
`RedactionCount` tutulur — eşleşen değerin kendisi ne saklanır ne loglanır.

`SecretPatternCatalog` bu desenlerin tek kaynağıdır; Faz 5'in SecretGuard'ı onu kopyalamak
yerine tüketecektir. Kapsam bilinçli olarak dardır: entropi analizi, ML, vault, rotasyon ve
sır yöneticisi yoktur.

**Güvenlik sınırı:** doğrulama çalıştırması bir **düşman kod kum havuzu değildir.** Build ve
test araçları repository'nin kendi build mantığını çalıştırır. Faz 4 hangi programın
başlayabileceğini, argümanlarını, ortamını, çalışma dizinini, süreç ömrünü ve kanıtını
kontrol eder; keyfi MSBuild/test kodunu mevcut Windows kullanıcısına karşı güvenli hâle
**getirmez**. Bu, yerel Core X Logic pilotu için kabul edilmiş bir sınırdır; WSL2 sert
izolasyon Faz 10'dur.
