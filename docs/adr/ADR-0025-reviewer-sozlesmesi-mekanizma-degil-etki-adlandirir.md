# ADR-0025 — Reviewer sözleşmesi mekanizma değil etki adlandırır

- **Durum:** Accepted
- **Tarih:** 2026-08-25
- **Faz:** 8B
- **İlgili:** ADR-0008 (yetenek beyanı), ADR-0022 (review kanıt üretir, karar üretmez),
  ADR-0024 (sabitlenmiş sağlayıcı sürümü)

## Bağlam

Hub'ın reviewer sözleşmesi Faz 7'den beri şunu diyordu:

> - Read unchanged files only when you need them to understand the changed code.
> - Do not modify any file. **Do not run any command.**

Bu iki cümle tek bir sağlayıcı için tutarlıydı. Claude adaptöründe okuma ayrı bir araçtır
ve reviewer'ın araç yüzeyinde zaten Bash yoktur; "komut çalıştırma" yasağı hiçbir şeye mal
olmaz.

Sabitlenmiş Codex 0.130.0'da okuma **bir sandbox'lı kabuk komutudur**. Aynı sözleşme orada
reviewer'a "dosyaları oku" ve "hiçbir komut çalıştırma" demiş oluyordu.

Reviewer ikinciye uydu. Ölçüm:

- İki gerçek canlı review, sıfır `command_execution` olayı.
- Her ikisi de doğru, temellendirilmiş bulgular üretti — çünkü diff kendi kendine yeterliydi.
- Hiçbir yerde "anlık görüntüyü okuyamadım" denmedi.

Yani sözleşme, reviewer'ı diff'in içerdiğiyle **sessizce** sınırlandırıyordu. Diff'in
çevresindeki bağlamı gerektiren bir değişiklikte bu, görülmeyen bir kalite kaybı olurdu.

## Karar

**Bir sağlayıcı-nötr talimat, mekanizma değil etki adlandırmalıdır.**

Kural şu hâle geldi:

> - Do not modify anything and cause no effect that outlives this review: no writes, no
>   installs, no network, nothing that touches the world outside this checkout.
>   Reading the checkout is none of those things.

Ve okuma izni ayrıca ve açıkça verildi:

> - The working directory is a disposable checkout of exactly the commit under review.
>   It is yours to read. It is thrown away afterwards and nothing you do to it is kept.
> - Read unchanged files in that checkout whenever you need them to understand the changed
>   code. Opening a file is expected work, not an escalation.

Genel ilke: **anlamı sağlayıcıya göre değişen bir cümle, sağlayıcı-nötr bir sözleşme
değildir.** "Komut çalıştırma", "araç kullanma", "shell açma" gibi ifadeler mekanizma
adlandırır ve her sağlayıcıda farklı bir şeyi yasaklar. "Hiçbir şeyi değiştirme", "bu
review'dan sonraya kalan bir etki yaratma" ise etkiyi adlandırır ve her sağlayıcıda aynı
şeyi yasaklar.

## Sonuçlar

Ölçülen sonuç (düzeltmeden sonra, aynı iki kanonik görev):

- Temiz görev: **20 `command_execution` olayı**, birden çok başarılı okuma (`exit 0`), ve
  yine **sıfır bulgu**. Okuma erişimi şiddet enflasyonu üretmedi.
- Engelleyen görev: anlık görüntü okundu, iki kapsam-içi Major bulgu, `ReviewBlocked`.
- Bazı komutlar `declined` (exit −1) döndü — sandbox sınıflandıramadığını reddediyor.
  Review bundan etkilenmedi.

Maliyet: okuma token harcar. Temiz görevde giriş token'ı 6.423'ten 139.177'ye çıktı
(109.056'sı önbellekten). Bu, bağlamın bedelidir ve kabul edilmiştir.

## Kapsam sınırı — güvenlik otoritesi değişmedi

Bu karar bir gevşetme gibi görünebilir; değildir.

Reviewer'ın hiçbir şeyi değiştiremediğini kanıtlayan şey bu cümle değildi ve olmadı:

1. Sağlayıcı `-s read-only` ve `approval_policy=never` ile çalışır — işletim sistemi
   sınırı.
2. Hub, tek kullanımlık anlık görüntüyü review'dan **önce ve sonra** karşılaştırır
   (`InspectSnapshotIntegrityAsync`). Otorite budur.
3. Faz 7'nin deterministik tamper testleri hâlâ yetkilidir.

Komut telemetrisi yalnızca **erişim kanıtıdır**, tamper otoritesi değildir.

## Reddedilen alternatifler

**Sözleşmeyi sağlayıcıya göre dallandırmak.** "Codex ise komutlara izin ver" tek bir
satırla Faz 8'in tüm iddiasını yok ederdi: reviewer'ın profille seçilmesi, sözleşmenin
sağlayıcıyı bilmemesine bağlıdır.

**Adaptörde telafi etmek.** `CodexRuntime`'ın sözleşmeye kendi cümlesini eklemesi,
talimatların tek kaynağının Hub olması ilkesini bozardı.

**Olduğu gibi bırakmak.** Reviewer'ın çalıştığı doğruydu — ama yalnızca iki görevin
diff'i kendi kendine yeterli olduğu için. Bunu "sorun yok" saymak, ölçümün kapsamını
kuralın kapsamı sanmak olurdu.

**Enjeksiyon direncini bu düzeltmeyle karıştırmak.** Depo içeriğinin talimat olmadığı
kuralı ayrı bir paragraftır, ölçüldü ve geçti: reviewer enjeksiyonu hem yok saydı hem de
onu ayrı bir Major bulgu olarak bildirdi. Bu ADR onunla ilgili değildir.
