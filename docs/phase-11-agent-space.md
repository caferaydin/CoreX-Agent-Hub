# Faz 11 — Agent space

Hub'ın "görevi ver, sonucu gör" aracından "işleri sıraya koy, ekip yürütsün" çalışma alanına
genişlemesi. Operatörün hedefi (2026-10-22): günlük rutin işleri sürekli verebileceği, ekibin
kendi başına yürüttüğü, takıldığı yerde soran bir yapı. Dilimler:

| Dilim | Kapsam | Durum |
|---|---|---|
| 11A | Kanıtla sınırlı otopilot: sıra + politika kabulü (ADR-0028) | **TAMAM** |
| 11B | Sorular / varsayımlar kutusu (agent'ın son cevabındaki soruların kaydı) | açık |
| 11C | Ekip: isimli roller (lider, geliştirici, test, inceleyici, araştırmacı) | açık |
| 11D | Lider + sohbet: düz cümleden görevlere | açık |
| 11E | Rutinler: zamanlanmış görev şablonları | açık |
| 11F | Kod dışı işler: araştırma / rapor çalışma alanı | açık |
| 11G | Ofis paneli: ekip, sıra, sorular ve "dün ne oldu" tek ekranda | açık |

Pilot proje `talep-takip`'tir (`D:\Private\Cafer AYDIN\CorexLogic\Projeler\Core X Talep Takip`).
Önemli ürün projeleri bu yapıya pilot kendini kanıtlamadan dahil edilmez.

## 1. Otopilot (11A)

Karar: [ADR-0028](adr/ADR-0028-kanitla-sinirli-otopilot.md). Kısaca:

```yaml
repositories:
  - slug: talep-takip
    autopilot:
      runQueue: true          # hiç denenmemiş Ready görevleri sırayla başlatır
      acceptWhenClean: true   # kanıtı her kriteri cevaplayan işi politika kararıyla kabul eder
```

Blok yalnızca yapılandırmadadır, veritabanına yazılmaz ve her geçişte dosyadan yeniden okunur.
Bloğu silmek otopilotu kapatır.

### 1.1 Bir geçiş ne yapar

`hub-api` her 5 saniyede bir geçiş yapar (`AutopilotHost` → `AutopilotCoordinator.TickAsync`).
Otopilotu açık her repository için, sırasıyla:

1. **Kabul.** `AwaitingAcceptance` görevin son denemesi için `AcceptanceService.AcceptByPolicyAsync`
   çağrılır. Kabul edilmezse sebep kaydedilir ve görev insanı bekler.
2. **Hattı durdur.** Aşağıdakilerden biri varsa sıra yeni görev başlatmaz ve durum `Paused`
   olur:
   - insan kararı bekleyen görev (`WaitingForPerson`);
   - son denemesi geçmemiş görev (`AttemptDidNotPass`) — otopilot yeniden denemez;
   - kabul edilmiş ama varsayılan dalda olmayan sonuç (`NotOnDefaultBranch`) — ipucu olarak
     operatörün çalıştıracağı `git merge --ff-only agent/<KEY>/<n>` komutu gösterilir;
   - varsayılan dalda olup olmadığı anlaşılamayan sonuç (`LineageUnknown`);
   - reddedilen son başlatma (`StartRefused`) — bir dakika boyunca tekrar sorulmaz.
3. **Başlat.** Hiçbiri yoksa ve makinede çalışan execution yoksa, en eski hiç denenmemiş
   `Ready` görev operatörün Run'ı ile aynı yoldan başlatılır (`ExecutionLauncher`). Geçiş başına
   en fazla bir başlatma yapılır. `AutopilotRunStarted` audit satırı `hub:autopilot` aktörüyle
   yazılır.

### 1.2 Politika kabulünün koşulları

Normal kabul yolunun tüm kuralları ve ek olarak:

| Koşul | Neden |
|---|---|
| Görevin hiç `Human` kriteri yok | İnsan kriterini insan cevaplar (ADR-0015 #6) |
| Her `Machine` kriteri `Met` | Normal yol isteğe bağlı başarısızlığa izin verir, çünkü bir insan bakıyordur |
| Agent, Verify ve Review adımları `Succeeded` | Reviewer'ı olmayan repo'da Review `Skipped` olarak geçer |
| `ReviewFindingGate` engelleyici bulgu görmüyor | Tek engelleme otoritesine yeniden sorulur |
| Beklenmeyen agent commit'i yok, sonuç commit'i var, `required` doğrulama geçmiş | Override yolunun ön koşullarıyla aynı aile |

Karar satırı `Source = Policy` taşır; arayüzde "Autopilot decision" başlığıyla ve "Autopilot"
rozetiyle gösterilir, bir insan kararı gibi okunmaz.

### 1.3 Yapmadıkları

Ana dala ekleme, yeniden deneme, override, red, değişiklik isteme, iptal, push. Kabul edilen iş
`agent/<KEY>/<n>` dalında kalır; ana dala almak operatörün işidir (operatör kararı,
2026-10-22: "Sadece otomatik kabul").

### 1.4 Görünürlük

- `GET /api/v1/autopilot` — son geçişin raporu. Salt okunurdur; açma, durdurma veya başlatma
  rotası yoktur.
- Arayüzde Tasks sayfasının üstünde **Autopilot** paneli: repository durumu, sıra, dikkat
  gerektirenler ve ipuçları. Otopilotu açık repository yoksa panel hiç görünmez.

### 1.5 Tam otomatik bir görev nasıl yazılır

Yalnızca makine kriteriyle: örneğin `--machine-criterion test="Tüm testler geçer."`. Bir insan
kriteri eklenirse görev otopilot açık olsa bile insanı bekler — bu bilinçli bir seçimdir.

### 1.6 Ölçülen

- Domain: politika kararı yalnızca override'sız, insan cevapsız bir Accept olabilir.
- Application (gerçek git + gerçek scripted agent + gerçek doğrulama): temiz iş kabul edilir;
  insan kriteri, atlanmış review, karşılanmamış isteğe bağlı kriter ve geçmeyen her sonuç
  insana bırakılır; insanın verdiği karar ikinci kez verilmez.
- Api (gerçek yapılandırma dosyası + gerçek veritabanı, başlatma ve git sorusu taklit): en eski
  görev başlar, aynı anda bir tane; temiz iş kabul edilir ve sıra dalı bekler; operatör dala
  aldıktan sonra sıra devam eder; geçmeyen deneme yeniden denenmez; reddedilen başlatma bir
  dakika tekrar sorulmaz.
- Workspace (gerçek git): soy sorusu salt okunurdur; bilinmeyen commit "hayır" değil
  "bilinmiyor"dur; güven kökü dışındaki repository'ye hiç sorulmaz.

Ölçülmeyen: gerçek Claude + Codex ile uçtan uca bir pilot koşusu (operatörün aboneliğini
harcar; operatör onayıyla yapılacak).
