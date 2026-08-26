# CoreX Agent Hub

Coding agent'ları çalıştıran, izole eden ve kanıtlayan **iç mühendislik kontrol düzlemi**.
Ticari bir ürün değildir; tek geliştirici iş istasyonunda, tek kullanıcı için çalışır.

Otoriter mimari doküman: [`docs/architecture/CoreX-Agent-Hub-Mimari-Plan-v1.1.1.md`](docs/architecture/CoreX-Agent-Hub-Mimari-Plan-v1.1.1.md)
(durum: **ARCHITECTURE ACCEPTED — DISCOVERY CLOSED**).
Kabul edilmiş kararlar: [`docs/adr/`](docs/adr/) (ADR-0001 … ADR-0023).

Bağımsız review: [`docs/phase-7-review.md`](docs/phase-7-review.md).

## Mevcut durum

| Faz | Kapsam | Durum |
|---|---|---|
| Faz 0 | Mimari kaydı, ADR'ler, format kararları | **TAMAM** |
| Faz 1 | Foundation + Domain + Persistence + CLI | **TAMAM** |
| Faz 2 | ProcessRunner (Job Object), FileSystemGuard | **TAMAM** |
| Faz 3 | Workspace / git / worktree | **TAMAM** |
| Faz 4 | Doğrulama motoru + kanıt deposu | **TAMAM** |
| Faz 5A | Context Pack + agent runtime sözleşmesi + ScriptedAgentRuntime | **TAMAM** |
| Faz 5B | K1 RuntimePolicyGate + K2 ScopeGuard + K3 SecretGuard | **TAMAM** |
| Faz 5C | ExecutionOrchestrator + K0 + result commit + doğrulama + lease | **TAMAM** |
| Faz 5D | AcceptanceService + insan kararları + `hub accept` / `request-changes` / `reject` / `evidence show` | **TAMAM** |
| Faz 6 | ClaudeCodeRuntime + gerçek agent kabulü | **TAMAM** |
| Faz 7 | Bağımsız review + inceleme override kabulü | **TAMAM** |
| Faz 8A | Sabitlenmiş `CodexRuntime` (ikinci gerçek sağlayıcı, sağlayıcı-nötr temel) | **TAMAM** |
| Faz 8B | Codex reviewer profil geçişi + gerçek ürün review kabulü | **TAMAM** |
| Faz 9A | Yerel Minimal API + sorgu sözleşmesi + SSE temeli | **TAMAM** |
| Faz 9B | React/TypeScript operatör kabuğu + salt-okunur görünümler | **TAMAM** |
| Faz 9C | Operatör eylemleri: create / ready / run / cancel | **TAMAM** |
| Faz 9D | İnsan yönetişimi arayüzü: accept / review override / request-changes / reject | **TAMAM** |
| Faz 9E | Production SPA barındırma + kalıcı karar geri okuması + gerçek tarayıcı kabulü | **TAMAM** |
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
| `CoreX.Hub.Api.Tests` | Gerçek HTTP + gerçek SQLite: sorgu sözleşmesi, mutasyon yetkileri, override ayrımı, SSE (snapshot/reconnect/disconnect/backpressure), yerel bağlanma ve kanıt erişim güvenliği |
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

Faz 5D insan kararı komutları:

```
hub accept          <TASKKEY>#<attempt> [--org <slug>] [--actor <ad>]
                    [--verdict <sıra>=met|notmet|na[:not]]...
hub accept          <TASKKEY>#<attempt> --override --reason "<metin>"
hub request-changes <TASKKEY>#<attempt> [--reason "<metin>"]
hub reject          <TASKKEY>#<attempt> [--reason "<metin>"]

hub evidence show   <TASKKEY>#<attempt> [--diff] [--org <slug>]
```

`run`, `cancel`, `doctor` komutları **bilinçli olarak yoktur**; sonraki fazlara aittir.
Özellikle `hub cancel`: `Running` bir görev başka bir süreçte çalışan bir agent'a karşılık
gelebilir, ve altındaki runtime'ı durdurmadan durumu çevirmek yalan olurdu — süreçler arası
iptal Faz 5E'ye aittir.

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

Depodaki `hub.config.yaml` fixture değerleri ile Hub'ın **kendi** repository'sini içerir.
`core-x-logic` organizasyonu yalnızca `agent-hub` projesini ve `corex-agent-hub`
repository'sini bildirir; başka bir gerçek ürün repository'si, gerçek Kronos verisi
(ADR-0017) ve hiçbir sır yoktur. `localPath` ve `filesystemRoot` bu makineye özgüdür:
organizasyon kökü repository'yi kapsamalıdır, yoksa hiçbir worktree hazırlanamaz.

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

### Agent runtime ayarları

Katalogdaki agent profili bir `runtimeKey` adlandırır; o anahtarın **bu makinede** ne
olduğunu yapılandırma söyler. Kataloğa değil yapılandırmaya ait olması bilinçlidir: hiçbir
şey veritabanına seed edilmez, dolayısıyla aynı katalog başka bir kurulumda migration'sız
çalışır.

```yaml
agents:
  scriptedRuntimes:
    - key: scripted
      fixturePath: 'D:\cx	ools\corex-fixture.exe'
      mode: apply-patch

  claudeCodeRuntimes:
    - key: claude-code
      executablePath: 'C:\Users\<kullanici>\.local\bin\claude.exe'
```

Her runtime ailesinin kendi bölümü vardır, çünkü kurulabilir olmak için gerekenler
farklıdır: deterministik koşum bir fixture ve senaryo ister, kurulu bir araç yalnızca
mutlak yolunu ister. Hepsini tek bir genel listeye sıkıştırmak, yarısı için anlamsız
ayarlar uydurmak ve yapılandırma dosyasına bir sağlayıcı anahtarı koymak olurdu.

Bildirilmemiş bir anahtarı adlandıran profil, elde ne varsa onu almaz — kurulum daha bir
execution satırı yazılmadan açık bir yapılandırma hatasıyla durur. Sağlayıcı seçimi,
fallback ve keşif yoktur.

`mode`, fixture'ın kendi fiillerinin yazıldığı gibi yazılır (`apply-patch`,
`out-of-scope-write`, `hang`, ...).

`claudeCodeRuntimes` **yalnızca** kurulu çalıştırılabilirin mutlak yolunu taşır. Orada
anahtar, token, uç nokta, hesap veya faturalandırma ayarı **yoktur**: kimlik doğrulama bant
dışıdır ve iş istasyonunda zaten açık olan abonelik oturumu kullanılır (ADR-0009). Araç
yüzeyi (`Read, Edit, Write, Glob, Grep`) ve izin modu (`acceptEdits`) da yapılandırma
değildir; adapter'de dondurulmuştur (ADR-0020). Bir kurulumun bir dosyadaki metinle kabuğu
geri açabilmesi, ADR-0020'nin ortadan kaldırdığı şeydir.

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
src/CoreX.Hub.Context         Faz 5A context pack üreticisi. Yalnızca Domain'e bağımlı;
                              yol güveni dar port üzerinden gelir.
src/CoreX.Hub.Agents          Faz 5A ince agent runtime sözleşmesi + ScriptedAgentRuntime.
                              Yalnızca Domain'e bağımlı; süreç dar port üzerinden gelir.
src/CoreX.Hub.Cli             Tek arayüz ve composition root'u.
tests/                        Domain / Application / Integration / Execution / Workspace /
                              Verification / Architecture / LoopZero testleri +
                              corex-fixture (deterministik test yardımcı exe'si).
docs/architecture             Otoriter mimari doküman + tarihçe.
docs/adr                      ADR-0001 … ADR-0023.
docs/phase-7-review.md        Bağımsız review: sınır, ölçümler, G7-1 kalibrasyonu.
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

## Context Pack ve agent runtime (Faz 5A)

Faz 5A **üç** sınırlı temel getirir: context pack üreticisi, sağlayıcıdan bağımsız ince
agent runtime sözleşmesi ve deterministik `ScriptedAgentRuntime`. Orchestrator, K0..K3
kapıları, AcceptanceService, lease/heartbeat ve `hub run` **yoktur** — onlar Faz 5B-D'dir.

### Context Pack

Her execution için Hub'a ait bir kökün altına materyalize edilir; kaynak reponun ve agent
worktree'sinin **dışındadır**:

```
<context-root>/<execution-id>/
  system.md          değiştirilemez Hub yürütme sözleşmesi
  rules.md           organizasyon + proje + repository kuralları, kabul edilen öncelikle
  task.md            görev, kapsam, kabul kriterleri (Machine/Human ayrımı korunur)
  repo-state.json    canlı git gerçekleri
  decisions/         açıkça seçilmiş kararlar
  manifest.json      dosya envanteri + ContextPackHash
```

`system.md` **sağlayıcı adı içermez** ve sağlayıcı CLI bayrağı taşımaz; hangi dosyanın
hangi CLI argümanına dönüştüğü adapter'ın kararıdır ve Context'e geri sızmaz.

Hedef repository **güvenilmez girdidir**: `CLAUDE.md`, `AGENTS.md`, `GEMINI.md`, `QWEN.md`,
`.cursorrules` gibi dosyalar okunmaz — pack'in hash'i bile değişmez. Önceki execution'ın
kanıtı da kendiliğinden girmez. Vector DB, embedding, RAG, semantik chunking ve agent
belleği **yoktur**.

**Bütçe 64 KB'dir ve serttir.** Aşılırsa `ContextBuildFailed` döner: sessiz kırpma yok,
son dosyaları atma yok, otomatik özetleme yok, sıkıştırmak için LLM çağırma yok.

**Execution kimliği context semantiği değildir.** `ExecutionId`, `AttemptNo`, üretilen
agent branch'i (`agent/HUB-001/1`), fiziksel worktree yolu ve materyalizasyon kökü
anlamsal dosyalara **hiç girmez** — aynı işin ikinci denemesi aynı pack'i üretmek
zorundadır (Loop Zero kabul kriteri #6). `repo-state.json` yalnızca kararlı repository
gerçeklerini taşır: `RepositoryId`, `BaseCommitSha`, `DefaultBranch`, `ProtectedBranches`.
Agent nerede çalıştığını zaten `AgentRunRequest.WorkingDirectory`'den öğrenir.
`AttemptNo` yalnızca `manifest.json`'da provenance olarak durur ve manifest hash'in
**dışındadır**.

**ContextPackHash** anlamdan türer. Algoritma açıktır: anlamsal dosyalar yazılır, her biri
SHA-256'lanır, `manifest.json` **hariç** sıralı envanterden kanonik bir metin kurulur,
ContextPackHash o metnin SHA-256'sıdır, sonra manifest yazılır. Kendine referans yoktur ve
dosya sistemi sıralamasına güvenilmez. Geçici kök, kullanıcı adı, süreç kimliği ve zaman
hash'i **değiştirmez**; amaç, kapsam, kriter, kural, karar, base commit ve branch
**değiştirir**.

Yayınlama atomiktir: `_pending/` altında kurulur, doğrulanır, tek bir rename ile yayınlanır.
Başarısızlıkta geçerli bir pack ortaya çıkmaz.

### Agent runtime sözleşmesi

```csharp
AgentCapabilities Describe();
Task<AgentRunResult> RunAsync(AgentRunRequest, IProgress<AgentEvent>, CancellationToken);
Task<HealthResult>   CheckHealthAsync(CancellationToken);
```

**Üç üye, o kadar.** `Authenticate`, `GetModels`, `GetBilling`, `GetQuota`, `GetAccount`,
`SetApiKey`, `CreateSession`, `Commit`, `Push` yoktur ve mimari test bunu artık *gerçek*
tip üzerinde doğrular. `AgentRunRequest` içinde provider, API anahtarı, access mode,
abonelik, faturalama, base URL veya endpoint **yoktur**: hangi runtime'ın çalışacağı
`RunAsync`'ten **önce**, composition'da çözülür.

Yetenekler gerçektir, taklit edilmez: eksik bir yetenek `Unsupported` ile görünür kalır.
`AgentRunResult` **kanıttır**, execution outcome'u değildir — hangi kapının ne dediğine
orchestrator karar verir.

### ScriptedAgentRuntime

Ağsız, AI'sız, hesapsız, faturasız, deterministik. Sekiz mod: `no-op` · `apply-patch` ·
`out-of-scope-write` · `secret-write` · `partial-write` · `hang` · `crash` ·
`unexpected-commit`. Her mod bir **koşul** üretir; hiçbiri o koşulun ne anlama geldiğine
karar vermez — `ScopeViolation`, `SecretGuardBlocked` ve `RuntimePolicyViolation` Faz 5B
kapılarının verdiğidir.

Gerçek bir sağlayıcı adapter'ıyla aynı sözleşmeyi kullanır ve aynı şekilde çalışır:
yapılandırılmış bir fixture çalıştırılabilirini `ProcessRunner` üzerinden başlatır.
Deterministik davranışın tamamı o fixture'dadır — böylece `unexpected-commit` senaryosunda
git'i **fixture** çağırır, üretim kodu değil. `ScriptedAgentRuntime` içinde git politikası
veya history onarımı yoktur (E.1 #5, E.1 #9).

Beyan edilen yetenekler (dürüstçe): **altısı da `false`**. `SupportsStructuredSchema`
dahil — runtime istendiğinde bir sonuç belgesi yazar, ama `StructuredOutputSchema`'yı
**ne okur ne doğrular**. Bir yola dosya yazmak şema desteği değildir; öyle beyan etmek
çağıranın doğrulanmamış bir belgeyi şemaya uygun sanmasına yol açardı. Şema gereken
çağıran `Unsupported` alır. Diğerleri: oturum yok, araç yok, maliyet yok, artımlı akış
yok, salt-okunur kısıtlaması yok.

## Yürütme güvenlik kapıları (Faz 5B)

Üç sabit ürün kapısı `CoreX.Hub.Application/Gates/` altındadır. Bunlar **yakalanmış makine
gerçeklerini** değerlendirir: git çalıştırmaz, süreç başlatmaz, dosya sistemine dokunmaz,
blob yazmaz, veritabanı açmaz. Genel bir policy engine, kural dili, plugin sistemi veya
chain-of-responsibility **yoktur** — üç tane kapı vardır, o kadar.

**K1 RuntimePolicyGate** — `HEAD == BaseCommitSha` ise geçer; değilse **bloklar**
(`UnexpectedAgentCommit`). Otorite HEAD'dir: agent'a "commit attın mı" diye sorulmaz,
stdout'undan çıkarım yapılmaz. Kapı **asla onarmaz** — reset, `branch -f`, `update-ref`,
amend, rebase, force push yok. Agent'ın commit'i bıraktığı yerde erişilebilir kalır
(ADR-0005, errata E1). Commit grafiği raporu zenginleştirir, kararı **vermez**: boş bir
grafik HEAD uyuşmazlığını geçişe çeviremez, sahte SHA da uydurulmaz.

**K2 ScopeGuard** — eşleştirme semantiğinin tamamı `Domain.Common.ScopeGlob` value
object'inindir (mimari § D.4). Bir scope girdisinin ne olabileceği, kanonik olarak ne
olduğu ve neyi eşlediği **tek yerde** tanımlıdır; Application'da ikinci bir glob dili
yoktur ve mimari test bunu kilitler. Dil dar: `*` segment içi, `?` tek karakter, `**`
segmentler arası, sonda `/` dizin öneki.

Geçersiz bir scope ifadesi **görev oluşturulurken** reddedilir (`DomainException`), yani
mutlak, sürücü nitelikli, UNC veya `..` içeren bir kapsam bildirimi hiçbir zaman Ready
bir göreve ulaşamaz — agent çalıştıktan sonra "görevin kendi politikası okunaksızmış"
diye keşfedilmez. Depolama düz metin kalır (`string[]`, migration yok); metnin **anlamı**
`ScopeGlob`'undur.

Dokunulan her yol önce `AllowedScope`'a uymalı, sonra **hiçbir**
`ForbiddenScope`'a uymamalıdır. **Deny kazanır**: `src/**` izinli + `src/generated/**`
yasaklı ise `src/orders/Order.cs` geçer, `src/generated/Client.cs` bloklanır. Değiştirilen,
eklenen, silinen ve rename'in **her iki ucu** aynı listeden gelir, dolayısıyla tek yüzü
kontrol ederek kaçış saklanamaz. Mutlak, sürücü nitelikli, UNC veya `..` içeren yol
**fail-closed** reddedilir. Bu bir dosya sistemi sandbox'ı değildir — o `FileSystemGuard`'dır
ve zaten çalıştı; K2 yalnızca "bu değişiklik görevin bildirdiği kaynak kapsamında mıydı"
sorusunu yanıtlar.

**K3 SecretGuard** — yalnızca **eklenen metin** diff satırlarını tarar. Tüm repo,
dokunulmamış geçmiş ve silinen satırlar kapsam dışıdır. **İkili/metin-dışı içerikte sır
tespiti MVP kapsamı dışındadır** ve öyle kalır: `FileInventory` bir ikili tarayıcıya
dönüştürülmez, yeni sır ailesi eklenmez, ikili çıkarım ve entropi taraması yapılmaz. Entropi, ML, vault, rotasyon **yok**.
Desenler **Faz 4'ün tek kataloğundan** bir port üzerinden gelir; ikinci bir regex kümesi
yoktur ve mimari test bunu kilitler — kapı ile redaksiyonun "sır neye benzer" konusunda
ayrışması iki eksikten de kötüdür. Tarama dosya başına **blok** üzerinde yapılır, çünkü PEM
özel anahtar bloğu gibi şekiller satıra sığmaz. Verdict aile adını, yolu ve satırı taşır —
**değeri asla**.

**Kanıt önce, kapı sonra.** Üç kapı da değerlendirilir (teşhis eksik kalmasın), ama sonucu
**ilk bloklayan** kapı belirler: K1 → K2 → K3. Teşhis toplamak ile kapı kazanmak aynı şey
değildir.

`ExecutionSafetyGates` yalnızca `EligibleForResultCommit` der; **commit atmaz**, execution
veya task durumu değiştirmez, acceptance yazmaz, doğrulama koşturmaz, worktree silmez.
Yaşam döngüsü Faz 5C'nindir ve bu tip bilinçli olarak orchestrator diye adlandırılmamıştır.

**`SecretGuardBlocked` geçersiz kılınamaz.** `ignoreSecretGuard`, `forceCommit`,
`overrideSecret` gibi bir mekanizma yoktur ve test bunu isim düzeyinde de kilitler.

## Execution orchestrator (Faz 5C)

`CoreX.Hub.Application.ExecutionOrchestrator` yürütme sıralamasının **tek sahibidir**.
Workspace'i, Context Pack'i, agent runtime'ını, kanıtı, dört kapıyı, Hub sonuç commit'ini
ve doğrulamayı dar port'lar üzerinden koordine eder; hiçbirini **kendisi uygulamaz**.
Süreç başlatmaz, git komutu kurmaz, sır deseni bilmez, test raporu ayrıştırmaz ve
sağlayıcı adı **taşımaz** — yerel bir harness ile barındırılan bir asistan aynı koda
gelir, çünkü içinde onları ayırt edecek hiçbir şey yoktur.

```
Task Ready → Execution Created → Task Running
  PreparingWorkspace → BuildingContext → RunningAgent → CapturingChanges
  K0 → K1 → K2 → K3 → CreateResultCommit → Verifying → Reviewing → Completed
Passed → Task AwaitingAcceptance      ·      diğer her şey → Task Ready
```

**Kanıt önce.** Agent çökse, zaman aşımına uğrasa veya iptal edilse bile `CapturingChanges`
adımına **girilir**: HEAD, GitStatus, GitCommitGraph, GitWorktreeDiff, FileInventory ve
beklenmeyen commit'ler toplanır, sonra terminal sonuç yazılır. Çağıranın iptal token'ı
agent'ı durdurur; **kanıt toplamayı durdurmaz** — aksi hâlde iptal edilen bir koşu, o
koşunun ne yaptığının tek kaydını yok ederdi.

**M4 çarpışması.** Agent terminal olarak başarısızsa **ve** HEAD taban commit'ten farklıysa,
sonucu **agent'ın hatası** belirler: `Failed`, `RuntimePolicyViolation` değil. Beklenmeyen
commit `UnexpectedAgentCommitDetected` + `UnexpectedCommitShas` olarak **teşhis** kaydedilir,
bıraktığı yerde erişilebilir kalır, hiçbir şey onarılmaz.

**Sonuç commit'i** yalnızca K0–K3'ün dördü de geçtiğinde ve yalnızca
`WorkspaceManager.CreateResultCommitAsync` üzerinden atılır — ikinci bir commit yolu yoktur.
Doğrulama `ResultCommitSha` var olmadan **başlamaz**.

**Baseline provenance.** Güncel sonuç execution worktree'sinde ölçülür; baseline
execution'ın **kendi taban commit'ine** sabitlenmiş tek kullanımlık bir snapshot'ta.
Pin ölçümden **önce ve sonra** doğrulanır; HEAD kaydıysa sonuç önbelleğe **girmez**.
Snapshot her hâlükârda bırakılır; bırakılamazsa artık (residual) olarak raporlanır ve
ölçümün ne olduğunu değiştirmez. `BaselineKey` üçlüsü değişmedi:
`RepositoryId + BaseCommitSha + ProfileHash`.

**Bağımsız review.** Doğrulamayı geçen bir sonuç, `ResultCommitSha`'ya sabitlenmiş tek
kullanımlık bir snapshot'ta bağımsız bir reviewer tarafından incelenir (ADR-0022). Agent'ın
worktree'si kullanılmaz. Reviewer **kanıt** üretir — şemaya uyan yapılandırılmış bulgular —
**karar** üretmez: şemada "verdict" alanı yoktur, düz metin hiç okunmaz ve sonuç
`ReviewFindingGate` tarafından verilir.

Reviewer'ın `outOfScope` iddiası kanıttır; **kapsamı Hub belirler**, execution'ın gerçekten
değiştirdiği yollardan. İki cevap uyuşmazsa uyuşmazlık kaydedilir, sessizce düzeltilmez.

Ağaç, reviewer çalışmadan **önce** ve her denemeden **sonra** ölçülür; HEAD, tree veya
temizlik değiştiyse sonuç `ReviewUnusable`'dır ve yeniden denenmez. Yeniden deneme **bir**
kezdir ve yalnızca gelmeyen/uymayan bir belge içindir; süresi dolan, iptal edilen veya düşen
bir sağlayıcı denenmez.

Biten bir sağlayıcı tamamlanmış bir review **değildir**: ölçüldü ki sağlayıcı sıfırla
çıkıp hiç yapılandırılmış çıktı döndürmeyebilir. "Çıktı yok" asla "bulgu yok" sayılmaz.

Reviewer profili katalogdadır (`reviewerProfile`); hangi sağlayıcının incelediği
yapılandırma sorusudur, kod sorusu değil. Profil bildirilmemişse repository review
edilmiyor demektir: adım `Skipped` + `ReviewNotConfigured` olarak kaydedilir — "kimse
bakmadı", "biri onayladı" demek **değildir**.

| Sonuç | Anlamı | İnsan kabulü |
|---|---|---|
| `Passed` | Review çalıştı, bloklayan bulgu yok | Düz kabul |
| `ReviewBlocked` | Review çalıştı, bloklayan bulgu var | Yalnızca override (ADR-0014) |
| `ReviewUnusable` | Kullanılabilir review **yok** | Hiçbiri |

**Lease.** Sahip, süreç kimliği **ve** host başlangıç zamanının çiftidir — işletim
sistemleri süreç kimliğini yeniden kullanır, geri dönüşmüş bir numara özgün sahip değildir.
Heartbeat uzun aşamalar boyunca kendi kısa ömürlü bağlantısında atar (EF `DbContext` iki
yerden kullanılamaz) ve execution terminal olunca durur. Sahibi ölmüş veya susmuş bir
execution `StaleExecutionReconciler` tarafından `Interrupted` yapılır, görev `Ready`'ye
döner ve **worktree korunur**.

**Worktree tutma.** `ResultCommitSha == null` olan hiçbir execution'ın worktree'si otomatik
silinmez (D6) — aggregate bunu `MarkWorkspaceReleased` içinde reddeder.

**Eşzamanlılık.** `MaxConcurrentExecutions = 1`, süreç içi tek semafor. Dağıtık kilit,
kuyruk, worker cluster yok. Aynı görevin iki terminal-olmayan execution'ı olamaz.

**İşlem sınırı.** Her durum geçişi kendi AuditEvent'iyle **tek işlemde** yazılır; hiçbir
veritabanı işlemi agent, git, context veya doğrulama çalışırken açık tutulmaz.

### Kalıcı doğrulama adımı gerçeği

Kabul kararı, koşuyu üreten süreç kapandıktan **sonra** verilir. Bu yüzden bir MACHINE
kabul kriterinin (`VerificationRef`) cevabı SQLite'ta, yorum gerektirmeyen bir biçimde
durmak zorundadır. Faz 5C bu bilgiyi hesaplıyordu ama yalnızca bellekteki
`VerificationRunView` taşıyordu; süreç bitince kayboluyordu. Bu bir mimari değişiklik
değil, Faz 1 kalıcılığının frozen gereksinimi karşılamamasının düzeltilmesidir.

Artık **sonuç** doğrulama koşusunun her adımı için, execution'ın `Verify` adımına bağlı bir
`CommandResult` satırı yazılır: `StepName`, `Status`, `Required`, `ExitCode`, süre ve test
sayaçları. Satırlar Verify adımının kapanışıyla **aynı işlemde** yazılır — doğrulamanın
geçtiğini söyleyen ama kanıtı bulunmayan bir execution oluşamaz.

**`Required` neden satırda duruyor.** Profil sonradan değişebilir. Dün isteğe bağlı olan
`lint` bugün zorunlu yapılırsa, dünkü execution yine de dünkü kurala göre okunmalıdır;
requiredness bu yüzden koşu anındaki değeriyle saklanır ve karar anında profilden
okunmaz.

**`ExitCode` bir cevap değildir.** `Passed` ve `ParseFailed` adımlarının ikisi de sıfırla
çıkar; çıkış koduna bakan bir kriter, Hub raporu hiç okuyamamışken işi doğrulanmış sayardı.
Bu yüzden yapısal `VerificationStepStatus` sözlüğünün tamamı saklanır. Domain kendi
kopyasını tutar (Domain hiçbir şeye bağımlı değildir) ve dönüşüm kompozisyon katmanında
**tüketicidir**: bilinmeyen bir durum sessizce `Failed`'e düşmez, orada hata verir.

**Baseline karışmaz.** `CommandResults` yalnızca execution'ın kendi worktree'sinde ölçülen
sonuç koşusuna aittir. Baseline kendi anahtarında (`RepositoryId` + `BaseCommitSha` +
`ProfileHash`) kalır; okuma execution → `Verify` adımı → komutlar yolundan yapıldığı için
tek kullanımlık ağaçta ölçülen baseline bir kriteri asla cevaplayamaz.

**Belirlenimcilik.** Bir execution içinde bir adım adı tek bir satır demektir —
`UX_CommandResults_Step_Name` bunu veritabanında zorlar; çözümleyici sıfır eşleşmede
"kayıt yok", birden fazla eşleşmede "çelişkili" der ve hiçbir zaman satır seçmez.

## İnsan kararı (Faz 5D)

`AcceptanceService` bir execution hakkındaki insan kararının kaydedildiği **tek** yerdir.
Üç şey yazar ve başka hiçbir şey yazmaz: Task'ın yeni durumu, tek ve değişmez bir
`AcceptanceDecision`, ve bir `AuditEvent` — üçü **aynı işlemde**. Execution'a, bulgulara,
doğrulama satırlarına ve git'e dokunmaz. Kabul, birinin sorumluluğu üstlendiği anlamına
gelir; merge, push veya release anlamına **gelmez** (ADR-0014).

**İki yol.** Normal kabul `AwaitingAcceptance` + `Completed(Passed)` ister; `--override`
verilirse reddedilir, çünkü aşılacak bir kapı yoktur. Override kabulü `Ready` +
`Completed(ReviewBlocked)` ister ve altı ön koşulun tamamını arar: agent adımı `Succeeded`,
beklenmeyen agent commit'i yok, `ResultCommitSha` var, kayıtlı `required` doğrulama
komutlarının tamamı `Passed`, en az bir bloklayıcı `Finding`, ve boş olmayan `Reason`.
Kapalı küme varsayılan olarak reddeder — `ScopeViolation`/`SecretGuardBlocked` kendi
outcome'larıyla bloklandığı için, `ReviewBlocked`'a ulaşmış olmak o kapıların geçtiğinin
yapısal kanıtıdır.

**MACHINE kriterlerinin üç sınıfı vardır.** `Passed` → `Met`. Çalışmış ama geçmemiş her
yapısal durum (`Failed`, `TimedOut`, `Cancelled`, `StartFailed`, `ParseFailed`, `Skipped`)
→ `NotMet`, ve orijinal durum gizlenmeden taşınır. Üçüncüsü bir verdict değildir: kayıt
yoksa, çift kayıt varsa veya durum `Unavailable` ise Hub makine gerçeğini kuramıyor
demektir ve kabul **fail-closed** durur. Eksik kanıtı `NotMet`'e çevirmek, cevaplanamayan
bir soruyu cevaplanmış göstermek olurdu.

**İsteğe bağlı başarısızlık geçerli bir sonuçtur.** `Passed` bir execution içinde
`required: false` bir adım `Failed` olabilir; ona işaret eden kriter dürüstçe `NotMet`'tir
ve insana gösterilir. "Bütün MACHINE kriterleri `Met` olmalı" diye bir kural **yoktur**;
otomatik kapı zaten execution outcome'udur.

**HUMAN kriterleri.** Her biri tam olarak bir kez sorulur ve bir kez cevaplanır.
`Met` / `NotMet` / `NotApplicable` üçü de geçerli cevaptır — hepsinin `Met` olması
istenmez, yoksa liste okunmayan bir formaliteye dönerdi. Machine kriteri elle
cevaplanamaz; ne serviste ne CLI'da böyle bir alan vardır.

**Bloklayıcı bulgu bir küme kuralıdır.** Frozen J.4: kapsam içi `Critical` >= 1 **veya**
kapsam içi `Major` >= eşik (frozen varsayılan **1**) bloklar; yalnızca `Minor`/`Info`
bloklamaz; `outOfScope` hiçbir zaman saymaz. Bu tek bir yerde durur —
`ReviewFindingGate.HasBlockingFindings` — çünkü tek bir bulguya "bloklayıcı mısın" diye
sorulamaz: `Critical` tek başına, `Major` ise sayıyla bloklar. Bu yüzden `Finding` yalnızca
`ParticipatesInReview` der; kararı küme verir. Override, execution'ın `ReviewBlocked`
demesine güvenip kalmaz, aynı kapıyı tekrar sorar: kayıtlı bulgular bloklamıyorsa override
reddedilir. **Major eşiğinin kalibrasyonu G7-1 / Faz 7'dir** ve aynı otoritede, tek bir
sayıyı değiştirerek yapılır.

**Sonuç doğrulaması agent worktree'sinde çalışmaz.** Hem baseline hem sonuç, tam commit'e
sabitlenmiş **tek kullanımlık** bir ağaçta ölçülür: baseline `BaseCommitSha`'da (X), sonuç
Hub'ın kendi commit'inde (Y). Sebep düzenlilik değil: doğrulama ağacı build çıktısıyla
doldurur, build çıktısıyla dolu bir worktree ise zorlamadan kaldırılamaz — ve Hub bir
execution worktree'sini asla zorlamaz. Ölçümü atılabilir bir kopyada yapmak, gerçek
worktree'nin commit'in bıraktığı hâlde kalmasını ve insan kararından sonra release
edilebilmesini sağlar. Ayrıca ölçümü, commit'i barındıran bir dizin hakkında değil,
**commit hakkında** yapar.

Pin iki kez doğrulanır: ölçümden önce ve sonra. HEAD'i oynatan bir komut başka bir commit'i
ölçmüştür; o sonuç raporlanmaz, atılır — yanlış commit'e atfedilmiş bir sonuç, hiç sonuç
olmamasından kötüdür, çünkü execution'ın makine gerçeği olarak kalıcılaşır. Kapanış pin
kontrolü **iptalle atlanmaz**: kendi sınırlı token'ında çalışır, yoksa her adımı biten ve bir
an sonra iptal edilen bir koşu, hangi ağacın ürettiği doğrulanmadan "geçti" diye
raporlanabilirdi.

Ölçülemeyen bir sonuç sessizlik değildir: motorun kendi "başlatamadım" davranışı gibi,
profildeki **her adım için bir satır** yazılır (`StartFailed`/`Cancelled`), böylece o adımı
işaret eden bir MACHINE kriteri "kayıt yok" yerine nedenini öğrenir. Hiçbiri asla `Passed`
olmaz.

`CreateResultCommitAsync` artık commit sonrası worktree'de ne kaldığını da bildirir
(`UncommittedPaths`), tam olarak git'in `worktree remove`'u reddettiği terimlerle: değişmiş
veya izlenmeyen yollar, ignore edilenler hariç. Boş olması beklenir; boş değilse ağaç ile
commit farklı şeylerdir ve orchestrator **fail-closed** davranır — doğrulama yapılmaz,
`VerificationFailed` ile kapanır, hiçbir dosya silinmez.

**Karar sonrası worktree.** Frozen G.6: `Passed` + kabul/ret → worktree kaldırılır, branch
ve result commit korunur, `WorkspaceState = Released`. Sıra kesindir: **önce** karar işlemi
(Task geçişi + `AcceptanceDecision` + `AuditEvent`) commit edilir, **sonra** release
denenir. Temizlik başarısızlığı kararı geri alamaz; ikisi ayrı ayrı raporlanır ve CLI
"Workspace cleanup" satırını kararın yanında ayrı basar. `RequestChanges` ve override
otomatik release **yapmaz**; commit'siz execution hiçbir zaman release edilmez (D6).

**Karar hedefi açıktır.** `hub accept HUB-001#1` — attempt adı verilir, en yenisi
tahmin edilmez. `LatestExecution` en yüksek `AttemptNo`'dur; daha yeni bir attempt varsa
eskisi ne kabul edilebilir ne override edilebilir.

**`hub evidence show`** yalnızca kayıtlı `EvidenceArtifact` meta verisini ve blob deposunu
okur. Commit'i olan execution `GitCommitDiff`, olmayan `GitWorktreeDiff` ile anlatılır ve
hangisinin gösterildiği açıkça yazılır. Review gerçek olduğundan beri aynı türü **iki üretici**
yazar — implementer'ın ve reviewer'ın transkripti de `AgentStdout`'tur — bu yüzden listede
`STEP` sütunu vardır: sahiplik `ExecutionStepId`'dedir ve okuyucu hangisine baktığını
tahmin etmez. Eksik artefakt **yeniden üretilmez** — mevcut
çalışma ağacından diff üretmek aynı kelimelerle başka bir soruyu cevaplamak olurdu.
Redaksiyon yazma zamanında olduğu için bu yolda ham tampon yoktur ve `--raw` gibi bir
seçenek bilerek eklenmemiştir.

## Loop Zero: çalıştır, incele, karar ver (Faz 5E)

Faz 5 iki komutla kapanır. `hub run <TASKKEY>` bir denemeyi baştan sona yürütür ve
`AwaitingAcceptance`'ta **durur**; `hub cancel <TASKKEY>` işi bırakır. Aradaki her şey —
kanıtı okumak, kararı vermek — zaten var olan komutlardır ve hepsi **ayrı süreçlerde**
çalışır: iki komut arasında bellekte hiçbir şey taşınmaz, taşınan tek şey SQLite'a ve blob
deposuna yazılmış olandır.

```
hub run HUB-001                          # Task -> AwaitingAcceptance
hub evidence show HUB-001#1 --diff       # kalıcı makine kanıtı
hub accept HUB-001#1                     # insan kararı -> Accepted, worktree release
```

`hub run` ayrı bir boru hattı **değildir**. Faz 5C'de kanıtlanmış `ExecutionOrchestrator`'ı
kurar ve çağırır; kendi git komutu, kendi agent'ı, kendi kapısı yoktur. Özetini de agent'ın
söylediğinden değil, kalıcılaşmış satırlardan üretir: base/result commit, ContextPackHash,
adım durumları, doğrulama komutları ve kabul kriterleri. Geçen bir koşuda sıradaki iki
komutu yazar; kabul **etmez**.

**Runtime yapılandırmadan gelir.** `agents.*Runtimes` bir `runtimeKey`'in arkasında neyin
durduğunu söyler — deterministik koşum için fixture yolu ve senaryo, kurulu bir araç için
mutlak çalıştırılabilir yolu. Katalogda profil hangi anahtarı adlandırdığını, yapılandırma
o anahtarın bu makinede ne olduğunu söyler; ikisi ayrı olduğu için aynı katalog başka bir
kurulumda migration'sız çalışır. Sağlayıcı seçimi, fallback veya keşif yoktur; çözüm
`hub run` bir execution satırı yazmadan önce yapılır, dolayısıyla bildirilmemiş bir anahtar
worktree kurulmadan reddedilir.

**M5 uyarısı.** Yeni bir deneme başlamadan önce, commit üretmemiş önceki denemelerin
worktree'leri için **tek satır** uyarı basılır. Silmez, engellemez: o ağaç yapılanların tek
kopyasıdır.

**Aynı anda tek execution.** Orchestrator'ın semafor kuralı süreç içidir ve ikinci bir CLI
çağrısı hakkında hiçbir şey söylemez. Kurulum başına bir dosya kilidi (`execution.lock`)
kuralı süreçlerin birbirini görebildiği yerde tutar. Mutex değil — mutex thread'e bağlıdır,
async komut ise thread değiştirir; isimli semafor da değil — sahibi öldürülürse sayacı geri
gelmez. Dosya tanıtıcısını işletim sistemi her koşulda serbest bırakır.

### `hub cancel` — istemekle durdurmuş olmak aynı şey değil

Operatörün iptali, bir execution'ın `Cancelled` bitmesiyle **aynı olay değildir**. İkincisi
bir denemenin durdurulduğunu söyler ve task normalde tekrar denenebilir hâle döner;
birincisi işin kendisinin istenmediğini söyler ve terminaldir. `TaskState.Cancelled`'ı
yazan tek yer budur.

Çalışan bir task için sıra kesindir: execution'ı sahibine **sordur**, veritabanı durduğunu
**söyleyene kadar** izle, ve ancak ondan sonra kararı yaz. Sinyal iletildi diye "iptal
edildi" demek, hâlâ worktree'ye yazan bir sürecin üstüne terminal bir kayıt koymak olurdu.
Durmazsa `CancellationRequested` raporlanır ve **hiçbir şey yazılmaz**. Dinleyen yoksa —
host ölmüşse — istek reddedilir; o execution'ı bir sonraki komutun yeniden uzlaştırması
sahiplenir.

Sinyal **yerel makineye ait**, execution kimliğiyle adlandırılmış bir isimli olaydır: ağ
yok, servis yok, kuyruk yok. Eski bir denemenin sinyali yenisine ulaşamaz, çünkü nesne onu
oluşturan süreçle birlikte yok olur ve yeni denemenin kimliği başkadır. Application katmanı
bir port görür; işletim sistemi tipi o portu geçmez.

Execution gerçeği hiçbir zaman task'a uydurulmaz. Result commit'i olan bir deneme onu
korur: doğrulama sırasında gelen iptal frozen davranışı izler — Verify adımı `Cancelled`,
execution `VerificationFailed`, commit yerinde. Verify adımı **zaten başarılı** olmuşsa
doğru cevap `ReviewUnusable`'dır: doğrulanmış ama kimsenin incelemediği bir sonuç vardır ve
ona `VerificationFailed` demek kayıttaki Verify adımıyla çelişirdi. Task terminal
`Cancelled` olabilir; makine gerçeği yerinde kalır ve `AcceptanceDecision` yazılmaz.

**Kesilen bir yazma sonrası otorite veritabanıdır.** Bir geçiş iki ayrı andır: aggregate
değiştirilir, sonra satır kaydedilir. İptal ikisinin arasına düşerse bellekteki örnek zaten
"terminal" der, satır ise demez — ve aynı context'e sorulduğunda kimlik haritası o
değiştirilmiş örneği geri verir, yani sapma onu yaratan yerden **görünmez**.

Bu yüzden iptali kapatan yol aggregate değil **kimlik** alır, ve durable satırları kendi
kapsamında yeniden okur. Kesilen store üzerinden kaydetmek, vazgeçilmiş mutasyonları da
diske yazmak olurdu. `SaveChanges`'ten gelen bir iptal, yazmanın gerçekleşip
gerçekleşmediği hakkında **hiçbir şey söylemez**; iki ihtimal de yalnızca veritabanına
sorularak ayrılır. Zaten terminal olan bir satır ikinci kez geçirilmez ve outcome'u
çağıranın niyetine uydurulmaz. Aynı kural execution'ı **talep eden** ilk işlem için de
geçerlidir: orada kesilen bir iptal, ya sahipsiz bir `Running` satırı bırakırdı ya da hiç
satır bırakmazdı — cevabı yine veritabanı verir.

Sınırlı `MandatoryWorkScope` her yazmayı yönetmeye devam eder: tek kapanış yolu vardır,
kendini çağırmaz, ve durable gerçek kurulamıyorsa satır **uydurulmaz** — dürüstçe raporlanır
ve son çare olarak uzlaştırıcı kalır. Normal bir iptal artık uzlaştırıcıya ihtiyaç duymaz.

### Çökmeden sonra

Her yeni CLI süreci açılırken sahipsiz kalmış execution'ları uzlaştırır. Result commit'i
**olmayan** bir execution `Interrupted` olur, task `Ready`'ye döner, worktree korunur, git
geçmişine dokunulmaz. Sahiplik pid değil, **pid + host başlangıç zamanı** çiftidir; geri
dönüştürülmüş bir pid orijinal sahip değildir.

Result commit'i **olan** bir execution Faz 5C'de kapatılamıyordu: her terminal hata durumu
"bu deneme hiçbir şey üretmedi" der, bu deneme ise depoda gerçekten duran bir commit
üretmişti. Faz 5E bunu **ileriye doğru** kapatır. Eksik olan tek şey "bu commit nasıl
ölçüldü" sorusunun cevabıdır ve onun iki dürüst kaynağı vardır: zaten kalıcılaşmış gerçek,
ya da commit'in yeniden ölçülmesi. Adım ile komut satırları tek işlemde yazıldığı için
okunabilir bir kayıt vardır; çelişkili bir kayıt (komutsuz bitmiş adım, adımsız komut)
yorumlanmaz, **fail-closed** raporlanır.

İkinci bir result commit yapmak dikkat meselesi değildir: kurtarma servisinde workspace
portu **yoktur**, dolayısıyla oradan bir commit'e giden yol yoktur. Agent yeniden
çalıştırılmaz, ref'e dokunulmaz, dosya silinmez.

**Review de aynı kuralla kapanır.** Kurtarmada reviewer portu da yoktur, yani bir review
elde edilemez — ama zaten elde edilmiş olan okunur. Host, review ile onun sonucunu yazan
satır arasında ölebilir; o anda bulgular ve Review adımı kalıcıdır, ve tek doğru cevap
bulguları okuyup **donmuş kapıya** sormaktır. Aksi hâlde bir reviewer'ın çoktan blokladığı
iş `Passed` olarak kabul edilirdi. Reviewer'ın hiç bakmadığı bir sonuç ise `ReviewUnusable`
olur: review edilen bir repository'de "kimse bakmadı" asla "geçti" değildir.

### Loop Zero kabul takımı

`tests/CoreX.Hub.LoopZero.Tests` gerçek `hub.exe`'yi gerçek bir git deposuna karşı
çalıştırır: gerçek süreç ağacı, gerçek doğrulama komutları, gerçek öldürme ve yeniden
başlatma. Hub'ı öldüren test bir uzlaştırıcı metodu çağırmaz — süreci dışarıdan öldürür ve
bir sonraki sürecin ne yaptığına bakar. Ağ yok, AI yok, sağlayıcı yok.

## Katkı kuralları

Çalışma akışı güvenlik önlemleri — beklenmeyen kirli çalışma ağacı, commit disiplini ve
test disiplini — `CONTRIBUTING.md` dosyasındadır. Orası mimari karar içermez.

## Sağlayıcı bağımsızlığı (provider neutrality)

Otorite **ADR-0008** ve mimari § F.1'dir; burada yeni bir karar yoktur, yalnızca Faz 5
başlamadan **mekanik olarak kilitlenen** kısıtın özeti vardır.

Hedef adapter aileleri: `scripted` · `claude-code` · `codex` · `gemini-cli` · `qwen-code`
· `custom-cli`. Faz 5'te bunlardan **yalnızca `scripted`** gerçek bir runtime olarak
uygulanır; diğerleri bugün ne çağrılır ne de yazılır.

- **Orchestrator sağlayıcı körüdür.** `if provider == "claude"`, `switch (provider)` gibi
  yollar yasaktır. Seçim `AgentProfile.Provider`/`RuntimeKey` verisinden gelir; runtime
  composition katmanında çözülür. Yeni bir adapter eklemek orkestrasyon kodunu
  değiştirmemelidir.
- **Kimlikler veridir.** `Provider` ve `RuntimeKey` `string`'dir, enum değildir; yeni bir
  aile eklemek domain veya şema değişikliği gerektirmez.
- **Yetenekler ilan edilir, taklit edilmez.** Eksik bir yetenek telafi edilmez; adım
  `Unsupported` olur (`ExecutionStepState.Unsupported` zaten vardır). Sessiz geçiş yoktur.
- **Sağlayıcıya özgü CLI bayrakları yalnızca o sağlayıcının adapter'ında yaşar.**
- **Süreç sınırı tektir:** her şey `ProcessRunner`'dan geçer.
- **MVP'de dinamik eklenti (DLL/plugin) çerçevesi yoktur.** Gelecekteki `CustomCliRuntime`
  sabit bir runtime kimliğiyle kayıt olur ve yalnızca şunları kullanır: mutlak
  çalıştırılabilir + `argv[]` + ilan edilmiş yetenekler + ortam allowlist'i +
  `ProcessRunner` yaşam döngüsü. Kabuk komut dizesi yoktur.

### Yerel / kendi barındırılan modeller

Sağlayıcı bağımsızlığı **bulut SaaS ile sınırlı değildir.** Kullanıcı modeli vLLM,
Ollama, OpenAI uyumlu bir uç nokta, kendi HTTP çıkarım servisi veya yerel kurulu bir
harness üzerinden çalıştırabilir. Bu yüzden iki kavram ayrı tutulur:

1. **Agent runtime / harness** — Hub'ın süreç olarak çalıştırdığı şey
2. **O harness'in kullandığı model / backend** — Hub'ın **bilmediği** şey

`ClaudeCodeRuntime` modeli Claude Code'un kendi kimlik doğrulamalı çalışma zamanından
alır; `QwenCodeRuntime` Qwen Code'un yapılandırılmış backend'ini kullanır;
`CustomCliRuntime` rastgele yerel bir kodlama-agent'ı çalıştırılabilirini sarar. İleride
bir `LocalAgentRuntime` altında vLLM/Ollama/OpenAI-uyumlu bir uç nokta olabilir — **bu
Hub'ın sorunu değildir.**

Varsayılmayanlar (hepsi test edilir):

- provider'ın Claude/Codex/Gemini/Qwen'den biri olması gerekmez,
- provider'ın API anahtarı gerektirmesi gerekmez — kimlik doğrulama her zaman
  anahtar/OAuth değildir,
- çalıştırılabilirin bir bulut satıcısına ait olması gerekmez,
- modelin uzakta barındırılması gerekmez,
- runtime'ın modele sahip olması gerekmez (`model` alanı **boş bırakılabilir**).

Katalogda **kimlik bilgisi veya uç nokta alanı yoktur**: `ApiKey`, `Token`, `BaseUrl`,
`Endpoint`, `Host`, `Port` gibi bir kolon ne domain'de ne yapılandırmadadır. Kimlik
bilgisine ihtiyaç duyan bir harness onu kendi yapılandırılmış ortamından alır.

Bugün şu profil **hiçbir tip veya şema değişmeden** ifade edilebiliyor (test ediliyor:
`SelfHostedProfileTests`):

```yaml
- name: local-implementer
  role: Implementer
  provider: self-hosted
  runtimeKey: local-agent
  model: qwen3-coder        # harness modele sahipse tamamen atlanabilir
  permissionProfile: WorkspaceWrite
  softTimeoutSeconds: 300
  idleTimeoutSeconds: 600
  hardTimeoutSeconds: 1800
```

Yukarıdaki `backend: { kind: openai-compatible, baseUrl: … }` tarzı bir nesne **Faz 5'te
eklenmez.** Mevcut yapılandırma bunu gerçekten gerektirmedikçe şema büyütülmez: erken LLM
gateway soyutlaması yok, LiteLLM klonu yok, genel sağlayıcı SDK'sı yok, model router yok.
Faz 5 hiçbir vLLM/Ollama/model HTTP istemcisi uygulamaz — `src/` altında bugün
`HttpClient` dahil hiçbir çıkarım istemcisi yüzeyi yoktur ve bu da test edilir.

### Erişim, kimlik doğrulama ve faturalama

Sağlayıcı bağımsızlığı bir adım daha ileri gider: **bir runtime, birbirinden bağımsız
faturalanan birden çok erişim yöntemini destekleyebilir.** Dört kavram ayrı tutulur ve
tek bir şeye katlanmaz:

```
Provider  ·  Runtime  ·  Authentication method  ·  Billing source
```

Gelecekte desteklenmesi gereken örnekler: `claude-code` hem plan doğrulamalı hesap
oturumu hem Anthropic API anahtarı ile; `codex` hem ChatGPT ile oturum açarak hem OpenAI
API faturalamasıyla; `gemini-cli` Google hesabı, Gemini API anahtarı veya Vertex AI kimlik
bilgisiyle; `qwen-code` Coding Plan, token/API planı, üçüncü taraf ya da yerel sağlayıcı
ile. Yerel/özel runtime'lar **hiçbir dış kimlik doğrulaması veya faturalama
gerektirmeyebilir.**

Varsayılmayanlar (hepsi test edilir):

- bir sağlayıcının tek bir kimlik doğrulama yöntemi olduğu,
- bir runtime'ın tek bir faturalama modeli olduğu,
- abonelik erişimi ile API erişiminin denk olduğu,
- var diye bir API anahtarının kullanılması gerektiği,
- abonelik/hesap erişiminden ücretli API erişimine **otomatik geçilebileceği.**

**Ücretli fallback asla sessizce olmaz.** Hesap oturumu yoksa bu raporlanacak bir
sonuçtur, para harcamaya başlamak için bir gerekçe değil. Erişim profilini kullanıcı
açıkça seçer.

Kavramsal ayrım (dikiş korunur, **Faz 5'te uygulanmaz**):

```
AgentProfile           Role · Provider · RuntimeKey · Model · Permissions · Limits
RuntimeAccessProfile   RuntimeKey · AccessMode · CredentialBinding? · hesap/bulut bağı?
                       · faturalama sınıflandırma meta verisi?
```

Örnek (yalnızca uyumluluk örneği, bugün şema **yok**):

```yaml
runtimeKey: claude-code   accessMode: account-session
runtimeKey: claude-code   accessMode: api-key         credentialRef: anthropic-production
runtimeKey: codex         accessMode: account-session
runtimeKey: gemini-cli    accessMode: google-account | api-key | vertex-ai
runtimeKey: local-agent   accessMode: local
```

**Sırlar `AgentProfile`'da veya normal yapılandırmada asla saklanmaz.** Bugün
yapılandırma grafiğindeki *hiçbir* tipte `ApiKey`/`Secret`/`Password`/`Token` adlı bir
alan yoktur ve bu test edilir. Gelecekteki bir kimlik bilgisi bağı bir **kaynağa referans
verir**, anahtarın kendisini taşımaz.

Orchestrator sağlayıcıya özgü kimlik doğrulamaya da kördür: `if provider == Claude &&
accessMode == ApiKey` gibi yollar yasaktır. Erişim profilini sağlayıcının gerçek
çağrısına çevirmek **adapter'ın işidir**. Yetenekler ileride erişim-modu bilgisini
taşıyabilir, ama `IAgentRuntime` bunun için şişirilmez — üç üyeli sözleşme guard ile
kilitlidir. Genel faturalama motoru yoktur.

Bu kısıtlar `tests/CoreX.Hub.ArchitectureTests/ProviderNeutralityTests.cs` içinde iki
grupta test edilir: bugün geçerli olan **ACTIVE** guard'lar ve ilgili üretim tipi ortaya
çıktığı anda gerçekten denetim yapmaya başlayan **ARMED** guard'lar. Bir ARMED guard,
konusu yokken bile kendi dedektörünü sentetik bir pozitif kontrolle doğrular; yani
"bakacak bir şey olmadığı için geçti" durumuna düşemez.

## Commit'e sabitlenmiş snapshot'lar

`WorkspaceManager.PrepareSnapshotAsync` çağıranın **elinde tuttuğu tam commit'te**
tek kullanımlık bir çalışma ağacı materyalize eder. Normal `PrepareAsync`'ten kasıtla
**ayrı bir işlemdir**: o `DefaultBranch`'i çözer ve `agent/<task>/<attempt>` branch'i
yaratır, bu ise **hiçbir şey çözmez**.

- `PinnedSnapshotRequest.CommitSha` **40 haneli tam object id** olmak zorundadır. `main`,
  `HEAD`, `origin/main` ve kısaltılmış id **reddedilir** — aksi hâlde çağıran sabitlemediği
  bir şeyi sabitlediğini sanırdı.
- Branch, HEAD, remote çözümü **yok**; `fetch`/`pull`/ağ **yok**. Commit yerelde yoksa
  fail-closed.
- Worktree **detached** eklenir (`git worktree add --detach`): baseline'ın branch ref'ine
  ihtiyacı yoktur ve repository ad alanı kirletilmez.
- `worktree add` sonrası HEAD **yeniden okunur** ve istenen commit'e eşit değilse snapshot
  reddedilir (`HeadMismatch`). Sonradan `VerifySnapshotPinAsync` ile tekrar doğrulanabilir —
  üretilen build çıktısı drift değildir, **HEAD'in kayması** driftir.
- Aynı repository kritik bölümü ve aynı Faz 3 sertleştirmesi kullanılır (ikinci bir kopya
  yok): Hub hook'ları, kapalı push hedefi, etkisiz credential helper.
- Snapshot `<workspace-root>/_snapshots/<id>` altındadır — kaynak reponun ve agent
  worktree'sinin **dışında**, execution worktree'lerinden ayırt edilebilir.

**Neden baseline agent worktree'sinde koşmuyor:** doğrulama `bin/`, `obj/`, test sonuçları
ve üretilmiş dosyalar bırakır. Bunlar agent'ın başlangıç dosya sistemini, sonraki
`GitWorktreeDiff`'i, `FileInventory`'yi, K2 kapsam ve K3 sır değerlendirmesini ve dolayısıyla
result commit uygunluğunu **kirletirdi**. Bunu `reset`/`clean` cambazlığıyla çözmeyi
reddediyoruz; izolasyon hem ucuz hem güvenilir.

**Yaşam döngüsü:** snapshot tek kullanımlıktır. "Result commit'i olmayan worktree korunur"
kuralı ona **uygulanmaz** — o kural agent'ın yazdığı, insanın hâlâ ihtiyaç duyabileceği işi
korur; snapshot'ta öyle bir iş yoktur. `ReleaseSnapshotAsync` bu yüzden üretilmiş dosyaları
atarak kaldırır. Bu, kod tabanındaki **tek bilinçli E.1 #8 istisnasıdır**, yalnızca
tek kullanımlık snapshot'lara kapsanmıştır, Hub'ın kendi yarattığı bir worktree'yi kaldırır
ve **hiçbir ref'e, branch'e veya history'ye dokunmaz**. Mimari test istisnanın tekliğini ve
execution worktree release'inin **hâlâ zorlamadığını** kilitler.

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
