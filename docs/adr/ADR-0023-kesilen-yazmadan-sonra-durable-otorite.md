# ADR-0023 — Kesilen bir yazmadan sonra otorite veritabanıdır

- **Durum:** Accepted
- **Tarih:** 2026-08-25
- **Kaynak:** STAB-01, STAB-02, Faz 7C §3-§5, ADR-0013, ADR-0016

## Karar

İptal edilmiş bir koşumun kalan zorunlu işi, **bellekteki aggregate'ten değil**, veritabanından
okunarak yapılır. Kapanış yolu aggregate değil **kimlik** alır ve durable satırları paylaşılmayan
kendi kalıcılık kapsamında yeniden okur.

## Sorun

Bir geçiş tek bir an değildir:

```
aggregate.Mutate()          // bellekte
await store.SaveChangesAsync(ct)   // diskte
```

İptal ikisinin arasına düşerse geriye iki ayrı gerçek kalır: bellekteki örnek "terminal" der,
satır demez. EF'in kimlik haritası aynı context'e sorulduğunda o **değiştirilmiş** örneği geri
verdiği için sapma, onu yaratan yerden görünmez.

Bunun iki somut sonucu ölçüldü:

**S1.** Kapanış yolu elindeki aggregate'i yeniden terminalize etmeye çalıştı; `Execution.Terminate`
`IsTerminal` iken `DomainException` atar. Sonuç: iptal istisnasının yerini bir domain istisnası
aldı ve satır veritabanında **terminal olmayan** kaldı — tam olarak STAB-01'in ortadan kaldırmak
için yazıldığı durum. (Gerçek Faz-7 reviewer'ı kodu okuyarak buldu; deterministik olarak
yeniden üretildi.)

**S2.** Execution'ı talep eden ilk işlem korunan bölgenin dışındaydı. Ölçüm:
`rows=1 state=Created terminal=False task=Running` — sahipsiz, kimsenin koşmadığı bir satır.

## Neden basit bir kontrol yetmez

```csharp
if (execution.IsTerminal)   // bellekteki örnek
    return;
```

Bu, hatayı başka biçimde korurdu: okunan şey yine vazgeçilmiş bir **niyet** olurdu.

Aynı store üzerinden **yazmak** da olmaz. Kesilen kaydın mutasyonları hâlâ takip edilmektedir;
o context'te yapılan bir sonraki `SaveChanges` onları da diske yazar — yani vazgeçilmiş geçiş
sessizce gerçekleşir.

## Kural

**A.** `OperationCanceledException`, yazmanın gerçekleşip gerçekleşmediği hakkında hiçbir şey
söylemez. İki ihtimal yalnızca veritabanına sorularak ayrılır; istisnadan çıkarım yapılmaz.

**B.** Zorunlu sonlandırma, hiçbir şey paylaşmayan bir kalıcılık kapsamında (`IDetachedStore`)
yapılır. Bu bir repository soyutlaması, unit-of-work ya da işlem yöneticisi **değildir**: tek
metodu vardır ve döndürdüğü şey herkesin kullandığı `IHubStore`'dur — eklediği tek şey yeni
olmasıdır. Lease heartbeat'in kendi nedeniyle zaten ihtiyaç duyduğu biçimdir.

**C.** Durable olarak zaten terminal olan bir execution **ikinci kez geçirilmez** ve outcome'u
çağıranın niyetine uydurulmaz. Gerçekte olan, kaydedilmekte olanın önüne geçer.

**D.** Result commit'i olan bir deneme asla `Cancelled` olmaz (ADR-0016). Ne kadar ilerlediği
**kendi adımlarından okunur**: Verify henüz başarılı değilse `VerificationFailed`, başarılıysa
`ReviewUnusable`. İkincisine `VerificationFailed` demek, `Succeeded` kaydedilmiş bir Verify
adımıyla çelişirdi.

**E.** Execution talebi kesilirse: bu görev için terminal olmayan bir execution varsa o
kapatılır; yoksa iptal olduğu gibi yeniden fırlatılır ve **hiçbir şey yazılmaz** — olmayan bir
denemeyi anlatan satır uydurulmaz.

**F.** Bütçe sınırlı `MandatoryWorkScope` olmaya devam eder. `CancellationToken.None` yoktur.
Tek kapanış yolu vardır ve kendini çağırmaz. Durable gerçek kurulamıyorsa dürüstçe raporlanır;
`StaleExecutionReconciler` son çare olarak kalır — ama normal bir iptal artık ona ihtiyaç duymaz.

## Sonuçlar

- Kapanış imzası kimlik alır (`CloseCancelledAsync(Guid executionId, Guid taskId)`); aggregate
  geçirmek derleme hatasıdır, dolayısıyla kural yorumla değil şekille korunur.
- Aynı otorite kurtarma yolunda da geçerlidir: host ölümünden sonra outcome, kalıcı Review adımı
  ve kalıcı bulgulardan **donmuş kapıya sorularak** yeniden kurulur (ADR-0022); reviewer'ın hiç
  bakmadığı sonuç `ReviewUnusable`'dır.
- Testlerin de aynı kuralı izlemesi gerekir: iptal edilmiş bir koşumun sonucunu koşumun kullandığı
  context'ten okumak, üretimde yasaklanan hatanın test tarafındaki hâlidir.
