# ADR-0024 — Sabitlenmiş sağlayıcı sürümü çalışma zamanının parçasıdır

- **Durum:** Accepted
- **Tarih:** 2026-08-25
- **Faz:** 8A
- **İlgili:** ADR-0006 (kabuk yok), ADR-0007 (deny-by-default ortam), ADR-0008 (yetenek
  beyanı ve `Unsupported`), ADR-0022 (review kanıt üretir, karar üretmez)

## Bağlam

İkinci gerçek sağlayıcı olarak Codex CLI eklendi. Faz 8 kapıları (G8-1, G8-2, G8-2C) bunu
ölçtü ve tek bir şeyi kanıtladı: **bu araçta bir review'ın dayandığı davranış sürümler
arasında değişiyor.**

Somut ölçüm:

- `codex-cli 0.149.1` üzerinde çalıştırma politikası, model tarafındaki her okumayı
  reddetti. Sandbox sağlamdı; engelleyen katman exec politikasıydı. Bu sürümde bir reviewer
  incelediği ağacı okuyamaz — yani review üretemez.
- `codex-cli 0.130.0` aynı isteği çalıştırdı ve üstüne yapılandırılmış `command_execution`
  telemetrisi verdi; reviewer'ın gerçekten ne çalıştırdığı akıştan okunabilir hâle geldi.

İki sürüm de "kurulu Codex"tir. Aralarındaki fark, bir bulgunun var olup olmamasıdır.

Ayrıca bu makinede global kurulum 0.149.1'i raporluyor. PATH üzerinden çözülen bir
çalıştırılabilir, ölçülüp reddedilmiş bir yapı üzerinde review çalıştırırdı ve bunu sessizce
yapardı.

## Karar

**Kabul edilen sürüm, çalıştırılabilir yol kadar çalışma zamanının tanımına dahildir.**

1. `CodexOptions` üç şeyi zorunlu tutar: mutlak `ExecutablePath`, tam `AcceptedVersion`,
   sağlık probu dizini. Üçü de `required`.
2. `CheckHealthAsync` yapılandırılan yolu `--version` ile sorar ve raporlanan sürümü
   `AcceptedVersion` ile **ordinal eşitlik** olarak karşılaştırır. Taban değil, aralık değil,
   `Version.Parse` ile "daha yeni olan iyidir" değil. Ölçülmemiş bir yapı denenmez, reddedilir.
3. PATH'e geri düşüş yoktur. Çıplak isim yoktur. Ortam değişkeninden yol okunmaz.
4. Otomatik kurulum, güncelleme veya sürüm düşürme yoktur. Bir sağlayıcıyı sağlamak, bu
   programın dışında bir kez verilen insan kararıdır. Paket kayıt defterine ulaşabilen bir
   çalışma zamanı, review'ın ortasında bulguyu üreten yapıyı da değiştirebilirdi.
5. Sabitlenen ikili depoya girmez. Kaynak kontrolünde binary yoktur.

## Sonuçlar

Kabul edilen: bir geliştirici makinesinde Codex'i güncellemek Hub'ın review'ını **bozar** —
sağlık kontrolü kapalı devre başarısız olur ve nedeni söyler. Bu istenen davranıştır. Sessizce
farklı bir yapıya geçmek, bir bulgunun izini sürülemez hâle getirirdi.

Kabul edilen ikinci şey: sabitleme bir bakım borcudur. Yeni bir sürüme geçmek yeni bir
ölçüm gerektirir, yapılandırmada bir değer değiştirmeyi değil.

Kazanılan: `AgentProfile.RuntimeKey` ile hangi yapının çalıştığı arasındaki bağ deterministiktir.
Bir kanıt paketi, üreten yapıya geri izlenebilir.

## Sağlayıcı-nötrlüğün korunduğu yer

Bu ADR bir sağlayıcı hakkındadır; mimari değildir. Faz 8A'da ölçülen ve guard'larla sabitlenen:

- `IAgentRuntime` hâlâ tam üç üye. İkinci gerçek sağlayıcı dördüncü bir üye gerektirmedi —
  gerektirseydi diğer ikisi bir yalan uygulamak zorunda kalırdı.
- `ExecutionOrchestrator`, `ReviewEngine` ve `AcceptanceService` diff'i **sıfır**. Hiçbiri
  Codex'i adlandırmaz.
- `CodexRuntime` tipini yalnızca iki dosya adlandırır: kendisi ve onu kaydeden tek
  composition dosyası.
- Reviewer'ın değişmesi, bir profilin farklı bir `runtimeKey` adlandırmasıdır; başka hiçbir şey.

## Dürüstçe farklı olan: talimat kanalı

Claude adaptörü, Hub'ın sözleşmesini ayrı bir sistem-prompt kanalından geçirir. Sabitlenen
Codex sürümünde ayrı bir sistem-prompt kanalı **yoktur**.

`CodexInvocationBuilder.ComposePrompt`, Hub'a ait sözleşmeyi kullanıcı istemin **önüne**,
açık bir sınır satırıyla koyar ve tümünü stdin üzerinden gönderir. Prompt hiçbir zaman
komut satırı argümanı olmaz.

Bunun anlamı net olmalı:

- **Korunan:** otorite. Talimatların tek kaynağı Hub'dır; incelenen ağaç veya kullanıcının
  kendi Codex yapılandırması reviewer'ı yapılandıramaz (`--ephemeral`,
  `--ignore-user-config`, `--ignore-rules` ve kapatılan özellik yüzeyi bunu sağlar).
- **Farklı olan:** kanal. Sözleşme ile materyal aynı akışta taşınır. Sağlayıcı tarafında
  bunları ayrı ağırlıkta tutan bir mekanizma yoktur.

Bu, Claude adaptörüne kıyasla bir **kanal ayrımı farkıdır** ve bir eşdeğerlik iddiası
değildir. Faz 8B'de gerçek bir ürün review'ı bu farkın pratikte bir sonucu olup olmadığını
ölçmelidir.

## Yetenek beyanı

ADR-0008 gereği beyan, adaptörün yaptığına göredir; aracın yapabileceğine göre değil.

| Yetenek | Beyan | Neden |
|---|---|---|
| `SupportsStructuredSchema` | evet | Verilen şema dosyaya yazılır ve sağlayıcıya geçilir; dönen belge olduğu gibi döndürülür |
| `SupportsReadOnlyMode` | evet | Tek mod budur; yazma yetkili istek başlatmadan önce adıyla reddedilir |
| `SupportsStreaming` | evet | Olaylar koşu sürerken okunur |
| `SupportsSessionResume` | **hayır** | Her koşu yeni bir sağlayıcı thread'idir; devam kimliği düşürülmez, reddedilir |
| `SupportsToolAllowlist` | **hayır** | Koşu gerçekten kısıtlı bir yüzeyle çalışır, ama **çağıran bir liste bildirip onun uygulanmasını sağlayamaz** — bu yetenek bunu vaat eder. Adaptörün kendi kümesini sabitlemesi nedeniyle bunu iddia etmek, çağıranın yönüne söylenmiş bir yalan olurdu |
| `SupportsCostCap` | **hayır** | Hub bir koşuyu eşzamanlılık ve üç deadline ile sınırlar; sağlayıcının raporladığı kullanım metadatadır ve buradan bir tavan uygulanamaz |

## Kimlik doğrulama

Bu fazda Hub'a hiçbir kimlik doğrulama mimarisi girmedi. API anahtarı yok, token yok, hesap
kavramı yok, kimlik bilgisi deposu yok, faturalama alanı yok. Oturum, iş istasyonunun kendi
Codex oturumudur ve bant dışıdır; Hub onu ne okur ne değiştirir.

`OPENAI_` adı kod tabanında iki yerde geçer ve ikisi de kullanım değildir: gizli dizi
kataloğunda **redakte edilecek** bir token şekli olarak, ve `CodexInvocationBuilder`
içinde bir çocuk sürecin **asla almayacağı** ortam öneki olarak.

`CodexRuntime` kullanıcının makine yapılandırmasına yazmaz: `~/.codex/config.toml`,
`auth.json`, kurallar, plugin'ler ve MCP yapılandırması dokunulmaz kalır. Her sertleştirme
çağrı başınadır. Hub'ın review'ını güvenli kılmak için bir insanın kendi kurulumunu
düzenlemek, Hub'ın kendi yaşam alanının dışına uzanması olurdu.

## Reddedilen alternatifler

**Minimum sürüm tabanı (`>= 0.130.0`).** Ölçüm bunu çürüttü: 0.149.1 daha yenidir ve review
üretemez. "Daha yeni daha iyidir" bu sağlayıcıda doğru değil.

**PATH'te bulunan Codex'e geri düşüş.** Bu makinede tam olarak reddedilmiş yapıyı çalıştırırdı.
Geri düşüş, bir yapılandırma hatasını sessiz bir doğruluk hatasına çevirir.

**Uyumsuz sürümü sessizce kabul edip review'ı denemek.** Sağlık kontrolünün amacı budur:
kapalı devre ve **belirli** başarısız olmak. "Orada çalıştırılabilir yok", "o farklı bir yapı"
ve "cevap vermedi" üç ayrı sorundur ve üçü ayrı ayrı söylenir.

**Sağlık kontrolünün oturumun kullanılabilirliğini iddia etmesi.** Ölçüldü: yerel oturum kaydı
diskte bir dosyadır ve sağlayıcı, sunucunun çoktan iptal ettiği bir token için de bunu memnuniyetle
raporlar. Yalnızca gerçek bir tur bunu çözer, dolayısıyla sağlık kontrolü bunu iddia etmez.
