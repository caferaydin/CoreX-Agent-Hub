# Faz 9B–9E — Operatör arayüzü

Faz 9B: React/TypeScript operatör kabuğu, salt okunur.
Faz 9C: ilk durum değiştiren eylemler — **create, ready, run, cancel**.
Faz 9D: insan kararları — **accept, review override, request-changes, reject**.
Faz 9E: **tek origin production barındırma**, **kalıcı karar geri okuması** ve
**gerçek tarayıcı kabulü**.

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
akıl yürütülecek şey o olurdu. Faz 9C ve 9D mutasyonları bunu değiştirmedi: her karardan sonra
sayfa görevi yeniden çeker ve dayanıklı cevap sunucunundur. Ekranlar arası cache tutarlılığı
gerçekten gerektiğinde yeniden değerlendirilir — öncesi değil.

`useQuery` davranışının tek bariz olmayan yanı kasıtlı: **tazeleme sırasında önceki veri
ekranda kalır**. Akış her tıkladığında boşalan bir execution sayfası kullanılamaz olurdu.

### Neden bileşen kütüphanesi yok

Gereken: rozet, tablo, panel, üç durum. Bunları elde etmek için kurumsal bir çatı almak,
beraberinde bir tema sistemi, bir bundle ve bu ürünün ihtiyacı olmayan bir yerleşim görüşü
getirirdi. Faz 9C ve 9D'nin ihtiyaç duyduğu tek şey bir diyalogdu ve o da gerçek bir
`<dialog>` ile karşılandı: modal semantiği, Escape, odak hapsi ve backdrop tarayıcıdan
geliyor, ve bunların elle yazılan her sürümü bir klavye tuzağı adayı.

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
# Tek surec, tek origin (kok dizinde) - once UI derlenir, sonra publish edilir
cd src/CoreX.Hub.Web && npm ci && npm run build && cd ../..
dotnet publish src/CoreX.Hub.Api -c Release -o .artifacts/hub
.artifacts/hub/hub-api.exe          # http://127.0.0.1:5177 - SPA + API

# Yalnizca API (gelistirme)
dotnet run --project src/CoreX.Hub.Api

# Arayüz (src/CoreX.Hub.Web içinde)
npm install
npm run dev          # 127.0.0.1:5178
npm test             # vitest
npm run typecheck    # tsc --noEmit, her iki proje için
npm run build        # tsc -b && vite build

# Gerçek tarayıcı kabulü (Faz 9E)
npm run e2e:prepare  # UI derle + API publish (wwwroot dahil) + fixture derle
npm run e2e          # dogrulama Hub'ini sifirla, sonra playwright test
```

`npm run e2e:prepare` uc sey uretir ve sirasi onemlidir:

1. `dist/` — production bundle.
2. `e2e/.artifacts/host/` — `dotnet publish` ciktisi; `wwwroot/` icinde (1) vardir.
3. `corex-fixture.exe` — deterministik agent ve dogrulama araci.

`dotnet build` **asla** npm calistirmaz. Iki derleme yalnizca `dotnet publish` adiminda
bulusur; dist yoksa publish sessizce API-only bir host uretmek yerine **hata verir**.

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

Durum değiştiren yüzey **tam sekiz çağrıdır** — dördü yaşam döngüsü, dördü yönetişim — ve her
biri yaptığı işlemin adını taşır. Genel bir `transition(path, body)` **yoktur ve olmayacaktır**:
accept, override, request-changes ve reject dört farklı karardır ve fiili parametre olarak alan
bir fonksiyon onları her çağırana birbirinin yerine geçebilirmiş gibi gösterirdi (guard).

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

## 11. Operatör eylemleri (Faz 9C)

Dört eylem, dördü de mevcut Faz 9A uçlarını çağırır.

| Eylem | Uç | Yanıt |
|---|---|---|
| Create | `POST /tasks` | 201 `TaskSummaryDto` |
| Mark ready | `POST /tasks/{k}/ready` | 200 `{transitioned, task, readyGateSatisfied, readyGateProblems}` |
| Run | `POST /tasks/{k}/runs` | **202** `{executionId, taskKey, attemptNo, eventsUrl}` |
| Cancel | `POST /tasks/{k}/cancel` | 200 `{state, taskState, detail, …}` / 409 |

### Sunucu otoritedir

Bir 409 çözülecek bir frontend hatası **değildir**. Sunucunun mesajı ve korelasyon kimliği
olduğu gibi gösterilir.

`src/lib/actions.ts` yalnızca **hangi düğmenin görüneceğine** karar verir ve bunu tek bir
değere — görevin durumuna — bakarak yapar. Ready gate'i, deneme geçmişini, doğrulama kanıtını
okumaz. Bunun tersi tehlikeli olanı: gerçek koşulları yeniden üreten bir frontend, state
machine'in ikinci bir kopyası olurdu ve ikisi ayrıştığı gün UI kendinden emin biçimde yanlış
olurdu. Bayat bir sayfadan gelen her eylem test edilir; operatörün gördüğü şey sunucunun
reddidir.

### Create

`/tasks/new` — kendi rotası, çünkü depo + amaç + kapsam + kriterler bir diyaloğa sığmaz.

Sorulmayanlar kasıtlıdır: runtime yok, permission profile yok, sandbox yok, model yok, reviewer
yok. Operatör bir iş tanımlar. **Hangi agent'ın çalıştıracağı da burada sorulmaz** — create
sözleşmesi bunu almaz; o seçim çalıştırma anındadır.

Doğrulama yalnızca ergonomiktir (depo seçilmemiş, amaç boş, anahtar biçimi). Anahtarın boşta
olup olmadığı Hub'ın cevabıdır.

Reddedilirse: **form olduğu gibi kalır**. Başarılıysa Hub'ın atadığı anahtara gidilir — yazılan
anahtara değil — ve detay sayfası taze çeker. Yeni görev yerelde kurulmaz.

Create **bir kez** gönderilir: başarılı bir create'in tekrarı ikinci bir görev demektir.

### Ready

Yalnızca `Draft` için görünür. 200 ama `transitioned: false` bir hata değildir — kapı cevap
vermiştir ve neyi beklediği zaten sayfada yazılıdır.

### Run

**202 ile döner ve gitmez.** React hiçbir yerde sağlayıcının bitmesini beklemez; başarıda
`/executions/{id}`'ye gidilir ve oradaki mevcut REST + SSE modeli koşuyu izler. Ayrı bir
"çalışan görev" frontend state machine'i yoktur.

Implementer profili `role === 'Implementer'` ile süzülür — sağlayıcı adına göre değil. Bir
reviewer profili asla implementer olarak sunulmaz.

Run da **bir kez** gönderilir.

### Cancel — istendi ≠ oldu

Onay diyaloğu gerekir ve ne vaat edebileceği konusunda dürüsttür: koşan süreçten durması
*istenir*, sonra durduğu *beklenir*, ve bu garanti değildir.

| `state` | UI |
|---|---|
| `Cancelled` | "Cancelled." |
| `CancellationRequested` | "Cancellation requested — hâlâ Running." **Terminal olarak gösterilmez.** |
| 409 | Sunucunun reddi |

Yerel olarak hiçbir zaman `Cancelled` yazılmaz. Terminal bir sonuç asla üzerine yazılmaz.

Cancel bir mutasyondur; **SSE'ye dokunmaz**. Execution sayfası kendi akışını koşu dayanıklı
olarak terminal olana kadar açık tutar. İptale özel EventSource yoktur.

### Actor

`hub:operator:ui`, tek yerde sabit (`src/lib/actor.ts`). Hub tek kullanıcılı yerel bir araçtır;
oturum açma **eklenmedi** ve bir string'i doldurmak için eklenmeyecek.

## 12. İnsan kararları (Faz 9D)

Dört karar, dört ayrı uç. Hiçbiri Faz 9A/5D sunucu davranışını değiştirmez; bu dilim yalnızca
mevcut uçları operatörün önüne koyar.

| Karar | Uç | Gövde | Yanıt |
|---|---|---|---|
| Accept | `POST /tasks/{k}/attempts/{n}/accept` | `{actor, humanVerdicts[]}` | 200 `AcceptanceResultDto` |
| Review override | `POST …/accept-override` | `{actor, reason, humanVerdicts[]}` | 200 |
| Request changes | `POST …/request-changes` | `{actor, reason?}` | 200 |
| Reject | `POST …/reject` | `{actor, reason?}` | 200 |

Reddetme: 409 `acceptance_refused`, 400 `invalid_request`. Her ikisi de sunucunun kendi
cümlesiyle ve korelasyon kimliğiyle gösterilir.

### Tek karar yüzeyi

Tümü **Task detail** üzerindeki tek bir "Human decision" panelindedir. Görev listesine,
execution listesine, bulgu satırına veya kenar çubuğuna dağıtılmadı — kararın bağlamı burada.

Panel, herhangi bir şeye tıklanmadan **önce** şunları gösterir: görev durumu, son deneme
numarası, son execution sonucu, verdict bekleyen Human kriter sayısı ve — override söz konusu
ise — review'ın kendi özeti ile kapsam içi bulgular.

Yaşam döngüsü paneli (`TaskActions`) ve yönetişim paneli (`TaskGovernance`) ayrı bileşenlerdir,
ve kabul durumunun salt-okunur projeksiyonu (`Acceptance`) üçüncü bir yerde kalır. Bir görev
projeksiyonu ile bir yönetişim formunu tek bileşende birleştirmek, ikisinin de gözden
geçirilemez hale gelmesi demekti.

### Makine gerçeği okunur, insan gerçeği verilir

Bu panelin var olma sebebi olan sınır. Doğrulama verdict'leri, bulgular, severity'ler ve
Hub'ın kendi kapsam hesabı **gösterilir ve dokunulamaz**. Operatörün eklediği tek şey Human
kriterlerin verdict'leri ve — override için — gerekçedir.

Yok: bulgu kapatma, severity değiştirme, false-positive işaretleme, gerekçe düzenleme,
`outOfScope` çevirme. Guard, `MachineCriteria` ve `Findings` bölümlerinde tek bir kontrol
olmadığını doğrular.

### Verdict'ler öntanımlı değildir

Her Human kriter **cevapsız** başlar. Radyo grubu, çünkü üç cevap eşittir ve hiçbiri bir
menünün arkasında olmamalıdır. Hepsi cevaplanmadan form sormaz.

Öndoldurulmuş bir "Met", operatör adına karar verip sonra o kararı operatörün adına yazardı.

### Override — ayrı yol, ayrı etiket

Etiket **"Accept with review override"**; hiçbir zaman sadece "Accept" değildir. Yalnızca
görev `Ready` **ve** son execution sonucu `ReviewBlocked` iken sunulur.

Neyin override edildiği **açık halde** gösterilir, bir disclosure arkasında değil: kapsam içi
bulgular severity sırasıyla ve review'ın kendi özet cümlesiyle birlikte. Gerekçe zorunludur,
boş başlar ve **şablonu yoktur** — hazır bir "false positive", Hub'ın hiç yazmamış birine mal
edeceği bir gerekçe olurdu. Yalnızca boşluktan oluşan bir gerekçe gerekçe değildir.

Başarıdan sonra görev **Accepted** olur, execution **ReviewBlocked kalır**. Bu iki eksen ayrı
tutulur; UI hiçbir yerde execution'ı `Passed` göstermez. Onay diyaloğu bunu açıkça söyler.

Reddedilirse yazılan hiçbir şey silinmez: gerekçe ve verdict'ler formda kalır.

### Sunucunun altı ön koşulu React'te yoktur

`src/lib/actions.ts` yalnızca **hangi düğmenin görüneceğine** karar verir. `canOfferOverride`
iki değere bakar — görev durumu ve son execution sonucu — çünkü bloke bir review görevi
`AwaitingAcceptance` değil `Ready` bırakır ve tek başına durum bu durumu hiç çalışmamış bir
görevden ayırt edemez.

Bunun ötesindeki her şey `AcceptanceService`'in cevabıdır: makine gerçeği kurulabildi mi,
karar zaten var mı, execution en sonuncusu mu. Guard, kural dosyasının imzalarını okur ve
`TaskState` ile `ExecutionOutcome` dışında bir tip almadığını doğrular.

### Onaylar ve tekrar

Dört kararın dördü de onay ister ve her biri **ne yapmadığını** söyler: bir karar kaydedilir,
hiçbir şey merge/push/deploy edilmez. Request-changes denemenin yerinde kaldığını ve yeni bir
denemenin **yanına** açılacağını; reject hiçbir şeyin silinmediğini söyler.

Yönetişim mutasyonları **asla otomatik tekrarlanmaz** (guard: `retry`/`setTimeout` yok) ve bir
karar uçarken diğer üçü kullanılamaz. Bu bir nezakettir; garanti sunucunun ikinci kararı
reddetmesidir ve o da test edilir.

Her karardan sonra — kaydedilmiş ya da reddedilmiş — sayfa görevi **yeniden çeker**. Yerel
olarak hiçbir durum yazılmaz.

Reject **cancel çağırarak yapılmaz**; kendi ucu vardır ve test bunu URL üzerinden doğrular.

### Karar verildikten sonra

Terminal bir görevde panel salt-okunur bir kayda dönüşür: tek bir düğme, tek bir radyo
kalmaz. İkinci bir Accept yoktur, bir Reject'i geri alma yolu yoktur.

### Kapatıldı (Faz 9E): kayıtlı kararın ayrıntısı

`AcceptanceStateDto.existingDecision` yalnızca kararın **türünü** taşır — actor, zaman damgası,
`overrideUsed`, gerekçe ve kaydedilen verdict'ler okuma sözleşmesinde yoktur. Bu yüzden
gerekçe ve değişmemiş execution sonucu yalnızca **karar anında**, mutasyonun kendi yanıtından
gösterilir; sayfa yenilenince kaybolur.

Faz 9E bu sözleşmeyi genişletti; §15'e bakınız. Yukarıdaki paragraf Faz 9D'nin durumunu
anlatır ve tarih olarak bırakılmıştır.

## 13. Faz 9B–9C'den taşınanlar

Salt-okunur panellerin hiçbiri gerilemedi: kapsam, kriterler, denemeler, zaman çizelgesi,
bulgular, diff, kanıt ve kabul projeksiyonu Faz 9B'deki gibi çalışır.

Faz 9B'nin "GET dışında verb yok" guard'ı Faz 9C'de dörtlü bir listeye, Faz 9D'de sekizli bir
listeye dönüştü. Özellik aynı kaldı: **arayüz tam olarak bu sekiz şeyi değiştirir, başkasını
değil** — ve bu bir vaat değil, tek istek noktası üzerinden okunabilen bir olgudur.

## 14. Production barındırma (Faz 9E)

Tek süreç, tek origin, tek port. Vite production'da **yok**.

```
http://127.0.0.1:5177/            -> SPA
http://127.0.0.1:5177/tasks/...   -> SPA (fallback)
http://127.0.0.1:5177/api/v1/...  -> API
```

`src/CoreX.Hub.Api/SpaHosting.cs` — tek yer. Üç kural, hepsi test edilmiştir.

**Bir API yolu asla sayfaya dönüşmez.** `/api` ile başlayan istek bu dalı hiç görmez;
haritalanmamış bir uç sıradan bir 404 ile cevap verir. JSON isteyip HTML alan bir istemci
belgeyi ayrıştırır, kendi içinde bir yerde patlar ve gerçek sorunla ilgisi olmayan bir şey
raporlar. `MapFallbackToFile` **kullanılmaz**: onun kuralına göre uzantısız her yol bir
"sayfa"dır ve bilinmeyen bir API yolu da uzantısızdır.

**Eksik bir dosya da sayfaya dönüşmez.** Son segmentinde nokta olan her istek ya static
file middleware tarafından cevaplanır ya da hiç. Eksik bir script yerine index.html vermek,
tarayıcının HTML'i JavaScript olarak çalıştırdığı ve asıl hatanın (bayat derleme, yanlış
publish) görünmez kaldığı senaryodur.

**Geri kalan her şey bir tarayıcı rotasıdır.** `/tasks/CXH-001` doğrudan açılabilir ve
yenilenebilir.

### Cache

| Ne | Politika | Neden |
|---|---|---|
| Hash'li asset (`.js`/`.css`/`.woff2`) | `public, max-age=31536000` | Vite içerik hash'i yazar: değişen dosya farklı URL'dir |
| `index.html` ve her tarayıcı rotası | `no-cache` | Güncel bundle'ları isimlendiren tek dosya |

CDN stratejisi yok. Yerel uygulama.

### Bağlanma ve CORS

Tek origin **uzaktan barındırma değildir**. `HubApiOptions.Bind()` hâlâ loopback dışını
reddeder; `COREX_HUB_LISTEN_URL` yalnızca **port** seçtirir ve o da aynı kontrolden geçer.
Aynı origin production'da CORS gereksinimi bırakmadığından mevcut politika gevşetilmedi.

### Paketleme

`dotnet publish`, `ComputeFilesToPublish` öncesinde `../CoreX.Hub.Web/dist` içeriğini
`wwwroot/` altına koyar. `dist` yoksa publish **hata verir**. `-p:PublishOperatorUi=false`
ile bilerek API-only publish edilebilir. `node_modules` ve `dist` commit **edilmez**.

Host UI'yi şu sırayla arar: açık seçenek → `COREX_HUB_WEB_ROOT` → yanındaki `wwwroot/`.
Hiçbiri yoksa **yalnızca API** sunar; bu desteklenen bir yapılandırmadır ve tüm API
testleri o şekilde koşar.

## 15. Kararın kalıcı geri okuması (Faz 9E)

Faz 9D'nin bilinen eksiği kapatıldı. `AcceptanceStateDto.existingDecision` artık kararın
**türünü değil, satırın kendisini** taşır:

```
existingDecision: {
  decision, actor, decidedAtUtc, overrideUsed, reason,
  humanVerdicts: [{ criterionId, verdict, note }]
}
```

Hiçbir alan türetilmedi: hepsi `AcceptanceDecisions` tablosunda zaten yazılıydı.
**Migration yok**, şema değişmedi, `AcceptanceService` semantiği değişmedi — değişen tek
satır `PreviewAsync`'in yalnızca `.Decision` alıp gerisini atmasıydı.

Execution sonucu bu DTO'da **bilerek yoktur**. Kabul, execution'ın ne olduğunu yeniden
yazmaz; override edilmiş bir execution hâlâ `ReviewBlocked`'dır ve bu execution'dan
okunur. Bir kararın outcome taşıması, istemciyi ikisini tek olgu gibi göstermeye davet
ederdi.

Arayüz tarafında: yenilemeden sonra panel karar / actor / zaman / override / gerekçe ve
kaydedilen verdict'leri **REST'ten** gösterir. Mutasyon yanıtından gösterilen bildirim bir
nezakettir, kayıt değildir.

## 16. Gerçek tarayıcı kabulü (Faz 9E)

Playwright + Chromium. `src/CoreX.Hub.Web/e2e/`.

Neye karşı koşar: **publish edilmiş** host, **derlenmiş** bundle, tek loopback origin,
gerçek SQLite, gerçek git deposu, gerçek `ProcessRunner`. jsdom değil, Vite dev server
değil, mock değil.

### Doğrulama ortamı

`e2e/validation-hub.mjs` — tek kullanımlık, her koşuda sıfırlanan bir Hub kurulumu: kendi
data dizini, kendi `hub.config.yaml`, kendi git deposu. Operatörün gerçek Hub'ına ve
makinesine hiçbir şey karışmaz. `tests/CoreX.Hub.LoopZero.Tests/LoopZeroHub.cs`
konvansiyonunun aynısıdır: aynı config dosyası, aynı scripted runtime anahtarı, aynı
fixture binary'si.

Tohumlanan tek şey **yapılandırmadır** — operatörün zaten yazmış olacağı şey. Hiçbir test
veritabanına Task, Execution, doğrulama sonucu ya da karar yazmaz; hepsi uygulamanın
kendisi tarafından üretilir (guard).

İki scripted runtime: `apply-patch` (sıradan koşu) ve `hang` (iptal edilecek bir şey
olması için). İkisi de config dosyasında bir bildirimdir; Hub hangisinin "test" olduğunu
bilmez. Repository **reviewer bildirmez**, dolayısıyla temiz koşu `Passed` biter.

### Kanıtlanan senaryolar

| Senaryo | Ne kanıtlar |
|---|---|
| Temiz yaşam döngüsü | create → ready → run → canlı ilerleme → Passed → verdict → Accept, tamamı UI'dan |
| Temiz yenileme | Accepted + Passed + karar/actor/zaman/verdict, sunucudan |
| Canlı akış | Gerçek `EventSource`, yenileme olmadan güncelleme, sayfadan ayrılmak koşuyu durdurmaz |
| Request changes | AwaitingAcceptance → Ready, deneme yerinde kalır, kendiliğinden yeni koşu başlamaz |
| Reject | Kendi ucundan (`/reject`), `/cancel` değil; geçmiş durur |
| Cancel | Running → iptal isteği → Hub'ın verdiği cevap neyse o; `CancellationRequested` terminal gösterilmez |
| Override görünürlüğü | `Passed` üzerinde override **yok**, sıradan Accept var; hiç koşmamış görevde hiçbiri yok |
| Production host | Kök, doğrudan rota, yenileme, bilinmeyen API 404'ü, eksik asset 404'ü, cache başlıkları |

Ekran görüntüleri `e2e/.artifacts/screenshots/` altındadır ve **commit edilmez**.

### Kapsanmayan: tarayıcıda ReviewBlocked override

Deterministik bir `ReviewBlocked` **üretilemedi**, ve nedeni yapısaldır:

`ExecutionComposition`, reviewer runtime'ından `SupportsReadOnlyMode` **ve**
`SupportsStructuredSchema` bildirmesini ister (ADR-0008). `ScriptedAgentRuntime` ikisini de
`false` bildirir — ve bu doğrudur, çünkü şemayı okumaz. Yani scripted runtime bir reviewer
olarak **composition anında reddedilir**.

Bunu aşmanın tek yolu `src/CoreX.Hub.Agents` içinde üretim kodu değiştirmek (yeni bir
scripted mod + yetenek bildirimi) ya da ücretli gerçek reviewer'ı (Codex 0.130.0) canlı
çağırmaktır. Ikisi de bu dilimin sınırları dışındadır.

Override yolu **kanıtsız değildir**, yalnızca tarayıcıda kanıtlanmamıştır: API seviyesinde
(`MutationEndpointTests`, `DecisionReadbackTests`) ve bileşen seviyesinde
(`TaskGovernance.test.tsx`) tam olarak kanıtlıdır.

## 17. Erişilebilirlik

Semantik navigasyon (`nav` + gerçek `a[href]`), tablo semantiği (`caption`, `th scope`),
görünür odak, ikon-only öğelerde erişilebilir ad, `role="status"` / `role="alert"` ile
duyurulan yükleme ve hata, renge bağlı olmayan durum. Div-button yok.

## 18. Duyarlılık

Masaüstü öncelikli. 900px altında kenar çubuğu ikonlara daralır. Yatay taşma yalnızca kasıtlı
kaydırılabilir alanlarda (tablolar, diff). Mobil optimizasyonu Faz 9 gereksinimi değildir.

## 19. Kalanlar

- Tarayıcıda ReviewBlocked override (yukarıdaki yapısal neden).
- Ham kanıt görüntüleyici.
- OpenAPI'den istemci üretimi.
