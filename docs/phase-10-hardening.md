# Faz 10 — Sertleştirme

Faz 10B-1: **yarışsız Windows süreç başlatma**.
Faz 10B-2: **Windows Job Object kaynak limitleri**.

Bu belge Faz 10'un sertleştirme işini kaydeder. Şu an 10B-1 ve 10B-2 uygulanmıştır; geri
kalan dilimler (10C ve sonrası) aşağıda ayrıca işaretlenmiştir ve **uygulanmamıştır**.

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

10B-2 mekanizmayı verir; **zorunluluk** 10C'nin readiness kontrolüne aittir ve burada
uygulanmamıştır.

## 9. Kalan sınırlamalar

- Hard izolasyon için **zorunlu sayısal politika** Faz 10C readiness'ine kadar etkin değildir.
- Linux kaynak zorlaması (systemd/cgroup: `MemoryMax`, `TasksMax`, `KillMode=control-group`)
  **10C**'ye aittir.
- CPU-rate kotası bu dilimin kapsamında değildir; yürütme süresinin otoritesi soft/idle/hard
  duvar saati zaman aşımlarıdır.
