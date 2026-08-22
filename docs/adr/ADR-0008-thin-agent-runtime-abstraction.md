# ADR-0008 — `IAgentRuntime` ince tutulur; yetenekler gizlenmez, ilan edilir

- **Durum:** Accepted
- **Tarih:** 2026-08-22
- **Kaynak:** mimari doküman § F.1, W #6

## Bağlam

Tek sağlayıcı varken yazılan "evrensel" adapter, o sağlayıcının bayraklarının aynası olur
ve ikinci sağlayıcıya uymaz. Öte yandan sağlayıcı farklarının orchestrator içine sızması
provider replaceability hedefini yok eder.

## Karar

1. `IAgentRuntime` üç üyeli ince bir arayüzdür: `Describe()`, `RunAsync(...)`,
   `CheckHealthAsync(...)`.
2. Sağlayıcı farkları **gizlenmez, ilan edilir**: `Capabilities` (şema desteği, oturum
   devamı, araç allowlist, maliyet tavanı, streaming, salt-okunur mod).
3. Orchestrator eksik bir yeteneği telafi etmez; ilgili adımı `Unsupported` işaretler.
4. Soyutlamanın ilk iki implementasyonu `ScriptedAgentRuntime` (test double) ve
   `ClaudeCodeRuntime`'dır; soyutlama tek sağlayıcıya göre şekillendirilmez.

## Sonuçlar

- Sağlayıcı değiştirilebilirliği ölçülebilir bir kabul kriterine dönüşür: inceleme adımının
  yalnızca `AgentProfile` değiştirilerek Codex'e geçmesi.
- Sessiz davranış farkı yerine açık `Unsupported` sinyali.
