# ADR-0004 — Worktree'ler repo dışında, kısa Hub kökünde

- **Durum:** Accepted
- **Tarih:** 2026-08-22
- **Kaynak:** mimari doküman § 0.2, G.1

## Bağlam

Worktree'lerin yönetilen reponun içinde tutulması; repo kirlenmesi, `.gitignore` bağımlılığı,
IDE/test runner taraması, yedekleme yükü ve Windows'un 260 karakter yol sınırı sorunlarını
doğurur.

## Karar

Worktree'ler yönetilen reponun **dışında**, Hub'ın sahibi olduğu kısa bir kökte tutulur:

    Workspace kökü : D:\cx\
    Fixture kökü   : D:\cx\_fixtures\
    Yerleşim       : <kök>\<org-slug>\<repo-slug>\<taskKey>-<attempt>\

Ek olarak Windows uzun yol desteği açılır, yönetilen repolarda `core.longpaths=true`
ayarlanır ve `D:\cx` antivirüs taramasından muaf tutulur.

## Sonuçlar

- Yönetilen repolar Hub çalışmalarından etkilenmez.
- Yol uzunluğu marjı derin bağımlılık ağaçları için yeterli kalır.
- Worktree kökü yapılandırmadan gelir; kod içinde sabitlenmez.
