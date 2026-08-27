# ADR-0027 — Beyan edilen izolasyon ölçülen izolasyon değildir

- **Durum:** Accepted
- **Tarih:** 2026-08-26
- **Faz:** 10C-1
- **İlgili:** ADR-0012 (mantıksal izolasyon pilotu), ADR-0017 (Kronos onboarding kapısı),
  ADR-0006 (kabuk yok), ADR-0007 (ortam deny-by-default), ADR-0009 (bant dışı kimlik
  doğrulama), Faz 10A-HI-01 (host↔guest principal bulgusu)

## Bağlam

Faz 1'den beri katalog şu kuralı uyguluyordu:

```
requiresHardIsolation && isolationLevel == Logical  ->  Blocked
```

Kural doğruydu ve amacı vardı: sert izolasyon **isteyen** ama mantıksal olarak yapılandırılmış
bir organizasyon kod ile bloke edilir (ADR-0017). Ama tersi hiç sorulmadı. YAML'a
`isolationLevel: Hard` yazmak organizasyonu `Active` yapıyordu — ve `Active` olan bir
organizasyon yürütme başlatabiliyordu.

Yani sert izolasyon, dosyaya yazılmış bir kelimeydi.

Faz 10A-HI-01 bunun neden yeterli olmadığını mekanik olarak gösterdi. Aynı Windows kullanıcısı
altında kayıtlı bir WSL dağıtımı, o kullanıcı altında çalışan güvenilmeyen koda karşı bir sınır
**değildir**: o kod `wsl -u root` der ve içeride uid 0 olur. Dahası varsayılan bir dağıtım
`/mnt/c` üzerinden host dosya sistemini bağlar, `binfmt_misc` üzerinden host programlarını
çalıştırabilir ve Windows PATH'ini içeri aktarır. Hiçbiri katalogdan görünmez.

## Karar

**Beyan niyettir; kabul ölçüm ister.**

İki kavram ayrılmıştır ve bir daha karıştırılamaz:

| | Nedir | Nerede yaşar |
|---|---|---|
| `IsolationLevel` | Operatörün beyanı | katalog (veritabanı) |
| `HardIsolationReadiness` | Makinenin sağladığı | çalışma anında ölçüm, saklanmaz |

Domain **değişmemiştir**. Bir Hard organizasyon hâlâ `Active`'dir ve `BlockReason` taşımaz;
domain makine gerçeği hakkında hiçbir şey bilmez ve bilmemelidir. Değişen tek şey, yürütmeye
kabul noktasının (`ExecutionOrchestrator.ValidateStartAsync` — sistemdeki tek nokta) Hard bir
organizasyon için üç soru daha sormasıdır:

1. **Kendi ortamı gerçek mi.** 21 zorunlu kontrol; `Unknown` asla `Pass` değildir.
2. **Makine bütün olarak güvenli düzenlenmiş mi.** Eş-kiracılık kuralı (aşağıda).
3. **Bu build o ortamın içinde çalıştırabiliyor mu.** 10C-1'de: hayır.

Her üçü de **fail-closed**'dır. Wire edilmemiş bir Hub, Hard organizasyonları serbest bırakan
bir Hub değildir: otoritenin yokluğu izin değildir.

## Eş-kiracılık kuralı

Organizasyon başına hazırlık bu soruyu cevaplayamaz, çünkü tehdit komşudandır:

> Bir Hard organizasyonun çalışmasına izin veriliyorsa, repository denetimli kod
> çalıştırabilen **her** organizasyon ya kendisi Hard ve hazır olmalı, ya da hiç
> çalıştıramıyor olmalıdır.

Kapı **Hard organizasyona** uygulanır, komşuya değil. Bu bilinçlidir: Logical bir organizasyon
aynen çalışmaya devam eder, kaybettiği tek şey yanında Hard bir kiracı bulundurma imkânıdır.
Tersi — bir komşuyu, yanına biri taşındı diye durdurmak — mevcut bir kullanıcıyı yeni bir
kullanıcı için cezalandırmak olurdu.

Sadece beyan eden bir komşu, hiç beyan etmemiş bir komşu kadar tehlikelidir: kod her iki
durumda da paylaşılan principal altında çalışır. Bu yüzden kural komşuların yapılandırmasını
değil **makinesini** sorar.

## Bir kontrol bilinçli olarak asla geçmez

`control-plane.unreachable`, sert izole bir agent'ın — çok kullanıcılı yetkilendirme sınırı
**olmayan** — Hub API'sine ulaşıp ulaşamadığını sorar. Bu bir ağ sorusudur ve bir bağlama
tablosuyla cevaplanamaz: loopback yönlendirmesi, host gateway'i ve mirrored networking hiçbir
iz bırakmaz.

Bu build'in böyle bir probe'u yoktur. Dolayısıyla kontrol, görülebilen bir dosya sistemi yolu
varsa `Fail`, yoksa `Unknown` döner; `Pass` seçeneği **yoktur** ve bir guard bunu kaynak
seviyesinde kilitler.

Sonucu kabul edilmiştir: hiçbir sert ortam bu dilimde `Ready` olamaz. Alternatif — hiçbir şey
bağlı değilse geçmiş saymak — bir yapılandırma varsayımını ölçüm kılığına sokmaktı, ki bu tam
olarak bu ADR'nin engellemek için var olduğu hatadır.

## Saklanmaz

`hardIsolation` bloğu (`backend`, `environmentId`, `agentUser`, `serviceUser`, kökler,
tavanlar) yapılandırma dosyasında yaşar ve veritabanına **yazılmaz**. Şema değişikliği yoktur,
migration yoktur.

Sebebi tek cümledir: hiçbiri katalog gerçeği değildir. Hepsi bu makinenin ne sağladığıyla
ilgilidir, ve bir ortamın var olduğunu iddia eden kalıcı bir satır — tanımladığı ortam
silindikten sonra bile — hazırlık kontrollerinin yakalamak için var olduğu tam olarak o
hatadır.

## Sonuçlar

Kazanılan: `isolationLevel: Hard` artık bir kelime değil, bir iddiadır ve iddia her yürütme
başlangıcında sınanır. Kronos onboarding'i (ADR-0017) için gereken kapı artık dokümanda değil
kodda ve testtedir. `hub doctor isolation` operatöre neyin eksik olduğunu tam olarak söyler ve
**hiçbir şeyi düzeltmez**.

Ödenen: bu build'de hiçbir Hard organizasyon çalışamaz — makinesi kusursuz olsa bile.
Yürütme düzlemi 10C-2'nin işidir.

Kabul edilen: bu bilinçli bir eksiklik, gizlenmiş bir hata değildir. Tek alternatif, ortam
bulunamadığında işi host üzerinde çalıştırmak olurdu; bu, organizasyonun tam olarak
olmamasını istediği şeydir ve herkese başarı gibi görünürdü. Geri düşüş yolu yoktur,
Logical'a düşürme yoktur.

## Reddedilen alternatifler

**Domain'de `ResolveStatus`'u değiştirmek** — "Hard beyan eden ama hazır olmayan organizasyon
`Blocked` olsun". Reddedildi: domain'in makine gerçeği yoktur ve olamaz. `Blocked` kalıcı bir
katalog durumudur; hazırlık ise ölçüldüğü anın gerçeğidir. İkisini aynı alana yazmak, WSL'i
kapattığında kalıcı olarak bloke olan ve geri açtığında kimsenin fark etmediği bir
organizasyon üretirdi.

**Hazırlığı veritabanına önbelleklemek.** Reddedildi: on dakika önce hazır olan bir makine,
şu andaki makine hakkında kanıt değildir.

**Kontrolleri iki değerli yapmak (`Pass`/`Fail`).** Reddedildi: "ölçüldü ve yanlış" ile
"ölçülemedi" farklı şeylerdir. İkisini birleştirmek ya sorulmamış bir soruyu bir arızaya, ya
da — çok daha kötüsü — bir başarıya çevirirdi.

**Probe'un eksik olanı kurması** (`hub doctor --fix`). Reddedildi: ölçtüğünü onaran bir teşhis,
bir daha "sorun yok" dediğinde kimsenin güvenemeyeceği bir teşhistir. Distro oluşturmak,
kullanıcı açmak, mount kapatmak operatörün bilerek aldığı kararlardır.

**Agent'ı yalnızca `uid != 0` ile güvenli saymak.** Reddedildi ve bu makinede ölçüldü:
varsayılan Ubuntu kullanıcısı uid 1000'dir ve `sudo` grubundadır — yani istediği an root olur.
Ayrıcalıksız bir uid taşımak ayrıcalıksız olmak değildir.
