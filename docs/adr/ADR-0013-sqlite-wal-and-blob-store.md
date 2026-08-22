# ADR-0013 — SQLite + WAL; büyük çıktılar içerik adresli blob deposunda

- **Durum:** Accepted
- **Tarih:** 2026-08-22
- **Kaynak:** mimari doküman § M.1, M.2, M.3

## Bağlam

Tek kullanıcı, tek süreç, yerel makine ve düşük yazma hacmi söz konusudur. Öte yandan
agent stdout'u, komut çıktıları ve diff'ler tek bir çalıştırmada megabaytlara ulaşabilir.
Bunların ilişkisel tabloya yazılması veritabanını hızla kullanılamaz hâle getirir.

## Karar

1. İlişkisel depo **SQLite**'tır; WAL modu, `busy_timeout`, `foreign_keys=ON` ve
   uygulama içi **tek yazıcı** semaforu ile kullanılır. Zaman damgaları UTC'dir.
2. Büyük çıktılar veritabanına konmaz. **İçerik adresli blob deposu** kullanılır
   (`blobs/<sha256[0:2]>/<sha256>`), atomik yazımla (temp + rename).
3. Veritabanı yalnızca meta veriyi tutar: `sha256`, boyut, tür, redaksiyon sayısı.
4. Ayrıştırılmış metrikler (test sayaçları, bulgular) sorgulanabilir olduğu için
   tabloya yazılır; ham hâlleri blob olarak da saklanır.
5. Blob'lar içerik adresli olduğu için silme referans sayımı gerektirir; MVP'de
   **silme yoktur**.

## Sonuçlar

- PostgreSQL, Redis veya harici depolama için gereksinim yoktur.
- Aynı içerik tekrar üretildiğinde yeniden yazılmaz.
- Yedekleme: `VACUUM INTO` anlık görüntüsü + blob dizini kopyası.
