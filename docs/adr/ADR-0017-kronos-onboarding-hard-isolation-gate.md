# ADR-0017 — Kronos onboarding sert izolasyon doğrulanana kadar kod ile bloke edilir

- **Durum:** Accepted
- **Tarih:** 2026-08-22
- **Kaynak:** mimari doküman § L.4, D.1, K.4

## Bağlam

ADR-0012 mantıksal organizasyon izolasyonunu **yalnızca Core X Logic pilotu** için kabul
eder. İki ayrı şirketin (Core X Logic ve Kronos) verisi aynı OS güvenlik principal'ı
altında işlenirse, uzlaşmış bir agent'a karşı hiçbir gerçek sınır kalmaz. Bu, bir belge
vaadiyle değil kodla engellenmelidir.

## Karar

1. Core X Logic ve Kronos agent süreçleri **aynı OS güvenlik principal'ı** altında
   çalıştığı sürece Kronos'a ait hiçbir repository, bağlam, kimlik bilgisi veya şirket
   verisi platforma kaydedilemez.
2. Kapının açılması için tümü gerekli: **G-K1** sert izolasyon uygulanmış (birinci aday
   WSL2, organizasyon başına dağıtım), **G-K2** çapraz erişim negatif testi geçmiş,
   **G-K3** sağlayıcı oturumu ayrımı çözülmüş, **G-K4** kanıt/log ayrımı OS düzeyinde
   doğrulanmış, **G-K5** `IsolationLevel = Hard` işaretli ve yükleyici doğrulamış.
3. **Kod ile zorlanır:** `Organization.RequiresHardIsolation = true` **ve**
   `IsolationLevel = Logical` ise organizasyon `Status = Blocked` olur ve hiçbir execution
   başlatamaz. Bu davranış birim testiyle kilitlenir.
4. Bu kapı geliştirmeyi, Core X Logic kullanımını, Loop Zero'yu, Claude/Codex
   entegrasyonunu veya UI'ı **bloklamaz**; yalnızca Kronos onboarding'ini bloklar.

## Sonuçlar

- Kronos yapılandırması gerçek veri içeremez; test/fixture verisi kullanılır.
- Sert izolasyon işi Faz 10 kapsamındadır; birinci aday WSL2'dir.
- Kapı durumu operasyonel olarak görünür olmalıdır (ileride `hub doctor` çıktısında).
