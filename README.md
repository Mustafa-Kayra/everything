# Everything Arama

Windows için anında dosya arama ve disk alanı analizi uygulaması. Windows 11 tarzı sade bir arayüzü var; açık ve koyu modu destekler.

## İndir

[Releases](../../releases) sayfasından iki sürümden birini seç:

| Dosya | Ne zaman |
|---|---|
| **Everything-Setup.exe** | Önerilen. Program Files'a kurar ve Başlat menüsüne ekler. İstersen Windows açılınca yönetici olarak (UAC sormadan) tepside başlatır. |
| **Everything-Portable.exe** | Kurulum gerektirmez; USB bellekte de çalışır. Ayarlar ve indeks exe'nin yanındaki `Everything-Data` klasöründe tutulur. |

İki sürüm de .NET kurulumu gerektirmez (64-bit Windows 10/11). Exe'ler imzasız olduğu için SmartScreen uyarı verebilir. Uyarıda **Ek bilgi → Yine de çalıştır** seçeneğiyle açabilirsin.

## Özellikler

- **🔍 Ara:** Bütün diskleri indeksler ve yazarken sonuç verir.
  - Gerçek dosya ikonları ve önizleme paneli var (resim, video, PDF küçük resimleri; metin dosyalarının içeriği).
  - Türkçe karakter gerekmez: "sarki" yazınca "Şarkı" da bulunur.
- **⚡ Hızlı arama:** `Alt+Space` (değiştirilebilir) ile her yerden küçük bir arama kutusu açılır. Uygulama tepside çalışır.
- **💽 Diskler:** Doluluk, model, SSD/HDD ve NVMe/SATA/USB bilgisi.
  - SMART sağlık bilgileri: kalan ömür, sıcaklık, çalışma süresi, toplam yazılan veri.
  - CrystalDiskMark tarzı **hız testi**.
- **📊 Alan Analizi:**
  - Büyükten küçüğe klasör ağacı ve WizTree tarzı **renkli harita** (treemap).
  - Kategori ve uzantı dökümü, en büyük dosyalar.
  - **Büyüyenler:** Son 1/7/30 günde hangi klasörlerin büyüdüğünü gösterir. Uygulama her gün klasör boyutlarını kaydeder.
- **🧩 Kopyalar:** Birebir aynı içeriğe sahip dosyaları bulur. Boyut, baş/son ve tam içerik karşılaştırılır. Tek tıkla "en yeniyi tut" / "en eskiyi tut" seçimi yapılabilir; her gruptan en az bir dosya her zaman korunur.
- **🧹 Temizlik:** Geçici dosyalar, Windows Update önbelleği, tarayıcı önbellekleri, hata dökümleri, geliştirici önbellekleri ve Geri Dönüşüm Kutusu. İndirilenler'deki eski büyük dosyalar da incelenmek üzere listelenir.
- **🔄 Güncellemeler:** GitHub'daki son sürümü denetler. Tek tıkla indirip kurar ya da portable exe'yi yerinde günceller. İndirilen dosya SHA-256 ile doğrulanır.

## Açılışta yeniden tarama yok

İndeks diske kaydedilir ve açılışta anında yüklenir.

- **Yönetici olarak çalışırken (hızlı NTFS modu):**
  - İlk tarama NTFS kayıtlarından (MFT) doğrudan okunur, birkaç saniye sürer.
  - Uygulama kapalıyken yapılan değişiklikler açılışta NTFS değişiklik günlüğünden (USN Journal) saniyeler içinde uygulanır.
  - Açıkken değişiklikler yine günlükten anında işlenir.
  - Kurulumda "Windows açılınca yönetici olarak başlat" seçeneği bu modu her oturumda UAC sormadan açar.
- **Yönetici olmadan:** Kayıtlı indeks olduğu gibi kullanılır. Açıkken değişiklikler anında işlenir. Kayıt ayarlanan süreden (varsayılan 3 gün) eskiyse arka planda sessizce tazelenir.

## Arama söz dizimi

| Yazdığın | Bulduğu |
|---|---|
| `rapor 2024` | adında hem "rapor" hem "2024" geçenler |
| `"yıllık rapor"` | tam ifade |
| `*.mp4`, `IMG_????.jpg` | joker karakterler |
| `!temp` | "temp" geçenleri hariç tut |
| `ext:pdf;docx` | uzantıya göre |
| `size:>1gb`, `size:<10kb`, `size:10mb..1gb` | boyuta göre (dosyalar) |
| `folder: size:>10gb` | 10 GB'tan büyük klasörler |
| `dm:bugün`, `dm:dün`, `dm:buhafta`, `dm:buay`, `dm:son7g`, `dm:2024`, `dm:>2024-05-01`, `dm:01.05.2024..31.05.2024` | değiştirilme tarihine göre |
| `file:` / `folder:` | yalnızca dosyalar / klasörler |
| `path:Belgeler`, `Belgeler\rapor` | tam yolda ara |

**Kısayollar:**
- `Ctrl+F`: arama kutusuna git
- `Enter`: aç
- `Ctrl+C`: yolu kopyala
- `Del`: Geri Dönüşüm Kutusu'na taşı
- `Esc`: aramayı temizle
- `F5`: yeniden tara

Hızlı arama penceresinde `Ctrl+Enter` dosyanın konumunu açar.

## Geliştirme

```
src/Everything.Core     platformdan bağımsız indeks, tarayıcı, NTFS MFT/USN, arama, kopya bulucu, treemap, hız testi (net10.0)
src/Everything.App      WPF arayüzü (Fluent teması), tepsi, kısayol, güncelleyici, SMART (net10.0-windows)
tests/Everything.Core.Tests   189 test (gerçek NTFS imajlarıyla MFT testleri dahil)
installer/              Inno Setup kurulum betiği
build/build-release.sh  portable exe + kurulum paketi + SHA256SUMS.txt üretir
```

```sh
dotnet test tests/Everything.Core.Tests
./build/build-release.sh        # Linux'ta Wine + Inno Setup ile; çıktılar artifacts/ altında
```
