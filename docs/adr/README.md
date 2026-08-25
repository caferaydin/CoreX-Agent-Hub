# Mimari Karar Kayıtları (ADR)

Bu dizin CoreX Agent Hub'ın kabul edilmiş mimari kararlarını kaydeder.

Otoriter mimari doküman: `../architecture/CoreX-Agent-Hub-Mimari-Plan-v1.1.1.md`
(durum: **ARCHITECTURE ACCEPTED — DISCOVERY CLOSED**).

ADR'ler kararı kaydeder; mimari dokümanı kopyalamaz. Çelişki hâlinde mimari doküman
v1.1.1 esas alınır.

| No | Başlık | Durum |
|---|---|---|
| ADR-0001 | Local-first, tek süreçli modüler monolit | Accepted |
| ADR-0002 | Kanıt otoritesi kademeleri | Accepted |
| ADR-0003 | Git worktree ile execution izolasyonu ve base-commit sabitleme | Accepted |
| ADR-0004 | Worktree'ler repo dışında, kısa Hub kökünde | Accepted |
| ADR-0005 | Sonuç commit'ini Hub atar; agent commit'i bloklayıcı politika ihlalidir | Accepted |
| ADR-0006 | Tüm süreç çalıştırma tek `ProcessRunner` üzerinden, kabuksuz | Accepted |
| ADR-0007 | Ortam değişkeni allowlist'i (denylist değil) | Accepted |
| ADR-0008 | `IAgentRuntime` ince tutulur; yetenekler ilan edilir | Accepted |
| ADR-0009 | Claude Code CLI subprocess; mevcut abonelik oturumu | Accepted |
| ADR-0010 | Context Pack diskte materyalize edilir ve hash'lenir | Accepted |
| ADR-0011 | Repo içi agent yapılandırması ve git hook'ları devre dışı | Accepted |
| ADR-0012 | Organizasyon izolasyonu Core X Logic pilotu için mantıksaldır | Accepted |
| ADR-0013 | SQLite + WAL; büyük çıktılar içerik adresli blob deposunda | Accepted |
| ADR-0014 | İnsan kabulü; tek override istisnası `ReviewBlocked` | Accepted |
| ADR-0015 | Kabul kriterleri `MACHINE`/`HUMAN` beyanı taşır | Accepted |
| ADR-0016 | Bloklu/başarısız execution'lar için result commit atılmaz | Accepted |
| ADR-0017 | Kronos onboarding sert izolasyon doğrulanana kadar kod ile bloke | Accepted |
| ADR-0018 | Doğrulama çalıştırılabilirleri allowlist ile çözülür; parser adımı rapor yolunu bildirir | Accepted |
| ADR-0019 | Baseline anahtarı repository + base commit + profil hash'idir; baseline izin değildir | Accepted |
| ADR-0020 | Faz 6 native Windows gerçek-agent profili kabuk (Bash) açmaz | Accepted |
| ADR-0021 | Faz 6 native Windows E1 kabul inceltmesi: araç yüzeyi ölçümü + deterministik K1 | Accepted |
| ADR-0022 | Bağımsız review kanıt üretir, karar üretmez; kapı Hub'ındır | Accepted |
| ADR-0023 | Kesilen bir yazmadan sonra otorite veritabanıdır | Accepted |

## Onaylanmış Faz 0 format kararları

| Karar | Değer |
|---|---|
| Workspace kökü | `D:\cx\` |
| Fixture kökü | `D:\cx\_fixtures\` |
| `TaskKey` formatı | `<PROJECT>-<3 hane>` |
| Loop Zero fixture TaskKey | `HUB-001` |
