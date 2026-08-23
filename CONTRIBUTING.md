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

## Test disiplini

- Kaynak dizesi taraması davranışsal testin **yerine geçmez**; yanına eklenir.
- Bir sabitin beklenen değeri, üretimdeki sabitten okunarak doğrulanmaz. Üretim ve test
  birlikte kayabilir; mimarinin yazdığı değer teste birebir yazılır.
- Aralıklı (intermittent) bir hata, tek bir yeniden çalıştırma geçtiği için zararsız
  sayılmaz. Önce **tekrar üretilir** (stres döngüsü), sonra kök nedeni bulunur.
