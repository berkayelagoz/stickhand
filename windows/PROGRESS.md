# Windows Port — İlerleme

Durum: **başlangıç**. Plan: [PLAN.md](./PLAN.md)
Güncelleme kuralı: bir madde bitince `[x]` yapılır, tarih ve kısa not eklenir.

## M1 — İskelet ve spike
- [ ] Çözüm iskeleti (sln, 3 proje, `global.json`), CI'da build → ilk `dotnet build` kontrolü
- [ ] İlk prototip: el görünüyor; çizim tıklaması alta sızmıyor; çift Shift'te tıklama alttaki uygulamaya ulaşıyor; Esc gerçek imleci geri getiriyor
- [ ] Spike: 4K fullscreen `UpdateLayeredWindow` @120 Hz — GDI+ önce mevcut makinede denenir (kare hızı, p95, CPU kaydı); takılma/yüksek CPU varsa kirli bölge ölçülür; ölçümler ve render kararı buraya yazılır
- [ ] Hook yaşam döngüsü: ayrı mesaj döngüsü, callback süresi ölçümü, kapat/aç sonrası yeniden kurulum
- [ ] Tray simgesi + aç/kapa menüsü (Ctrl+Alt+H)
- [ ] Test matrisi: çizim / toolbar / click-through (elin opak kısmı dahil) / fare basılıyken mod değişimi / Esc; farklı DPI'lı iki monitör; ekran kilidi (Win+L)

## M2 — Çizim
- [ ] Mürekkep çizimi (GDI+)
- [ ] Ctrl basılıyken ok ucu *(Ctrl onayı bekliyor)*
- [ ] Fade animasyonu
- [ ] Ctrl+Alt+X / Esc kısayolları

## M3 — Etkileşim kalitesi
- [ ] Çift Shift click-through (M1 prototipinde doğrulanan davranışın ürün kalitesine taşınması)
- [ ] Basılı interact tuşu → geçici click-through (`interactKeepsHand` ile)
- [ ] Toolbar (36 stil + lazer + ses + çıkış)
- [ ] TapSound (NAudio)
- [ ] Busy/idle fps yönetimi

## M4 — Sistem entegrasyonu
- [ ] `tinyhand://` protokol + named pipe tek-instance
- [ ] state.json yazımı (styles listesi dahil; normal çıkışta silinir — sözleşme)
- [ ] Ayarlar penceresi (interact tuşu, `interactKeepsHand` dahil)
- [ ] Welcome penceresi
- [ ] HandMaker custom PNG akışı (PNG doğrulama + compose kodunun C# portu)

## M5 — Dağıtım
- [ ] Velopack kurulumu + öz-güncelleme (`releases.<channel>.json` + `.nupkg` tam/delta paketleri Release'e dahil)
- [x] Kod imzalama: imzalanmayacak (karar verildi; SmartScreen uyarısı belgelenir, `uiAccess` bu nedenle planda yok)
- [ ] GitHub Actions windows işi: Windows varlıkları macOS işinin oluşturduğu aynı GitHub Release'e eklenir (tek yayın adımı)
- [ ] Çok monitör + DPIv2 + %150 test geçişi

## Açık kararlar
- [ ] Ok ucu tuşu: Ctrl onayı
- [x] Çift Shift'te imleç: macOS referansı — el görünür kalır, gerçek imleç gizli (M1 prototipinde doğrulanacak)
- [x] Hedef işletim sistemi: yalnızca Windows 11 x64; Windows 10 desteklenmez

## Günlük (bitince buraya ekleyin)
| Tarih | Not |
|---|---|
| (henüz kayıt yok) | |
