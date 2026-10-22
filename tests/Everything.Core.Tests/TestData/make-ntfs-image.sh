#!/usr/bin/env bash
# MftReaderTests için küçük NTFS imajları üretir (bir kez çalıştırılır; çıktılar depoya eklenir).
# Gerekenler: ntfs-3g paketi (mkntfs + FUSE sürücüsü) ve sudo.
#
#   ./make-ntfs-image.sh <çıktı.img.gz> <sektör boyutu> <küme boyutu> [imaj boyutu MB] [full|fragmented-mft]
#
# Depodaki imajlar:
#   ./make-ntfs-image.sh ntfs-512-512.img.gz 512 512
#   ./make-ntfs-image.sh ntfs-4096-8192.img.gz 4096 8192 40
#   ./make-ntfs-image.sh ntfs-fragmented-mft.img.gz 512 512 8 fragmented-mft
set -euo pipefail

out=$(realpath -m "$1")
sector=$2
cluster=$3
size_mb=${4:-16}
layout=${5:-full}

work=$(mktemp -d)
img="$work/ntfs.img"
mnt="$work/mnt"
mkdir -p "$mnt"
cleanup() {
    cd /
    if mountpoint -q "$mnt"; then sudo umount "$mnt"; fi
    rm -rf "$work"
}
trap cleanup EXIT

truncate -s "${size_mb}M" "$img"
mkntfs -F -f -q -s "$sector" -c "$cluster" -L mfttest "$img" >/dev/null 2>&1
sudo ntfs-3g -o streams_interface=windows,uid="$(id -u)",gid="$(id -g)" "$img" "$mnt"

cd "$mnt"
zeros() { head -c "$1" /dev/zero; }

populate_full() {
    # Boş, kalıcı (resident) ve kalıcı olmayan dosyalar
    zeros 0 > empty.txt
    zeros 100 > tiny.txt
    mkdir -p docs/nested/deep
    zeros 600 > docs/readme.md
    zeros 3145728 > docs/nested/deep/big.bin

    # Seyrek dosya (yalnızca seyrek aralıklardan oluşan veri)
    truncate -s 10M sparse.bin

    # Parçalı dosyalar: iki dosyaya sırayla eklenerek kümeler iç içe geçer.
    mkdir frag
    for _ in $(seq 20); do
        zeros 65536 >> frag/a.bin
        zeros 65536 >> frag/b.bin
    done

    # Unicode / Türkçe adlar
    mkdir 'Türkçe'
    zeros 1234 > 'Türkçe/ğüşıöç İ.txt'

    # Sabit bağlantı (hard link): aynı dosya iki klasörde
    mkdir links
    zeros 500 > links/original.txt
    ln links/original.txt docs/hardlink.txt

    # Çok sayıda sabit bağlantı: $FILE_NAME öznitelikleri kayda sığmaz, $ATTRIBUTE_LIST + ek kayıtlar oluşur.
    mkdir hl
    zeros 777 > hl/target.bin
    for i in $(seq -w 1 40); do
        ln hl/target.bin "hl/link-with-a-rather-long-name-to-fill-the-mft-record-$i.bin"
    done

    # Alternatif veri akışı (ADS): dosya boyutuna sayılmamalı
    zeros 10 > ads.txt
    zeros 5000 > 'ads.txt:secret'

    # Sembolik bağlantı (ntfs-3g bunu Interix biçiminde, 16 baytlık sistem dosyası olarak yazar)
    ln -s docs link-to-docs

    # Gerçek bir yeniden ayrıştırma noktası: "\??\C:\target" hedefli bağlantı noktası (junction)
    mkdir junction
    setfattr -n system.ntfs_reparse_data -v 0x030000a03800000000001a001c0012005c003f003f005c0043003a005c00740061007200670065007400000043003a005c007400610072006700650074000000 junction

    # Silinmiş dosya: MFT kaydı kullanımda değil olarak kalır
    zeros 2000 > deleted.txt
    rm deleted.txt

    # MFT'nin ilk ayrılan alanının ötesine büyümesi için çok sayıda dosya
    mkdir many
    for i in $(seq -w 0 2999); do
        zeros $((10#$i % 7)) > "many/f$i.txt"
    done

    # Sabit değiştirilme zamanları
    touch -d '2020-01-02 03:04:05 UTC' tiny.txt
    touch -d '2021-06-07 08:09:10 UTC' docs/nested/deep/big.bin
    touch -d '2019-11-12 13:14:15 UTC' docs
}

# MFT'yi yüzlerce parçaya böler: birim 800 baytlık dosyalarla doldurulur, her ikinci dosya silinir
# ve MFT'nin büyümesi için yeni dosyalar oluşturulur. MFT 2 kümelik deliklere yerleşir; $MFT'nin
# aralık listesi kayıt 0'a sığmaz ve $ATTRIBUTE_LIST ile ek kayıtlara taşar.
populate_fragmented_mft() {
    mkdir fill
    for i in $(seq -w 1 2500); do
        zeros 800 > "fill/s$i"
    done
    for i in $(seq -w 1 2 2500); do
        rm "fill/s$i"
    done
    mkdir grow
    for i in $(seq -w 1 2000); do
        : > "grow/n$i"
    done
}

case "$layout" in
    full) populate_full ;;
    fragmented-mft) populate_fragmented_mft ;;
    *) echo "bilinmeyen düzen: $layout" >&2; exit 1 ;;
esac

cd /
sync
sudo umount "$mnt"
gzip -9 -n -c "$img" > "$out"
ls -l "$out"
