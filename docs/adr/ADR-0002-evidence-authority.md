# ADR-0002 — Kanıt otoritesi kademeleri

- **Durum:** Accepted
- **Tarih:** 2026-08-22
- **Kaynak:** mimari doküman § I.1, I.2

## Bağlam

Bir coding agent'ın "yaptım, testler geçti" beyanı doğrulanabilir bir olgu değildir.
Platformun varlık sebebi, makinece üretilmiş kanıtı agent beyanından ayırmaktır.

## Karar

Dört doğruluk kademesi tanımlanır ve asla karıştırılmaz:

| Kademe | İçerik | Otomatik outcome'u etkiler mi? |
|---|---|---|
| A — Makine gerçeği | exit code, SHA, hash, diff, test sayaçları, kapı sonuçları | **Evet** |
| B — İnceleyici bulgusu | şema doğrulamalı `Finding[]` | Evet, yalnızca yapılandırılmış kapı kurallarıyla |
| C — Agent beyanı | serbest metin özet | **Hayır — asla** |
| D — İnsan değerlendirmesi | `Human` kriter verdictleri, inceleme override kararı | Hayır; yalnızca nihai kabulü etkiler |

`Execution.Outcome` **yalnızca** Kademe A ve yapılandırılmış Kademe B kapılarından hesaplanır.

## Sonuçlar

- Agent beyanı saklanır ama hiçbir durum geçişine girdi olamaz.
- Kademe D eylemleri Kademe A/B kayıtlarını değiştirmez (ADR-0014).
- Kural kod tabanında tek bir hesaplama noktasında denetlenebilir olmalıdır.
