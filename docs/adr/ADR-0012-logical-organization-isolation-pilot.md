# ADR-0012 — Organizasyon izolasyonu Core X Logic pilotu için mantıksaldır

- **Durum:** Accepted
- **Tarih:** 2026-08-22
- **Kaynak:** mimari doküman § L.1, L.2, K.4

## Bağlam

Agent süreçleri geliştiriciyle **aynı Windows kullanıcısı** altında çalışır. Uygulama içi
kapsamlama (veritabanı, dosya sistemi kökleri, ortam değişkeni allowlist'i, bağlam derleme)
gerçek bir OS güvenlik sınırı değildir.

## Karar

1. v1 MVP'de organizasyon izolasyonu **mantıksaldır**: her tabloda `OrganizationId`,
   organizasyon başına filesystem kökü, organizasyon başına env allowlist, bağlam
   derlemesinde organizasyon filtresi.
2. Bu izolasyon **kaza önleyicidir, saldırı önleyici değildir** ve bu artık risk açıkça
   kabul edilir.
3. Kabul yalnızca **Core X Logic pilotu** için geçerlidir. Sert OS-seviyesi izolasyon
   gerektiren organizasyonlar için ADR-0017 uygulanır.

## Sonuçlar

- Belge ve arayüzlerde izolasyon "güvenlik sınırı" olarak sunulmaz.
- Yükseltme tetikleyicileri tanımlıdır: güvenilmeyen repo, ikinci insan kullanıcı,
  üretim kimlik bilgilerinin repo yakınında bulunması, Kronos onboarding talebi.
