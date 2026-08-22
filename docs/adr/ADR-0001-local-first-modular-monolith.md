# ADR-0001 — Local-first, tek süreçli modüler monolit

- **Durum:** Accepted
- **Tarih:** 2026-08-22
- **Kaynak:** `docs/architecture/CoreX-Agent-Hub-Mimari-Plan-v1.1.1.md` § A, N, U

## Bağlam

CoreX Agent Hub tek geliştirici iş istasyonunda, tek kullanıcı için çalışan bir mühendislik
kontrol düzlemidir. Dağıtık çalıştırma, çoklu kullanıcı, ölçeklenme veya yüksek erişilebilirlik
için ölçülmüş bir gereksinim yoktur.

## Karar

Sistem **local-first, tek süreçli bir modüler monolit** olarak inşa edilir. Modül sınırları
ayrı .NET projeleri ve tek yönlü bağımlılık grafiğiyle ifade edilir; ayrı servis, kuyruk,
mesaj otobüsü veya container altyapısı kullanılmaz.

## Sonuçlar

- Dağıtık altyapı (PostgreSQL, Redis, RabbitMQ, Kafka, Kubernetes) kapsam dışıdır.
- Modül sınırları derleme zamanında zorlanır (mimari test paketi).
- İleride uzak worker gerekirse sınırlar zaten var olduğu için çıkarma maliyeti sınırlıdır.
- Tek süreç varsayımı; eşzamanlılık kontrolü in-process semafor + SQLite WAL ile çözülür.
