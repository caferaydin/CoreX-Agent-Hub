# Faz 10 — Sertleştirme

Faz 10B-1: **yarışsız Windows süreç başlatma**.
Faz 10B-2: **Windows Job Object kaynak limitleri**.
Faz 10C-1: **sert izolasyon hazırlığı ve topoloji kapısı** (WSL'de görev çalıştırma YOK).
Faz 10C-2: **WSL2 sert süreç yürütme düzlemi** (systemd transient unit + cgroup çevrelemesi).

Bu belge Faz 10'un sertleştirme işini kaydeder. Şu an 10B-1, 10B-2, 10C-1 ve 10C-2
uygulanmıştır; geri kalan dilimler (10C-3 ve sonrası) aşağıda ayrıca işaretlenmiştir ve
**uygulanmamıştır**.

**Tam Hard görev kabulü 10C-2'den sonra da REDDEDİLMEKTEDİR** — sebebi §11.12'dedir.

## 1. Kapatılan yarış

Faz 2'den beri `ProcessRunner` çocuğu şöyle başlatıyordu:

```
Process.Start()          <- çocuk ÇALIŞMAYA BAŞLAR
AssignProcessToJobObject <- ancak burada Job Object'e girer
```

`System.Diagnostics.Process` yalnızca **zaten çalışan** bir süreç döndürür. Dolayısıyla iki
satır arasında gerçek bir pencere vardı: o aralıkta çocuğun yarattığı bir torun Job Object'e
**girmez** ve Hub onu ne sayabilir ne de öldürebilirdi.

Bu, mimaride F.5 #1 olarak **açıkça kabul edilmiş artık riskti** ve kodda da öyle
yazılıydı — gizlenmedi, ertelendi. Faz 10A bunu mekanik olarak yeniden kanıtladı.

Yönetilen API ile kapatılamaz: `CREATE_SUSPENDED` karşılığı yoktur.

## 2. Yeni sıra

`src/CoreX.Hub.Execution/Interop/NativeProcessLauncher.cs` — tek yer.

```
1. Job Object oluşturulur ve yapılandırılır
2. pipe'lar, komut satırı ve ortam bloğu hazırlanır
3. CreateProcessW(CREATE_SUSPENDED | CREATE_UNICODE_ENVIRONMENT)
     -> çocuk VARDIR, hiçbir komut çalıştırmamıştır
4. AssignProcessToJobObject
5. ebeveyn, çocuğun pipe uçlarındaki kopyalarını kapatır
6. ResumeThread
7. mevcut asenkron gözetim/pump mantığı
```

**Adım 4 başarısızsa çocuk asla devam ettirilmez**; olduğu yerde sonlandırılır. Job'suz
başlatan bir geri düşüş yolu **yoktur**.

### Bu nasıl kanıtlanıyor

Kaynak kodundaki sıralama kanıt değildir. Asıl kanıt davranışsaldır
(`tests/CoreX.Hub.Execution.Tests/NativeLaunchTests.cs`):

`BeforeResume` seam'i ile başlatma tam o anda 2 saniye askıya alınır — çocuk vardır, Job'a
atanmıştır, devam ettirilmemiştir. Fixture ilk işi olarak bir dosyaya yazar ve .NET bir
sürecin açılması için 2 saniye fazlasıyla yeterlidir. O pencerede dosya **boştur**; resume
sonrası **doludur**.

`CREATE_SUSPENDED` kaldırılsa ya da resume assign'dan öne alınsa bu test **düşer** —
adversarial geçişte ikisi de mutasyonla doğrulandı.

## 3. Yerel kaynak sahipliği

| Kaynak | Tip | Serbest bırakma |
|---|---|---|
| Süreç | `SafeProcessHandle` | `NativeChildProcess.Dispose` |
| Birincil iş parçacığı | `SafeThreadHandle` | launcher `finally` |
| Job Object | `JobObjectHandle` (`SafeHandle`) | `ProcessRunner` `using` |
| Pipe uçları | `SafeFileHandle` (`WindowsPipe`) | `FileStream` + `Dispose` |
| Öznitelik listesi | `AllocHGlobal` | `DeleteProcThreadAttributeList` + `FreeHGlobal` |
| Komut satırı tamponu | `StringToHGlobalUni` | `FreeHGlobal` |
| Ortam bloğu | pinned `char[]` | `fixed` kapsamı |

Hepsi `finally` içinde, **her** yolda serbest bırakılır. Çift kapatma imkânsızdır
(`SafeHandle`), normal yollarda finalizer'a bağımlılık yoktur.

### Handle kalıtımı

`PROC_THREAD_ATTRIBUTE_HANDLE_LIST` ile çocuğa **tam olarak üç** handle geçer: stdin, stdout,
stderr. Ebeveynin kendi uçları `SetHandleInformation` ile devralınamaz yapılır.

Bu dekoratif değil: aksi hâlde çocuk kendi stdout'unun **okuma** ucunu da devralır, boru
hiçbir zaman akış sonu bildirmez ve ebeveyn hiç kapanmayacak bir yazıcıyı bekler. Klasik
"redirected pipe hang" bir kalıtım hatasıdır.

Kanıt: ebeveyn devralınabilir bir sentinel handle açar ve numarasını çocuğa argüman olarak
verir; çocuk o numaraya yazmayı dener. Sentinel **dosyası** boş kalır.

## 4. Başarısızlık semantiği

| Ne başarısız oldu | Sonuç |
|---|---|
| Job oluşturma | `StartFailed` — hiçbir şey başlatılmaz |
| Pipe oluşturma | `StartFailed` |
| `CreateProcessW` | `StartFailed`, PID yok, pump yok |
| `AssignProcessToJobObject` | çocuk **sonlandırılır**, asla resume edilmez, `StartFailed` |
| `ResumeThread` | çocuk sonlandırılır (kalıcı askıda bırakılmaz), `StartFailed` |
| Resume öncesi iptal | çocuk sonlandırılır, **`Cancelled`** — ve asla çalışmaz |

Son satır bilinçlidir: iptal edilmiş bir koşuyu "normal iptal mekanizması öldürsün diye"
devam ettirmek, çağıranın geri çektiği kodu çalıştırmak olurdu.

## 5. Değişmeyenler

Sözleşme korunmuştur ve mevcut 117 Execution testi **değiştirilmeden** geçer:

argv (kabuk yok, argüman vektörü) · çalışma dizini · ortam deny-by-default (ADR-0007) ·
stdin/stdout/stderr · üç seviyeli timeout (soft/idle/hard) · iptal · çıkış kodu ·
`ProcessRunResult` şekli · süreç ağacı temizliği (`KILL_ON_JOB_CLOSE`).

Sağlayıcılarda (`IAgentRuntime`, Claude, Codex, Scripted) **hiçbir değişiklik yoktur**;
`ProcessRunner` içinde sağlayıcıya özel dal yoktur.

### Windows argüman kodlaması

`CreateProcessW` argv almaz, tek bir dize alır. Kodlayıcı
(`Processes/WindowsCommandLine.cs`) `CommandLineToArgvW` kurallarının tam tersidir ve tablo
ile test edilir: bir ters bölü **tırnaktan hemen önce** gelmiyorsa literaldir; geliyorsa
kaçış olur. Sondaki ters bölü dizisi ikiye katlanır, aksi hâlde kapanış tırnağını kaçırır.
Boş argümanın tek gösterimi `""`dir.

Ayrıca gerçek bir süreçten **tur kontrolü** yapılır: kodlayıcı kendi içinde tutarlı ama
yanlış olsaydı bile bu test düşerdi.

## 6. Kaynak limitleri (Faz 10B-2)

10B-1 ağacı sınırlıyordu ama **kaynak tüketimini** sınırlamıyordu. 10B-2 mekanizmayı ekler.

### Sözleşme

`ProcessResourceLimits` — üç isteğe bağlı tavan, başka hiçbir şey:

```
ActiveProcessLimit?        kaç süreç (kök dahil)
JobMemoryLimitBytes?       job'un TOPLAM commit'i
ProcessMemoryLimitBytes?   her süreç ayrı ayrı
```

Sağlayıcı alanı yok, organizasyon kavramı yok, mutable sözlük yok. `ProcessRequest` üzerinden
taşınır; Win32 çevirisi **yalnızca** `CoreX.Hub.Execution` içindedir (guard).

**Yok ≠ sıfır.** Bir değer verilmemişse o kota **hiç etkinleştirilmez**: bayrağı set edilmez,
native alanı sıfır kalır ve Windows onu okumaz. Yönetilen sözleşme "sıfır limit" ifade
edemez, dolayısıyla yanlış okunacak bir sentinel yoktur.

### Win32 eşlemesi

`JobLimitPlan.For(limits)` saf bir fonksiyondur ve kendi tablo testleri vardır:

| İstenen | Bayrak | Alan |
|---|---|---|
| *(her zaman)* | `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE` | — |
| `ActiveProcessLimit` | `JOB_OBJECT_LIMIT_ACTIVE_PROCESS` | `BasicLimitInformation.ActiveProcessLimit` |
| `JobMemoryLimitBytes` | `JOB_OBJECT_LIMIT_JOB_MEMORY` | `JobMemoryLimit` |
| `ProcessMemoryLimitBytes` | `JOB_OBJECT_LIMIT_PROCESS_MEMORY` | `ProcessMemoryLimit` |

Bu eşleme neden ayrı bir değer ve ayrı test? Çünkü **sessizce** bozulur: değeri olmayan bir
bayrak, bayrağı olmayan bir değer, ya da `JobMemoryLimit` yerine yanındaki `ProcessMemoryLimit`
alanına yazılan bir sayı — Windows üçünü de kabul eder ve kimsenin istemediği şekilde
sınırlamaz. Adversarial geçişte dördü de mutasyonla doğrulandı.

`KILL_ON_JOB_CLOSE` **koşulsuzdur**. Bir kota eklemenin bedeli ağacın toplanmaması olamaz.

### Fail-closed

Limitler job **oluşturulurken** uygulanır; yani çocuk, istenen her tavanı zaten uygulayan bir
job'un içine doğar. `SetInformationJobObject` reddederse:

- job dispose edilir,
- `StartFailed` döner,
- **çocuk hiç yaratılmaz** (marker dosyası boş kalır — test edilmiştir).

Uyarı logolayıp devam etmek yoktur. İstenen bir güvenlik politikası verilemiyorsa cevap
"başka bir politika" değildir.

Geçersiz tavan (0 veya negatif) istek sınırında **reddedilir** — sessizce clamp edilmez ve
önce sınırsız bir çocuk başlatılmaz.

### Süreç sayısı semantiği

Job üyeliği Hub'ın başlattığı süreci **de** sayar. Ölçüldü, varsayılmadı:

- `ActiveProcessLimit = 1` → kök çalışır, **hiçbir şey** spawn edemez.
- `ActiveProcessLimit = 2` → kök + bir çocuk; sonraki denemeler `CreateProcess` seviyesinde
  başarısız olur.

### Bellek semantiği

- `JobMemoryLimit`: job'daki her şeyin **toplamı**. Asıl istenen budur — yirmi süreç açarak
  süreç başına tavanın altında kalan bir koşu sınırlanmış sayılmaz.
- `ProcessMemoryLimit`: her süreç ayrı ayrı. Uygulandı ve **bağımsız** test edildi.

Bellek testi makine güvenlidir: 192 MiB tavan, 8 MiB'lik parçalar, fixture kendi 512 MiB
tavanında zaten durur. Kontrol testi de vardır — limitsiz aynı fixture **durdurulmaz** — yoksa
"her zaman başarısız olan bir fixture" testi yanlış nedenle yeşil yapardı.

### Kota ihlali sonucu

Yeni bir `ExecutionOutcome`/`TaskState`/`ProcessRunOutcome` **icat edilmedi**. Kota bir OS
containment olgusudur; gözlemlenebilen şey işletim sisteminin verdiğidir (allocation
başarısızlığı, `CreateProcess` reddi, sonlandırma) ve mevcut `ProcessRunResult` semantiği
üzerinden akar. Üst seviye orkestrasyon anlamları değişmedi.

### Sayısal politika YOKTUR

`ProcessRequest.ResourceLimits` varsayılanı `None`'dır. Üründe evrensel bir sayı **yoktur** ve
bir guard bunu kilitler. Nedeni mekanik: `ProcessRunner` yalnızca agent'ı değil **doğrulamayı
ve git'i de** çalıştırır — agent için seçilmiş bir tavan sessizce bir build'in ve bir clone'un
tavanı olurdu. Mekanizma buradadır; politika çağırandan/kompozisyondan gelir.

Mevcut Logical koşular kota istemez ve aynen çalışmaya devam eder. Bu **geçici uyumluluktur**,
"kotalara gerek yok" beyanı değildir.

## 7. 10C'ye kalan: WSL / Linux süreç ağacı

10B-1 **yalnızca Windows** başlatma ilkelidir. WSL kapsam dışıdır:

Bir WSL organizasyonu için çocuk `wsl.exe` olacaktır ve **Windows Job Object, distro
içindeki Linux süreç ağacını kapsamaz**. Faz 10A'da doğrulandığı üzere çözüm Linux
tarafındadır: organizasyon başına kalıcı distro + execution başına systemd transient
unit/cgroup (`KillMode=control-group`, `MemoryMax`, `TasksMax`). Bu 10C'nin işidir.

Ayrıca Faz 10A-HI-01'in bulduğu eş-kiracılık kuralı da 10C'ye aittir: aynı Windows
kullanıcısı altında kayıtlı bir distro, o kullanıcı altında çalışan güvenilmeyen bir
execution'a karşı sınır değildir.

## 8. Faz 10C'ye devir — zorunlu gereksinim

`HardIsolationReadiness`, Hard bir yürütme topolojisini şu durumda **reddetmelidir**:

- Linux/systemd yürütme birimi için **sonlu** bir süreç sayısı limiti yoksa, veya
- **sonlu** bir bellek limiti yoksa.

Hard izolasyon için başlatılan Windows tarafı yardımcı süreçler de, uygulanabilir olduğu
yerde, sonlu limitli bir Windows Job kullanmalıdır.

10B-2 mekanizmayı verir; **zorunluluk** 10C'nin readiness kontrolüne aittir.

> **Devir tamamlandı (10C-1).** Bu iki zorunluluk artık `policy.process-limit` ve
> `policy.memory-limit` kontrolleri olarak uygulanmıştır (§10.3). Beyan edilmemiş bir tavan
> ortamı hazır olmaktan çıkarır.

## 9. Kalan sınırlamalar

- Linux kaynak zorlaması (systemd/cgroup: `MemoryMax`, `TasksMax`, `KillMode=control-group`)
  **10C-2'de uygulandı** — bkz. §11. 10C-1 yalnızca bu zorlamanın mümkün olup olmadığını ölçer.
- Sert izole bir ortam içinde **bir süreç** çalıştırılabilir (§11); **bir Hub görevi**
  çalıştırılamaz. Workspace, context, git, doğrulama, review, kanıt ve sağlayıcı kimlik
  doğrulaması hâlâ host tarafındadır ve 10C-3'e aittir.
- CPU-rate kotası bu dilimin kapsamında değildir; yürütme süresinin otoritesi soft/idle/hard
  duvar saati zaman aşımlarıdır.
- Sert izole bir ortam içinde **görev** çalıştırma yolu yoktur; hazır bir makine bile
  reddedilir (§10.12, §11.12).
- Kontrol düzlemi **ağ** izolasyonu ölçülmemiştir ve bu build'de ölçülemez (§10.9); bu tek
  başına hiçbir sert ortamın `Ready` olamayacağı anlamına gelir.

## 10. Sert izolasyon hazırlığı ve topoloji kapısı (Faz 10C-1)

**Bu dilim WSL içinde görev ÇALIŞTIRMAZ.** Yaptığı tek şey, bir organizasyonun beyan ettiği
sert sınırın bu makinede gerçekten var olup olmadığını sormak ve cevabı hayır olduğunda
yürütmeyi reddetmektir.

### 10.1 Kapatılan boşluk

Faz 1'den beri `Organization.ResolveStatus` şunu söylüyordu:

```
requiresHardIsolation && isolationLevel == Logical  ->  Blocked
aksi hâlde                                          ->  Active
```

Yani YAML'a `isolationLevel: Hard` yazmak, organizasyonu **Active** yapıyordu. Katalog
açısından doğruydu — operatörün niyeti buydu — ama niyet bir sınır değildir. Distro hiç
oluşturulmamış olabilir, agent kullanıcısı `sudo` grubunda olabilir, `/mnt/c` bağlı olabilir.
Katalog bunların hiçbirini bilemez ve bilmemelidir.

10C-1 bu iki soruyu ayırır:

| Soru | Kim cevaplar | Nerede yaşar |
|---|---|---|
| Operatör ne beyan etti? | `IsolationLevel` | katalog (veritabanı) |
| Makine gerçekten ne sağlıyor? | `HardIsolationReadiness` | çalışma anında ölçüm |

Domain **değişmedi**: bir Hard organizasyon hâlâ `Active`'dir. Değişen, yürütmeye kabul
noktasıdır.

### 10.2 Üç soru, bu sırayla

`HardIsolationAdmissionService` — üretimde tek uygulama, guard ile kilitli:

```
1. bu organizasyonun kendi ortamı gerçek mi          -> HardIsolationReadiness
2. makinenin tamamı bu sınırı değerli kılacak
   biçimde düzenlenmiş mi                            -> IsolationTopologyReadiness
3. bu build o ortamın içinde bir görev çalıştırabilir mi -> IHardExecutionPlane
```

Sıra teşhis içindir, karar için değil: hiçbir adım bir reddi kabule çeviremez. Distro'su
olmayan bir operatöre komşusundan bahsetmek onu yanlış yere gönderirdi.

Reddin gerekçe kodu stabildir ve loglanabilir:

| Kod | Anlamı |
|---|---|
| `hard_isolation_not_configured` | Hard beyan edildi, `hardIsolation` bloğu yok/geçersiz |
| `hard_isolation_not_ready` | Ortam bu makinede kurulu değil |
| `isolation_topology_not_ready` | Komşu bir organizasyon sınırı delebilir |
| `hard_execution_plane_unavailable` | Ortam doğru; bu build içinde çalıştıramıyor |

### 10.3 Hazırlık: 21 soru, hepsi zorunlu

`HardIsolationChecks.Required` kapalı bir listedir. Sonuç, listedeki **her** kimliği taşıyacak
şekilde tamamlanır; bir probe soruyu cevaplamayı unutursa cevap `Unknown` olur.

**`Unknown` asla `Pass` değildir.** Üç değerli olmasının sebebi budur: "ölçüldü ve yanlış"
(`Fail`) ile "ölçülemedi" (`Unknown`) farklı şeylerdir, ikisi de hazır değildir, ve bunları
ikiye indirmek "kimse bakmadı"yı bir başarıya çevirirdi.

| Aile | Kontroller |
|---|---|
| Ortam | `environment.exists`, `environment.version` |
| Agent kimliği | `agent.user.exists`, `agent.user.not-root`, `agent.user.not-privileged` |
| Host erişilebilirliği | `host.automount.disabled`, `host.interop.disabled`, `host.path.not-imported` |
| Kökler | `execution-root.native`, `evidence-root.native`, `execution-root.usable`, `evidence-root.not-agent-readable` |
| Kaynak denetimi | `systemd.available`, `cgroup.v2`, `cgroup.controller.memory`, `cgroup.controller.pids` |
| Politika | `policy.process-limit`, `policy.memory-limit` |
| Sızıntı | `host.provider-auth.unreachable`, `peer.filesystem.unreachable`, `control-plane.unreachable` |

`control-plane.unreachable` bu build'de **hiçbir zaman `Pass` olmaz** — sebebi §10.9'dadır.

Politika satırları §8'in devridir: sonlu bir süreç tavanı ve sonlu bir bellek tavanı **beyan
edilmemişse** ortam hazır değildir. Ürün genelinde varsayılan bir sayı yoktur (§6).

### 10.4 Neden `uid != 0` yeterli değil

`agent.user.not-privileged` grup üyeliğine bakar: `sudo`, `wheel`, `admin`, `adm`, `docker`,
`lxd`, `kvm`, `disk`, `shadow`, `root`. Sebebi mekanik — `sudo` grubundaki bir hesap istediği
an root olur, `docker`/`lxd` grubundaki bir hesap ayrıcalıklı bir daemon'a yaptırır,
`disk`/`shadow` grubundaki bir hesap root olmadan root'un okuduğunu okur.

Grup üyeliği tek yol da değildir: `sudoers` içindeki bir satır bir kullanıcıyı doğrudan
adlandırabilir ve o kullanıcı dikkat çeken hiçbir gruba ait olmaz. Bu yüzden gruplar temizse
ikinci soru sudo'nun kendisine sorulur — `sudo -n -l`, yalnızca **listeler**, hiçbir şey
çalıştırmaz. Sıfır çıkış kodu parolasız yükselme demektir ve `Fail`'dir. Bir guard, sudo'nun
bu tek biçim dışında çağrılamayacağını kaynak seviyesinde kilitler.

İddia bilinçli olarak dardır: **yapılandırılmış yönetici ayrıcalığı yoktur**. Bilinmeyen bir
Linux yetki yükseltme açığına bağışıklık iddia edilmez.

Bu makinedeki gerçek ölçüm (varsayılan Ubuntu): `uid=1000`, gruplar `adm` ve `sudo` →
**FAIL**. Sıradan bir WSL dağıtımı bir sınır değildir.

### 10.5 PATH kontrolü neden sentetik bir işaretçi kullanır

WSL, çağıran Windows sürecinin PATH'ini distro içindeki PATH'e ekler. Probe neredeyse boş bir
ortamla başlatıldığı için, "agent'ın PATH'i temiz mi" diye sormak yapılandırma ne olursa olsun
temiz bir cevap döndürürdü — yani **hiçbir zaman düşemeyecek** bir kontrol.

Bu yüzden `wsl.exe`'ye tek bir sentetik PATH girdisi verilir: var olmayan ve hiç
oluşturulmayacak bir dizin. O işaretçi distro içinde görünüyorsa PATH aktarımı açıktır.
Operatörün gerçek PATH'i hiçbir zaman verilmez ve hiçbir zaman raporlanmaz.

### 10.6 Eş-kiracılık kuralı (topoloji)

Faz 10A-HI-01'in bulgusu: aynı Windows kullanıcısı altında kayıtlı bir distro, o kullanıcı
altında çalışan güvenilmeyen koda karşı sınır değildir — o kod basitçe `wsl -u root` der.

Dolayısıyla organizasyon başına hazırlık bu soruyu **cevaplayamaz**. Kural komşular
hakkındadır:

> Bir Hard organizasyonun çalışmasına izin veriliyorsa, repository denetimli kod
> çalıştırabilen **her** organizasyon ya kendisi Hard ve hazır olmalı, ya da hiç
> çalıştıramıyor olmalıdır.

Kapı **Hard organizasyona** uygulanır, komşuya değil. Logical bir organizasyon aynen çalışmaya
devam eder; kaybettiği tek şey yanında Hard bir kiracı bulundurma imkânıdır.

Yürütemeyen bir komşu tehdit değildir (bloke bir organizasyon hâlâ katalogda durur). Sadece
beyan eden bir komşu ise tehdittir: beyan etmek olmak değildir, kod her iki hâlde de paylaşılan
principal altında çalışır.

### 10.7 Probe: yalnızca okur, yalnızca ProcessRunner üzerinden

`WslReadinessProbe` sistemde WSL'in var olduğunu bilen tek yerdir (yanındaki
`WslStatResponse`, toplu `stat` cevabını okuyan saf fonksiyondur). Guard'larla kilitlenen
özellikler:

- **Kendi başlatıcısı yok.** `wsl.exe` sıradan bir çalıştırılabilirdir ve `ProcessRunner`
  üzerinden geçer (ADR-0006, E.1 #1): job object, üç seviyeli timeout, sınırlı yakalama, boş
  ortam. `Process.Start` / `ProcessStartInfo` yoktur.
- **Her çağrıda `--exec`.** Onsuz `wsl.exe` kalanı distro'nun login shell'ine verir; bu, kabuk
  istemeyen bir sisteme kabuk geri koymak ve cevabı agent'ın düzenleyebildiği bir dotfile'a
  bağlamak olurdu.
- **Yalnızca okuyan araçlar.** Probe'un adlandırabileceği guest çalıştırılabilirleri kapalı bir
  kümedir: `cat`, `id`, `printenv`, `stat`, `systemctl`, `test`. `mkdir`/`chown`/`mount` bir
  kontrolü geçirmek için eklenemez — önce bu testin değiştirilmesi gerekir.
- **Provizyon yok.** `--import`, `--install`, `--unregister`, `--set-version`, `--shutdown`,
  `--terminate` ve `wsl.conf` kaynakta yasaktır. Ölçtüğünü onaran bir teşhis, bir daha
  "sorun yok" dediğinde kimsenin güvenemeyeceği bir teşhistir.

#### Ortam değişkeni: yalnızca `SystemRoot`

Ölçüldü, varsayılmadı. `wsl.exe` WSL servisine RPC ile ulaşır ve `SystemRoot` yokken bu çağrı
herhangi bir distro'ya temas etmeden `0x8007072c` ile düşer. Distro **listelemek** onsuz da
çalışır — yani bu, her guest sorusunu sessizce sahte bir `Unknown`'a çeviren türden bir yarı
çalışırlıktı. Başka hiçbir değişken verilmez; `WSLENV` ayarlanmadığı için hiçbiri distro'ya
geçmez.

#### Ölçüldü mü, yoksa ölçülemedi mi

Toplu her `stat` çağrısına, var olmaması imkânsız bir yol (`/proc/version`) eklenir. `stat`
eksik yolu stderr'e yazıp diğerlerini listelediği için "bu yol için satır yok" normalde
"yol yok" demektir — ama `wsl.exe` ortama hiç ulaşamadığında da tam olarak aynı şeyi gösterir.
Kontrol satırı yoksa hiçbir şey ölçülmemiştir: `Unknown`, bulgu değil.

Bu karar `WslStatResponse` içinde saf bir fonksiyondur ve makinesiz test edilir; gerçek bir
ortamla test edilemez, çünkü "kayıtlı ama girilemeyen" bir dağıtım hazırlamak operatörün
makinesini bozmak demektir.

Aynı sebeple `test` çağrılarında yalnızca 0/1 çıkış kodları cevap sayılır ve `systemctl`
çıktısı systemd'nin kendi durum sözlüğüne karşı doğrulanır.

### 10.8 Sır sızdırmaz

Hazırlık sonucu yalnızca şunlardan karar verilir: varlık, çıkış kodları, uid/gid, grup adları,
izin bitleri, dosya sistemi tipleri, bağlama noktaları. İçeriği okunan iki dosya vardır —
bağlama tablosu ve cgroup denetleyici listesi — ve ikisi de saklanmadan önce bu alanlara
indirgenir. Bağlama kaynakları (gerçek Windows yolları) **atılır**; sadece bağlama noktası ve
dosya sistemi tipi tutulur.

Test bunu makinenin kendi kimliğine karşı doğrular: rapor, host kullanıcı profili yolunu,
hesap adını, `/etc/shadow`'u ya da bir kimlik bilgisi konumunu içeremez.

### 10.9 Kontrol düzlemi kontrolü neden asla `Pass` olmaz

Soru şudur: sert izole bir agent, çok kullanıcılı bir yetkilendirme sınırı **olmayan**
Hub API'sine ulaşabiliyor mu? Bir dosya sistemi gözlemi bunu cevaplayamaz. Hiçbir host birimi
bağlı olmayan bir ortam bile Hub'a loopback yönlendirmesi, host gateway'i veya mirrored
networking üzerinden ulaşabilir ve bunların hiçbiri bağlama tablosunda iz bırakmaz.

Bu yüzden:

- görülebilen bir **dosya sistemi yolu** varsa (Hub'ın veri dizinini taşıyan birim bağlıysa)
  → `Fail`, çünkü bu zaten kesin bir erişim yoludur;
- yoksa → `Unknown`, çünkü ağ yolları elenmiş değildir ve bu build'in bunu ölçen bir probe'u
  yoktur.

`Pass` seçeneği yoktur ve bir guard bunu kaynak seviyesinde kilitler. Sonucu şudur: **hiçbir
sert ortam bu dilimde `Ready` olamaz.** Bu bilinçlidir — ağ zorlaması ve onu kanıtlayabilecek
probe 10C-2/10C-3'e aittir. Bir yapılandırma varsayımını ölçüm kılığında `Pass` yapmak, tam
olarak bu kontrolün engellemek için var olduğu hatadır.

### 10.10 Yapılandırma (veritabanına yazılmaz)

```yaml
organizations:
  - slug: ornek
    isolationLevel: Hard
    requiresHardIsolation: true
    hardIsolation:
      backend: Wsl2
      environmentId: corex-ornek        # distro adı
      agentUser: corex-agent            # ayrıcalıksız
      serviceUser: corex-hub            # agent'tan FARKLI olmalı
      executionRoot: /srv/corex/exec    # mutlak Linux yolu
      evidenceRoot: /srv/corex/evidence # agent okuyamamalı
      processLimit: 64
      memoryLimitBytes: 4294967296
```

Şema değişikliği **yoktur**, migration **yoktur**. Bunların hiçbiri katalog gerçeği değildir;
hepsi bu makinenin operasyonel yapılandırmasıdır. Bir ortamın var olduğunu iddia eden ve
tanımladığı şeyden daha uzun yaşayan bir satır, hazırlık kontrollerinin yakalamak için var
olduğu tam olarak o hatadır. Bir guard `HardIsolationConfig`/`HardIsolationProfile`/
`HardIsolationReadiness` isimlerinin Infrastructure'a girmesini engeller.

`agentUser` ile `serviceUser` aynı olamaz: gözlemlenen tarafın kendi kanıtını yazıp
düzenleyebildiği bir düzenek kanıt değildir.

### 10.11 `hub doctor isolation`

Salt okunur teşhis. Kapının kullandığı **aynı** hesaplamadan geçer — ayrı bir uygulama bugün
hemfikir olup yarın olmayabilirdi.

```
hub doctor isolation
```

| Çıkış kodu | Anlamı |
|---|---|
| 0 | Hard organizasyon yok, **veya** hepsinin ortamı kurulu |
| 4 | Beyan edilmiş bir ortam bu makinede kurulu değil |
| 1 | Kullanım hatası |

4, 2'den (reddedildi) ayrıdır: burada hiçbir şey reddedilmemiştir; komut istendiği işi tam
olarak yapmıştır ve cevap hayırdır.

Komut **0 ile çıkarken bile** yürütmenin reddedildiğini raporlayabilir. Bu bir tutarsızlık
değil: makine doğru olabilir ve bu build yine de içinde çalıştıramaz. Bu yüzden iki satır
ayrıdır:

```
environment: ready
executions: refused [hard_execution_plane_unavailable] ...
```

### 10.12 Bu build neden hazır bir makineyi bile reddediyor

`UnavailableHardExecutionPlane` — Application katmanında, üretimdeki tek `IHardExecutionPlane`
uygulaması. Bir yer tutucu değil, bu build'in dürüst tarifi: sert bir ortamı **ölçebilir**,
içinde bir görev **çalıştıramaz**.

Tek alternatif işi host üzerinde çalıştırmak olurdu ve bu, organizasyonun tam olarak
olmamasını istediği şeydir. Geri düşüş yolu yoktur, Logical'a düşürme yoktur, "WSL yok,
Windows'ta çalıştırıyorum" yoktur.

### 10.13 Kararlar makinesiz kanıtlanır

Yukarıdaki kontrollerin çoğu, sağlıklı bir dizüstünde **hiçbir zaman** görülmeyecek cevaplardan
karar verir: `sudo` grubundaki bir agent, agent'ın okuyabildiği bir evidence dizini, `pids`
denetleyicisi olmayan bir çekirdek. Bunları gerçek bir makinede kurmak, operatörün dağıtımını
bozmak demektir — ve bu faz bunu açıkça yasaklar.

Bu yüzden dikiş **komut çalıştırıcısıdır** (`IWslCommandRunner`). Üstündeki hiçbir şey
değiştirilmez: gerçek probe, gerçek ayrıştırma ve gerçek kararlar çalışır; yalnızca ortamın
cevapları yazılır. Her test doğru bir ortamdan başlar ve **tam olarak bir** cevabı bozar, böylece
düşen kontrol her zaman testin değiştirdiği şeye atfedilebilir.

Doğru senaryonun kendisi de bir testtir: 21 kontrolün 20'si geçer, geriye yalnızca kontrol
düzlemi `Unknown` kalır (§10.9). Bu, aşağıdaki başarısızlıkların baştan yanlış bir script'in
yan ürünü olmadığını garanti eder.

### 10.14 Gerçek makine ölçümü (negatif fixture)

`tests/CoreX.Hub.Integration.Tests/WslReadinessProbeTests.cs` gerçek WSL'e karşı çalışır ve
hiçbir şeyi değiştirmez. Bu makinedeki sonuç (varsayılan Ubuntu, agent olarak `root`
sorulmuş):

| Kontrol | Sonuç |
|---|---|
| `agent.user.not-root` | FAIL — uid 0 |
| `agent.user.not-privileged` | FAIL — `root` grubu |
| `host.automount.disabled` | FAIL — `/mnt/c`, `/mnt/d` (9p) |
| `host.interop.disabled` | FAIL — `WSLInterop` kayıtlı |
| `host.path.not-imported` | FAIL — işaretçi distro içinde göründü |
| `host.provider-auth.unreachable` | FAIL — host profil birimi `/mnt/c` üzerinden erişilebilir |
| `control-plane.unreachable` | FAIL — Hub veritabanının birimi `/mnt/c` üzerinden erişilebilir |
| `peer.filesystem.unreachable` | FAIL — `/mnt/wsl`, `/mnt/wslg` tüm distrolarca paylaşılır |

Testler her makinede anlamlıdır: WSL olmayan bir makinede ortam incelenemez, her kontrol
`Unknown` olur ve `Unknown` hazır değildir. Ulaşılabilir bir ortamda ise **hiçbir kontrol
`Unknown` kalmamalıdır** — kalması, probe'un aslında soramadığı bir soruyu bir makine sorunu
gibi göstermesi olurdu.

### 10.15 Bu dilimde YAPILMAYANLAR

- WSL içinde görev çalıştırma (`10C-2`).
- systemd transient unit / cgroup uygulaması (`MemoryMax`, `TasksMax`,
  `KillMode=control-group`).
- Distro oluşturma, kullanıcı açma, `wsl.conf` düzenleme, sağlayıcı oturumu — hiçbiri, hiçbir
  komutta.
- Kontrol düzlemi ağ izolasyonunun **zorlanması** ve ölçülmesi (`10C-2`/`10C-3`).
- Şema değişikliği, migration, API/UI alanı. `OrganizationDto` durum taşımaz ve taşımaya
  başlamadı.
- Gerçek Kronos repository'si, bağlamı, kimlik bilgisi veya şirket verisi (ADR-0017).

## 11. WSL süreç yürütme düzlemi (Faz 10C-2)

**Bu dilim bir Hub görevini sert izole ETMEZ.** Yaptığı tek şey, WSL2 ortamının içinde
*çevrelenmiş bir süreç* çalıştırabilen ilkeli eklemektir. Görevin geri kalanı — repository,
context, git, doğrulama, review, kanıt, sağlayıcı kimlik doğrulaması — hâlâ host tarafındadır ve
tam Hard görev kabulü **hâlâ reddedilir** (§11.12).

### 11.1 Güven ayrımı

```
Windows Hub            = GÜVENİLEN kontrol düzlemi   -> distro'ya root olarak girer
systemd transient unit = sınır
workload               = GÜVENİLMEYEN               -> --uid=<agentUser>
```

Root'a girmek zorunludur: systemd unit oluşturmak ayrıcalıklı bir işlemdir ve çevrelemeyi
dayatmaya izinli olmadan çevreleme istenemez. Güvenli kılan şey, aynı komutta workload'ın
**başka bir kimliğe** verilmesidir. `--uid` olmadan workload da root olurdu ve o durumda sınır
diye bir şey kalmazdı.

Profil bir agent kullanıcısı adlandırmıyorsa istek **reddedilir**; root'a düşülmez.

### 11.2 Komut yüzeyi (kurulu systemd 259'a karşı ölçüldü)

Dokümantasyondan değil, makineden. Her seçenek bu sürümün `systemd-run --help` çıktısında vardır
ve dayanılan davranış ayrıca doğrulanmıştır.

```
wsl.exe -d <environment> -u root --exec
  /usr/bin/systemd-run
    --quiet                       systemd-run'ın kendi gürültüsü workload stdout'una karışmasın
    --wait                        servis bitene kadar bekler ve ÇIKIŞ KODUNU döndürür
    --pipe                        stdin/stdout/stderr doğrudan bağlanır
    --collect                     başarısız olsa bile unit'i kaldırır
    --unit=corex-hub-<32 hex>.service
    --uid=<agentUser>
    --working-directory=<mutlak Linux yolu>
    --property=TasksMax=<processLimit>
    --property=MemoryMax=<memoryLimitBytes>
    --property=MemorySwapMax=0
    --property=KillMode=control-group
    --property=RuntimeMaxSec=<hardTimeout + 15>
    --setenv=NAME=VALUE ...
    -- <executable> <argv...>
```

İptal:

```
wsl.exe -d <environment> -u root --exec /usr/bin/systemctl stop <unit>
```

`wsl.exe` sıradan bir Windows çocuğudur ve 10B-1/10B-2'de dondurulan yarışsız Job Object
sınırından geçer. `Process.Start` yoktur, `cmd.exe` yoktur, `powershell` yoktur.

### 11.3 Kabuk yoktur

`systemd-run` orkestrasyondur, yorumlayıcı değil. Çalıştırılabilir ve **her** argüman ayrı birer
argv girdisi olarak kalır; kaçırılacak bir komut satırı yoktur.

`--` ayırıcısı önemlidir: ondan sonrası workload'dır, dolayısıyla `--uid=root` gibi görünen bir
workload argümanı systemd-run tarafından kendi seçeneği sanılamaz. Bu bir testle kilitlidir.

### 11.4 Unit kimliği

`corex-hub-<correlation id, 32 hex>.service`

Yalnızca Hub'ın ürettiği bir correlation id'den türetilir. Görev hedefi, repository adı, API
parametresi veya agent çıktısı **hiçbir biçimde** unit adını etkileyemez — çünkü unit adı bir
kontrol düzlemi tutamağıdır ve onu etkileyebilen, sahibi olmadığı bir unit'i adlandırabilir.

`corex-hub-` öneki dekoratif değildir: iptal yolu bu öneki taşımayan hiçbir ada işlem yapmaz.
`ssh.service`, `*`, `corex-hub-something.service` — hepsi reddedilir.

Ad çakışması **fail-closed**'dır: unit zaten yüklüyse çalıştırma reddedilir, mevcut bir unit'e
bağlanılmaz.

### 11.5 Ön koşullar önce sorulur

systemd, çözülemeyen bir kullanıcıyı 217, girilemeyen bir çalışma dizinini 200 ile bildirir — ve
bunlar 217 ile çıkan bir workload'dan **ayırt edilemez**. Bu yüzden hepsi önceden sorulur:

| Soru | Komut | Başarısızsa |
|---|---|---|
| agent kullanıcısı çözülüyor mu | `id -u <agentUser>` | reddedilir |
| agent uid 0 mı | aynı çıktı | reddedilir |
| çalışma dizini var mı, dizin mi | `stat -c %A <dir>` | reddedilir |
| unit adı boşta mı | `systemctl show <unit> --property=LoadState` | reddedilir |

Sonuç: bunlardan sonra `systemd-run --wait`'in döndürdüğü kod **workload'ın kendi çıkış kodudur**
ve bu ölçülmüştür (0, 1, 124 aynen geçer). "Unit oluşturuldu" hiçbir zaman "workload 0 ile çıktı"
diye yorumlanmaz.

### 11.6 Çalışma dizini

Segment bazlı doğrulama (`LinuxPath`), metin bazlı değil:

- mutlak Linux yolu olmalı,
- `..` reddedilir (çözülmez — Hub'ın kendi ürettiği bir yol değilse `..` ya hatadır ya girişim),
- `\` veya `:` içeremez (Windows yolu),
- `/mnt`, `/media`, `/proc`, `/sys`, `/dev` altında olamaz,
- profilin `executionRoot`'u **altında** olmalı.

`/srv/corex/execution`, `/srv/corex/exec` altında **değildir** — önek karşılaştırması bunu yanlış
yapardı, segment karşılaştırması yapmaz.

### 11.7 Ortam ve HOME

Workload'ın ortamı, ortamın bir servise verdiği şey **artı** istekte açıkça adlandırılanlardır.
Windows tarafından hiçbir şey miras alınmaz: istek isim listesi değil **değer** taşır, ve WSL
yalnızca `WSLENV`'in adlandırdığını iletir — burada `WSLENV` ayarlanmaz.

Gerçek makinede ölçülen servis ortamı: `LANG`, `PATH` (yalnızca Linux dizinleri), `USER`,
`LOGNAME`, `HOME`, `INVOCATION_ID`, `MEMORY_PRESSURE_*`. Windows PATH'i, `ANTHROPIC_*`,
`OPENAI_*`, `USERPROFILE`, `APPDATA` — **hiçbiri yok**.

`HOME` uydurulmaz: systemd onu servisin çalıştığı uid'in passwd kaydından çözer. Windows
`%USERPROFILE%` iletimi yoktur.

### 11.8 Kaynak tavanları — ve ölçümün yakaladığı gerçek açık

| Politika | systemd özelliği |
|---|---|
| `HardIsolationProfile.ProcessLimit` | `TasksMax` |
| `HardIsolationProfile.MemoryLimitBytes` | `MemoryMax` |
| *(aynı politika)* | `MemorySwapMax=0` |
| — | `KillMode=control-group` |
| `HardProcessRequest.HardTimeout` | `RuntimeMaxSec` (+15 sn) |

Hiçbiri koşullu değildir: bir tanesi atlanmış koşu "biraz daha az çevrelenmiş" değil, atlanan
boyutta **hiç çevrelenmemiş** bir koşudur.

**`MemoryMax` tek başına bir bellek tavanı değildir.** Bu varsayılmadı, ölçüldü: 64 MiB
`MemoryMax` altında 192 MiB isteyen bir workload 192 MiB'in **tamamını** aldı — tavanın üstündeki
her şey swap'e gitti. Bu, gerçek WSL testi tarafından yakalandı ve üretim komutuna
`MemorySwapMax=0` eklendi. Sayfalanarak aşılabilen bir sınır, koşunun tükettiği şeyin sınırı
değildir.

Süreç sayısı semantiği Windows Job Object ile aynıdır: **kök de sayılır**. `TasksMax=3` ölçüldü →
workload + 2 çocuk; üçüncü `fork` `EAGAIN` aldı. Limitsiz kontrol koşusu 10 çocuğun hepsini
oluşturdu.

### 11.9 İki süre tavanı, bilinçli

```
host   : ProcessRunner hard timeout        -> HardTimeout
Linux  : RuntimeMaxSec = HardTimeout + 15s -> Hub yokken bile geçerli
```

Redundant değildirler. `wsl.exe`'yi öldürmek, başlattığı Linux servisi hakkında **hiçbir şey**
kanıtlamaz. Linux tavanı daha uzundur ki Hub ayaktayken host önce fark etsin ve unit'i kendisi
durdursun; Hub yoksa da iş yine de sınırlansın.

Gerçek ölçüm (§30 senaryosu): başlatıcı hemen çıktı, `RuntimeMaxSec=5` unit'i sonlandırdı
(`Result=timeout`), cgroup kayboldu. `--collect` sayesinde başarısız unit kendini de kaldırdı —
`reset-failed` gerekmedi.

`HardProcessOutcome`'da "ortam tavanı bitirdi" diye **yedinci bir değer yoktur**. Sebebi dürüst:
`systemd-run`, `RuntimeMaxSec` tetiklendiğinde de workload 1 ile çıktığında da aynı kodu döndürür
(ölçüldü), ve ikisini ayırmanın tek yolu unit'i yüklü bırakıp sonradan sorgulamaktır — ki bu,
terk edilmiş bir Hub'ın arkasında başarısız unit bırakmamasını sağlayan kendi kendine toplanmadan
vazgeçmek demektir. Tavan bir değerle ilan edilmek yerine testle kanıtlanır.

### 11.10 İptal ve ağaç temizliği

İptal "wsl.exe'yi öldür ve umut et" değildir. Hub'ın **kendi** unit'ine karşı açık bir
`systemctl stop` işlemidir, ve normal olmayan **her** bitiş (iptal, soft/idle/hard timeout) bunu
tetikler.

Gerçek ölçüm: parent + child + grandchild (`TasksCurrent=3`, cgroup'ta 3 pid) → `systemctl stop`
→ cgroup dosyası yok, üç `/proc/<pid>` girdisinin üçü de yok, unit `LoadState=not-found`.

Kanıt **cgroup üyeliğidir**, çalıştırılabilir adı değil: ada göre öldürmek meşgul bir makinede
yanlış süreçleri bulur ve sessiz bir makinede çevreleme hakkında hiçbir şey kanıtlamaz.

### 11.11 Transient unit yaşam döngüsü

`--collect` ile unit, başarısız olsa bile kendini kaldırır. Kalıcı `.service` dosyası yoktur,
`/etc/systemd/system` veya `/usr/lib/systemd/system` altına hiçbir şey yazılmaz,
`daemon-reload` gerekmez. Test hem başarılı hem başarısız koşudan sonra `LoadState=not-found`
doğrular.

### 11.12 Hard görev kabulü HÂLÂ REDDEDİLİYOR

`UnavailableHardExecutionPlane` üretimdeki tek `IHardExecutionPlane` uygulamasıdır ve
**değişmedi**.

`IHardExecutionPlane` "bu build **bütün bir görevi** sert izole ortamda çalıştırabilir mi" diye
sorar. `IHardProcessExecutor` "bu build ortamın içinde çevrelenmiş bir süreç çalıştırabilir mi"
diye. Bunlar **aynı yetenek değildir** ve ikincisinin var olması birincisini değiştirmez.

Bir Hub görevi ayrıca workspace hazırlar, context paketi kurar, git çalıştırır, doğrular, review
eder ve kanıt yazar — hepsi hâlâ host tarafında. Süreç çevrelenebiliyor diye kabul etmek, agent'ı
sınırın içinde çalıştırırken repository'yi, kimlik bilgilerini ve Hub'ın kendi koşu kaydını dışarıda
bırakmak olurdu; bu izolasyon değildir ve dürüst bir retten çok daha zor fark edilir.

Bir guard, `IsolationComposition`'ın kabul otoritesini bir executor'dan **inşa edemeyeceğini**
kaynak seviyesinde kilitler.

### 11.13 Hub çöküşü / yeniden başlatma

Transient unit, Windows başlatıcısından **daha uzun yaşayabilir** — `RuntimeMaxSec`'e kadar.
10C-2'nin bu konudaki dürüst durumu:

- **Kaçak koşu yoktur**: Linux tavanı her koşuyu kendiliğinden sınırlar (ölçüldü).
- **Anında toplama iddiası yoktur**: yeniden başlayan bir Hub, önceki koşuların unit adlarını
  bilmez. Unit adı `correlation id`'den *deterministik* olarak türer, dolayısıyla kimlik
  yeniden inşa **edilebilir** — ama bunun için korelasyonun kalıcı olması gerekir.
- Mevcut execution reconcile hâlâ otoritedir ve değişmedi.
- **Kalıcılık eklenmedi.** Gerekliliği kanıtlanmadan şema açılmaz; gerçek ihtiyaç, bir görev
  gerçekten sert izole çalıştığında (10C-3) ortaya çıkar. O noktada `Execution` satırının zaten
  taşıdığı kimlikten unit adı türetilebilir; ayrı bir "systemd unit" tablosu bugün gerekli
  değildir.

### 11.14 Kontrol düzlemi ağı

`control-plane.unreachable` **değişmedi** ve süreç çevrelemesi çalıştığı için `Pass` yapılmadı.
Semantiği aynen 10C-1'deki gibidir (§10.9): görülebilen bir dosya sistemi yolu varsa `Fail`,
yoksa `Unknown`. Ağ zorlaması 10C-3'e aittir.

### 11.15 Gerçek makine ölçümleri

Mevcut Ubuntu'da, `daemon` hesabı workload kimliği olarak, `/var/tmp` altında benzersiz ve
sonradan silinen bir dizinde:

| Ne | Sonuç |
|---|---|
| transient servis çalışıyor | evet, çıkış kodu 0 |
| workload uid | `daemon` (uid 1), root değil |
| çalışma dizini | tam olarak istenen |
| stdout / stderr | ayrı ayrı yakalandı |
| stdin + EOF | marker okundu, EOF görüldü |
| çıkış kodu 0 / 3 | aynen geldi |
| ortam izolasyonu | Windows PATH / sağlayıcı değişkeni / hesap adı **yok** |
| `TasksMax=3` | `SPAWNED=2`; kontrol koşusu `SPAWNED=10` |
| `MemoryMax=64MiB` | 192 MiB'e **ulaşamadı**; kontrol koşusu `REACHED=192` |
| host deadline | koşuyu bitirdi, cgroup boşaldı |
| terk edilmiş koşu | Linux tavanı bitirdi, `LoadState=not-found` |
| iptal | parent+child+grandchild üçü de gitti |
| transient unit | başarılı ve başarısız koşudan sonra `not-found` |
| Ubuntu readiness | **hâlâ NotReady** |

**Bu, Ubuntu'yu Hard-ready yapmaz.** Yalnızca systemd/cgroup mekaniklerinin mevcut olduğunu
kanıtlar. Dağıtım hâlâ host sürücülerini bağlıyor, hâlâ Windows interop kayıtlı, hâlâ izole bir
agent hesabı yok — 10C-1'in kanonik sonucu değişmedi.

### 11.16 Dokümantasyon köken notu (10C-1 ölçümleri)

İki farklı ölçüm belgede yan yana duruyor ve karıştırılmamalıdır:

| Ölçüm | Agent kimliği | Nerede |
|---|---|---|
| **Kanonik negatif** (`hub doctor isolation`) | `cafer` — makinenin varsayılan hesabı | §10.14 |
| Probe/adversarial testleri | `root` — her makinede aynı olsun diye | `WslReadinessProbeTests` |

Kanonik olan `cafer` ile olandır: §10.14'teki 11 başarısız kontrol o koşudan gelir ve
`agent.user.not-privileged` orada `adm, sudo` gerekçesiyle düşer. Test paketi `root` sorar çünkü
testin hangi makinede koştuğuna bağlı olmaması gerekir; orada aynı kontrol `root` grubu
gerekçesiyle düşer. İkisi de aynı şeyi kanıtlar, farklı hesaplarla.

### 11.17 Bu dilimde YAPILMAYANLAR

- Hard workspace / worktree yerleşimi (`10C-3`).
- Hard context girdi yerleşimi.
- Organizasyon başına evidence/CAS ayrımı.
- Sağlayıcı kimlik doğrulama ayrımı ve sağlayıcı runtime yönlendirmesi.
- Kontrol düzlemi ağ izolasyonu.
- Hard sınır içinde git / doğrulama / review.
- Orkestratör yönlendirmesi ve Hard görev kabulü.
- Şema değişikliği, migration.
- Provizyon: distro oluşturma/import, kullanıcı açma, `wsl.conf` düzenleme, mount değiştirme.
- Gerçek Kronos verisi (ADR-0017). Kronos **BLOCKED**.
