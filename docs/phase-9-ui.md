# Faz 9B — Operatör arayüzü kabuğu

Bu belge Faz 9B'nin teslimidir: React/TypeScript operatör arayüzü, **salt okunur**.

Faz 9A sözleşmesi: `docs/phase-9-api.md`.

## 1. Konum ve araç zinciri

`src/CoreX.Hub.Web` — çözümün `src/CoreX.Hub.*` konvansiyonuyla aynı.

| Karar | Seçim | Neden |
|---|---|---|
| Çatı | React 19 + TypeScript 5.9 | İstenen |
| Derleyici | Vite 7 | Mevcut bir frontend yığını yok; en küçük standart kurulum |
| Paket yöneticisi | **npm** | Ortamda zaten kurulu (11.16). pnpm indirme gerektiriyordu, yarn classic. `package-lock.json` commit edildi |
| Durum | **Düz hook'lar** | Aşağıya bakınız |
| Bileşen kütüphanesi | **Yok** | Aşağıya bakınız |
| İkonlar | Yerel SVG seti (6 glif) | Tek aile, bağımlılık yok |
| Test | Vitest + React Testing Library | Vite'ın doğal eşleniği |

**Tek uygulama.** Monorepo yok, workspace yok, storybook yok, design-system paketi yok.

### Neden cache kütüphanesi yok

Bu arayüzün ihtiyacı: rota girişinde getir, canlı akış bir şey değiştiğini söyleyince tazele,
panel başarısız olunca tekrar dene. Bu yaklaşık kırk satır (`src/api/useQuery.ts`). Bunu
almak için gelen bir bağımlılık, beraberinde geçersizleştirme kuralları da getirirdi ve asıl
akıl yürütülecek şey o olurdu. Faz 9C mutasyonlardan sonra ekranlar arası cache tutarlılığı
isterse, yeniden değerlendirme anı orasıdır — öncesi değil.

`useQuery` davranışının tek bariz olmayan yanı kasıtlı: **tazeleme sırasında önceki veri
ekranda kalır**. Akış her tıkladığında boşalan bir execution sayfası kullanılamaz olurdu.

### Neden bileşen kütüphanesi yok

Gereken: rozet, tablo, panel, üç durum. Bunları elde etmek için kurumsal bir çatı almak,
beraberinde bir tema sistemi, bir bundle ve bu ürünün ihtiyacı olmayan bir yerleşim görüşü
getirirdi. Faz 9C diyalog ve menü isterse, yeniden değerlendirme anı orasıdır.

## 2. Barındırma ve geliştirme kökeni

| Ortam | Köken |
|---|---|
| Production | Aynı köken (`/api/v1` göreli) |
| Geliştirme | Vite dev server `127.0.0.1:5178`, `/api` isteklerini `127.0.0.1:5177`'ye **proxy** eder |

Proxy sayesinde geliştirme de **aynı kökendir**. Faz 9A'nın Development CORS desteği
gevşetilmedi *ve kullanılmadı* — ihtiyaç duyulmadı. Vite `127.0.0.1`'e bağlanır, `0.0.0.0`'a
değil.

`VITE_HUB_API` yalnızca ikisini bilerek ayrı çalıştıran biri için vardır. Bir derleme
değişkenidir; çalışma zamanında ağ arayüzü keşfi **yoktur**.

**Production SPA barındırma (API'nin `dist/` sunması) bu dilimde yapılmadı.** Kendi dilimini
hak ediyor: varlık yolu, fallback rota, cache başlıkları ve derleme sırası kararları var.
`dotnet build` ile `npm run build` ayrı komutlar olarak kalıyor; MSBuild'e npm enjekte
edilmedi (guard).

## 3. Komutlar

```
# API (kök dizinde)
dotnet run --project src/CoreX.Hub.Api

# Arayüz (src/CoreX.Hub.Web içinde)
npm install
npm run dev          # 127.0.0.1:5178
npm test             # vitest
npm run typecheck    # tsc --noEmit
npm run build        # tsc -b && vite build
```

Global kurulu Vite veya TypeScript gerekmez; hepsi paket script'leri üzerinden.

## 4. Rotalar

| Rota | Ekran |
|---|---|
| `/` | `/tasks`'a yönlendirir |
| `/tasks` | Ana çalışma ekranı |
| `/tasks/:taskKey` | Görev detayı |
| `/executions/:executionId` | Teşhis ekranı |
| `/repositories` | Repository'ler |
| `/agents` | Agent profilleri |
| `*` | Bulunamadı |

Kenar çubuğu backend'i yansıtmaz: organizasyon ve proje, operatörün *gittiği* yerler değil
*okuduğu* bağlamdır, dolayısıyla ekranlarda breadcrumb olarak görünür, navigasyonda değil.

## 5. API istemci sınırı

`src/api/client.ts` — tek yer. Bileşenlerde `fetch` **yok** (guard).

İçerdiği: base URL, hata zarfı, `AbortSignal`, JSON/metin ayrımı. İçermediği: retry politikası,
interceptor yığını, cache. SDK değil.

Savunmacı sınırlar: JSON olmayan gövde, sunucunun vaat etmediği hata şekli, kopan bağlantı,
iptal edilen istek — her biri tipli bir başarısızlığa dönüşür. Ayrı bir şema kütüphanesi
eklenmedi; TypeScript DTO'ları + bu sınırlar 9B için yeterli.

## 6. Hata zarfı

Sunucunun `{ code, message, correlationId, details? }` şekli tek bir sunum yolundan geçer
(`Failed` bileşeni). Gösterilen: mesaj ve korelasyon kimliği. Gösterilmeyen: stack trace, yol,
ham exception.

Sunucunun şeklinde **olmayan** bir gövde (proxy zaman aşımı, 502) genel bir zarfa çevrilir —
bir hatayı işlerken hata fırlatan bir istemci olmasın diye.

## 7. Canlı durum (SSE)

`src/sse/useExecutionStream.ts` — tek yer, `EventSource` başka hiçbir bileşende yok.

- **Yalnızca terminal olmayan** execution'da bağlanır.
- Akış **otorite değildir**. Hook bir `revision` sayacı verir; sayfa onunla dayanıklı sorguyu
  tazeler. Düşen bir olay bir anlık bayatlık demektir, yanlış ekran değil.
- **Yinelenen render yok**: olaylar `sequence` ile anahtarlanır, çakışan bir reconnect aynı
  olayı ikinci kez çizemez.
- **Reconnect**: tarayıcı kendi kopmalarında `Last-Event-ID`'yi kendisi yollar. Sayfadan
  ayrılıp geri dönme bunu kapsamaz — bu yüzden imleç execution başına modül kapsamında
  hatırlanır ve `?after=` olarak gönderilir. Koşu bitince girdi silinir.
- Bounded: en fazla 200 olay tutulur. Sınırsız bellek içi log yok.
- Unmount **yalnızca istemci akışını** kapatır.

Gösterge üç kelime: **Live / Reconnecting / Offline snapshot**. Socket terminolojisi yok.

**Ek polling yok.** `setInterval` yok. Sunucu zaten dayanıklı durumu kendi temposunda okuyor.

## 8. Durum sunumu

`src/lib/status.ts` — tek tablo. Bileşenlere dağılmış switch **yok**.

Kritik ayrım korunuyor: `ReviewBlocked` (reviewer bir şey buldu), `ReviewUnusable` (kullanılabilir
review elde edilemedi) ve `VerificationFailed` (makine kontrolleri düştü) **üç farklı şeydir** ve
hiçbiri genel "Failed"e indirgenmez.

Renk asla tek kanal değildir: her durum bir **etiket**, bir **ton** ve bir **şekil**
(outline / solid / striped) taşır. Renk körlüğü olan bir operatör etiketi okur; ekran okuyucu
etiketi ve anlamı okur.

## 9. Ekranlar

**Tasks** — tablo. Operatörün geldiği soru ("bekleyen var mı, bir şey düştü mü") satır
açılmadan cevaplanır. Sıralama sunucunundur.

**Task detail** — genel bakış, kapsam (path olarak), kriterler (Machine/Human görsel olarak
ayrı), denemeler, kabul durumu. Ham JSON yok.

**Execution detail** — özet, zaman çizelgesi, doğrulama, bulgular, diff, kanıt. Her panel
**kendi başına** yüklenir: diff düşerse bulgular ayakta kalır.

**Timeline** — kalıcı adımlardan. Uydurma yok: yazılmamış bir adım "pending" olarak
görünmez.

**Findings** — kanonik Hub bulguları. Kapsam dışı olanlar **gizlenmez, işaretlenir**. Sıra:
bloke edenler önce.

**Diff** — basit metin. Monaco yok, editör yok. `+++`/`---` dosya başlığıdır, ekleme/silme
değil (test edildi). Her satır metin olarak render edilir; `dangerouslySetInnerHTML` yok.

**Evidence** — yalnızca metadata. Hiçbir blob otomatik çekilmez. **Ham kanıt görüntüleyici 9B'de
yok**: boyut, kodlama ve metin olmayan içerik kararları kendi adımını hak ediyor.

**Repositories / Agent profiles** — güvenli metadata. Yerel yol yok, executable yok, ortam yok,
kimlik bilgisi yok.

## 10. Sağlayıcı nötrlüğü

Arayüz hiçbir yerde sağlayıcıya göre dallanmaz. `provider` bir hücrede yazılan değerdir.
`ClaudeCodeRuntime` / `CodexRuntime` isimleri arayüzde hiç geçmez. Guard: karşılaştırma
regex'i + tip adı taraması.

## 11. Salt okunurluk

Faz 9B'de mutasyon **yok**: create, ready, run, cancel, accept, override, request-changes,
reject — hiçbiri. Devre dışı "yakında" düğmesi bile yok.

Bu bir vaat değil, kodun bir olgusu: `client.ts` tek istek noktasıdır ve orada GET dışında
bir verb ya da bir mutasyon rotası yoktur (guard + testler).

Sayfalardaki tek düğmeler bir kimliği panoya kopyalar.

## 12. Erişilebilirlik

Semantik navigasyon (`nav` + gerçek `a[href]`), tablo semantiği (`caption`, `th scope`),
görünür odak, ikon-only öğelerde erişilebilir ad, `role="status"` / `role="alert"` ile
duyurulan yükleme ve hata, renge bağlı olmayan durum. Div-button yok.

## 13. Duyarlılık

Masaüstü öncelikli. 900px altında kenar çubuğu ikonlara daralır. Yatay taşma yalnızca kasıtlı
kaydırılabilir alanlarda (tablolar, diff). Mobil optimizasyonu Faz 9 gereksinimi değildir.

## 14. Faz 9C–9E'ye kalanlar

- **9C:** operatör eylemleri — run, cancel; onay akışları.
- **9D:** kabul akışı. Override düğmesi **yalnızca** `overrideApplicable` true iken ve boş
  olmayan gerekçe zorunlu. Sunucu zaten reddediyor; UI dürüst olmalı.
- **9E:** gerçek tarayıcı E2E + yetkili tam sistem regresyonu.
- Ayrıca: production SPA barındırma, ham kanıt görüntüleyici, OpenAPI'den istemci üretimi.
