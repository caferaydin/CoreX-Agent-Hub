# ADR-0028 — Kanıtla sınırlı otopilot: sıra ve politika kabulü, entegrasyon insanda

- **Durum:** Accepted
- **Tarih:** 2026-10-22
- **Faz:** 11A
- **İlgili:** ADR-0014 (insan kabulü), ADR-0015 (MACHINE/HUMAN kriterleri), ADR-0022 (review
  kanıt üretir, karar üretmez), ADR-0005 (sonuç commit'i Hub'ındır), E.1 #6/#9, mimari doküman
  A.3 ve "risk #10: otonomi eklemek"

## Bağlam

Operatör Hub'ı bir "agent space" olarak kullanmak istiyor: günlük işleri sıraya koyacak, ekip
onları kendi başına yürütecek, operatör sonuçları sonradan görecek. Bugünkü Hub bunun tam
tersine kuruldu. Mimari doküman A.3 "otonom yazılım fabrikası değil" der, risk listesinin 10.
maddesi "otonomi eklemek: kanıtı ve determinizmi öldürür" der. ADR-0014'e göre kabul
sistemin tek insan otoritesi noktasıdır.

Riskin gerekçesi doğru, ama otonominin kendisini değil belirli bir biçimini hedefliyor: agent
beyanını kanıt yerine koyan, başarısız işi döngüyle "düzelten", kararı kimin verdiğini
silikleştiren otonomi. Bu ADR, kanıtın ve determinizmin hiçbir yerine dokunmayan dar bir
otonomi tanımlar.

Operatörün kararları (2026-10-22):

- Pilot proje `talep-takip`; önemli ürün projeleri bu yapıya dahil edilmez.
- Otonomi seviyesi: **tam otomatik** — temiz iş otomatik kabul edilir.
- Ana dala ekleme: **operatörde kalır** ("Sadece otomatik kabul"). Hub `main`'e dokunmaz.

## Karar

1. **Otopilot repository kapsamlıdır, yalnızca yapılandırmada yaşar, varsayılanı kapalıdır.**
   `hub.config.yaml` içinde repository altında bir `autopilot:` bloğu. Veritabanına seed
   edilmez (`reviewerProfile` gibi), her turda dosyadan yeniden okunur; satırı silmek onu
   kapatır. Organizasyon veya proje düzeyinde bir anahtar yoktur: açmak her repository için
   ayrı, bilinçli bir satırdır.

2. **İki yetki vardır ve her biri ayrı açılır:**
   - `runQueue` — `Ready` olan ve **hiç denenmemiş** görevleri, oluşturulma sırasıyla, her
     seferinde bir tane başlatır. Başlatma yolu operatörün "Run" düğmesiyle aynıdır
     (`ExecutionLauncher`); makine başına tek execution slotu aynen geçerlidir.
   - `acceptWhenClean` — politika kabulü (madde 3).

3. **Politika kabulü `AcceptanceService` içindedir ve normal kabul yolunun daraltılmış
   hâlidir.** Normal kabulün tüm kuralları (I-A1, I-A9, makine gerçeğinin kurulabilmesi) ve
   ek olarak:
   - görevin **hiç `Human` kriteri yoktur** — insan kriteri insan tarafından cevaplanır
     (ADR-0015 #6 aynen geçerlidir) ve politika bir insan cevabı uyduramaz;
   - **her** `Machine` kriteri `Met`'tir, yalnızca `required` olanlar değil;
   - Agent, Verify ve **Review adımları `Succeeded`**'dır — reviewer tanımlı olmayan bir
     repository'de execution Review adımı `Skipped` olarak `Passed` biter; bu bir insanın
     kabul edebileceği ama politikanın kabul edemeyeceği bir geçiştir;
   - `ReviewFindingGate` engelleyici bulgu görmez, beklenmeyen agent commit'i yoktur, sonuç
     commit'i vardır, `required` doğrulama geçmiştir.

   Karar satırı `AcceptanceDecision.Source = Policy` taşır (yeni sütun, migration
   `RecordAcceptanceDecisionSource`); aktör `hub:autopilot`, gerekçe kanıttan üretilir.
   Domain, `Policy` kaynaklı bir kararın override, red, değişiklik isteği veya insan
   cevabı taşımasını reddeder. Görev geçişi aynıdır (`AwaitingAcceptance → Accepted`,
   `HubTask.Accept`); yeni bir durum, yeni bir kenar veya yeni bir mutator yoktur.

4. **Entegrasyon yoktur.** ADR-0014 #1 ve E.1 #9 aynen geçerlidir: kabul merge değildir,
   Hub varsayılan dala dokunmaz. Kabul edilen iş `agent/<KEY>/<n>` dalında kalır; ana dala
   almak operatörün işidir. Otopilot yalnızca **okur**: kabul edilmiş bir sonucun varsayılan
   dalda olup olmadığına salt okunur bir soy kontrolüyle bakar.

5. **Hattı durdur.** Bir repository'nin sırası şu durumlardan herhangi biri varken yeni
   görev başlatmaz:
   - bir görev bir insanın kararını bekliyor;
   - bir görevin son denemesi geçmedi (otopilot yeniden denemez);
   - kabul edilmiş bir sonuç henüz varsayılan dalda değil — sonraki görev onun üstüne
     kurulmalıdır, eski bir taban üzerine değil;
   - herhangi bir execution çalışıyor.

   Durma sessiz değildir: durum ve sebep `GET /api/v1/autopilot` ile ve arayüzde görünür.

6. **Otopilotun yapmadıkları:** yeniden deneme, override, red, değişiklik isteme, iptal,
   merge, push. Bunların her biri ya bir insan kararıdır ya da bu ADR'nin reddettiği bir
   onarım döngüsüdür.

7. **Barındırma:** otopilot `hub-api` içinde bir `BackgroundService`'tir (mimari testi
   `IHostedService`'i yalnızca Api projesinde izin verir). Karar mantığı Composition'da sade
   bir sınıftır; host yalnızca zamanlayıcıdır. CLI'de otopilot komutu yoktur.

## Sonuçlar

- "Kanıt" ve "determinizm" değişmez: hiçbir agent beyanı karara girmez, execution outcome
  yazılmaz, kapılar ve review aynıdır. Değişen tek şey, kanıt her soruyu cevapladığında
  "evet"i kimin söylediğidir — ve bu artık satırda yazılıdır.
- İnsan kriteri olan her görev, otopilot açık olsa bile insanı bekler. Tam otomatik akış
  isteyen görev yalnızca makine kriteriyle yazılmalıdır.
- Kabul edilen iş ana dala operatör tarafından alınana kadar sıra durur. Tek tek görevlerin
  birbirinin üstüne kurulması bu bilinçli adıma bağlıdır.
- Mimari doküman A.3 ve risk #10 bu ADR ile daraltılmıştır: Hub hâlâ "otonom yazılım
  fabrikası" değildir; kanıtla sınırlı, repository kapsamlı, kapalı varsayılanlı bir sıra
  yürütücüsüdür.

## Reddedilen alternatifler

- **Hub'ın kabul edilen işi ana dala kendisinin ileri sarması.** Tasarlandı; operatör
  reddetti. Teknik olarak yalnızca ileri sarma ve sıkı ön koşullarla güvenli yapılabilir,
  ama E.1 #9'un tek istisnası olurdu ve operatörün çalışma kopyasını değiştirirdi.
- **Politikanın insan kriterlerini "Met" diye cevaplaması.** ADR-0015 #6'nın ve "insan
  cevabı önceden doldurulmaz" kuralının doğrudan ihlali.
- **Başarısız işi otomatik yeniden deneme / onarım döngüsü.** Aynı nesnel girdiyle aynı
  sonucu üretir, aboneliği harcar ve "kanıt ne dedi" sorusunu bulanıklaştırır. Geri bildirimli
  yeniden deneme ayrı bir karar konusudur.
- **Organizasyon veya proje düzeyinde tek anahtar.** Bir satırla önemli bir repository'nin de
  otomatik kabul kapsamına girmesine izin verirdi.
