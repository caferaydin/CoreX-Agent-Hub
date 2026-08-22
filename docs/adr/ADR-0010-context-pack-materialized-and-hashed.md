# ADR-0010 — Context Pack diskte materyalize edilir ve hash'lenir

- **Durum:** Accepted
- **Tarih:** 2026-08-22
- **Kaynak:** mimari doküman § H.3, H.5

## Bağlam

Agent'a verilen bağlam dev bir prompt bloğu olarak geçirilirse ne hash'lenebilir ne
denetlenebilir ne de sağlayıcılar arasında taşınabilir olur. Kurumsal hafızanın platformda
durması, bağlamın tekrar üretilebilir olmasını gerektirir.

## Karar

Context Pack bir prompt string'i değil, **diskte bir dizindir**: `system.md`, `rules.md`,
`task.md`, `repo-state.json`, `decisions/`, `manifest.json`. Dizin agent'a okunur kılınır;
Hub sözleşmesi ve proje kuralları ayrı prompt dosyaları olarak geçirilir. Dizinin içerik
hash'i (`ContextPackHash`) execution kaydına yazılır. Toplam metin için sert bir bütçe
uygulanır; bütçe aşımında derleme başarısız olur, **sessiz kırpma yasaktır**.

## Sonuçlar

- "Hangi bağlamla çalıştı" sorusu kesin cevaplanır.
- Aynı base commit ve aynı kural sürümü aynı `ContextPackHash`'i üretir.
- Sağlayıcıdan bağımsızdır; Codex de aynı dizini okur.
