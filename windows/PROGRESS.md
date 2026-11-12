# Windows Port — İlerleme

Durum: **M1 kodu tamam; etkileşim senaryoları manuel doğrulamada.** Plan: [PLAN.md](./PLAN.md)
Güncelleme kuralı: bir madde bitince `[x]` yapılır, tarih ve kısa not eklenir.

## M1 — İskelet ve spike
- [x] Kurulum: .NET 10 SDK (10.0.404) kuruldu, `dotnet --list-sdks` doğrulandı (12 Kasım 2026). Windows 11 x64 makine; NAudio/Velopack kurulum gerektirmez (NuGet), CI SDK'ı `actions/setup-dotnet` ile açıkça kurar.
- [x] Çözüm iskeleti: `TinyHand.sln` (slnx değil, klasik sln), `global.json` (10.0.404, rollForward patch), 3 proje (`TinyHand` WinForms tray host + overlay, `TinyHand.Interop` P/Invoke, `TinyHand.Tests` xUnit). İlk `dotnet build` başarılı; 18 birim testi geçiyor. CI: `build.yml`'e `build-windows` işi eklendi (SDK açıkça kurulur, build+test) — ilk CI koşusu bir sonraki push'ta (12 Kasım 2026).
- [x] Girdi/imleç prototipi **(kod tamam; etkileşim doğrulaması Tablo'ya kalandı — otomatik koşulamaz, etkileşimli)** (12 Kasım 2026):
  - `WH_MOUSE_LL`/`WH_KEYBOARD_LL` kendi mesaj döngülü ayrı iş parçacığında (`LowLevelHooks`); kendi `SendInput` olayları `dwExtraInfo` imzasıyla ayıklanır (`0x54494E59_48414E44`; `LLMHF_INJECTED` tek başına ölçüt değil).
  - Overlay: monitör başına layered window (`WS_EX_LAYERED|NOACTIVATE|TOPMOST|TOOLWINDOW`), 32bpp PARGB GDI+ yüzey → `UpdateLayeredWindow`; el imleç konumunda (M1: `hand_black.png`, parmak ucu ~üst-orta pin; per-style `tip/tipX` M4'te taşınır), DPI ölçekli (per-monitor v2 manifest).
  - Çizim modunda tıklamalar hook tarafından yutulur; toolbar (birincil monitör üstü) hit-test hook içinden yapılır, komutlar overlay'e kuyraktan iletilir — yutulan olay pencere mesajı gelmez, planla uyumlu.
  - **İmleç gizleme mekanizması (M1'in doğrulama hedefi):** çizim modunda overlay'i ölçülmeden görünmez kılan **alfa=1 arka plan** hit-test'i overlay'e alır → `WM_SETCURSOR` + `SetCursor(NULL)`; ayrıca her karede `SetCursor(NULL)` (yutulan olaylardan hiç WM_SETCURSOR üretilmese diye). Click-through modunda `WS_EX_TRANSPARENT` overlay'i hit-test'ten çıkarır (opak el pikselleri dahil geçer) ve gerçek imleç **`SetSystemCursor` küresel boş imleç** setiyle gizli kalır (kayıtlar korunur, Esc/kapatışta geri kurulur). Bu iki mekanizma birlikte PLAN'daki imleç tablosunu gerçekler; el her iki modda görünür kalır.
  - Çift Shift (350 ms pencere, macOS `shiftDoubleMax=0.35`) → click-through geçişi; down/up eşleşmesi `MouseDragTracker` ile: up yalnızca bizim yuttuğumuz down'un ardından yutulur → mod değişse de down/up aynı hedefe gider.
  - Esc → konturu temizle + kapat + gerçek imleç geri (`SetCursor(ok)` + global set geri); Ctrl+Alt+H `RegisterHotKey` ile aç/kapa.
- [x] Hook yaşam döngüsü doğrulandı (`--hookcheck`, 12 Kasım 2026): kur → gözle → kapat → **yeniden kur** işlemleri iki geçişte başarılı; ayrı mesaj döngüsü çalışıyor. Callback süreleri (enjekte edilmiş gerçek olaylarla): fare p50 0.013 / p95 0.049 ms, max 1.6 ms; klavye p50 0.002 / p95 0.019 ms (`LowLevelHooksTimeout` limitinin ~10.000 katı altında). Not: Windows hook'u sessizce kaldırırsa otomatik kurtarma M1 kapsamı dışı (plan kararı) — kapat/aç yeniden kurar.
- [x] Spike: 4K `UpdateLayeredWindow` (12 Kasım 2026; makine DESKTOP-IGOSACC, Win11 10.0.26300, 16 çekirdek, **3840×2160 @ 60 Hz**). Hedef 120 Hz:
  | Sahne | Kare | Render avg | ULW avg | Toplam avg/p95 | CPU |
  |---|---|---|---|---|---|
  | Stress (tam çerçeve: gradyan+el+24 elips) | 30.3 fps | 29.8 ms | 3.25 ms | 33.0 / 39.8 ms | %6.2 |
  | Gerçekçi (M1 sahnesi, tam çerçeve) | 66.3 fps | 11.5 ms | 3.63 ms | 15.1 / 18.3 ms | %6.3 |
  | Kirli bölge (`UpdateLayeredWindowIndirect`, ~800×1200 el bölgesi) | 67.9 fps | 14.6 ms¹ | **0.04 ms** | 14.6 / 17.1 ms | %6.3 |
  ¹ Kirli bölge render'ı spike'ta bile en büyük kalemdi çünkü el örnek boyutu (800×1200) uygulamanın gerçeğinden ~4 kat büyük çizildi; ULWI present maliyeti tam çerçeveye göre **~90 kat** düştü. Kayıtlar: `spike-stress.json`, `spike-realistic.json`, `spike-dirty.json` (hepsi bu makinede 6–8 sn ölçüm).
  **Render kararı:** 4K tam çerçeve GDI+ boyası 120 Hz bütçesini (8.3 ms) aşar (11.5–30 ms); kirli bölge ULWI yolu pratikte bedelsiz present sağlıyor. Karar: **GDI+ kalır**; M2/M3'te çizim yolu kirli bölge tabanlı olacak (el + mürekkep deltaları; tam boyama yalnız aktivasyon/monitör geçişinde), Direct2D/DirectComposition'a geçiş gerekmedi. Ekranda 60 Hz olduğu için 120 Hz busy kompozisyon hedefi bu makinede karşılanamaz — busy hız hedefi monitör yenilemesine bağlanacak (M3 fps yönetimi).
- [x] Tray simgesi + aç/kapa menüsü **(kod tamam; manuel)** (12 Kasım 2026): `NotifyIcon` + menü (Aktif/Ctrl+Alt+H, Çıkış), Ctrl+Alt+H `RegisterHotKey` (MOD_NOREPEAT). Prototip otomatik etkin başlar (test kolaylığı).
- [x] Test matrisi — maddeler 1–5 `scripts/e2e-m1.ps1` ile **otomatik doğrulandı (10/10 PASS, 12 Kasım 2026)**. Harness gerçek girdi enjeksiyonu (~mouse_event/keybd_event) yapar, altta bir "logger" penceresi tıklama alıp almadığını sayar; `e2e-m1-result.txt` kaydı:
  1. Normal çizim: sürükleme tıklaması alta sızmıyor **(PASS, 0 olay)**
  2. Toolbar tıklaması: "Click-through" ve "Exit" hook içinden yönlendi **(PASS)**
  3. Çift Shift: tıklama alttaki uygulamaya, elin opak pikselleri üzerinden de gidiyor; el görünür, gerçek imleç gizli **(PASS)**
  4. Fare basılıyken mod değişimi: down/up her iki yönde aynı hedefe **(PASS — yutmalı ve geçişli senaryolar ayrı ayrı)**
  5. Esc → imleç hareket ettirilince geri döner; Ctrl+Alt+H aç/kapa döngüleri hook yeniden kurulumunu kanıtladı **(PASS)**. E2E sırasında bulunan ve düzeltilen hata: Esc/toolbar-exit `ActiveRequested` senkron durumunu güncellemiyordu → Ctrl+Alt+H ilk basışta yanlış yöne çeviriyordu; tüm deaktivasyon tek yol `SetActive(false)` üzerinden geçiyor.
  - **Görsel/elle kalansılar:** 6. madde: farklı DPI'lı iki monitör geçişi ve ekran kilidi (Win+L) — uygun donanım gerektirir, beklemede. İmleç görünürlüğü artık otomatik doğrulanıyor: `scripts/cursorcheck-m1.ps1` (7/7 PASS) — çizim modunda gerçek imleç baskılanıyor, click-through'da küresel imleç **tam şeffaf** (32bpp alpha=0; eski mono "boş" imleç siyah kutu render ediyordu), Esc/Ctrl+Alt+H sonrası şema **piksel-mükemmel** geri geliyor (kayıtlı kopya + `SPI_SETCURSORS` registry yeniden yükleme; baseline 112 opak piksel/alphaSum 26161 ile birebir). Toolbar üst-ortada çizili (piksel-diff kanıtı).
  - **Bilinen sınırlar (M1 notları):** tek-instance koruması M4'te; birden çok TinyHand çalıştırılırsa hook'lar üst üste biner (Elle test sırasında iki instance açık kalmış — o bahattır). Anormal ölümde imleç seti boş kalırsa: `SystemParametersInfo(SPI_SETCURSORS)` (harness da temizlikte çağırır).
- **Çıkış kriteri durumu:** 1–5 senaryoları otomatik E2E ile geçti (10/10); kalan: imleç gizlemenin görsel onayı, çift-DPI ve Win+L maddeleri (donanım/görsel kontrol gerektirir).

### M1 sonuç notları (protipten çıkan teknik bulgular, üst bileşenlere taşınacak)
- Yutulan tıklama overlay'e pencere mesajı üretmez; toolbar ve çizim yönlendirmesi hook içinden yapılmalı (plan varsayımı doğruydu).
- Alfa=1 arka plan, overlay'i tüm monitörde hit-test'e alır → `WM_SETCURSOR`/`SetCursor(NULL)` imleci gizleme yöntemi çalışır; ancak üstte başka topmost pencere gelirse imleç görünür kalabilir (öntanımlı kabul).
- Click-through'da gerçek imleci gizli tutmanın Windows'ta tek güvenilir yolu `SetSystemCursor` (global set; kopyaları saklanır, çıkışta geri kurulur). Anormal sonlanmada imleç boş kalabilir — kurtarma: `SystemParametersInfo(SPI_SETCURSORS)` (M5'te belgeye).
- `SetCursorPos` LL hook'a olay üretmez; `mouse_event`/`SendInput` üretir (LLMHF_INJECTED) — ölçüm ve ilerideki SendInput işleri için önemli.
- Güçlü kısayol kombinasyonu yok; Esc ve Ctrl+Alt+H her senaryodan çıkmayı garantiler.

## M2 — Çizim
- [ ] Mürekkep çizimi (GDI+, kirli bölge)
- [ ] Ctrl basılıyken ok ucu *(Ctrl onayı bekliyor)*
- [ ] Fade animasyonu
- [ ] Ctrl+Alt+X / Esc kısayolları

## M3 — Etkileşim kalitesi
- [ ] Çift Shift click-through (M1 prototipinde doğrulanan davranışın ürün kalitesine taşınması)
- [ ] Basılı interact tuşu → geçici click-through (`interactKeepsHand` ile)
- [ ] Toolbar (36 stil + lazer + ses + çıkış)
- [ ] TapSound (NAudio)
- [ ] Busy/idle fps yönetimi (busy hedefi monitör yenilemesine göre)

## M4 — Sistem entegrasyonu
- [ ] `tinyhand://` protokol + named pipe tek-instance
- [ ] state.json yazımı (styles listesi dahil; normal çıkışta silinir — sözleşme)
- [ ] Ayarlar penceresi (interact tuşu, `interactKeepsHand` dahil)
- [ ] Welcome penceresi
- [ ] HandMaker custom PNG akışı (PNG doğrulama + compose kodunun C# portu)

## M5 — Dağıtım
- [ ] Velopack kurulumu + öz-güncelleme (`releases.<channel>.json` + `.nupkg` tam/delta paketleri Release'e dahil)
- [x] Kod imzalama: imzalanmayacak (karar verildi; SmartScreen uyarısı belgelenir, `uiAccess` bu nedenle planda yok)
- [ ] GitHub Actions windows işi: publish + Velopack → Windows varlıkları macOS işinin oluşturduğu aynı GitHub Release'e eklenir (tek yayın adımı)
- [ ] Çok monitör + DPIv2 + %150 test geçişi

## Açık kararlar
- [ ] Ok ucu tuşu: Ctrl onayı
- [x] Çift Shift'te imleç: macOS referansı — el görünür kalır, gerçek imleç gizli (M1 prototipinde Windows tarafında uygulandı: `SetSystemCursor` yolu)
- [x] Hedef işletim sistemi: yalnızca Windows 11 x64; Windows 10 desteklenmez
- [x] M1 spike render kararı: GDI+ + kirli bölge ULWI; Direct2D/DirectComposition gerekli değil (ölçümler yukarıda)

## Günlük (bitince buraya ekleyin)
| Tarih | Not |
|---|---|
| 12 Kasım 2026 | M1 kodu tamamlandı: iskelet + interop (hook, layered window, ULW/ULWI, imleç stratejisi) + prototip overlay + tray; 18 test geçti; `--hookcheck` (kur/kapat/yeniden kur + callback ölçümleri) ve 4K spike (stress/gerçekçi/kirli bölge) çalıştırıldı; render kararı verildi (GDI+ kalır). Etkileşim matrisi maddeleri (çizim yutma, toolbar, çift Shift, down/up, Esc, çift DPI) manuel doğrulamada. |
| 12 Kasım 2026 | E2E harness (`scripts/e2e-m1.ps1`) eklendi; test matrisi 1–5 otomatik doğrulandı (10/10 PASS). Bulunan/düzeltilen: Esc/toolbar-exit `ActiveRequested`'i bozuyordu → tüm deaktive tek yol `SetActive(false)`; DPI ölçek `%150` tam sayı bölme hatası düzeltildi (yuvarlama). Kalan: imleç gizleme görsel onay, çift-DPI, Win+L (donanım gerektirir). |
| 12 Kasım 2026 | Kullanıcı raporu üzerine ikinci imleç hata seti düzeltildi ve `scripts/cursorcheck-m1.ps1` (7/7 PASS) eklendi: (1) "boş" imleç mono `CreateCursor` yerine 32bpp alpha=0 `CreateIconIndirect` — eskisi siyah kutu render ediyordu; (2) şema orijinalleri süreç başına yalnız bir kez kaydediliyor — eski kod her click-through bölümünde zaten-boşatılmış imleci "orijinal" diye kaydedip restore'yu zehirliyordu (Ctrl+Alt+H/Esc sonrası imleç dönmüyordu, siyah kutu kalıyordu); (3) her restore'da `SPI_SETCURSORS` ile registry şeması yeniden yükleniyor → piksel-mükemmel dönüş (baseline 112 opak piksel/alphaSum 26161 ile birebir). Toolbar sabitleri büyütüldü (4K'da görünür; E2E geometrisiyle senkron). Kalan manuel: çift-DPI, Win+L. |
