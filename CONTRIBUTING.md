# Katkı ve çalışma kuralları

Bu dosya mimari karar içermez. Mimari otorite
`docs/architecture/CoreX-Agent-Hub-Mimari-Plan-v1.1.1.md` ve `docs/adr/`'dir; buradaki
maddeler yalnızca **iş akışı güvenlik önlemleridir**.

## Beklenmeyen kirli çalışma ağacı

Sınırlı bir uygulama veya denetim görevi, **beklenmeyen** biçimde kirli bir çalışma
ağacıyla başlıyorsa — yani istenen işten önce zaten var olan değişiklikler bulunuyorsa:

1. **Farkı raporla.** Hangi dosyanın nasıl değiştiğini, görev başlamadan önce açıkça
   bildir. Sessizce devam etme.
2. **Sessizce geri alma.** Değişiklik ilgisiz görünse bile `checkout`/`reset` ile
   silinmez; başkasının yarım kalmış işi olabilir.
3. **Sessizce dahil etme.** İstenen commit'e iliştirilmez. Bir denetim commit'inin içinde
   ilgisiz bir üretim değişikliği taşımak, o değişikliği görünmez biçimde onaylatır.
4. **İstenen değişiklikleri ayrı tut.** Git buna izin verdiği ölçüde (yol bazlı
   `git add`, ayrı commit) istenen iş kendi commit'inde kalır.
5. **Ayrıştırma güvenli biçimde yapılamıyorsa BLOCKED dön.** Örneğin kirli değişiklik
   istenen işin derlenmesi için gerekiyorsa veya aynı satırlara dokunuyorsa, karar
   isteyene aittir.

**İlgisiz bir kirli değişikliğin bilinçli olduğu varsayılmaz.** Faz 4 kabul denetiminde
`GitEnvironment.HubCommitterEmail` tam olarak böyle bir yoldan (`agent@corex.local` →
`info@corexlogic.com`) ilgisiz bir commit'e girdi ve mimarinin sabitlediği makine
kimliğini bozdu; kural bu olaydan sonra yazıldı.

## Commit disiplini

- Bir görev = bir yerel commit. `--amend` yok, `push` yok, `merge` yok, history rewrite
  yok — aksi açıkça istenmedikçe.
- Commit mesajı ne yapıldığını **ve neden** yapıldığını anlatır; kırılan bir değişmez
  varsa adıyla anılır.

## Test çalıştırma politikası (Faz 5'ten itibaren)

Her sınırlı dilimden sonra tüm çözümü koşturmak yok. **Etki tabanlı** test edilir: neyin
değiştiği, hangi test kümesini gerektirir.

| Değişen | Koşulacak | Koşulmayacak |
|---|---|---|
| **Tek modül, yerel değişiklik** | o modülün test assembly'si + `ArchitectureTests` | Execution / Workspace / Verification testleri (üretim kodu veya sözleşmesi elleniyorsa hariç) |
| **Paylaşılan sözleşme** | üreten modül + **doğrudan** etkilenen tüketiciler + `ArchitectureTests` | gerçek bağımlılık etkisinin dışı |
| **`ProcessRunner` / `FileSystemGuard`** | `Execution.Tests` + doğrudan etkilenen integration + `ArchitectureTests` | pahalı süreç-ağacı ve junction senaryoları — yalnızca o sınır gerçekten değiştiyse ya da faz kapanışında |
| **Workspace / git üretim kodu** | `Workspace.Tests` + `ArchitectureTests` | 50 iterasyonluk eşzamanlılık stresi (aşağıya bak) |
| **Verification / Evidence** | `Verification.Tests` + ilgili integration + `ArchitectureTests` | Workspace/Execution (sözleşmeleri değişmedikçe) |
| **Domain / config / persistence / EF modeli** | `Domain.Tests` + `Integration.Tests` + `ArchitectureTests` | — |
| **Yalnızca test veya doküman** | değişen test assembly'si (+ guard değiştiyse `ArchitectureTests`) | tam çözüm koşusu **gerekmez** |

Derleme, güvenilir olduğu yerde etkilenen projelerle sınırlanabilir.

**Eşzamanlılık stres süiti** (`RepositoryConcurrencyStressTests`, 50 iterasyon) yalnızca şu
durumlarda zorunludur: kilitleme/eşzamanlılık kodu değiştiyse · repository metadata yaşam
döngüsü değiştiyse · bilinen bir race araştırılıyorsa · faz kapanış regresyonu yapılıyorsa.
İlgisiz değişiklikler için koşulmaz.

`dotnet ef migrations has-pending-model-changes` **yalnızca** EF/domain persistence modeli
makul biçimde değişmiş olabilecekse koşulur. Doküman veya yalnız-mimari-testi
değişikliklerinde koşulmaz.

### Faz kapanışı (tek yetkili tam regresyon)

Büyük bir fazın sonunda **bir kez**:

```
dotnet restore CoreX.Hub.slnx
dotnet build   CoreX.Hub.slnx -c Release --no-restore
dotnet test    CoreX.Hub.slnx -c Release --no-build
```
Beklenen: 0 failed · 0 skipped · 0 warning.

### Güvenlik istisnası

Faz kapanışından önce de tam regresyon koşulabilir — ama **refleksle değil**, gerekçesi
yazılarak. Geçerli gerekçeler kesişen bir güvenlik sınırının değişmesidir: `ProcessRunner` ·
`FileSystemGuard` · Workspace git sertleştirmesi · kanıt redaksiyonu · persistence işlem
semantiği · Execution durum makinesi · Acceptance durum makinesi · birden çok modülü
etkileyen paylaşılan runtime sözleşmesi.

### Raporlama zorunluluğu

Her uygulama raporu şunları yazar:

```
TEST SCOPE:           Focused | Expanded | Full
WHY:                  hangi değişen üretim sınırı bu kapsamı gerektirdi
TESTS RUN:            hangi assembly'ler / hangi odaklı senaryolar
DEFERRED REGRESSION:  bilinçli olarak koşulmayanlar ve neden etkilenmedikleri
```

Bağımlılık yüzeyine dokunulmadıysa **"koşulmadı" bir eksiklik değildir**; öyle sunulmaz.

## Test disiplini

- Kaynak dizesi taraması davranışsal testin **yerine geçmez**; yanına eklenir.
- Bir sabitin beklenen değeri, üretimdeki sabitten okunarak doğrulanmaz. Üretim ve test
  birlikte kayabilir; mimarinin yazdığı değer teste birebir yazılır.
- Aralıklı (intermittent) bir hata, tek bir yeniden çalıştırma geçtiği için zararsız
  sayılmaz. Önce **tekrar üretilir** (stres döngüsü), sonra kök nedeni bulunur.
