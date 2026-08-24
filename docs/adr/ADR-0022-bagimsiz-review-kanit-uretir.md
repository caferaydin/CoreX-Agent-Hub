# ADR-0022 — Bağımsız review kanıt üretir, karar üretmez

- **Durum:** Accepted
- **Tarih:** 2026-08-24
- **Kaynak:** mimari J.3 / J.4, ADR-0008, ADR-0014, E.1, Faz 7A ölçümleri

## Karar

Bağımsız review adımı **kanıt** üretir: yapılandırılmış bulgular. **Sonucu** üretmez.
`Passed` / `ReviewBlocked` / `ReviewUnusable` ayrımını Hub verir; şemada bir "verdict"
alanı yoktur ve düz metin hiçbir zaman okunmaz.

Review, **commit'i** inceler; görevi, çalışma ağacını veya varsayılan dalı değil. Girdi,
sonuç commit'i zaten atılmış ve kendisinden istenen makine doğrulamasını zaten geçmiş bir
execution'dır.

## Bunu gerektiren üç ölçüm

**1. Biten sağlayıcı, tamamlanmış review demek değildir.**

Faz 7A'da ölçüldü: gerçek sağlayıcı sıfırla çıkabilir, kendini başarılı ilan edebilir ve
**hiç** yapılandırılmış çıktı döndürmeyebilir. Şema hatası dışarıdan başarıya benzer.
Ayırt eden tek şey `structured_output` alanının **varlığıdır**.

Bu yüzden "çıktı yok" asla "bulgu yok" sayılmaz: `ReviewUnusable` olur. Aksi hâlde her
şema hatası sessiz bir geçişe dönüşürdü.

**2. Sağlayıcı kendi araç yüzeyine bir araç ekler.**

Ölçüldü: şema verildiğinde sağlayıcı, istenen listeye kendi `StructuredOutput` aracını
ekler. Beklenen etkin araç yüzeyi bu yüzden `istenen + StructuredOutput` olarak
hesaplanır; adapter bunu bir ihlal değil, ilan edilmiş bir gerçek olarak doğrular.

**3. Reviewer kapsamı belirleyemez.**

Şemadaki `outOfScope` alanı reviewer'ın **iddiasıdır** ve kanıttır. Hub'ın cevabı,
execution'ın gerçekten değiştirdiği yollardan türetilir. Alanı reviewer'a bırakmak,
bloklayan bir bulgunun kendi kendini muaf tutmasına izin vermek olurdu.

İki cevap uyuşmazsa uyuşmazlık **kaydedilir**; sessizce düzeltilmez.

## Sınırlar

- Review tek kullanımlık, `ResultCommitSha`'ya sabitlenmiş bir snapshot'ta çalışır.
  Agent'ın worktree'si **kullanılmaz**.
- Ağaç, reviewer çalışmadan **önce** ve her denemeden **sonra** ölçülür. HEAD, tree ya da
  temizlik değiştiyse sonuç `ReviewUnusable`'dır ve **yeniden denenmez**: ölçtüğü şeyi
  değiştiren bir reviewer onun review'ünü üretmemiştir.
- Yeniden deneme **bir** kezdir ve **yalnızca** gelmeyen ya da uymayan bir belge içindir.
  Süresi dolan, iptal edilen veya düşen bir sağlayıcı şema sorunu değildir; denenmez.
- Review hiçbir şeyi onarmaz, commit atmaz, merge/push yapmaz ve git komutu kurmaz.
  Tuttuğu portlarda bunların hiçbiri yoktur.
- Reviewer sözleşmesi Hub'a aittir ve Hub'ın kendi dizinine her koşumda yeniden yazılır.
  İncelenen ağacın içindeki hiçbir metin talimat değildir.

## Eşik

`ReviewFindingGate.MajorThreshold = 1` korunur.

G7-1 kalibrasyonu, gerçek bir Hub commit'i üzerinde gerçek bir reviewer ile tek seferlik
ölçüldü. Ölçüm `docs/phase-7-review.md` içindedir. Eşik, ölçüme rağmen değil, ölçüm onu
desteklediği için sabit kalır: tek bir gerçek Major bulgu, bir insanın bakmasını
gerektirmeye yeter — ve `ReviewBlocked` zaten bir insanın override edebileceği tek
sonuçtur (ADR-0014).

Reviewer sözleşmesi bilerek **bulgu sayısı hedefi vermez**. Bir şey bulması söylenen
reviewer bir şey bulur; o sayıya göre ayarlanan eşik, kodu değil talimatı ölçerdi.

## Sonuçlar

- `ReviewBlocked`, çalışan bir review'dür: adım `Succeeded`'dır ve ADR-0014 override'ına
  uygundur.
- `ReviewUnusable`, review'ün **yokluğudur**: ne düz kabul ne de override ile geçilebilir.
- Reviewer'ı değiştirmek bir yapılandırma değişikliğidir: `reviewerProfile` başka bir
  profili gösterir. Faz 8'de Codex'e geçiş kod değişikliği gerektirmez.
- Host'u ölen bir execution kurtarma yolunda **review edilmiş sayılmaz**: kurtarma
  reviewer tutmaz, bu yüzden review edilen bir repository'de sonucu `Passed` diyemez.
