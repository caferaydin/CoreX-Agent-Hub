# ADR-0026 — Composition root adaptörlerden ayrıldı

- **Durum:** Accepted
- **Tarih:** 2026-08-25
- **Faz:** 9A
- **İlgili:** ADR-0008 (yetenek beyanı), ADR-0013 (yerel-öncelik durum), mimari bölüm 28
  (DI container yok, düz kurulum)

## Bağlam

Faz 1'den Faz 8'e kadar `CoreX.Hub.Cli` iki şeydi aynı anda: konsol adaptörü **ve** sistemin
composition root'u. Tek bir çağıran olduğu sürece bu bir sorun değildi.

Faz 9 ikinci bir adaptör getiriyor: yerel operatör API'si. Bu noktada iki seçenek vardı ve
ikisi de kötüydü:

1. **API, CLI'a referans verir.** `hub.exe` bir çalıştırılabilirdir; bir web host'unun ona
   bağımlı olması bağımlılık yönünü ters okutur ve Faz 9B–E bunu miras alır.
2. **API kendi wiring'ini yazar.** İki wiring, ilki düzenlendiği gün iki farklı sisteme
   dönüşür. §43'ün "CLI ve HTTP aynı application authority'lerini compose etmelidir" kuralı
   tam olarak bunu yasaklar.

## Karar

**Composition root kendi projesidir ve hiçbir adaptörün adını taşımaz.**

```
Api  ──┐
       ├──> Composition ──> Application ──> Domain
Cli  ──┘
```

`src/CoreX.Hub.Composition/`: `HubSession`, `HubPaths`, `Orchestration/`, `Agents/`,
`Verification/`, `Workspace/`, `Context/`, `Gates/`.

`src/CoreX.Hub.Cli/`: `Program`, `HubCli`, `ArgParser`, `ConsoleInput`, `Output`, `Commands/`.

Taşıma mekaniktir: dosyalar taşındı, namespace'ler `CoreX.Hub.Cli.X` → `CoreX.Hub.Composition.X`
oldu, hiçbir mantık değişmedi.

## İki şey ayrıca eklendi

**`HubRuntime` / `HubScope`.** `HubSession` bir komut ömrü için tasarlanmıştır: açar, migrate
eder, seed eder, reconcile eder ve tek bir EF context'i hayatının sonuna kadar tutar. Bu CLI
için doğru, eşzamanlı istek gören bir host için yanlıştır — bir EF context iki yerden aynı
anda kullanılamaz.

`HubRuntime` pahalı işi **bir kez**, aynı `HubSession.OpenAsync` üzerinden yapar; sonrası
kendi bağlantısını taşıyan kısa ömürlü `HubScope`'lardır. Bir scope, CLI'ın kurduğu
`TaskService` ve `AcceptanceService`'in aynısını verir. **İkinci bir servis kümesi yoktur.**

**`ExecutionLaunchPlanner`.** "Bu görevi çalıştır" ifadesini composed bir yürütmeye çevirmek
`RunCommands` içindeydi. Tek çağıran varken doğruydu. İki çağıran varken, "hangi profil
uygular, hangi profil gözden geçirir, bu görev çalıştırılabilir mi" sorularının iki kopyası,
biri düzenlendiği gün iki cevap demektir. Planner artık ortaktır; `RunCommands` ondan
çağırır ve kendi private çözücüleri silinmiştir.

Bir tek fark korundu: birden fazla implementer profili olduğunda konsol operatöre hangi
bayrağı ekleyeceğini söyleyebilir, API söyleyemez. Bu yüzden `AmbiguousLaunchException`
kendi tipidir ve iki adaptör onu farklı yanıtlar — mesaj üzerinde string eşleştirme yaparak
değil.

## Sonuçlar

Kazanılan: bağımlılık yönü dürüsttür ve `The_api_reaches_the_system_only_through_the_composition_root`
guard'ı bunu iddia edebilir. Faz 9B–E yeni adaptörler eklerken aynı kapıdan geçer.

Ödenen: 33 dosyalık mekanik bir taşıma ve 8 guard dosyasının yol/assembly referanslarının
güncellenmesi. Diff büyüktür, riski düşüktür — tam paket taşımadan önce ve sonra aynıdır.

Kabul edilen: `CoreX.Hub.Cli` artık "the composition root" değildir. Bunu iddia eden guard
(`Cli_is_the_only_composition_root`) yerini
`One_project_composes_the_system_and_every_adapter_goes_through_it`'e bıraktı — asıl önemli
olan özellik "CLI köktür" değil, **"tam olarak bir kök vardır"**dı.

## Reddedilen alternatifler

**API → CLI referansı.** En küçük diff, en yanlış yön. Faz 10 sertleştirmesi bunu geri almak
zorunda kalırdı.

**API'de ikinci bir wiring.** §43'ün açıkça yasakladığı şey. İki wiring bir gün sessizce
ayrışır ve ayrıştığı gün tarayıcı ile terminal farklı şeyler yapar.

**`HubSession`'ı host ömrü boyunca tutmak.** Tek bir EF context'i eşzamanlı isteklere
paylaştırmak; ilk iki paralel istekte patlar.

**Taşımayı Faz 9E'ye ertelemek.** Değerlendirildi ve reddedildi: erteleme, 9B–D'nin üç fazının
yanlış yön üzerine inşa edilmesi demekti.
