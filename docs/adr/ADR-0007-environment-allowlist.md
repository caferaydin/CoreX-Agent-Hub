# ADR-0007 — Ortam değişkeni allowlist'i (denylist değil)

- **Durum:** Accepted
- **Tarih:** 2026-08-22
- **Kaynak:** mimari doküman § F.5 #6, K.3 #3

## Bağlam

Geliştirici iş istasyonunun ortam değişkenleri bulut anahtarları, veritabanı bağlantı
dizeleri ve token'lar taşır. Alt sürece varsayılan olarak tüm ortamın devredilmesi en
büyük tekil kimlik bilgisi sızıntısı yüzeyidir. Denylist yaklaşımı ise bilinmeyen değişken
adlarına karşı yapısal olarak eksiktir.

## Karar

Agent ve doğrulama süreçleri **boş bir ortamdan** başlatılır; yalnızca açıkça listelenmiş
değişkenler eklenir. Allowlist organizasyon başına tanımlanır. Ayrıca şu değişkenler her
zaman zorlanır: `GIT_TERMINAL_PROMPT=0`, `GIT_ASKPASS=<no-op>`, `NO_COLOR=1`, `CI=1`.

## Sonuçlar

- Listelenmemiş her değişken alt süreçte görünmez; bu davranış testle doğrulanır.
- Yeni bir araç için gereken değişken açık bir yapılandırma kararı olur.
- Sağlayıcı kimlik doğrulaması için gereken minimum değişken seti belgelenmelidir.
