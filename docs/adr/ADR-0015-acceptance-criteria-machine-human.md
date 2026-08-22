# ADR-0015 — Kabul kriterleri `MACHINE`/`HUMAN` beyanı taşır

- **Durum:** Accepted
- **Tarih:** 2026-08-22
- **Kaynak:** mimari doküman § B.1, D.2, I.2, P.5 (v1.1.1)

## Bağlam

v1'de "her kabul kriteri makinece doğrulanabilir olmalı, aksi hâlde görev tanımsızdır"
kuralı önerilmişti. Bu kural yanlıştır: geçerli mühendislik ve ürün kriterlerinin bir
kısmı makinece doğrulanamaz (örneğin "değişiklik iş kuralına uygun"). Kuralın kendisi
denetimde **reddedilmiştir**.

## Karar

1. Her `AcceptanceCriterion` doğrulama **yöntemini beyan etmek zorundadır**:
   `VerificationKind = Machine | Human`.
2. `Machine` kriter, seçili `VerificationProfile` içindeki bir adım adına çözülen
   `VerificationRef` taşımak zorundadır.
3. `Human` kriter geçerlidir ve **hiçbir doğrulama komutu gerektirmez**; isteğe bağlı
   `HumanCheckHint` taşıyabilir.
4. Geçerli kural: *"Doğrulama yöntemi beyan edilmemiş kabul kriteri geçersizdir."*
   Ready gate bunu zorlar.
5. **Otomatik** execution outcome'u yalnızca makine kanıtı ve yapılandırılmış review
   kapılarından hesaplanır; kriter metni ve agent beyanı hesaba giremez.
6. `Human` kriter verdictleri (`Met` / `NotMet` / `NotApplicable`) **her iki kabul yolunda
   da zorunludur** ve `AcceptanceDecision` içinde saklanır.
7. Eşleme tek bir string referansıdır. **Kural motoru, koşul ifadesi, boolean birleştirme
   veya kriter bağımlılık grafiği yazılmaz.**

## Sonuçlar

- Hiç `Machine` kriteri olmayan görev geçerlidir; yalnızca doğrulama profilinin ötesinde
  otomatik sinyal üretmez ve bu bir uyarı olarak gösterilir.
- Ready gate açık, sabit kodlanmış kontrol listesidir; genişletilebilir bir DSL değildir.
