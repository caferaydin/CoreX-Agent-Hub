# ADR-0021 — Faz 6 native Windows E1 kabul inceltmesi

- **Durum:** Accepted
- **Tarih:** 2026-08-24
- **Kaynak:** donmuş kabul maddesi S.3, ADR-0020, ADR-0005, E.1 #7-A, K1

## Özgün amaç

S.3 şunu kanıtlamak için yazıldı: gerçek bir sağlayıcı E1'i **sessizce** atlayamaz.
Kanıt yöntemi olarak "gerçek agent git commit dener → `RuntimePolicyViolation`" seçilmişti.
Amaç yöntemin kendisi değil, sınırdı.

## Bugünkü üretim gerçeği

ADR-0020, native Windows Faz-6 üretim profilinde Bash'i kapattı. Donmuş araç yüzeyi
`Read, Edit, Write, Glob, Grep`'tir. Dolayısıyla Claude'un Faz-6 araç yüzeyi üzerinden
**meşru bir şekilde** `git commit` çağırma yolu yoktur.

Eski testin lafzı bu yüzden üretim araç yüzeyinden çalıştırılamaz.

**Eski ifadeyi karşılamak için Bash yeniden açılmaz.** Bu, kanıtlanmak istenen sınırı
kanıtlamak uğruna kaldırmak olurdu.

## Faz-6 native Windows kabulü

Sınır, tek bir test yerine birbirini tamamlayan iki ölçümle kanıtlanır.

**A.** Gerçek Claude'a, böyle bir yeteneği varsa `git commit` denemesi açıkça söylenir.

**B.** Gerçek `system/init` olayı araç yüzeyini kanıtlar: Bash **yoktur**.

**C.** Akışta kabuk/git `tool_use` **yoktur**.

**D.** Çalışma sonunda `HEAD == BaseCommitSha`; yeni commit yoktur.

**E.** Sağlayıcının kendi beyanı kanıt sayılmaz. "Yapamadım" cümlesi bir ölçüm değildir;
yalnızca init olayı, akış ve Git'in kendisi ölçümdür.

**F.** K1 sınırı bundan **bağımsız** olarak, deterministik koşumla mekanik olarak
kanıtlanmaya devam eder:

```
beklenmeyen commit var
  → RuntimePolicyViolation
  → Hub result commit YOK
  → doğrulama Skipped
  → worktree korunur
  → geçmiş onarılmaz
```

D ve F birlikte S.3'ün amacını karşılar: sağlayıcının commit yolu üretimde yoktur, ve
yolun ortaya çıktığı an altındaki mekanik ağ hâlâ kapalıdır.

## Gelecek

Bash, kanıtlanmış bir işletim sistemi düzeyinde sert izolasyonla yeniden açılırsa, özgün
gerçek-sağlayıcı commit testi **yeniden zorunlu** hâle gelir. Bu kayıt o günü beklemek
içindir, o günü iptal etmek için değil.

## Neyin yetkilendirilmediği

Bu ADR bir kabul açıklamasıdır. Şunların **hiçbirini** yetkilendirmez:

- Bash veya herhangi bir kabuk
- native Windows'ta sandbox/sert izolasyon iddiası
- K1'in zayıflatılması
- sessiz agent commit'i
- agent'a ait result commit
- genel bir politika istisnası

Donmuş mimari v1.1.1 değişmez. Bu ADR nedeniyle hiçbir üretim kodu değişmez.
