# ADR-0006 — Tüm süreç çalıştırma tek `ProcessRunner` üzerinden, kabuksuz

- **Durum:** Accepted
- **Tarih:** 2026-08-22
- **Kaynak:** mimari doküman § E.1 #1/#3, F.5, I.3

## Bağlam

Coding agent'lar ve doğrulama komutları iş istasyonunda keyfî süreçler çalıştırır. Dağınık
`Process.Start` çağrıları; timeout, ağaç öldürme, kodlama, ortam değişkeni sızıntısı ve
komut enjeksiyonu kontrollerinin tutarsız uygulanmasına yol açar.

## Karar

1. Süreç başlatan **tek yer `ProcessRunner`'dır**; başka hiçbir yerde `Process.Start` bulunmaz.
2. **Kabuk kullanılmaz.** Her çalıştırma `executable + string[] argv` biçimindedir.
3. `ProcessRunner` merkezî olarak sağlar: Windows Job Object ile süreç ağacı öldürme,
   üç seviyeli timeout (soft / idle / hard), asenkron stdout+stderr tüketimi, UTF-8
   zorlaması ve ANSI temizleme, ortam değişkeni allowlist'i.

## Sonuçlar

- Komut enjeksiyonu kaynağında ortadan kalkar.
- Asılı ve torun süreçler garantili sonlandırılır.
- Değişmez, kaynak taraması yapan bir mimari testle sürekli denetlenir.
- Windows'ta `.cmd`/`.bat` shim'leri gerçek çalıştırılabilire çözülür.
