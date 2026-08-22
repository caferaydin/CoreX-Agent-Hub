# ADR-0005 — Sonuç commit'ini Hub atar; agent commit'i bloklayıcı politika ihlalidir

- **Durum:** Accepted
- **Tarih:** 2026-08-22
- **Kaynak:** mimari doküman § G.3, G.4, E.1 #7/#8/#9 (v1.1.1 final errata E1)

## Bağlam

Sonuç commit'inin kim tarafından atıldığı hem kanıt determinizmini hem güvenlik garantilerini
belirler. v1.1'de her zaman başarısız olan bir `pre-commit` hook'u önerilmişti; bu, Hub'ın
kendi sonucunu commit edebilmesiyle çelişiyordu. Ayrıca agent'ın kendi commit'ini atması
yalnızca bir bulgu olarak kaydediliyordu; bu, SecretGuard'ın commit öncesi çalışma
gerekçesini geçersiz kılıyordu.

## Karar

1. Sonuç commit'ini **yalnızca Hub** atar; sabit kimlikle.
2. Worktree'de **`pre-commit` hook'u kurulmaz.** Yalnızca daima başarısız olan bir
   `pre-push` hook'u kurulur.
3. Agent'ın commit atmasının engellenmesi bir **orkestrasyon/runtime politikasıdır**,
   güvenlik sınırı değildir.
4. Agent adımından sonra `HEAD != BaseCommitSha` ise bu **bloklayıcıdır**:
   `Execution.Outcome = RuntimePolicyViolation` (detay `UnexpectedAgentCommit`).
   Sonuç commit'i atılmaz, doğrulama ve inceleme çalıştırılmaz, worktree korunur.
5. **Hub, agent'ın oluşturduğu git geçmişini asla onarmaz veya silmez.**
   `git reset`, `git branch -f`, `git update-ref`, `git commit --amend`, `git rebase`,
   `git push --force` kod tabanında bulunmaz.
6. Hub kendi koruma mekanizmasını atlatan bayrak kullanmaz: `--no-verify`,
   `-c core.hooksPath=`, `--force` yasaktır.
7. Kazara uzağa yayınlamanın engellenmesi daha güçlü bir güvenlik gereksinimidir ve beş
   bağımsız katmanla sağlanır: yapısal branch ayrımı, protected-branch doğrulaması,
   `pre-push` hook'u, geçersiz `remote.origin.pushurl`, ve Hub'da push kod yolunun
   hiç yazılmaması.

## Sonuçlar

- Deterministik diff ve ayırt edilebilir yazar kimliği.
- Politika ihlali sessizce sürmez; akış durur ve kanıt korunur.
- Agent'ın bıraktığı commit worktree branch'inde kalır; temizliği açık bir insan işidir.
- 5. ve 6. maddeler mimari testle zorlanır.
