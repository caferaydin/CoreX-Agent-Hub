# ADR-0014 — İnsan kabulü; tek override istisnası `ReviewBlocked`

- **Durum:** Accepted
- **Tarih:** 2026-08-22
- **Kaynak:** mimari doküman § P.1, P.5, J.4, E.1 #6/#10/#11 (v1.1.1 final errata E2)

## Bağlam

Kabul, sistemin tek gerçek insan otoritesi noktasıdır. v1.1'de `ReviewBlocked` görevi
`Ready` durumuna döndürüyor, buna karşın `hub accept --override` belgeleniyordu; bu
durum makinesi düzeyinde uygulanamaz bir çelişkiydi. Ayrıca kabulün merge/push/deploy
ile karıştırılması geri alınması zor yan etkiler doğurur.

## Karar

1. **Kabul; merge, push, release veya deployment anlamına gelmez.** Bunlar ayrı eylemlerdir
   ve MVP'de kod yolu dahi yoktur.
2. `Task.State = Accepted` yazan **tek yer `AcceptanceService`'tir**; `ExecutionOrchestrator`
   içinde bu geçişe giden kod yolu bulunmaz. `hub run` asla kabul edemez.
3. **Normal kabul:** `Task == AwaitingAcceptance` ve son Execution `Outcome == Passed`.
4. **İnceleme override kabulü:** `Task == Ready` ve son Execution `Completed(ReviewBlocked)`
   ve altı ön koşul: uygulama adımı başarılı, ScopeGuard geçti, SecretGuard geçti,
   `required` makine doğrulaması geçti, `ResultCommitSha` mevcut, inceleme kullanılabilirdi
   ve en az bir geçerli bloklayıcı bulgu üretti. `Reason` zorunludur.
5. Override **kapalı bir kümedir**: yalnızca `ReviewBlocked`. Uygulama açık
   `default: deny` ile yazılır; enum büyüse bile varsayılan reddetmektir.
6. Override **`Execution.Outcome`'u yeniden yazmaz** (`ReviewBlocked` kalır) ve bulguları
   silmez/bastırmaz. `AcceptanceService` hiçbir zaman `Execution.Outcome` yazmaz.
7. `Accepted` ve `Rejected` terminaldir ve geri alınamaz; `AcceptanceDecision` değişmezdir.

## Sonuçlar

- Override'ın anlamı: "İnsan geçerli bir bloklayıcı görüşü gördü ve sorumluluğu üstlendi."
  "İnceleyici yanılmıştı" anlamına gelmez.
- Makine gerçeği ve runtime bütünlüğü hataları kabul override'ı ile başarılı işe dönüşemez.
- 2., 5. ve 6. maddeler mimari/birim testleriyle zorlanır.
