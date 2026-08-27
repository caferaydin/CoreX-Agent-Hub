# Faz 10 — Sertleştirme

Faz 10B-1: **yarışsız Windows süreç başlatma**.
Faz 10B-2: **Windows Job Object kaynak limitleri**.
Faz 10C-1: **sert izolasyon hazırlığı ve topoloji kapısı** (WSL'de görev çalıştırma YOK).
Faz 10C-2: **WSL2 sert süreç yürütme düzlemi** (systemd transient unit + cgroup çevrelemesi).
Faz 10C-3A: **sert workspace sınırı** (sabitlenmiş kaynak → Linux-native git worktree).

Bu belge Faz 10'un sertleştirme işini kaydeder. Şu an 10B-1, 10B-2, 10C-1, 10C-2 ve 10C-3A
uygulanmıştır; geri kalan dilimler (10C-3B ve sonrası) aşağıda ayrıca işaretlenmiştir ve
**uygulanmamıştır**.

**Tam Hard görev kabulü 10C-3A'dan sonra da REDDEDİLMEKTEDİR** — sebebi §11.12 ve §12.20'dedir.

> **Sert workspace vardır ≠ Sert görev kullanılabilir.** Kaynak artık sınırın içindedir; context,
> evidence, sağlayıcı kimlik doğrulaması ve ağ hâlâ host tarafındadır.

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
- Sert izole bir ortam içinde **bir süreç** (§11) ve **bir workspace** (§12) vardır; **bir Hub
  görevi** hâlâ yoktur. Context, evidence, sağlayıcı kimlik doğrulaması ve ağ host tarafındadır
  ve 10C-3B'ye aittir; orkestrasyon yönlendirmesi ve G-K kabulü 10C-4'e.
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

## 12. Sert workspace sınırı (Faz 10C-3A)

**Sert workspace bir ilkeldir; sert bir Hub görevi DEĞİLDİR.** Sabitlenmiş bir kaynak commit'i
artık ortamın içine, Linux-native depolamaya, agent'ın git kontrol düzlemine sahip olmadığı bir
biçimde yerleştirilebilir. Görevin geri kalanı — context, evidence, sağlayıcı kimlik doğrulaması,
ağ — hâlâ host tarafındadır ve tam Hard görev kabulü **hâlâ reddedilir** (§12.13).

### 12.1 Güven sınırı

```
Windows kaynak repository   GÜVENİLEN, agent HİÇ görmez
        │  (yalnızca okuma)
        ▼
git bundle (yalnızca commit edilmiş nesneler)
        │  base64 · stdin · ProcessRunner · wsl.exe
        ▼
ServiceUser: /srv/.../repositories/<id>/git   kontrollü bare repo
        │  git worktree add (sabitlenmiş sha)
        ▼
AgentUser:   /srv/.../worktrees/<id>          tek gördüğü şey
```

Agent kaynak repository'yi görmez, host dosya sistemini görmez, başka bir organizasyonun
workspace'ini görmez ve git kontrol verisini göremez.

### 12.2 Mevcut sınır yeniden kullanıldı, ikinci bir tane yaratılmadı

Hard adapter, Windows workspace'in zaten cevapladığı **aynı** Application portunu
(`IExecutionWorkspace`) cevaplar. Orkestratör hangi tarafta olduğunu öğrenmez; bu satırın
üstünde hiçbir yerde `if (hard)` yoktur ve mevcut Windows uygulaması **değiştirilmedi** (guard).

Preflight de bilinçli olarak **mevcut olanıdır**. Hard'a özel ikinci bir inceleme, submodule /
filter / LFS / güvensiz yapılandırma hakkında ikinci bir politika olurdu — ve iki politikadan
zayıf olanı karar verir. Repository her zaman yargılandığı kurallarla, host üzerinde, tek bayt
transfer edilmeden önce yargılanır.

### 12.3 Ne geçer: yalnızca commit edilmiş nesneler

Transfer formatı **git bundle**'dır ve bu seçim ölçüldü:

- `git bundle create <dosya> <branch>` çalışır; çıplak bir sha ile **reddedilir**
  (`Refusing to create empty bundle`) — bu yüzden preflight'ın çözdüğü branch paketlenir.
- `git bundle list-heads` ile paketin **gerçekte ne taşıdığı** sorulur ve sabitlenmiş sha ile
  karşılaştırılır. Branch preflight'tan sonra kımıldadıysa transfer **reddedilir**, sessizce
  başka bir commit taşınmaz.

Bir bundle bir çalışma ağacı taşıyamaz. Dolayısıyla geliştiricinin commit edilmemiş
değişiklikleri, untracked dosyaları ve ignored dosyaları politika gereği dışarıda bırakılmaz —
formatın taşıdığı şey türünden değildirler. Bu, "dışlamaya çalışan bir kopyalama"dan daha güçlü
bir garantidir.

### 12.4 Host artefaktı ve bayt kanalı

Geçici bundle Hub'ın **kendi** veri dizinindedir, benzersiz adlıdır, hiçbir worktree'nin içinde
değildir ve başarılı da olsa başarısız da olsa **silinir**.

Baytlar `stdin` üzerinden geçer. Host yolu guest'e **verilmez**; `/mnt/c/temp.bundle`, SMB,
paylaşım, HTTP yoktur.

ProcessRunner'ın stdin kanalı metindir, bundle ise ikilidir. Bu yüzden bayt akışı base64
kodlanır ve guest tarafında tek bir sabit araçla çözülür:

```
/usr/bin/openssl base64 -d -A -out <staging>/<id>.bundle      [stdin = base64]
```

`-A` (tek satır) **zorunludur**: onsuz aynı çağrı 0 bayt yazdı (ölçüldü). `base64 -d` yalnızca
stdout'a yazdığı ve kabuk olmadan yönlendirme yapılamadığı için openssl seçildi; 10B-1/10B-2'de
dondurulan ProcessRunner sözleşmesine ikili stdin eklemek yerine metin kanalı korundu.

Paket, **iki tarafta da** doğrulanır: host paketlediğini, ortam aldığını sorar. Tek kontrol
transferin kendisini incelenmemiş bırakırdı.

### 12.5 Linux-native yerleşim

```
<ExecutionRoot>/
  control/            Hub'ın git yapılandırması + BOŞ hooks dizini
    hooks/
    empty.gitconfig
  stage/              transfer artefaktları, yalnızca servis, silinir
  repositories/<repositoryId:32hex>/git      kontrollü bare repo
  worktrees/<executionIdentity:32hex>        tek kullanımlık agent worktree
```

Her yol **güvenilen bir Hub kimliğinden** türer. Organizasyon adı, repository görünen adı, task
hedefi hiçbir zaman bir yol segmenti olmaz — bir yol segmenti başkasının seçtiği bir şeyse,
seçeceği ilk şey içinde bulunması gereken kökten çıkış yoludur.

`/mnt`, `/media`, `/proc`, `/sys`, `/dev` altındaki bir kök **reddedilir**. WSL tabanlı bir süreç
kullandığı için bir Windows worktree'sine "Hard" denmez.

### 12.6 Sahiplik modeli — sınırın gerçekten yaşadığı yer

Gerçek makinede ölçülen izinler:

```
drwxrwx--T  root daemon   <worktree>          sticky + grup yazılabilir
-rw-r-----  root daemon   <worktree>/.git     agent okur, yazamaz
-rw-r--r--  daemon daemon <worktree>/*.txt    agent'ın kaynak dosyaları
drwx------  root root     <bare>/             agent listeleyemez bile
```

Kritik olan **sticky bit**'tir. Worktree kökü agent tarafından yazılabilir olmalıdır, yoksa yeni
dosya oluşturamaz; yazılabilir bir dizin ise normalde içindeki **her şeyin** silinmesine izin
verir — yazamadığı bir dosya dâhil. Sticky bit bunları ayırır: bir girdi yalnızca sahibi
tarafından kaldırılabilir. Agent kendi dosyalarını oluşturur ve siler, servisinkilere dokunamaz.

Dünya değil **grup** izni: aynı makinedeki ikinci bir organizasyonun hesabı aynı değişiklikle
içeri davet edilmez.

Ölçülen agent denemeleri:

| Deneme | Sonuç |
|---|---|
| `tracked.txt` düzenle | ✅ izinli |
| yeni dosya oluştur | ✅ izinli |
| `.git` üzerine yaz | ❌ Permission denied |
| `.git` sil | ❌ Operation not permitted (sticky) |
| `.git` taşı | ❌ Operation not permitted |
| bare repo listele | ❌ Permission denied |
| bare `config` yaz | ❌ Permission denied |
| `refs/heads/...` yaz | ❌ Permission denied |
| `git status` | ❌ `fatal: not a git repository` |
| `git commit` | ❌ `fatal: not a git repository` |

Agent'ın commit edememesi bir **araç kısıtı değil**, bir izin sonucudur: git gitdir'e ihtiyaç
duyar, gitdir onun değildir. Çalışma zamanı allowlist'i aşılabilir; izin aşılamaz.

Agent hiçbir ayrıcalıklı gruba eklenmedi.

### 12.7 İlk checkout güvenliği

Windows workspace ölçmüştü: `git worktree add`'in kendisi `post-checkout` çalıştırır, dolayısıyla
kendi `core.hooksPath`'ini yapılandıran bir repository **ilk checkout sırasında** kod çalıştırma
hakkı kazanır. "Önce checkout, sonra sertleştir" tam olarak engellemesi gereken şeyden sonra
sertleştirmektir.

Hard tarafta aynı garanti korunur. Hub'ın yapılandırması `GIT_CONFIG_COUNT`/`KEY`/`VALUE` ile,
komut **çalışmadan önce** yürürlüktedir ve checkout yapabilen ilk komut olan `worktree add`'e
uygulanır:

```
/usr/bin/env -i
  PATH=/usr/bin:/bin  HOME=<control>
  GIT_CONFIG_NOSYSTEM=1  GIT_CONFIG_GLOBAL=<control>/empty.gitconfig
  GIT_TERMINAL_PROMPT=0  GIT_OPTIONAL_LOCKS=0
  GIT_CONFIG_COUNT=6
  core.hooksPath=<control>/hooks   gc.auto=0   core.quotePath=false
  core.fsmonitor=false   commit.gpgSign=false   tag.gpgSign=false
  /usr/bin/git ...
```

Anahtar kümesi Windows tarafıyla **paylaşılır** (`GitConfigKeys`), kopyalanmaz: iki edebi kopya
yalnızca biri düzenlenene kadar eşit kalır.

Ortam değişkenleri host'tan gelmez — WSL yalnızca `WSLENV`'in adlandırdığını iletir. `env -i`
ile ortam **sıfırdan** kurulur, böylece servis hesabının kendi login ortamı (credential helper,
alias, proxy) hiçbir şey söyleyemez.

`env` bir kabuk değildir: değişken atar ve exec eder; her argüman kendi argv girdisi olarak
kalır.

### 12.8 Hedef repository yapılandırması Hub'ındır

Hedef, artefaktan **clone edilmez**; `git init --bare` ile Hub tarafından oluşturulur ve bundle
ondan sonra fetch edilir. Ölçülen sonuç yapılandırması:

```
core.repositoryformatversion=0
core.filemode=true
core.bare=true
```

Kaynak repository'nin `.git/config`'i transfer edilmez ve danışılmaz. Commit edilmiş içerik
güvenilmezdir; geliştirici makinesinin yerel git yapılandırması ise Hub'ın benimsemesi için hiçbir
sebep olmayan bir şeydir — credential helper, alias, filter driver hepsi onunla gelirdi.

### 12.9 Ağ yok

Hedef repository'de **remote yoktur** (ölçüldü: `git remote` boş). Kaynağın origin'i ve kimlik
bilgileri kopyalanmaz. Bu dilimde `fetch`/`pull`/`push` yoktur; kaynak, Hub'ın zaten sahip olduğu
güvenilen kontrol düzlemi verisidir. Bir guard bu fiilleri kaynak seviyesinde yasaklar.

### 12.10 Sabitlenmiş worktree ve branch

Worktree **tam olarak** `BaseCommitSha`'dan oluşturulur ve sonrasında `HEAD == Base` doğrulanır.
Preflight'tan sonra kaynak varsayılan branch'inin kayması worktree'yi etkilemez.

Branch adı mevcut modelle aynıdır: `agent/<taskKey>/<attempt>`. Branch'i **servis kimliği**
oluşturur; agent'a branch oluşturma yetkisi verilmez ve korumalı bir branch doğrudan checkout
edilmez.

### 12.11 Yakalama ve beklenmeyen commit

Capture, Windows tarafındaki aynı olguları ortamın **içinde**, güvenilen kontrol sınırından
toplar: `HEAD`, `status --porcelain=v2`, `Base..HEAD` commit grafiği, `Base`'e göre diff, değişen
yollar. `\\wsl$` veya `/mnt` üzerinden Windows'tan okuma **otorite değildir**.

`--no-ext-diff` ve `--no-textconv` her kanıt komutundadır: repository'nin yapılandırdığı bir
external diff ya da textconv sürücüsü, tam da Hub onun hakkında kanıt toplarken çalışırdı.

K1 mekanik olarak anlamlıdır: agent adımından sonra `HEAD` hâlâ `BaseCommitSha` olmalıdır. Aksi
hâlde beklenmeyen agent commit'i tespit edilir — ve **onarılmaz**. Reset/amend/rebase yoktur;
onarmak, olduğunun kanıtını yok etmek olurdu.

İki gerçek ayrı ayrı test edilir:

- **A.** Sıradan agent principal'ı kalıcı bir git commit'i oluşturamaz, çünkü OS/gitdir sınırı
  reddeder (yukarıdaki tablo).
- **B.** Güvenilen test kurulumu kontrollü repoya bir commit **enjekte ederse**, capture
  `HEAD != Base` olduğunu görür ve tek beklenmeyen sha'yı raporlar.

A genellikle B'yi imkânsız kılar; bu, E1'i zayıflatmak için değil güçlendirmek için bir sebeptir.

### 12.12 Result commit

Yalnızca güvenilen servis workspace otoritesi oluşturur. Agent'ın git izni gerekmez — zaten
yoktur. Kimlik mevcut kabul edilmiş olanın **aynısıdır** ve paylaşılan sabitten gelir:

```
CoreX Agent Hub <agent@corex.local>
```

Gerçek ölçüm: parent tam olarak `BaseCommitSha`, author/committer dördü de Hub, değişen dosya
tam olarak beklenen, `Base..HEAD` sayısı **1**, branch `agent/HARD-2/3`.

Amend/rebase/reset/force **yoktur** ve bir guard bunu kilitler.

Workspace kendi çıktısını **onaylamaz**: K0/K1/K2/K3 değerlendirmesi workspace koduna
taşınmadı. Result commit bir ilkeldir; ne zaman çağrılacağına orkestrasyon otoritesi karar verir.

### 12.13 Kaynak repository dokunulmaz

Gerçek ölçüm — hazırlıktan önce ve sonra aynı: `HEAD`, `show-ref`, `status --porcelain`,
`config --list --local`. Geliştiricinin kirli düzenlemesi ve untracked dosyası hâlâ yerinde.

Transfer okur, yazmaz. Geçici bir ref, geçici bir branch veya bir stash her biri artefaktı
üretmenin kolay yolu olurdu ve her biri birinin repository'sini değiştirirdi.

### 12.14 Linux yol otoritesi ve symlink kaçışı

Windows `FileSystemGuard` semantiği Linux'ta varsayılamaz. Hard workspace, Linux-native bir yol
otoritesi kullanır: mutlak yol zorunlu · segment bazlı kök içerme · `..` reddi · host mount kökü
reddi.

Symlink kaçışı **makineye sorularak** tespit edilir: yol önce `realpath -m` ile fiziksel olarak
çözülür, içerme kararı çözülmüş hâl üzerinde verilir. Gerçek test: agent worktree içinde `/etc`'ye
symlink oluşturur (izinli), `<worktree>/escape/passwd` **reddedilir**, `<worktree>/tracked.txt`
kabul edilir.

Agent symlink oluşturabilir; Hub onlara güvenmez.

### 12.15 Release ve orphan

Release **yalnızca** denemenin worktree'sini kaldırır. Kontrollü repository kalır: result
commit'in üzerinde olduğu branch'i tutar ve bir kabul kararı tam olarak o provenance hakkındadır.

Release başarısız olursa **raporlanır, zorlanmaz**. `--force` yoktur, `rm -rf` yoktur. Gerçek
test: worktree yönetim verisi silinir, release başarısız olur, worktree **hâlâ yerindedir** ve
sonuç orphan olarak bildirilir. "Daha sert kaldır" kanıtın kaybolma biçimidir.

D6 semantiği korunur: `ResultCommitSha == null` iken otomatik yıkıcı temizlik hâlâ yasaktır.

### 12.16 Reconciliation — sonraki ihtiyaç

Kimlik, sonraki bir Hub sürecinin keşfetmesine yetecek kadar deterministiktir: bare repo
`repositoryId`'den, worktree ise execution kimliğinden türer. Tam reconciler **yapılmadı**.

Gerçek ihtiyaç 10C-3B/10C-4'te doğar: kayıtlı Hard worktree'leri, orphan'ları ve kayıtsız
dizinleri ayırt edebilmek için worktree kimliğinin `Execution` satırında taşınması gerekir —
`WorktreePath` bunu zaten taşıyabilir (§12.17). Kayıtsız bir dizin **hiçbir zaman** otomatik
silinmez.

### 12.17 Kalıcılık semantiği

**Şema değişikliği yok, migration yok.**

Mevcut `Execution` alanları anlamlarını koruyarak kullanılır: `BranchName`, `BaseCommitSha` ve
`WorktreePath`. `WorktreePath` bir `string`'dir ve port sözleşmesinde (`PreparedExecutionWorkspace`)
Windows'a özgü hiçbir varsayım yoktur — bir Linux yolunu **dürüstçe** taşıyabilir. Sahte bir
Windows yolu yazılmaz.

API/UI, fiziksel worktree yolunu **açığa çıkarmaz**: mevcut güvenli projeksiyonlar yolsuzdur, yeni
endpoint yoktur, workspace tarayıcısı yoktur.

### 12.18 Komut yüzeyi kapalıdır

Hard workspace'in adlandırabildiği guest çalıştırılabilirleri kapalı bir kümedir:

```
git · env · openssl · realpath · mkdir · chown · chmod · rm · stat · touch
```

Uygulama veya sağlayıcı kodundan erişilebilen genel bir `RunAnyLinuxCommand` **yoktur**. Kabuk
yoktur, `bash -c` yoktur, `Process.Start` yoktur; her şey 10B-1/10B-2'de dondurulan
ProcessRunner sınırından ve 10C-2 WSL geçidinden geçer.

Kısa güvenilen kontrol komutlarının süre sınırları açıkça tanımlıdır: kurulum/transfer 2 dakika,
git 10 dakika.

### 12.19 Servis kimliği hakkında dürüst not

Üretim sözleşmesi `HardIsolationProfile.ServiceUser`'ı **gerektirir** ve uygulamada root'a özel
hiçbir dal yoktur.

Mevcut Ubuntu'da adanmış bir Hub servis hesabı yoktur. Bu yüzden **testler** profilin service
user'ını `root` olarak yapılandırır: sahiplik ayrımını (kontrollü repo agent'ın olmayan birine
ait) operatörün dağıtımında kalıcı bir hesap açmadan modeller. Sahiplik kurulumu (`chown`) zaten
root gerektirir ve bu, brief'in açıkça izin verdiği tek ayrıcalıklı adımdır.

### 12.20 Hard görev kabulü HÂLÂ REDDEDİLİYOR

`UnavailableHardExecutionPlane` üretimdeki tek `IHardExecutionPlane` uygulamasıdır ve
**değişmedi**. `IsolationComposition.CreateWorkspace` bilinçli olarak kabul otoritesinden
erişilemez ve bir guard, kabulün bir workspace veya executor'dan inşa edilemeyeceğini kilitler.

Kaynak artık sınırın içinde diye işin içinde olduğu sonucuna varmak kolay olurdu. Context,
evidence, sağlayıcı kimlik doğrulaması ve ağ hâlâ host tarafındadır.

### 12.21 Kontrol düzlemi ağı

`control-plane.unreachable` **değişmedi**: dosya sistemi yolu görünürse `Fail`, aksi hâlde
`Unknown`; `Pass` yoktur. Workspace mekaniği çalıştığı için hiçbir Hard ortam `Ready` olamaz.

### 12.22 Bu dilimde YAPILMAYANLAR

- Organizasyon başına Evidence/CAS ayrımı (`10C-3B`). G-K4 **iddia edilmiyor**.
- Sağlayıcı kimlik doğrulama ayrımı ve sağlayıcı runtime yönlendirmesi (`10C-3B`).
- Kontrol düzlemi ağ izolasyonu (`10C-3B`).
- Hard context girdi yerleşimi.
- Hard sınır içinde doğrulama ve review.
- Orkestratör yönlendirmesi, tam Hard görev kabulü, G-K kabulü (`10C-4`).
- Tam reconciler.
- Şema değişikliği, migration, yeni API/UI yüzeyi.
- Provizyon: distro/kullanıcı/grup oluşturma, `wsl.conf`, mount değişikliği.
- Gerçek Kronos verisi (ADR-0017). Kronos **BLOCKED**.

## 13. Sert kanıt ve sağlayıcı kimlik sınırı (Faz 10C-3B1)

Bu bölüm 10C-3B1'in ne kurduğunu ve neyi hâlâ kurmadığını anlatır. Bir cümle önden: **Evidence /
Auth sınır ilkelleri var ≠ Tam Hard Görev kullanılabilir.** Bu dilim ne ağ izolasyonu getirir ne de
Hard görev kabulünü açar.

### 13.1 Organizasyon başına fiziksel CAS

Sert bir organizasyonun kanıtı kendi ortamının içinde, kendi köküyle yaşar. İki organizasyon aynı
baytları saklarsa iki ayrı dosya oluşur ve hiçbiri diğerinin sakladığını gözlemleyemez.

Ortak bir içerik adresli depo bunu yapamaz: "bu baytlar zaten var mı?" sorusunu soran herkese
cevaplar ve o cevap başkasının içeriği hakkındadır. Bu yüzden Hard organizasyonlar mevcut ortak
blob CAS'ını **paylaşmaz**.

Yerleşim mevcut `EvidenceBlobStore` düzeniyle aynıdır — `blobs/<sha[0:2]>/<sha>` ve `_pending` —
ama kök, organizasyonun kendi profilinden gelir ve başka hiçbir yerden gelmez. Sabit yok, varsayılan
yok, çağıranın verdiği yol yok.

### 13.2 Sahiplik: servis kimliği yazar, agent hiçbir şey göremez

Kanıt ağacının sahibi organizasyonun servis kimliğidir, modu `0700`'dür. Agent:

- dizini listeleyemez,
- hash'ini bildiği bir blob'u okuyamaz,
- blob yazamaz,
- **kendi çalıştırmasının** kanıtına bile erişemez.

Sonuncusu bir katılık gösterisi değil: kanıt, insanın karar verdiği kayıttır. Kendi çalıştırmasının
kaydını okuyabilen ya da değiştirebilen bir taraf, kaydı hiçbir şey ifade etmeyen taraftır.

Bu, tahmin edilemeyen isimlerle değil sahiplikle sağlanır. **Hash bir sır değildir.** Dosya adının
bilinmesi erişim vermemelidir; burada vermiyor da — ölçülen ret gerçek bir işletim sistemi reddidir.

Tek ayrıcalıklı adım kurulumdur: kök dizin `root` olarak oluşturulur, sonra servis kimliğine
devredilir ve kapatılır. Saklanan hiçbir blob bu adımdan geçmez.

Organizasyonlar arasında paylaşılan bir Linux grubu yoktur ve bir guard `groupadd` / `usermod` /
`chgrp` kullanımını kilitler: tek bir servis grubu düzenli yönetim gibi görünür ve her
organizasyonun kanıtını her diğerine okutur — organizasyon başına deponun engellemek için var
olduğu özelliğin tam kendisi.

### 13.3 Bayt taşıma ve atomik yayımlama

Windows Hub, bunların hiçbirini yapmak için ortamın dosya sistemine dokunmaz. Yetki yolunda
`\\wsl$` yolu yoktur, bağlı sürücü yoktur, `File.ReadAllBytes` yoktur.

Baytlar kontrol kanalında base64 olarak taşınır. Bu tercih, dondurulmuş `ProcessRunner`
sözleşmesine ikili bir stdin eklememek içindir (ADR-0020).

Yayımlama tek bir yerde olur:

1. Hub gönderdiği baytların SHA-256'sını ölçer.
2. Baytlar `_pending` altındaki geçici bir isme yazılır — **asla nihai isme**.
3. Ortam, aldığı dosyayı kendi tarafında yeniden hash'ler.
4. İki ölçüm uyuşmazsa hiçbir şey yayımlanmaz.
5. Uyuşurlarsa `mv -n` ile içerik adresli isim oluşur.

Böylece bir blob'un adı her zaman içindeki baytları tarif eder. Yarıda kalan bir aktarım en kötü
ihtimalle yayımlanmamış bir dosya bırakır, asla bir blob bırakmaz: o ismi yalnızca yeniden
adlandırma yaratır.

Çağıranın verdiği SHA'ya tek başına güvenilmez; okuma da geri döndüğü baytları kendi adına karşı
doğrular. İkili kanıt bozulmaz: hostile bir yük (gömülü `0x00`, `0xFF`, CRLF, geçersiz UTF-8)
baytı baytına geri gelir.

**Depolama tavanı yoktur.** Sert depo, mantıksal `EvidenceBlobStore`'un kabul ettiği bir eseri
yalnızca arka uç Hard olduğu için reddedemez — ederse izolasyon arka ucu neyin kanıt sayıldığına
karar vermiş olur ve aynı çalıştırma, hangi tarafta koştuğuna göre farklı bir kayıt tutar. Üstelik
sessizce: port "saklanacak bir şey yok" için `null` döner ve orkestratör bunu hiç var olmamış bir
eser olarak okur.

Sınırlanan şey kanaldır, eser değil. `TransferChunkBytes` = 4 MiB ham bayt bir **taşıma
penceresidir**: baytlar pencereler hâlinde geçer ve yayımlanmamış dosyanın sonuna eklenir
(`dd oflag=append conv=notrunc`), böylece eser bütün saklanırken maliyet tek pencere kadar kalır.
Okuma da aynı şekilde pencerelenir (`dd skip=<i> count=1` + `base64 -w 0`), yoksa tek bir yakalanan
akış eserin tamamını tutardı.

### 13.3.1 Base64 muhasebesi

Base64 üç baytı dört karakter yapar. Bu yüzden iki sayı ayrı tutulur:

- `TransferChunkBytes` — **ham** bayt penceresi (4 MiB),
- `EncodedChunkCharacters` — aynı pencerenin **kodlanmış** hâli (`(chunk + 2) / 3 * 4`).

Yakalama sınırı kodlanmış sayıdan türetilir; **ham bir eser hiçbir zaman kodlama sonrası bir sınıra
karşı yargılanmaz.** Aksi hâlde meşru bir eser, kendi temsilinin boyutu yüzünden reddedilirdi.

### 13.4 Redaksiyon: tek katalog

Sert kanıt, Windows tarafının kullandığı **aynı** `EvidenceRedactor` kataloğundan geçer ve
redaksiyon hash'lemeden **önce** çalışır. Hard organizasyonlar için ikinci bir desen listesi, sırrın
ne olduğuna dair ikinci bir tanım olurdu ve zayıf olan, disk'e yazılanı belirlerdi.

### 13.5 SQLite metadata güvenilen Windows tarafında kalır

Blob'lar ortamın içindedir; kanıt kaydı (`EvidenceArtifact`) Hub'ın veritabanında kalır.
`EvidenceArtifact` zaten `organizationId` taşıdığı için depo yönlendirmesi belirsiz değildir —
**şema değişikliği ve migration gerekmedi**.

SQLite WSL'e taşınmadı: kontrol düzlemi güvenilen taraftır.

### 13.6 Sağlayıcı kimlik doğrulama güven modeli

Üç taraf vardır ve yalnızca biri izinlidir:

| Taraf | Erişim | Neden |
|---|---|---|
| Agent'ın kendi oturumu | **Okur ve günceller** | Sağlayıcı süreci o kimlik olarak çalışır; kendi durumunu yenileyebilmelidir. |
| Başka organizasyonun oturumu | **Reddedilir** | İki gerçek uid, dört gerçek ret (oku/listele/yaz/yerleştir), iki yönde de. |
| Host geliştiricinin oturumu | **Ulaşılamaz olmalı** | Aksi hâlde sınır tutar ama içermek için kurulduğu şey zaten içeridedir. |

Organizasyonun servis kimliğinin de bir oturumda işi yoktur. Auth, agent'a ait gizli durumdur;
workspace ve kanıtı yöneten servis aynı zamanda kimlik bilgilerini de okuyabilseydi, tek bir kimlik
hem kaydı hem onu üretme aracını elinde tutardı.

`HardIsolationProfile` sekiz alan taşır ve hiçbiri sağlayıcıya ait değildir: `claudeHome`,
`codexHome`, `anthropicToken`, `openaiToken` **yoktur ve eklenmeyecektir**. İzolasyon kimlikleri ve
sınırları verir; bir sağlayıcının kendi durumunu bunların içinde nerede tuttuğu o adaptörün işidir.

Agent'ın ev dizini `getent passwd` alan 5'ten çözülür ve **yargılanır**: var olmalı, dizin olmalı,
agent'a ait olmalı, Linux-native olmalı. `/home/<agentUser>` string birleştirmesiyle **asla
üretilmez** — üretilseydi doğru görünen, var olmayan ve izolasyon diye raporlanacak bir yol çıkardı.
Operatörün dağıtımındaki stand-in hesaplarda cevap bir reddir (`daemon`'ın evi `/usr/sbin` ve
root'a aittir) ve bu doğru cevaptır: evi başkasına ait olan bir agent orada oturum tutamaz, tutsaydı
da sahibi onu okuyabilirdi.

Host'un oturumunun ortama taşınmasına ait tek bir kod yolu yoktur. Readiness'ın host profilini
adlandırdığı tek yer bir sürücü harfi türetmek içindir; içerik okunmaz, hiçbir oturum yolu rapora
girmez. `HOME`'un atandığı tek yer, git'in global yapılandırma okumasını servis kontrol köküne
sabitler — bir oturumu içeri taşımanın tersi.

**Kimlik bilgisi içeriği hiçbir yerde okunmaz, yazdırılmaz veya raporlanmaz.**

Bu makinede host sürücüleri bağlı olduğu için host oturumu **ulaşılabilirdir** ve
`host.provider-auth.unreachable` `Fail` raporlar. Test, ulaşılmazlığı değil **ikisinin
uyuştuğunu** doğrular: agent'ın host dosya sistemini okuyabildiği hâlde readiness'ın o kontrole
`Pass` dediği bir yapı, iki hatanın tek başına olduğundan çok daha kötüdür.

### 13.7 Sentetik doğrulama — ve ne kanıtlamadığı

Testler dört gerçek Linux kimliği kullanır (`bin`, `daemon`, `sys`, `games`) ve tek kullanımlık bir
`/var/tmp` ağacında çalışır. Bunlar gerçek uid'lerdir, dolayısıyla retler gerçek işletim sistemi
retleridir — bir modelleme değil. Hesap, grup, mount, `wsl.conf` veya sağlayıcı ev dizini
**değiştirilmez**; ağaç sonunda silinir.

Oturum işaretçileri sentetiktir. **Gerçek bir Claude/Codex sırrı kullanılmadı**, hiçbir sağlayıcı
çalıştırılmadı, hiçbir oturum açılmadı: soru bir işletim sistemi sorusudur ve onu gerçek bir kimlik
bilgisiyle sormak, bir test uğruna gerçek bir kimlik bilgisini bir yere koymak olurdu.

**Kanıtlanmayan:** organizasyonlar arası **distro** ayrımı. Her iki organizasyon da tek bir
dağıtımın içindedir. Kurulan şey depolama ve sahiplik modelidir — ayrı ortamların anlam taşıması
için önce doğru olması gereken kısım. Gerçek çoklu-distro ayrımı `wsl --import` gerektirirdi; brief
bunu önce durup rapor etmeden yasakladığı için **yapılmadı**. Dolayısıyla:

> **G-K3 / G-K4 AÇIK kalır.** Organizasyon başına fiziksel CAS ayrımı ve çapraz-kimlik reddi tek
> distro içinde ölçülmüştür; çapraz-distro reddi ölçülmemiştir.

Eşzamanlılık dosya sistemi üzerinden ölçülür, `mv -n`'in atomikliğine güvenilerek değil: aynı
içeriği yazan sekiz eşzamanlı yazar sonrasında shard dizininde **tam bir dosya** bulunur, adı
hash'tir, misafir tarafı `sha256sum` o adı doğrular ve `_pending` **boştur**. Kontrol olarak dört
farklı içerik eşzamanlı yazıldığında dört ayrı blob oluşur — fixture'ın her şeyi sırayla
çalıştırmadığının kanıtı. Aktarımın ortasında iptal edilen bir yazar, tamamlayan yazarın
yayımlanmış blob'una dokunamaz; temizliği yalnızca kendi yayımlanmamış dosyasını kaldırır.

Mantıksal ve Sert depoların **tek bir anlam otoritesi** olduğu, eski 16 MiB tavanının üstünde bir
eserle doğrulanır: iki taraf da kabul eder, aynı hash, aynı boyut, aynı redaksiyon sayısı; ve iki
taraf da boş kanıtı aynı kuralla reddeder.

Gerçek ortamda kanal yalan söyleyemez, bu yüzden hash uyuşmazlığı senaryosu betiklenmiş bir ağ
geçidiyle ayrıca test edilir. Çalışan bir boru her zaman gönderileni teslim eder ve o hâlde
karşılaştırma hiçbir şeyin sebebi olmaz — tek bir test fark etmeden kaldırılabilecek bir korumanın
tam da şekli budur.

### 13.8 Ağ bu dilimde YOK

`nftables`, `iptables`, `netns`, firewall, proxy: **hiçbiri yok** ve bir guard bunu kilitler.
Yarım kurulmuş bir ağ sınırı — var olan, uygulanmış görünen ve hiç ölçülmemiş bir kural — bu
dilimin en kötü çıktısı olurdu.

`control-plane.unreachable` **değişmedi**: dosya sistemi yolu görünürse `Fail`, aksi hâlde
`Unknown`. `Pass` yoktur ve olamaz.

### 13.9 Hard görev kabulü HÂLÂ REDDEDİLİYOR

`UnavailableHardExecutionPlane` üretimdeki tek `IHardExecutionPlane` uygulamasıdır ve
**değişmedi**. `IsolationComposition.CreateEvidence` ve `CreateAgentHome` bilinçli olarak kabul
otoritesinden erişilemez.

Çalışan bir kanıt deposu bu ortamı Hard-ready yapmaz; kanıt saklamak `control-plane.unreachable`'ı
`Pass` yapmaz. İkisi de testle sabittir.

### 13.10 10C-3B2'ye devir — ağ sınırı

Bir sonraki dilimin cevaplaması gerekenler:

- Hard agent, Hub'ın API'sine loopback yönlendirme, host ağ geçidi veya mirrored networking
  üzerinden ulaşabiliyor mu?
- Ulaşamadığı **ölçülebilir** mi? (Bir yapılandırma varsayımı ölçüm kılığına giremez.)
- `control-plane.unreachable` hangi kanıtla `Unknown`'dan çıkabilir?
- Sağlayıcı egress'i hangi yolla açık kalır, hangi yolla kapanır?

Bunlar cevaplanana kadar hiçbir Hard ortam `Ready` olamaz.

### 13.11 Bu dilimde YAPILMAYANLAR

- Ağ izolasyonu (`10C-3B2`).
- Tam Hard görev kabulü ve orkestratör yönlendirmesi (`10C-4`).
- Evidence GC / saklama politikası.
- Sağlayıcı runtime'ının Hard sınır içinde çalıştırılması; Claude/Codex **çalıştırılmadı**.
- Hard context girdi yerleşimi, sınır içi doğrulama ve review.
- Şema değişikliği, migration, yeni API/UI yüzeyi.
- Provizyon: distro/kullanıcı/grup oluşturma, `wsl.conf`, mount değişikliği.
- Gerçek Kronos verisi (ADR-0017). Kronos **BLOCKED**.

## 14. Sert ağ sınırı (Faz 10C-3B2)

Bir cümle önden: **ağ politikası ve hazırlık otoritesi var; gerçek zorlama ölçülmedi.** Bu bölüm
neyin kurulduğunu, neyin ölçüldüğünü ve neyin hâlâ kanıtlanmadığını ayırır.

### 14.1 Ölçülen topoloji (10C-3B2A)

Bu makinede WSL2 **NAT** modundadır — bir yapılandırma anahtarının yokluğundan değil, çalışma
zamanı gerçeğinden:

| Gerçek | Değer |
|---|---|
| Guest `eth0` | `192.168.66.185/20` |
| Default route | `via 192.168.64.1` |
| Windows adaptörü | `vEthernet (WSL (Hyper-V firewall))` = `192.168.64.1/20` |
| Guest `lo` ek adres | `10.255.255.254/32` (DNS tüneli uç noktası) |
| `/etc/resolv.conf` | symlink → `/mnt/wsl/resolv.conf`, `nameserver 10.255.255.254` |
| `.wslconfig` ağ anahtarı | yalnızca `localhostForwarding=true` |
| Hyper-V firewall (WSL VMCreatorId `{40E0AC32-…}`) | `Enabled=True`, `DefaultInboundAction=Block`, `DefaultOutboundAction=Allow`, `LoopbackEnabled=True`, `AllowHostPolicyMerge=True` |
| Host proxy | **yok** (WinHTTP direct, `ProxyEnable=0`, `AutoConfigURL` yok) |
| Public internet | **erişilebilir** (`1.1.1.1:443` TCP) |

Guest `127.0.0.1` guest'in **kendi** loopback'idir; Windows loopback'ine ulaşmaz. Bu, NAT modunun
sonucudur, bir sınırın değil.

### 14.1.1 10C-3B2A raporuna düzeltme — host, alt ağ adresinde ERİŞİLEBİLİR

10C-3B2A keşfi, guest'ten `192.168.64.1`'e (Windows'un WSL alt ağındaki adresi) TCP bağlantısının
**timeout** verdiğini raporlamıştı. **Bu sonuç yanlıştı.** 10C-3B2B'de aynı yol tekrar ölçüldü:
Windows tarafında `192.168.64.1:<port>`'a bağlanan sentetik bir dinleyici, guest'ten ayrıcalıksız
agent kimliğiyle `nc` ile **art arda üç kez** başarıyla okundu ve marker geri geldi.

Doğru tablo:

| Yol | Ölçülen |
|---|---|
| Guest → Windows `127.0.0.1` | **ulaşılamaz** (guest'in kendi loopback'i) |
| Guest → `localhost` | **ulaşılamaz** (guest'e çözülüyor) |
| Guest → host'un alt ağ adresi (`192.168.64.1`), loopback'e bağlı servis | **ulaşılamaz** (servis orada dinlemiyor) |
| Guest → host'un alt ağ adresi, **o adrese bağlı** servis | **ERİŞİLEBİLİR** |

Sonuç: `DefaultInboundAction=Block` tek başına bu yolu kapatmıyor (148 mevcut Hyper-V kuralından
biri izin veriyor olabilir; `AllowHostPolicyMerge=True` de host kurallarının karışmasına izin
veriyor). Dolayısıyla:

> **Loopback-only bağlama, Hard için bir güvenlik sınırı DEĞİLDİR.** Hub bugün ulaşılamaz durumda,
> ama bu Hub'ın nereye bağlandığının bir sonucu; bir sınırın değil. Windows'ta o adrese bağlanan
> herhangi bir servis bugün guest'ten erişilebilir.

Bu düzeltme, guest tarafı nftables'ın **birincil** otorite seçilmesini doğruluyor.

### 14.2 Birincil otorite: kök network namespace'te root sahipli nftables — **§14.15 ile GEÇERSİZ**

Karar (10C-3B2 kilitli): birincil per-org ağ otoritesi, her Hard organizasyonun **kök network
namespace**'inde root'a ait nftables politikasıdır. Hyper-V/Windows firewall savunmada derinlik ve
topoloji kanıtı olarak kalır, tek sınır değildir.

Gerekçe ölçüldü: `New-NetFirewallHyperVRule` yalnızca **VMCreatorId** hedefleyebiliyor — yani tüm
WSL. Parametre listesinde dağıtım, VM veya arabirim yok. Hyper-V firewall Hard-A ile Hard-B'yi
**birbirinden ayıramaz**.

AgentUser'ın bu politikayı değiştiremediği ölçüldü:

| Deneme | Sonuç |
|---|---|
| `CapEff` / `CapPrm` / `CapAmb` | `0000000000000000` — CAP_NET_ADMIN yok, CAP_NET_RAW yok |
| `ip route add` (kök netns) | **Operation not permitted**, route tablosu değişmedi |
| `unshare -n` (düz) | **Operation not permitted** |
| `unshare --user --net --map-root-user` | **başarılı** — iç içe namespace kurulabiliyor |
| userns içinde `CapEff` | `000001ffffffffff` (tam) |
| userns içinden `ip route add` (**kök netns**) | **Operation not permitted**, route tablosu değişmedi |

Dar ve doğru değişmez şudur — geniş olanı yanlıştır:

> Kullanıcı namespace'leri **kapalı değildir**. Agent bir tane açıp içinde root olabilir. Elde
> edemediği şey, **kendisinin yaratmadığı** network namespace üzerinde yetkidir; o namespace bir üst
> user namespace'e aittir. Agent'ın kendisi için yarattığı bir netns erişimi yalnızca **daraltabilir**.

### 14.15 ÖLÇÜM DÜZELTMESİ — WSL dağıtımları ağ namespace'i PAYLAŞIR

§14.2'de seçilen **"dağıtım başına kök netns nftables"** mimarisi, tek-kullanımlık iki temiz
dağıtımla ölçüldü ve **GEÇERSİZ** çıktı. §14.2'nin otorite seçimi bu bölümle **hükümsüzdür**;
uygulama kodu (politika modeli, render, kimlik, hazırlık) değişmedi ve hiçbir yerde politika
uygulanmadı — ama peer izolasyonu için seçilen mekanizma WSL2'de mevcut değildir.

**Kurulum:** temiz Debian 13 şablonundan içe aktarılmış iki tek-kullanımlık dağıtım
(`corex-hard-net-a-*`, `corex-hard-net-b-*`), her birinde kendi sentetik `corex-agent-*` /
`corex-service-*` hesapları, `automount=false`, `interop=false`, `appendWindowsPath=false`,
systemd açık. Operatörün Ubuntu'suna ve docker-desktop'a dokunulmadı.

**Ölçülenler:**

| Soru | Sonuç |
|---|---|
| `readlink /proc/1/ns/net` — A / B / Ubuntu | **üçü de `net:[4026531833]`** |
| `hostname -I` — A / B / Ubuntu | **üçü de `192.168.66.185`** |
| `ip -4 route` — A / B / Ubuntu | **aynı** |
| A'da yaratılan **inert** `inet corex_probe_*` tablosu B'den görünüyor mu | **EVET** |
| A'dan silince B'den de kayboluyor mu | **EVET** |
| B'nin `127.0.0.1:<port>` dinleyicisi, A'nın ayrıcalıksız agent'ından | **ERİŞİLEBİLİR** |
| A'nın `127.0.0.1:<port>` dinleyicisi, B'nin ayrıcalıksız agent'ından | **ERİŞİLEBİLİR** |
| B'nin arabirim adresindeki dinleyici, A'dan | **ERİŞİLEBİLİR** |
| A'nın `127.0.0.1:<port>` dinleyicisi, **operatörün Ubuntu'sundan**, ayrıcalıksız | **ERİŞİLEBİLİR** |

**Sonuç:** bu makinedeki tüm WSL2 dağıtımları **tek bir kök network namespace'i, tek bir
loopback'i ve tek bir nftables ruleset'ini** paylaşır. Dağıtım sınırı bir dosya sistemi ve PID
sınırıdır; **ağ sınırı değildir.**

**Bunun iki bağımsız sonucu var:**

1. **Peer izolasyonu ifade edilemez.** A ve B aynı adrese sahip, aynı loopback'i paylaşıyor ve
   aynı ruleset'i görüyor. "A → B reddedildi" diye bir kural yazılamaz: A ile B arasında
   filtrelenecek bir ayrım yok. `peer.network.unreachable` bu mekanizmayla **hiçbir zaman**
   Pass olamaz.

2. **Politika izolasyonu da ifade edilemez.** Bir organizasyonun `inet corex_hard` tablosu, aynı
   kernel netns'inde yaşadığı için **her** dağıtım için geçerlidir — operatörün kendi Ubuntu'su
   dahil. Bir Hard organizasyon için yüklenen `policy drop` zincirleri geliştiricinin kendi
   ortamını da keserdi. Ayrıca iki organizasyon **aynı tabloyu** görür: birinin yüklediği politika,
   diğerinin `network.policy.enforced` kontrolünü de tatmin ederdi — yani kontrol, ölçtüğünü
   sandığı şeyi ölçmezdi.

Bu yüzden Hub politikası bu turda **yüklenmedi**: yüklenmesi, ölçülen paylaşımdan dolayı
operatörün çalışan ortamını filtreleyecekti.

**Kapatılamayan:** `NftablesRuleset.Canonical`'ın gerçek `nft list table` çıktısına karşı
doğrulanması. Doğrulama, üretim politikasının yüklenmesini gerektirir; yukarıdaki nedenle
yapılmadı. Açık kusur olarak kalır. Fail-closed olduğu için hiçbir şeyi erişilebilir yapmaz.

**Sonraki mimari seçenekleri** (bu dilimde karşılaştırılmadı, kullanıcı kararı):

- **A.** Organizasyon başına açık kök network namespace'leri (root'un yarattığı, agent'ın sahip
  olmadığı), kontrollü uplink/yönlendirme ile — iş yükü `systemd-run
  --property=NetworkNamespacePath=` ile oraya konur. Paylaşılan kernel netns problemi ortadan
  kalkar; maliyet: her açılışta kurulum ve doğrulama.
- **B.** Ayrı Windows yerel kullanıcısı + WSL sahipliği — dağıtımlar farklı Windows kullanıcılarına
  ait olur, dolayısıyla farklı WSL VM'lerinde çalışır. Ölçülmedi.
- **C.** Ayrı sanal makine / ayrı fiziksel makine. En güçlü ayrım, en yüksek maliyet.

Peer izolasyonu **zayıflatılmaz**. Seçenek belirlenene ve ölçülene kadar
`peer.network.unreachable` **Unknown**, `peer.filesystem.unreachable` **Fail**, tam Hard hazırlık
**false** ve `IHardExecutionPlane` **kullanılamaz** kalır.

### 14.3 Politika: efektler, mekanizma değil

`HardNetworkPolicy` sağlayıcı-nötr bir **efekt** modelidir: hangi yetenek açık, hangi adres sınıfı
kapalı, ve her kapatmanın **gerekçesi**. nftables, zincir, öncelik geçmez.

**İzin verilenler** (ad değil, yetenek):
- ad çözümleme: ölçülen resolver adresine, **yalnızca** UDP/TCP 53
- public HTTPS: TCP 443

**Reddedilenler** (her biri gerekçeli): `10/8`, `172.16/12`, `192.168/16` (RFC1918), `100.64/10`
(CGNAT), `169.254/16` (link-local), `127/8` (loopback arabirimi dışından), `0.0.0.0/8`, `224/4`
(multicast), `240/4` (reserved); IPv6 karşılıkları `::1/128`, `fc00::/7`, `fe80::/10`, `ff00::/8`,
`::/128`.

IPv6 bu makinede yönlenmese de kapatılır: yalnızca operatörün bugün kullandığı adres ailesi için
tutan bir sınır, bu değiştiği ilk anda **izin veren** yönde bozulur.

Politika **yapılandırılamaz**: tek bir donmuş örnek, public kurucu yok, set edilebilir özellik yok,
`Configuration` altında adı geçmiyor. Bir organizasyon kendi sınırını yapılandırmayla genişletemez.

**Sağlayıcı adı yok.** Hostname allowlist'i hem izolasyonu sağlayıcı-şekilli yapardı hem de
göründüğünden zayıf olurdu: filtrelenen ad, iş yükünün kendisinin verdiği addır.

### 14.4 Varsayılan duruş ve sıralama

Üç zincir, üçü de `policy drop`, üçü de açık `drop` ile biter: `output`, `input`, `forward`.
`forward` hiçbir şeye izin vermez — iş yükü yönlendirici değildir.

Sıralama tesadüf değil, testle sabit:

1. `oifname "lo" ip daddr 127.0.0.0/8 accept` — loopback **arabirim** olarak, adres olarak değil.
   Başka bir arabirimden çıkan 127.0.0.1 paketi, köprülenmiş bir host loopback'inin göründüğü şekildir
   ve aşağıda düşürülür.
2. Resolver izni — **özel adres reddinden önce**. Resolver'ın kendisi özel bir adrestir; reddin
   arkasına konsa kural yüklenir, doğru okunur ve **hiç eşleşmez**.
3. Tüm özel/reserved hedef reddi.
4. `tcp dport 443 accept` — **tüm reddin arkasında**. "Public" olmasını sağlayan budur; öne
   konsaydı tek bir port üzerinde sınırsız giden trafik olurdu ve incelemede aynı görünürdü.
5. `input` zincirinde `ct state established,related accept`, **kaynak reddinden sonra**. Reddedilen
   bir hedefe açılmış akış, "kurulmuş olmakla" kabul edilebilir hâle gelmez.

### 14.4.1 Gateway tuzağı

Default gateway özel bir adrestir ve public trafiğin **sonraki atlaması**dır. Kurallar **hedef**
adrese göre eşleşir: `192.168.0.0/16` hedefini reddetmek, `1.1.1.1`'e adreslenmiş paketin aynı
gateway üzerinden yönlenmesini engellemez. Ruleset gateway'i **hiçbir yerde ad olarak anmaz** ve
hiçbir kural giden public trafik için arabirim eşleşmesi yapmaz — bu, reddi rotaya bağlamış olurdu.

### 14.5 Hub'ın sahip olduğu tek tablo

`inet corex_hard`. Makinenin ruleset'i **flush edilmez**, okunmaz, yargılanmaz; operatörün kuralları
onundur. Yükleme, Hub'ın kendi tablosunun `delete`+`add`'idir (nft dosyayı atomik uygular), asla
`flush ruleset` değil.

Paylaşılan bir ruleset'te bunun otorite olmasının nedeni mekanizmanın kendisidir: nftables'ta bir
hook'a kayıtlı **her** base chain paketi görür; **accept** paketi diğer zincirlerden muaf tutmaz,
ama **drop** kesindir. Yani başka bir tablo, bu tablonun düşürdüğü trafiği kurtaramaz — önceliği ne
olursa olsun. Mekanizma değişirse bu özellik yeniden kurulmalıdır.

### 14.6 Politika kimliği

Hub "kurallar var mı" diye sormaz. Render **bayt düzeyinde deterministiktir** ve render edilen
metnin SHA-256'sı politikanın kimliğidir. Hazırlık, yüklü tabloyu geri okur, ikisini aynı biçime
normalize eder ve karşılaştırır.

Normalizasyon yalnızca gürültüyü atar: `# handle N`, boşluk, ve yüklü tablonun parçası olmayan
delete-preamble. **Tanımadığı hiçbir satırı atmaz** — bir adres, bir verdict veya bir kural
değişirse eşleşme bozulur. Testler bunu doğrudan kanıtlar: `10.0.0.0/8 drop` → `10.0.0.0/32 drop`,
`drop` → `accept`, `dport 443` → `dport 1-65535`, `policy drop` → `policy accept` ve **silinmiş bir
kural** — hepsi eşleşmiyor.

Çevre değişirse kimlik değişir: farklı resolver → farklı hash.

### 14.7 Hazırlık kontrolleri

Zorunlu kontrol kümesi 21'den **26**'ya çıktı. Yeni beşi:

| Kontrol | Pass koşulu |
|---|---|
| `network.mode.supported` | Ölçülen düzen NAT. Mirrored → **Fail**, sınıflandırılamayan → **Unknown** |
| `network.host-loopback.disabled` | Agent, Hub'ın dinleyicisine hiçbir yoldan ulaşamadı **ve** izinli kontrol çalıştı |
| `network.proxy.not-inherited` | Agent hiçbir `*_PROXY` değişkeni miras almadı |
| `network.policy.enforced` | Yüklü tablo, bu build'in render ettiği politikanın **aynısı** |
| `peer.network.unreachable` | İki yönlü peer reddi — **bu build'de her zaman Unknown** |

`control-plane.unreachable` yükseltildi: artık hem mount tablosundan (dosya sistemi yolu) hem de
ağ probe'undan cevap alır ve **daha katı olan kazanır**. Ağdan gelen bir Pass, bağlı bir host
biriminin `Fail`'ini iptal etmez; temiz bir mount tablosu da açık bir portu kapatmaz.

Ağ probe'u hiç yoksa beş kontrol de **Unknown**'dır — yokluk izin değildir.

### 14.8 Probe modeli: her ret bir kabulle eşlenir

Negatif cevaplar **agent'ın kendi hesabından**, hedef ortamın içinden, iş yükünün kullanacağı
mekanizmayla ölçülür. Hub, probe süresince kendi dinleyicilerini açar: biri `127.0.0.1`'de (Hub'ın
kendi bağladığı yer), biri ortamın **kendi alt ağındaki host adresinde**. Makinenin her arabirimine
bağlanmaz — bir teşhis, güvenli olup olmadığını öğrenmek için operatörün ağına port koymamalıdır.

Her negatif cevap bir **pozitif kontrol** ile eşlenir: politikanın izin verdiği resolver, izin
verdiği portta. Kontrol de başarısızsa sonuç **Unknown**'dır. Sebebi: ağı tamamen kopmuş bir ortam
her şeyi reddeder, ve bunu izolasyon diye raporlamak en güçlü verdict'i en zayıf gerekçeyle vermek
olurdu.

Teşhis, hangi yolun açık olduğunu **söyler** — ve iki farklı arızayı birbirinin adıyla anmaz:
ortamın kendi loopback'i üzerinden ulaşıldıysa host loopback'i köprülenmiştir; host'un alt ağ
adresinden ulaşıldıysa host basitçe erişilebilirdir. Operatörün gideceği yer farklıdır.

Proxy kontrolü **yalnızca değişken adlarını** raporlar; bir proxy değeri kimlik bilgisi taşıyabilir
ve onu yazdıran bir teşhis, onu bu çıktıyı yakalayan her log'a koyardı.

### 14.9 `.wslconfig` salt-okunur

Hub `.wslconfig` veya `wsl.conf` **yazmaz**, Windows/Hyper-V firewall kuralı **oluşturmaz veya
değiştirmez**. Bir guard, bu adları anan hiçbir üretim dosyasının aynı zamanda dosya yazma API'si
taşımamasını kilitler.

Yapılandırma metni ile çalışma zamanı gerçeği çeliştiğinde **çalışma zamanı kazanır** ve hazırlık
sürüklenmeyi raporlar. Gizli bir self-heal yoktur: ölçtüğünü onaran bir teşhis, her zaman başarı
raporlayabilirdi.

### 14.10 Hub API kimlik doğrulaması — KAPSAM DIŞI

Bu dilimde **uygulanmadı** ve bir ikame olarak da ele alınmadı. `Negotiate`, JWT, API key, client
sertifikası, yeni login — hiçbiri yok, guard'la kilitli.

Sert ağ sınırı bağımsız olarak zorunludur. API kimlik doğrulaması gelecekte **savunmada derinlik**
olabilir — ağ politikası başarısız olsa bile tutan ikinci bir katman — ama birincisinin yerine
geçmez. §14.1.1'deki düzeltme bunu daha da acil kılıyor: loopback-only bugün bir sınır değil.

### 14.11 Provizyon sınırı ve KANITLANMAYAN

Gerçek nftables **zorlaması** bu dilimde **ölçülmedi**.

Neden: mevcut Ubuntu'ya nftables kurmak yasaktı (o, negatif/keşif fikstürü olarak kalır) ve temiz
tek-kullanımlık bir dağıtım **provizyonlanamadı** — `wsl --install -d Debian --no-launch` denendi ve
oturumun izin sınıflandırıcısı tarafından reddedildi ("Blocked by classifier"). Mevcut Ubuntu'nun
klonlanması da yasak: `/home`, `/root`, sağlayıcı oturumu ve makineye özgü kullanıcı verisi taşırdı.

Bu yüzden **kanıtlanmamış** olanlar:

- Host reddinin bir **kural** tarafından yapıldığı (bugünkü davranış bu makinenin tesadüfi
  yapılandırmasıdır — ve §14.1.1'e göre host aslında **erişilebilir**).
- Peer reddi (A→B, B→A) — iki tek-kullanımlık ortam gerekir.
- Politika yüklüyken public HTTPS'in hâlâ çalıştığı (§14.4.1'deki gateway argümanı **ölçülmedi**).
- Politikanın WSL yeniden başlatmasından sonra yeniden uygulandığı.
- Politikanın iş yükünden **önce** aktif olduğu.
- `nft list table` çıktısının gerçek biçimiyle normalizasyonun uyuştuğu — karşılaştırıcı gerçek
  `nft` çıktısına karşı **doğrulanmadı**.

Bunların hiçbiri sessizce "çalışıyor" sayılmaz: `network.policy.enforced` bu makinede **Fail**'dir,
`peer.network.unreachable` **Unknown**'dır, dolayısıyla hiçbir Hard ortam `Ready` olamaz ve
`IHardExecutionPlane` **kullanılamaz** kalır.

### 14.12 Paylaşılan `/mnt/wsl` — ayrı ve açık bir engel

Keşif, `/mnt/wsl`'in her WSL2 dağıtımı arasında paylaşılan `1777` bir tmpfs olduğunu ve
**AgentUser'ın yazabildiğini** ölçtü; `/mnt/wslg` de paylaşılıyor. Peer dağıtımın socket dizinleri
(`docker-desktop/shared-sockets/{host,guest}-services`) Ubuntu'dan görünüyor.

Bu bir **dosya sistemi** engelidir ve ağ politikasının kabulünü geçersiz kılmaz — ama tam Hard
hazırlığını false tutar ve **G-K2'yi açık bırakır**. `peer.filesystem.unreachable`, ağ peer
izolasyonu çalıştı diye **Pass yapılmaz**; ikisi ayrı kontrollerdir ve nihai G-K kabulü ikisini de
ister.

Gelecekteki provizyon gereksinimi: Hard ortam `Ready` olmadan önce paylaşılan WSL mount
yüzeylerinin agent erişiminden **kaldırılması/maskelenmesi/reddedilmesi**.

### 14.13 10C-4'e devir

Provizyon yetkilendirildiğinde iki tek-kullanımlık Hard dağıtımla (`corex-hard-net-a-<guid>`,
`corex-hard-net-b-<guid>`, temiz rootfs, sonunda unregister) kanıtlanması gerekenler:

1. A → B sentetik TCP: **DENY**; B → A: **DENY**.
2. A ve B → Windows sentetik uç noktaları (loopback, alt ağ adresi, LAN adresi): **DENY**.
3. A ve B → izinli public HTTPS: **ALLOW** (aksi hâlde politika kullanışsızdır).
4. Politika WSL yeniden başlatmasından sonra yüklü; hazırlık doğruluyor.
5. Politika iş yükünden önce aktif (root sahipli systemd birimi, Hub yürütme biriminden önce).
6. AgentUser politikayı değiştiremiyor (A ve B'de ayrı ayrı).
7. Politika eksik/yanlışsa kabul **reddediliyor** — filtresiz yürütmeye geri dönüş yok.
8. `/mnt/wsl` ve `/mnt/wslg` hâlâ paylaşılıyor mu — kayda geçer, bu dilimde düzeltilmez.

### 14.14 Bu dilimde YAPILMAYANLAR

- Gerçek nftables zorlaması (temiz tek-kullanımlık ortam provizyonu gerekiyor).
- İki-distro peer kabulü.
- Politikayı **uygulayan** üretim kodu — bu build yalnızca render eder ve doğrular.
- Mevcut Ubuntu'ya kurulum veya kural yükleme; `.wslconfig` / `wsl.conf` / firewall değişikliği.
- Hub API kimlik doğrulaması.
- Tam Hard görev kabulü ve orkestratör yönlendirmesi (`10C-4`).
- Şema değişikliği, migration, yeni API/UI yüzeyi. Ağ hazırlığı **kalıcı doğru olarak saklanmaz**.
- Sağlayıcı çağrısı; Claude/Codex **çalıştırılmadı**.
- Gerçek Kronos verisi (ADR-0017). Kronos **BLOCKED**.
- G-K1..G-K5 **AÇIK** kalır.
