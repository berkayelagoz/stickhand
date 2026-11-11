# Windows Port — İlerleme

Durum: **başlangıç**. Plan: [PLAN.md](./PLAN.md)
Güncelleme kuralı: bir madde bitince `[x]` yapılır, tarih ve kısa not eklenir.

## M1 — İskelet ve spike
- [ ] `windows/` çözüm iskeleti (sln, 3 proje, build CI'da)
- [ ] Spike: 4K fullscreen `UpdateLayeredWindow` @120 fps ölçümü
- [ ] Tray simgesi + aç/kapa menüsü (Ctrl+Alt+H)
- [ ] `WH_MOUSE_LL` tıklama yutma + `LLMHF_INJECTED` ayıklama
- [ ] Overlay: layered window + el çizimi + gerçek cursor gizli

## M2 — Çizim
- [ ] Mürekkep çizimi (GDI+)
- [ ] Ctrl basılıyken ok ucu *(Ctrl onayı bekliyor)*
- [ ] Fade animasyonu
- [ ] Ctrl+Alt+X / Esc kısayolları

## M3 — Etkileşim kalitesi
- [ ] Çift Shift click-through
- [ ] Toolbar (36 stil + lazer + ses + çıkış)
- [ ] TapSound (NAudio)
- [ ] Busy/idle fps yönetimi

## M4 — Sistem entegrasyonu
- [ ] `tinyhand://` protokol + named pipe tek-instance
- [ ] state.json yazımı
- [ ] Ayarlar penceresi
- [ ] Welcome penceresi
- [ ] HandMaker custom PNG akışı

## M5 — Dağıtım
- [ ] Velopack kurulumu + öz-güncelleme
- [ ] Kod imzalama *(sertifikaya bağlı)*
- [ ] GitHub Actions windows işi
- [ ] Çok monitör + DPIv2 test geçişi
- [ ] (Opsiyonel) `uiAccess="true"`

## Açık kararlar
- [ ] İmzalama sertifikası (EV/OV)
- [ ] Ok ucu tuşu: Ctrl onayı

## Günlük (bitince buraya ekleyin)
| Tarih | Not |
|---|---|
| (henüz kayıt yok) | |
