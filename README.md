# everything

Windows için hızlı dosya arama ve disk alanı analizi uygulaması.

- **🔍 Ara:** Tüm yerel diskler arka planda indekslenir. Yazdığın anda sonuçlar gelir; değişiklikler canlı olarak indekse işlenir.
- **💽 Diskler:** Her sürücü için doluluk, dosya sistemi, disk modeli, SSD/HDD, bağlantı türü (NVMe/SATA/USB), sağlık durumu ve seri numaraları.
- **📊 Alan Analizi:** Diskte yer kaplayan şeyleri gösterir. Klasörler büyükten küçüğe bir ağaçta yüzde çubuklarıyla listelenir. Ayrıca kategori dökümü (Video, Resim, Oyun…), uzantı dökümü ve en büyük 500 dosya görüntülenir.

## Kurulum

[Actions](../../actions) sekmesindeki son başarılı `build` çalışmasından **Everything-win-x64** dosyasını indir. İçinden çıkan `Everything.exe` dosyasını çalıştırman yeterli. Kurulum ya da .NET gerekmez, yönetici izni de istemez.

Bir `v*` etiketi (ör. `v1.0.0`) gönderildiğinde exe otomatik olarak **Releases** sayfasına da eklenir.

İlk açılışta diskler taranır; bu, disk boyutuna göre birkaç saniye ile birkaç dakika sürebilir. Tarama sırasında arama yapılabilir. İndeks `%LOCALAPPDATA%\Everything\cache` klasörüne kaydedilir, böylece sonraki açılışlarda arama anında hazır olur. Ardından indeks arka planda tazelenir.

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
| `file:` / `folder:` | yalnızca dosyalar / klasörler |
| `path:Belgeler`, `Belgeler\rapor` | tam yolda ara |

**Kısayollar:**
- `Ctrl+F`: arama kutusuna git
- `Enter`: ilk sonucu (ya da seçiliyi) aç
- `Esc`: aramayı temizle
- `Ctrl+C`: yolu kopyala
- `Del`: Geri Dönüşüm Kutusu'na taşı
- `F5`: yeniden tara

Sağ tık menüsünde *Aç*, *Dosya konumunu aç*, *Tam yolu kopyala* ve *Geri Dönüşüm Kutusu'na taşı* seçenekleri bulunur. Alan analizinde bir uzantıya çift tıklarsan o klasördeki o türdeki dosyalar arama sekmesinde boyuta göre sıralanır.

## Notlar

- Yönetici olmadan erişilemeyen klasörler (başka kullanıcıların profilleri, `System Volume Information` vb.) atlanır. Bu yüzden alan analizi "Fark" satırında indekslenen dosyalarla diskteki kullanılan alan arasındaki farkı gösterir.
- Boyutlar mantıksal dosya boyutudur. Sabit bağlantılar (ör. `WinSxS`) birden fazla sayılabilir. OneDrive'ın yalnızca bulutta tutulan dosyaları da tam boyutlarıyla görünür.
- Junction ve symlink'lerin içine girilmez. Böylece döngü ve çift sayım olmaz.

## Geliştirme

```
src/Everything.Core     platformdan bağımsız indeks, tarayıcı, arama motoru ve alan analizi (net8.0)
src/Everything.App      WPF arayüzü ve WMI disk bilgileri (net8.0-windows)
tests/Everything.Core.Tests
```

```sh
dotnet test tests/Everything.Core.Tests
dotnet publish src/Everything.App -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
```

Çekirdek kütüphane Linux'ta da derlenip test edilebilir. Arayüz `EnableWindowsTargeting` sayesinde Linux'ta derlenir, ancak yalnızca Windows'ta çalışır.
