# ADR-0018 — Doğrulama çalıştırılabilirleri allowlist ile çözülür; parser adımı rapor yolunu bildirir

- **Durum:** Accepted
- **Tarih:** 2026-08-23
- **Kaynak:** mimari doküman § I.3 ("`executable` allowlist'ten gelir, mutlak yola
  çözülür. `args` dizidir; kabuk yok."); Faz 4 uygulama kapsamı § 6 ve § 8

## Bağlam

Bir doğrulama profili adımı `executable: dotnet` gibi mantıksal bir ad taşır. Bu adın
nasıl bir programa dönüştüğü güvenlik açısından belirleyicidir: `PATH` üzerinden arama
yapmak, çalışma dizinine bırakılmış bir `dotnet.exe` ile ele geçirilebilir. Aracı bulmak
için `where.exe` veya `Get-Command` çalıştırmak ise sorunu bir başka süreç başlatarak
çözmeye kalkmaktır.

Ayrıca bir sonuç ayrıştırıcısı (`trx`, `junit`) okuyacağı dosyayı bilmek zorundadır.
Repository'de "bir yerlerde bir `.trx`" aramak, doğrulamayı diskte o an ne varsa ona
bağımlı kılar.

## Karar

1. Mantıksal ad → **mutlak çalıştırılabilir yolu** eşlemesi yapılandırmada tutulur
   (`hub.config.yaml` → `verification.executables`). Veritabanı tablosu açılmaz.
2. Eşlemede olmayan bir ad **fail-closed** reddedilir: süreç hiç başlatılmaz, adım
   `StartFailed` olur.
3. `PATH` araması yoktur, harici çözücü araç çalıştırılmaz. Yapılandırılan yol mutlak
   olmak zorundadır ve **yazıldığı gibi** kullanılır; kanonikleştirme `FileSystemGuard`'a
   aittir (E.1 #2).
4. Allowlist'te olmak yeterli değildir: Faz 2 çalışma zamanı kabuk yasağı yürürlükte
   kalır, yanlışlıkla eşlenmiş bir `cmd.exe` yine reddedilir.
5. `VerificationStep` **`resultPath`** alanı kazanır. Parser bildiren adım bunu bildirmek
   **zorundadır**; parser bildirmeyen adım bildiremez. Yol görelidir ve doğrulama çalışma
   dizininin altında kalmak zorundadır.

## Sonuçlar

- Yeni bir aracın kullanılabilmesi bilinçli bir yapılandırma değişikliği gerektirir.
- Yapılandırma hatası (göreli yol, boş eşleme) sessizce düşürülmez, raporlanır.
- `resultPath` adım JSON belgesinin parçasıdır; ilişkisel şema değişmez, migration yoktur.
- Rapor yolu yönetilen yol sınırından geçer: `..` ile çıkış ve reparse point kaçışı
  reddedilir.
