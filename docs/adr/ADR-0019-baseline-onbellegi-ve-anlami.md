# ADR-0019 — Baseline anahtarı repository + base commit + profil hash'idir; baseline izin değildir

- **Durum:** Accepted
- **Tarih:** 2026-08-23
- **Kaynak:** mimari doküman § I.4; Faz 4 uygulama kapsamı

## Bağlam

Zaten kırık bir repository'de "2 test kırık" ile "2 test kırık" arasındaki farkı görmek
gerekir. Bunun için işin başladığı commit'te aynı profilin ne skor yaptığı bilinmelidir.
Bu bilgi pahalıdır, bu yüzden önbelleklenir — ve tam da bu yüzden yanlış anahtarla
önbelleklenmesi tehlikelidir.

## Karar

1. Önbellek anahtarı **tam olarak** şu üçlüdür: `RepositoryId` + `BaseCommitSha` +
   `ProfileHash`. Başka hiçbir kimlik katılmaz — task ID, deneme sayısı, branch adı ve
   güncel `HEAD` özellikle **dışarıdadır**.
2. `ProfileHash`, profilin anlamını taşıyan alanların deterministik SHA-256'sıdır:
   çalışma dizini, adım sırası, mantıksal çalıştırılabilir adları, argümanlar, timeout,
   `required`, parser ve `resultPath`. Zaman damgası, kimlik ve profil adı hash'e girmez.
3. Önbellek Hub'a ait yerel bir dizindir (`<data>/cache/verification/<anahtar>.json`),
   her worktree'nin dışındadır ve atomik yazılır. Kısmi giriş oluşmaz; bozuk giriş
   **miss** sayılır, asla yanlış baseline olarak sunulmaz.
4. Silme/eviction yoktur; temizlik Faz 10'dur.
5. Baseline **kanıttır, izin değildir.** Karşılaştırma yalnızca `Improved` / `Regressed` /
   `Unchanged` / `NoBaseline` üretir. Güncel zorunlu adım başarısızsa doğrulama
   başarısızdır — baseline daha kötü olsa bile.
6. Doğrulama modülü baseline çalışma ağacını **kendisi oluşturmaz**: git worktree'sini
   bilmez. Baseline'ı üreten geri çağrımı çağıran taraf sağlar; nasıl elde edileceğine
   Faz 5 karar verir.

## Sonuçlar

- Profilin herhangi bir anlamlı alanı değişince önbellek ıskalar; yanlış recete ile
  karşılaştırma yapılamaz.
- Base commit değişince önbellek ıskalar.
- "10 kırıktan 9 kırığa" bir iyileşme olarak raporlanır ama zorunlu adım yine başarısızdır.
