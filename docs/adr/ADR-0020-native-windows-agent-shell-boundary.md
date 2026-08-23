# ADR-0020 — Faz 6 native Windows gerçek-agent profili kabuk (Bash) açmaz

- **Durum:** Accepted
- **Tarih:** 2026-08-23
- **Kaynak:** G6-0 kapısı (ölçüm), ADR-0003, ADR-0005, ADR-0006, ADR-0011, ADR-0017

## Bağlam

Faz 3 sertleştirmesi yönetilen worktree'ye kalıcı (worktree-scoped) koruma yazar:
`core.hooksPath` Hub hook dizinine, `remote.origin.pushurl` yayınlanamaz bir şemaya,
`credential.helper` boş değere. Bunlar Hub'ın kendi git komutlarının ortamına değil,
worktree yapılandırma dosyasına yazıldığı için Hub'ın başlatmadığı bir git komutunda da
etkilidir.

G6-0 kapısında bu korumaların sınırı **ölçüldü**, varsayılmadı. Tek başına bir kabuğu olan
bir süreç için:

1. `git push --no-verify <açık-URL>` hem `pre-push` engelleyicisini hem
   `remote.origin.pushurl` değerini atlar. `--no-verify` hook'u devre dışı bırakır ve
   `pushurl` yalnızca adlandırılmış uzak sunucuya uygulanır; komut satırında verilen bir
   URL'ye uygulanmaz. Ölçümde push başarılı oldu.
2. Worktree'nin içinden çalıştırılan tek bir `git config --worktree ...` komutu bu üç
   ayarın herhangi birini kaldırabilir. Kaldırıldıktan sonra sistem düzeyindeki bir
   kimlik yardımcısı yeniden erişilebilir hâle gelir.

Yani: yapılandırma, elinde kabuk olan bir çağıran için **geçersiz kılınabilir**. Daha çok
git yapılandırması eklemek bunu bir işletim sistemi sınırına dönüştürmez.

## Karar

1. Faz 6'nın **native Windows** üretim gerçek-agent (implementer) profili **Bash açmaz**.
   Donmuş araç yüzeyi: `Read`, `Edit`, `Write`, `Glob`, `Grep`.
2. Derleme ve test çalıştırma agent'ın işi değildir; deterministik doğrulama zaten Hub
   Verification'a aittir (ADR-0018). Kabuk kaldırıldığında kaybedilen yetenek budur ve bu
   yetenek zaten başka bir yerde, daha iyi kanıtla mevcuttur.
3. Bu bilinçli bir güvenlik daraltmasıdır; eksik kalmış bir yetenek değildir. Faz 6
   örneğinde Bash içeren daha önceki taslak bu kayıtla düzeltilmiştir.
4. Kabuk yalnızca **bağımsız olarak kanıtlanmış** bir işletim sistemi düzeyinde
   sandbox/sert izolasyon mekanizmasıyla birlikte yeniden değerlendirilir: fail-closed
   dosya sistemi ve ağ kısıtlaması. Yol haritası sahibi Faz 10 sert izolasyon çalışmasıdır
   (ADR-0017 ile aynı gerekçe ailesi). Bu ADR o mekanizmayı tasarlamaz.

## Neyin iddia EDİLMEDİĞİ

- Çalışma zamanı izin kuralları — `--disallowedTools` ile `Bash(git push*)`,
  `Bash(git commit*)`, `Bash(git config*)`, `Bash(git remote*)` reddi — **derinlemesine
  savunmadır, güvenlik sınırı değildir**. Sağlayıcı politikası eşleşmezse ya da atlanırsa
  altında bir işletim sistemi sınırı yoktur.
- Sağlayıcının kendi "güvenli mod" bayrağı bir işletim sistemi sandbox'ı sağlamaz;
  yalnızca kendi özelleştirmelerini kapatır.
- `credential.helper` sıfırlanmış olması keyfî bir push'u imkânsız kılmaz. Ölçüm bunu
  çürüttü. Sıfırlama, `GIT_TERMINAL_PROMPT=0` ve devre dışı bırakılmış `GIT_ASKPASS`
  derinlemesine savunma olarak korunur.

## Sonuçlar

- Faz 6 runtime'ı agent'a kabuk vermediği için, agent'ın doğrudan git çalıştırma yolu
  üretimde yoktur. Buna rağmen Hub tarafı sertleştirme kaldırılmaz: aynı turda kapatılan
  worktree yapılandırma denetimi, sertleştirme bütünlüğü doğrulaması ve çalışma ağacı
  `.gitattributes` ön taraması yerinde kalır ve K1 (`HEAD != Base`) davranışı değişmez.
- Agent alt süreçlerinin git yapılandırma tabanı Hub'ınkiyle eşitlenir
  (`GIT_CONFIG_NOSYSTEM`, Hub'a ait boş global yapılandırma). Bu bir sınır değil,
  determinizmdir: geliştiricinin sistem ve global dosyaları artık iki farklı cevap
  üretmez.
- Kabuk açıldığı gün bu ADR yerine yenisi yazılır; bu kayıt sessizce genişletilmez.
