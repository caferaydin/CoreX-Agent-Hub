# ADR-0009 — Claude Code entegrasyonu CLI subprocess ile; mevcut abonelik oturumu kullanılır

- **Durum:** Accepted
- **Tarih:** 2026-08-22
- **Kaynak:** mimari doküman § 0.1, F.3, V.2 (G6-1)

## Bağlam

.NET için resmî bir Claude Agent SDK yoktur (SDK Python ve TypeScript'tir). Yalnızca SDK
için bir Node/Python sidecar taşımak, kazandırdığından fazla operasyonel yük getirir.
Claude Code CLI ise yapısal çıktı, şema doğrulama, izin modu, araç allowlist ve oturum
kimliği atamayı zaten sunar.

## Karar

1. Entegrasyon **CLI subprocess** ile yapılır; sidecar kullanılmaz.
2. Kimlik doğrulama için iş istasyonunda **zaten açık olan abonelik oturumu** kullanılır.
   **Ayrı API-key faturalandırma mimarisi tasarlanmaz.**
3. `claude setup-token` yalnızca ileride gözetimsiz/CI/uzak worker çalıştırma gerekirse
   değerlendirilecek bir kaçış yoludur; Faz 6 için gerekli değildir.
4. `--bare` kullanılmaz (API key dayatır, abonelik oturumunu kırar).
5. Bütçe kontrolü sağlayıcı bayrağına dayandırılmaz; Hub tarafında eşzamanlılık limiti,
   günlük çalıştırma sayacı ve üç seviyeli timeout ile sağlanır.
6. Ham stdout her zaman blob olarak saklanır; yapısal ayrıştırma başarısız olsa bile
   kanıt kaybolmaz.

## Sonuçlar

- Faz 6 öncesi tek doğrulama gereksinimi: mevcut oturumun `--safe-mode --setting-sources user`
  altında headless çalıştığının teyidi (kapı G6-1).
- Süreç sınırı, iptal ve timeout kontrolü Hub'da kalır.
