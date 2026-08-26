# Faz 10 — Sertleştirme

Faz 10B-1: **yarışsız Windows süreç başlatma**.

Bu belge Faz 10'un sertleştirme işini kaydeder. Şu an yalnızca 10B-1 uygulanmıştır; geri
kalan dilimler aşağıda "kalanlar" başlığı altındadır ve **uygulanmamıştır**.

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

## 6. 10B-2'ye kalan: Job Object kotaları

Faz 10A şunu buldu ve **hâlâ geçerlidir**: Job Object yalnızca
`JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE` ile yapılandırılmıştır. Şunlar **etkin değildir**:

- `ActiveProcessLimit` (süreç sayısı)
- `ProcessMemoryLimit` / `JobMemoryLimit` (bellek)
- CPU/zaman kotaları

Yani ağaç sınırlanır ama **kaynak tüketimi sınırlanmaz**. 10B-1 bilerek sayısal kota
politikası icat etmez. `JobObjectHandle` genişletilmeye hazır bırakılmıştır: Windows'un
beklediği `JOBOBJECT_EXTENDED_LIMIT_INFORMATION` yapısı zaten oradadır, bir limit eklemek
bir bayrak ve bir alandır — yeniden tasarım değil. Bir guard bunu kilitler.

## 7. 10C'ye kalan: WSL / Linux süreç ağacı

10B-1 **yalnızca Windows** başlatma ilkelidir. WSL kapsam dışıdır:

Bir WSL organizasyonu için çocuk `wsl.exe` olacaktır ve **Windows Job Object, distro
içindeki Linux süreç ağacını kapsamaz**. Faz 10A'da doğrulandığı üzere çözüm Linux
tarafındadır: organizasyon başına kalıcı distro + execution başına systemd transient
unit/cgroup (`KillMode=control-group`, `MemoryMax`, `TasksMax`). Bu 10C'nin işidir.

Ayrıca Faz 10A-HI-01'in bulduğu eş-kiracılık kuralı da 10C'ye aittir: aynı Windows
kullanıcısı altında kayıtlı bir distro, o kullanıcı altında çalışan güvenilmeyen bir
execution'a karşı sınır değildir.

## 8. Kalan sınırlamalar

- Job Object süreç/bellek kotaları **10B-2**'ye ertelenmiştir.
- Linux/WSL süreç ağacı denetimi **10C**'ye ertelenmiştir.
