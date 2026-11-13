# TinyHand — Windows Port Planı

Kaynak: macOS uygulaması (`Sources/TinyHand/`, Swift/AppKit). Hedef: özellik eşliği, tek depoda iki ayrı uygulama. Kod tabanı paylaşılmaz; eşliği garanti eden ortak davranış sözleşmeleri ve varlıklar (aşağıda ayrı bölüm).

## Kararlaştırılmış teknoloji

| Katman | Seçim |
|---|---|
| Dil / runtime | C# / .NET 10 (LTS; .NET 8 desteği 10 Kasım 2026'da sona erdi). SDK sürümü `global.json` ile sabitlenir; CI'da açıkça kurulur |
| Ana süreç, tray, ayarlar | WinForms (`NotifyIcon` + mesaj pompası) |
| Overlay (monitör başına) | Ham Win32: `WS_EX_NOREDIRECTIONBITMAP \| WS_EX_NOACTIVATE \| WS_EX_TOPMOST` + DirectComposition target/visual (M1 prototipi: `WS_EX_LAYERED` layered window; geçiş M1.5'te) |
| Overlay çizim | Direct2D (D3D11) → DXGI flip-model swap chain → DirectComposition; kompozisyon timer'ı (busy hedef monitör yenilemesi, idle 20 Hz). M1 prototipi: GDI+ → `UpdateLayeredWindow` (M1.5'te değişir) |
| Girdi yakalama | P/Invoke: `WH_MOUSE_LL`, `WH_KEYBOARD_LL` (`SetWindowsHookEx`); hook'lar tray/overlay pompasından izole, kendi mesaj döngüsü olan ayrı iş parçacığında |
| İmleç gizleme | `WM_SETCURSOR` + boş imleç; `Cursor = None` tek başına tasarım kararı değil (bkz. eşleme tablosu ve M1) |
| Global kısayollar | `RegisterHotKey` (Ctrl+Alt+H, Ctrl+Alt+X); Esc hook'tan |
| Otomasyon | `tinyhand://` protokolü (HKCU `Software\Classes`), tek-instance named pipe, `%APPDATA%\TinyHand\state.json` |
| Ses | NAudio |
| Kurulum / öz-güncelleme | Velopack (delta güncelleme dosyaları dahil) |
| CI | `windows-latest` işi: `dotnet publish -r win-x64 --self-contained` + Velopack bundle; Windows varlıkları macOS işinin zaten oluşturduğu GitHub Release'e aynı yayın adımında eklenir |

Elenen alternatifler: WPF overlay (`AllowsTransparency` + fullscreen topmost + 120 Hz yavaş), Rust (geliştirme hızı), WinUI/Tauri (interop yükü, kazanım yok), GDI+ → ULW/ULWI sunumu (M1'in derin ölçümleriyle elendi — present DWM redirection senkronunda değişken bloklanıyor; bkz. spike bölümü sonuç notu ve M1.5).

## M1 öncesi netleşecek karar: tıklama yönlendirme ve imleç görünürlüğü

M1'in asıl sınaması, el çizilmesiyle birlikte gerçek imlecin, tıklamanın ve pencere odağının doğru yönetilmesi. Prototipten şu kuralların çıkması gerekir:

| Etkileşim | Tıklama nereye gider | Gerçek imleç |
|---|---|---|
| Çizim (overlay aktif) | Hook yutar; alttaki uygulama almaz | Gizli |
| Toolbar bölgesi | Hook yutar; komut hook içinden overlay'e iletilir — yutulan olay overlay'e pencere mesajı olarak gelmez | Gizli |
| Çift Shift click-through | Hook yutmaz; alttaki uygulama alır | macOS referansı: gizli — TinyHand eli görünür kalır; M1 prototipinde doğrulanır |
| Interact tuşu basılıyken geçici click-through (M3'te uygulandı) | Hook yutmaz; alttaki uygulama alır | `interactKeepsHand` ayarına bağlı: false (macOS varsayılanı) ise gerçek imleç görünür, el gizlenir; true ise el kalır |
| Esc / kapatma | Normal davranış | Geri döner |

Layered window'da alfa değeri 0 olan pikseller hit-test'i alttaki pencereye geçirir; bu yüzden gizleme (imleç) ve geçirme (hit-test) ayrı şeylerdir ve M1 prototipinde birlikte doğrulanır. Click-through modunda hook'un tıklamayı bırakması tek başına yeterli değil: hit-test, elin **opak** piksellerini de overlay'e yönlendirebilir. Geçiş pencere stili düzeyinde yönetilmelidir (ör. `WS_EX_TRANSPARENT`); hangi yöntemin opak alanı da geçirdiği M1 prototipinde ölçülür, varsayım sayılmaz. Fare basılıyken mod değişirse down ve up olaylarının farklı pencerelere gitmediği de aynı prototipte doğrulanır.

## Bileşen eşlemesi (macOS → Windows)

| macOS | Windows | Not |
|---|---|---|
| `InputTap` (CGEventTap) | `WH_MOUSE_LL`/`WH_KEYBOARD_LL` | Yutma: hook'tan `1`. `LLMHF_INJECTED` yalnızca kendi olaylarında set olmaz — başka süreçlerin enjekte ettiği olaylarda da set olur; bu yüzden TinyHand'in `SendInput` ile enjekte ettiği olaylar `dwExtraInfo` imzasıyla ayıklanır, yoksa döngü. `maxWaitMs` kaynakla aynı anlamda korunur: olayın kaynaktan hook'a ulaşma gecikmesi (kaynaktaki ~300 ms varsayımı taşınmaz); hook callback süresi ayrı ölçülür. `LowLevelHooksTimeout` aşımında Windows hook'u bildirim vermeden kaldırır; M1'de aç/kapat sonrası yeniden kurulum doğrulanır, otomatik kurtarma M1 kapsamı dışıdır. Accessibility izni gerekmez |
| `CursorHider` (private CGS) | `WM_SETCURSOR` + boş imleç | Hook bir olayı yuttuğunda overlay o olayı pencere mesajı olarak almaz; layered window'un alfa 0 alanları hit-test'i alttaki pencereye geçirir. `Cursor = None` tek başına yeterli değil; imleç gizleme + click-through M1 prototipinde birlikte doğrulanır |
| `OverlayController` (NSWindow) | Layered window, monitör başına | `Screen.AllScreens` |
| `HotKeyCenter` (Carbon) | `RegisterHotKey` | |
| `Ink` (CAShapeLayer) | Direct2D vektör çizim (M1 prototipi GDI+), fade animasyonu timer'ı | Gölge: önceden blur'lanmış sprite (gerçek zamanlı blurdan kaçınılır) |
| ⌘ basılı ok ucu | Ctrl (Windows tuşu Start menüsünü açar) | Açık uçlu: kullanıcı onayı |
| Çift Shift click-through | LL keyboard hook, aynı mantık | İlk doğrulama M1 prototipinde |
| `interactKey` basılıyken geçici click-through (macOS varsayılan ⌥) | Aynı mantık, ayarlanabilir tuş (varsayılan Alt) | `interactKeepsHand` ayarı: true ise el kalır, false (varsayılan) ise gerçek imleç görünür. Davranış M3, ayarı M4. Windows'ta modifier **yutulmaz** (keyup'ı yutmak sistem tuş durumunu kilitler); tek başına Alt tap'i alttaki uygulamanın menü çubuğunu vurgular — belgelenmiş davranış |
| NSStatusItem | `NotifyIcon` + `ContextMenuStrip` | Açık/koyu taskbar için registry teması |
| `Toolbar` | Overlay içine çizili + hit-test bölgesi | `passThroughRects` mantığı korunur |
| `Automation` | Protokol kaydı + named pipe + state.json | Protokol ikinci instance doğurur → mutex + komut iletme. Davranış sözleşmesi: state.json styles listesini yayımlar, `style/<id>` overlay'i açar, normal çıkışta dosya silinir |
| Sparkle `Updater` | Velopack (delta dahil) | |
| `HandMaker` | C# portu gerekli | Prompt şablonu platform-bağımsız; PNG doğrulama + işleme (compose, mürekkep rengi çıkarımı) ImageIO/CoreGraphics ile yapıldığından Windows'a taşınacak iş var. Kurallar birebir taşınır (bkz. sözleşmeler bölümü) |

## Windows'a özgü tuzaklar

1. **DPI**: per-monitor v2 aware manifest; el/mürekkep ölçeği monitör DPI'sına göre çarpılır. En hızlı hata kaynağı. Farklı DPI'lı iki monitörde geçiş ve ekran kilidi (Win+L) M1 test matrisinde doğrulanır.
2. **UAC / yükseltilmiş pencereler**: overlay üstlerinde çizemez. Tüm aşamalarda belgelenmiş sınırlama olarak kabul edilir. `uiAccess="true"` imzalı ikili + Program Files gerektireceği için imzasız dağıtım kararıyla çelişir; plandan çıkarıldı.
3. **Antivirüs / SmartScreen**: global hook AV heuristiği tetikleyebilir. Karar gereği sertifika alınmıyor; SmartScreen uyarısı kullanıcıya belgelenir, AV false positive riski kabul edilir.
4. **Hook gecikmesi ve sessiz kaldırma**: hook callback'i birkaç ms'nin altında tutulur ve ayrıca ölçülür (kaynağa ulaşma gecikmesinden farklı metrik). Hook'lar kendi mesaj döngülü ayrı iş parçacığında çalışır. `LowLevelHooksTimeout` aşımında Windows hook'u bildirim vermeden kaldırır; uygulamaya bildirim mekanizması yoktur. M1'de doğrulanan set: ayrı mesaj döngüsü çalışıyor, callback süresi ölçülüyor, kapatıp açınca hook'lar yeniden kuruluyor. Otomatik kurtarma (ör. `SendInput` ile periyodik kontrol) M1 kapsamı dışıdır; ileride gerek görülürse ayrı değerlendirilir.
5. **Görev çubuğu**: overlay penceresine `WS_EX_TOOLWINDOW`.

## Ortak davranış sözleşmeleri (tek depo, iki uygulama)

Swift/AppKit ve C#/WinForms aynı depoda iki ayrı uygulamadır; kod paylaşımı yok. Eşliği garanti eden sözleşmeler, iki tarafta birebir aynı olmalı:

- **state.json** (macOS: `~/Library/Application Support/TinyHand/` — Windows: `%APPDATA%\TinyHand\`): pid, aktiflik, seçili stil, ses **ve styles listesi** (custom eller dahil, değişince yeniden yazılır). `style/<id>` komutu overlay'i açar; **normal çıkışta dosya silinir**.
- **URL komut grameri**: `tinyhand://on | off | toggle | clear | style/<id> | sound/<arg>`; aynı komut seti, aynı bilinmeyen komut davranışı.
- **Stil kümesi**: 36 yerleşik stil + laser + custom PNG eller; aynı id'ler ve sıra (toolbar her iki tarafta aynı el kümesini gösterir). Yerleşik asset PNG'ler (macOS `assets/hand_*.png`) **el + sap compose'ı içerir** — el, hafif eğik uzun bir sap/stem ile tutulur (kullanıcı görsel referansı, 13 Kasım); aynı PNG'ler Windows tarafına gömülür, sap görünümü ek iş gerektirmez.
- **HandMaker çıktı sözleşmesi**: aynı prompt şablonu; PNG doğrulaması aynı kurallarla (şeffaf arka plan zorunlu, portre kare, parmak ucu en yüksek nokta, tam el görünür); compose çıktısı aynı biçimde el + sap; mürekkep rengi aynı algoritmayla çıkarılır. C# portu bu kuralları birebir izler.
- **Click-through ve imleç**: çift Shift kalıcı click-through modunda TinyHand eli görünür kalır, gerçek imleç gizli kalır. Ayarlanabilir interact tuşu (macOS varsayılan ⌥) basılıyken geçici click-through; `interactKeepsHand` true ise el görünür kalır, false (varsayılan) ise gerçek imleç görünür ve el gizlenir.
- **Mürekkep rengi**: `Preferences.InkChoice` birebir — varsayılan `.match` = seçili el stilinin rengi ("el hangi renkse o renkte çizer"); sabit seçenekler red/orange/yellow/green/blue/purple(`#8754F2`)/white/black; stil renkleri `PointerStyle` tablosundaki hex değerleridir.
- **Fade**: `Preferences.fadeSeconds` aynı varsayılan ve aralık.
- **Ses**: aynı tap sesi seti — macOS tarafında gömülü `tap_*.wav` dosyaları: thock, click, tock, clack, typewriter, snap, wood block, pop, boop, blip, coin, pew, squeak, quack, chirp (15 ses; aynı dosyalar depodaki `assets/` altından Windows'a gömülür), davranış: tıklamada çalma.

## Erken doğrulanacak varsayım (M1 spike)

4K fullscreen `UpdateLayeredWindow` @ 120 Hz maliyeti. Ölçüm eşik dayatmak için değil, karar vermek içindir:

1. Önce **GDI+ mevcut makinede denenir**. Ölçüm kaydı tutulur (referans makine, monitör yenileme hızı, görüntülenen kare hızı, p95 kare süresi, CPU kullanımı), ama %90 fps / 10 ms / %5 CPU gibi eşikler önceden geçiş şartı yapılmaz.
2. **Görünür takılma veya yüksek CPU kullanımı** varsa kirli bölge güncellemesi (`UpdateLayeredWindowIndirect`) ölçülür.
3. Sonuç yetersizse Direct2D + `DirectComposition` kararı o zaman verilir — bu geçiş yüzey ve sunum yönetimini de etkilediğinden ayrı bir teknik karardır (tek render sınıfı sınırları içinde kalsa da).

Ölçüm değerleri ve render kararı PROGRESS.md'de spike maddesinin yanına yazılır.

**Spike sonucu ve karar (12 Kasım 2026):** İlk spike kararı "GDI+ kalır, kirli bölge ULWI" idi. M1 sırasında eklenen kare-süresi ölçümleri (HUD: periyot/draw/present halkaları, `--fpscheck`) bu kararı revize etti: ULW/ULWI present çağrıları bu makinede DWM redirection senkronunda **değişken 7–16 ms** bloklanıyor; kirli-bölge present ile gerçek el kadansı ~30 fps'e düşüyor (fps sayacı 60 gösterse de), tam-çerçeve present ile 60 fps'e sığınabiliyor ama periyot p95 21–27 ms'ye dağılıyor. Flip-model sunum (DXGI `Present`) DWM'e asenkron kuyruklanır ve bu bloklamayı ortadan kaldırır. Karar: **M2'ye geçmeden Direct2D + DirectComposition geçişi — M1.5 kilometre taşı.** Ölçüm kayıtları PROGRESS.md günlüğünde (12 Kasım fps turu).

## Proje yapısı

```
windows/
├── PLAN.md              ← bu dosya
├── PROGRESS.md          ← ilerleme izleme
├── global.json          ← .NET SDK sürümü sabitleme
├── TinyHand.sln
│   ├── TinyHand/           (WinForms tray host)
│   ├── TinyHand.Interop/   (P/Invoke: hook, layered window, protokol)
│   └── TinyHand.Tests/
└── installer/              (Velopack config)
```

## Kilometre taşları

### M1 — İskelet ve spike

#### Kurulum gereksinimleri (geliştirmeye başlamadan önce)

Geliştirme makinesi (Windows 11 x64 — hedef işletim sistemi yalnızca Windows 11 x64'dür, Windows 10 desteklenmez):

- [x] .NET 10 SDK (`winget install Microsoft.DotNet.SDK.10`; WindowsDesktop/WinForms iş yükünü içerir) — runtime tek başına yeterli değildir *(kurulu, 10.0.404 — 12 Kasım 2026)*
- [x] Git (kurulu) *(12 Kasım 2026)*
- [x] `dotnet --list-sdks` çıktısında .NET 10 SDK göründüğünü doğrula *(12 Kasım 2026)*
- [x] İlk `dotnet build` doğrulaması çözüm iskeleti oluşturulduktan sonra yapılır (aşağıdaki ilk M1 görevi); iskelet yokken build kontrolü anlamsızdır *(başarılı; 18 birim testi geçti — 12 Kasım 2026)*

Kurulumsuz kalanlar:

- NAudio, Velopack: NuGet / `dotnet tool install` ile otomatik; ayrı kurulum gerekmez
- GDI+ / Win32 API'leri: Windows'un kendi bileşenleri, kurulum yok
- CI (`windows-latest`): .NET 10 SDK `global.json`'daki sürümle açıkça kurulur (`actions/setup-dotnet`); runner imajındaki varsayılan SDK'ya güvenilmez

> **Kural:** Yukarıdaki gereksinimler kurulup doğrulandıktan **sonra** M1 geliştirmesine başlanır. Gereksinim eksikken kod yazılmaz.

M1 görevleri:

- [x] Çözüm iskeleti (sln, 3 proje, `global.json`), CI'da build → ilk `dotnet build` kontrolü bu görevle yapılır *(12 Kasım 2026; `build-windows` CI işi eklendi; kayıt PROGRESS.md M1)*
- [x] Girdi/imleç prototipi (tek prototipte birlikte) *(12 Kasım 2026; alt maddelerin tamamı `scripts/e2e-m1.ps1` ile otomatik doğrulandı — 10/10; ayrıntılar PROGRESS.md M1)*:
  - `WH_MOUSE_LL`: aktifken tıklamayı yut; kendi `SendInput` olayları `dwExtraInfo` imzasıyla ayıklanır (`LLMHF_INJECTED` başka süreçler için de set olur, tek başına ölçüt değil); hook'lar ayrı mesaj döngülü iş parçacığında
  - Overlay: monitör başına layered window, imleç konumunda el çizimi *(M1.5'te DirectComposition'a taşındı)*
  - Çizim sırasında alttaki uygulama tıklanmaz; toolbar bölgesi hit-test → komut hook'tan iletilir
  - Çift Shift → alttaki uygulamaya tıklama (click-through); tıklama, elin **opak** piksellerinin üzerinde de hedef uygulamaya gider (yalnızca hook'un olayı bırakması yetmez — hit-test de geçmeli)
  - Fare basılıyken mod değişirse down ve up olayları aynı pencereye gider
  - Esc → temizle + kapat + gerçek imlecin geri gelmesi; gerçek imleç aktifken `WM_SETCURSOR` ile gizli *(imleç görünürlüğü `scripts/cursorcheck-m1.ps1` ile otomatik doğrulandı — 7/7)*
- [x] Hook yaşam döngüsü: ayrı mesaj döngüsünün çalıştığı, callback süresinin ölçüldüğü ve kapatıp açınca hook'ların yeniden kurulduğu doğrulanır *(12 Kasım 2026; `--hookcheck`)*
- [x] Spike: 4K fullscreen `UpdateLayeredWindow` @120 Hz — GDI+ önce mevcut makinede denenir (görüntülenen kare hızı, p95 kare süresi, CPU kullanımı kaydedilir); takılma/yüksek CPU varsa kirli bölge (`UpdateLayeredWindowIndirect`) ölçülür; DirectComposition kararı sonuca bırakılır *(12 Kasım 2026; ölçümler PROGRESS.md'de; sonuç → M1.5 kararı)*
- [x] Tray simgesi + aç/kapa menüsü (Ctrl+Alt+H) *(12 Kasım 2026)*
- [x] Test matrisi: normal çizim, toolbar tıklaması, çift Shift ile alttaki uygulamaya tıklama (elin opak kısmı dahil), fare basılıyken mod değişimi (down/up aynı pencereye), Esc ile imleç dönüşü; farklı DPI'lı iki monitör arasında geçiş; ekran kilidi (Win+L) sonrası overlay/imleç/hook davranışı *(ilk 5 madde + imleç görünürlüğü otomatik doğrulandı: e2e 10/10, cursorcheck 7/7 — 12 Kasım 2026; **çift-DPI ve Win+L donanım gerektiriyor, M5'in çok monitör test geçişine taşındı**)*
- **Çıkış kriteri (karşılandı, 12 Kasım 2026):** TinyHand eli görünüyor; çizim tıklaması alta sızmıyor; çift Shift'te tıklama alttaki uygulamaya ulaşıyor; Esc gerçek imleci geri getiriyor. Ek doğrulamalar: Ctrl+Alt+H ile açılıp kapanıyor, hook kapatıp açınca yeniden kuruluyor, test matrisi (opak piksel, down/up, farklı DPI'lı monitör geçişi, ekran kilidi) geçiyor *(çift-DPI ve Win+L hariç — M5'e taşındı)*; spike ölçümleri ve render kararı PROGRESS.md'de kayıtlı.

### M1.5 — Render yüzeyi geçişi (Direct2D + DirectComposition)
Karar gereği M2'den önce uygulanır; gerekçe spike bölümündeki sonuç notu (ULW/ULWI present bloklaması ölçümleri). Girdi/imleç/hook katmanı ve kompozisyon döngüsü mimarisi değişmez; yalnız boyama ve sunum değişir (tek render sınıfı sınırları içinde).

- [x] Overlay pencere stili: `WS_EX_LAYERED` → `WS_EX_NOREDIRECTIONBITMAP`; D3D11 cihaz + DXGI flip-model swap chain + DirectComposition cihaz/target/visual iskeleti, monitör başına (13 Kasım 2026)
- [x] El sprite, mürekkep, toolbar, HUD çizimlerinin Direct2D portu (M1 sahnesi görsel olarak birebir; DPI ölçekleme korunur) (13 Kasım 2026). Mürekkep, ilk biçimin O(toplam mürekkep) kare çizimi (her karede tüm segment `DrawLine` + nokta listesi kopyası) uzun çizimlerde fps düşürdüğü için kalıcı `InkLayer` bitmap'ine taşındı: hook→`InkJournal`→monitör başına artımlı boya, karede tek blit; kare maliyeti O(yeni nokta)
- [x] İmleç gizleme ve click-through mekanizmaları aynen kalır: `WM_SETCURSOR` + boş imleç; click-through stili gerçeklemede `WS_EX_TRANSPARENT` **tek başına** `WS_EX_NOREDIRECTIONBITMAP` pencerede hit-test'i geçirmiyor (M1.5 e2e turunda ölçüldü) — `WS_EX_TRANSPARENT | WS_EX_LAYERED` çiftiyle (DComp görselini bozmaz, pixel probe ile doğrulandı) + `WM_NCHITTEST → HTTRANSPARENT` aynı-iş parçacığı yedekleme yoluyla gerçeklendi; `WS_EX_LAYERED` yalnız click-through bölümlerinde eklenir. E2E ve cursorcheck ile yeniden doğrulandı *(13 Kasım 2026)*
- [x] Kompozisyon döngüsü (yenilemeye kilitli ticker) ve ölçüm altyapısı (HUD, fpscheck, halkalar) aynen taşınır (13 Kasım 2026; kirli-bölge ULW kodu kalktı — flip modelde kalıcı yüzey yok, her kare tam çerçeve)
- **Çıkış kriteri (karşılandı, 13 Kasım 2026):** present kuyruğa bırakma <1 ms'de, bloklama yok; 60 Hz ekranda kare periyotu p95 ~17 ms civarı sabitleşir; `fpscheck` wiggle + sweep senaryolarında görsel takılma kaybolur; e2e-m1 10/10, cursorcheck 7/7, birim testleri geçer; ölçümler PROGRESS'e kaydedilir.

### M2 — Çizim
- [x] Sürükle → mürekkep çizimi (Direct2D; M1.5 sonrası), renk/kalınluk ölçeği *(13 Kasım 2026)*
  - Görsel sözleşme (macOS `Ink.swift` + `Preferences.InkChoice` — 13 Kasım 2026 kaynak taraması): **renk `InkChoice` ile belirlenir** — varsayılan `.match` = seçili el stilinin rengi (`PointerStyle` tablosundaki hex; kullanıcı gözlemi: "el hangi renkse o renkte çiziyor"); sabit seçenekler red/orange/yellow/green/blue/purple(`#8754F2`)/white/black (`Preferences.inkChoice`, ayarı M4'te); stroke gölgesi siyah, opacity 0.18, radius 1.5, offset (1, −1.5); yuvarlak cap/join; **yumuşatılmış path** (macOS `smoothPath`) + **1.5 pt mesafe dedup** (`extend`); kalınlık `max(3, monitörYüksekliği × 0.0055)` (`ArmView.inkWidth`), DPI ile ölçekli
  - InkLayer mimarisi per-stroke fade'ye hazır: macOS da stroke bitince bitmap'e dondurur (`freeze`); M2'de kalıcı katman per-stroke bitmap/lifetime'a genişletilir (üst üste stroke'lar bağımsız solar)
  - **Uygulama (13 Kasım 2026):** `Logic/InkModel.cs` — 36 yerleşik stil + laser tablosu (`PointerStyle`), `InkChoice` çözümlemesi, `InkMetrics` (kalınlık/ok/dedup), `InkFade` (easeIn cubic-bezier), `InkGeometry` (smoothPath midpoint + `addHead`). `Overlay/InkLayer.cs` — hook→`InkJournal` stroke yaşam döngüsü (Begin/Extend/End/Clear), canlı stroke monitör boyutlu bitmap'e artımlı boyanır (kare maliyeti O(yeni nokta)), stroke bitince bbox bitmap'e dondurulur ve bağımsız solar. Gölge, 18 % siyah + radius kadar genişletilmiş offset stroke geçişiyle yaklaşıklanır (1.5 pt yarıçapta blur farkı bir pikselin altında). Prototip stili `hand_black.png` = macOS "Midnight" → `.match` = `#1D1D21`; M3'ün 36 stil seti macOS varsayılanı lavender (`#8754F2`) ile geldi (13 Kasım 2026). **Yumuşatma (13 Kasım, kullanıcı isteği):** macOS'ta yalnız `smoothPath` var; Windows'ta minik mouse hareketlerinin çizgiyi bozmaması için girişe iki aşamalı hat eklendi (`Logic/InkSmoothing.cs`): (1) **izotropik One Euro** filtresi (`dCutoff` 1 Hz; kesim frekansı hız büyüklüğünden — eksen başına kesim köşegen çizgiyi döndürüyordu), (2) **streamline** `InkStabilizer` — son `window` px'lik yolun `(1 − d/window)²` ağırlıklı ortalaması; düz çizgiyi birebir korur, fare kuantizasyonunun 1 px merdivenini ve filtrenin bıraktığı dalgayı siler. Nokta hattı **sub-pixel** (`PointF`): tam sayıya yuvarlama yumuşatılmış yolu yeniden merdivene çeviriyordu. **Ctrl+Alt+S** seviyeyi canlı döngüler: off / light (1 Hz, 0.05, 6 pt) / medium (0.5, 0.03, 10 pt) / strong (0.3, 0.02, 13 pt) / max (0.15, 0.015, 16 pt; varsayılan); her değişiklikte üst-orta bölgede 1.5 s'lik toast. Bırakışta `Drain` pencereyi sıfıra indirip stroke'u imlecin bırakıldığı noktaya düz kuyruk bırakmadan getirir. Ölçüm: 20° / 40 px/s / 6 Hz ±1,5 px titreme + 1 px kuantizasyonda sapma 1,00 px → 0,19 px; gecikme ~5 px (el sprite'ının altında), duran imleçte stroke ~1 px gerisine oturur. **El ucu (13 Kasım düzeltmesi):** macOS `HandAsset.measure` portu — en üst opak satır + `h/200` altındaki satırın x-merkezi `tip/tipX` olarak ölçülür ve el bu noktayla imlece çivilenir (eski üst-orta çivileme ucu ~6 px sol / ~14 px altta bırakıyordu); M3'te aynı ölçüm stil başına uygulanacak.
- [x] Ctrl basılıyken ok ucu *(başlangıçta Ctrl önerilir; onay **bekliyor**)* — ok başı `size = max(14, lineWidth × 3.2)` (macOS `addHead`) *(13 Kasım 2026; Ctrl uygulandı, kullanıcı onayı hâlâ açık — macOS ⌘)*
- [x] Fade animasyonu (`Preferences.fadeSeconds` uyumlu): per-stroke, delay `max(0.5, fadeSeconds)`, süre **0.6 s**, easeIn; bittikten sonra stroke kaynağından çıkar (macOS `fade`) *(13 Kasım 2026; varsayılan 3 s, `TH_FADE=0` başlangıç keep modu; fade sürerken kompozitör busy kadansa geçer; **Ctrl+Alt+F** fade'i runtime'da açar/kapatır)*
- [x] Ctrl+Alt+X: mürekkebi temizle; Esc: temizle + kapat *(13 Kasım 2026; `RegisterHotKey` Ctrl+Alt+X, Esc hook'tan)*
- [x] Çizim yumuşatma: iki aşamalı hat — izotropik One Euro giriş filtresi + mesafe-ağırlıklı streamline (`InkStabilizer`), sub-pixel nokta hattı, Ctrl+Alt+S ile canlı seviye döngüsü (off / light / medium / strong / max) *(13 Kasım 2026, kullanıcı isteği: "en düşük mouse hareketi çizimde çok belli oluyor"; macOS'ta yalnız `smoothPath` var, bu Windows'a özgü ek)*
- [x] Ayar göstergesi: kısayol değişikliklerinde üst-orta bölgede 1,5 s'lik toast (`Smoothing: max`, `Fade: off`, `Ink cleared`) *(13 Kasım 2026)*
- **Çıkış kriteri (karşılandı, 13 Kasım 2026):** el ile çizim yapmak, oka metametin, tam klavye kısayol seti. Kanıt: `--inkcheck` **20/20** (çizim rengi `#1D1D21` piksel-mükemmel, canlı stroke tam genişlikte, Ctrl'siz ok yok / Ctrl'li ok var ve açık V, el ucu imleçte, fade sonrası mürekkep 0 piksel, Ctrl+Alt+X anında temizler, Ctrl+Alt+F fade'i açar/kapatır, Ctrl+Alt+S seviyeyi döngüler ve toast görünür); `--fpscheck --ink` 60,0 fps (draw 0,81 / present 0,40 ms, periyot p95 16,93 ms); birim testleri **57/57**; e2e-m1 10/10; cursorcheck 7/7.

### M3 — Etkileşim kalitesi — TAMAM (13 Kasım 2026)
- [x] Çift Shift → click-through (M1 prototipinde doğrulanan davranışın ürün kalitesine taşınması) *(13 Kasım 2026)*
  - macOS `shiftChanged` semantiği birebir: **Shift tap** = 0,3 s içinde basılıp bırakılan ve arada başka girdi olmayan Shift; iki tap 0,35 s içinde → click-through kilidi. `ShiftTapDetector` (saf mantık, saat enjekte edilir) girdi sayacı tutar (başka tuş / mouse down) — büyük harf yazmak click-through açmaz; Ctrl/Alt/Win basılıyken tap sayılmaz. Mod değişiminde üst-orta toast (`Click-through: on/off`); el görünür kalır, gerçek imleç gizli kalır (sözleşme).
- [x] Basılı interact tuşu → geçici click-through (macOS `interactKey`, varsayılan ⌥ ↔ Alt); `interactKeepsHand` davranışı *(13 Kasım 2026)*
  - Alt basılıyken tıklamalar alta geçer; `interactKeepsHand` false (varsayılan) ise el gizlenir ve gerçek imleç geri gelir, true ise el görünür kalır ve imleç gizli kalır. **Alt tek başına** gerekir: Ctrl+Alt uygulamanın kısayol öneki olduğundan akor interact modunu tetiklemez (Windows'a özgü iyileştirme).
  - **Modifier asla yutulmaz:** LL hook'ta bir keyup'ı yutmak sistemin tuş durumunu "basılı" bırakır (sonraki her tuş Alt+tuş olarak gider — M3 test turunda klavye kilitlenmesi olarak görüldü). Bu yüzden Alt keyup'ı uygulamaya gider; sonuç tek başına Alt tap'inin standart Windows davranışıdır (alttaki uygulamanın menü çubuğu vurgulanır). Interact tuşu M4 ayarlarından değiştirilebilir.
- [x] Toolbar: 36 stil + lazer + ses düğmesi + çıkış; hit-test / `passThroughRects` mantığı *(13 Kasım 2026)*
  - `ToolbarModel`: 37 stil (36 el + lazer) + ses + çıkış = 39 düğme; 40 px düğme, 6 px aralık, 10 px iç boşluk, 16 px köşe radiusu; monitör dar ise satırlara sarar (her düğme panel içinde kalır). Panel düğmeler arası boşlukta da tıklamayı yutar (pass-through bölgesi); komut hook içinden yönlendirilir. Bant yalnız **çizim modunda** tıklanabilir: click-through veya interact tuşu basılıyken tıklamalar alta geçer (önce moddan çıkılır). Yalnız birincil monitörde çizilir; sıra macOS tablosuyla aynıdır (lavender … cookie, lazer, ses, çıkış). Seçimde düğme %18 beyaz vurgu alır, toast stil adını gösterir ve `InkChoice.Match` mürekkebi anında o stilin rengine çevirir.
  - Görsel: yarı saydam koyu panel (24,24,28 / %84) + %18 beyaz hairline; seçili stil %18 beyaz yuvarlak vurgu; stil ikonları gömülü PNG'lerden üretilen 96×96 kırpılmış thumbnail'lar (arka planda ön yüklenir, hazır olana kadar renkli nokta), lazer ikonu kırmızı parlayan nokta, ses ikonu hoparlör (kapalıyken çizik), çıkış ✕. Panel bir kez bitmap'e boyanıp her karede tek blit edilir (39 düğmeyi her karede boyamak ~1,7 ms sürüyordu; ölçüm: draw p95 1,7 → 0,9 ms).
- [x] **El görsel eşliği** (macOS `ArmView`; asset PNG'ler el + sap compose'ıyla geldiğinden sap için ek iş yok) *(13 Kasım 2026)*
  - `HandAssets`: macOS `HandAsset.measure` portu (handTop/tip/wrist/rodEnd/tipX/rodX/handMinX/handMaxX), 2 girişli sprite LRU'su, 96×96 thumbnail. Gölge silueti 1/4 çözünürlükte, 3 geçişli box blur ile ve blur'un taşabileceği saydam dolguyla (2 × yarıçap) üretilir; gölge parçaları dolguyu yalnız bilek dikişinden uzağa doğru genişletir (iki yöne genişletince dikdörtgenler bilekte çakışıp gölgeyi iki kez çiziyordu).
  - `HandPoseSolver`: gövde ekran altındaki grip noktasından uzanır (grip = imleç + %30 ekran genişliği sağda, ekran altının %12 altında), gövde uzunluğu `pointerSize × ekranYüksekliği × (1 − 0.06 × press)`, sap mesafeye göre uzar (`minRodFrac` %5 taban), dönüş parmak ucunu imlece oturtur, kaldırma `liftFrac × ekranYüksekliği × (1 − 0.8 × press)`. Grip takibi `gripFollow` 0,08'in **120 Hz referansına göre dt ile ölçeklenir** ve Windows'ta **%25 hızlandırılır** (`GripFollowSpeedup = 1.25`): 60 Hz'de kare başına ~0,188, 20 Hz idle tick'te ~0,47, τ ≈ 0,08 s (macOS referansı 0,1 s).
  - **Gölge (Windows ayarları, 14 Kasım):** blur her kaldırmada tam basınç yarıçapıdır (4K'da 14 px; macOS dinlenmede 45 px kullanır) — gölge her zaman elin siluetini okur; dinlenme gölgesi %8 büyük ve daha şeffaftır (`ShadowScale = 1 + 0.08 × (1 − press)`, opaklık `0.20 + 0.08 × press`); ofset `ShadowOffsetScale = 0.3` ile ele yakındır (dinlenmede 23, 39 px; basınçta 5, 8 px). Kaldırma yalnız ofseti, boyutu ve opaklığı değiştirir.
  - Sprite iki parçaya bölünür: el (PNG'nin üst yarısı, parmak ucu en üstte) ve sap (alt yarısı); el parçası sprite'ın üstünde, sap altında çizilir ve sprite dik durur. **Düzeltme (13 Kasım, kullanıcı raporu "el ve değnek ters"):** ilk uygulama `contentsRect`'i y-down sanıp el/sap kaynak dikdörtgenlerini ters aldı — el sapın yerine, sap elin yerine çiziliyordu; kaynak dikdörtgenleri el = üst yarı, sap = alt yarı olacak şekilde düzeltildi ve yerel uzay gövde −y yönünde uzanacak biçimde tanımlandı (sprite dik). `--m3check` "hand hangs below the cursor" + "hand above the stem" piksel kontrolleri ve `SpriteTip_MapsToTheCursor` birim testi bunu kilitler.
  - Lazer stili: el yok; kırmızı nokta + radyal gradyan parıltı + açık çekirdek (`r = max(4, ekranY × 0.0038 × size/0.13) × (1 + 0.2 × press)`, parıltı `r × (2.2 + press)`).
- [x] **Tıklayarak gösterme** (macOS `ArmView.press()` + `aim()` basınç döngüsü, `ArmView.swift:117-130`) *(13 Kasım 2026)*
  - Her yutulan down'da `pressAmount = 1` (anında, sönümsüz) + tap sesi; bırakınca kompozitör her karede `press += (0 − press) × 0.18` uygular (basınç 0'a inene kadar busy kadans). Interact modunda da basılır (macOS `onInteractDown`), ses çalınmaz. Tap/çizim ayrımı korunur: mürekkep yalnız 4 pt sürüklemede.
- [x] `TapSound` (NAudio), ses aç/kapa *(13 Kasım 2026)*
  - 15 gömülü `tap_*.wav` (macOS dosyaları birebir), 4 sesli `WaveOutEvent` havuzu, kendi iş parçacığında çalar (hook asla ses cihazında bloklanmaz; macOS da ayrı kuyruk kullanır). Toolbar hoparlör düğmesi `Preferences.SoundOn`'u çevirir; ses seçimi M4 ayarlarına (`Preferences.TapSound`, varsayılan thock).
- [x] Busy 120 Hz / idle 20 Hz fps yönetimi (OverlayController mantığı) *(13 Kasım 2026)*
  - Busy hedefi monitör yenilemesine bağlı (`Clamp(hz, 60, 120)`), idle 20 Hz; geçiş histerezisi: son girdiden 500 ms sonra idle, basınç sönümü veya grip oturması sürerken busy kalır. Idle'da da kare üretilir (macOS 20 Hz timer'ı gibi) — fps sayacı ve HUD idle'da anlamlı kalır; kirli bayrak o kareyi busy kadansa yükseltir.
- **Çıkış kriteri (karşılandı, 13 Kasım 2026):** macOS kullanım tablosundaki her davranış el ile yapılabiliyor ve akıcı. Kanıt: yeni `--m3check` **28/28** (toolbar 39 düğme + boyanmış + boşluk yutma, stil seçimi + mürekkep rengi, lazer noktası + el yok, ses aç/kapa, el aşağı sarkıyor + el sapın üstünde, basınçta gölge görünür + gölge elin siluetini izliyor (yayılım ≤ 45 px), basınç küçültmesi + geri oturma, tap sesi, interact tuşu: el gizli + tıklama alta gidiyor + geri dönüş, çift Shift: kilit + el görünür + tıklama alta gidiyor + kilit açma + çizim tıklaması yutuluyor, busy 60 / idle 20 fps); `--inkcheck` 20/20; `--fpscheck --ink` 60,0 fps (draw p95 0,5 / present p95 0,4 ms, periyot p95 16,9 ms); birim testleri **91/91**; e2e-m1 10/10; cursorcheck 7/7.

### M4 — Sistem entegrasyonu — TAMAM (14 Kasım 2026)
- [x] `tinyhand://` protokol kaydı + named pipe tek-instance + komut iletme (`on/off/toggle/clear/style/<id>/sound/*`) *(14 Kasım 2026)*
  - `Automation`: HKCU `Software\Classes\tinyhand` (`URL Protocol`, `shell\open\command = "<exe>" "%1"`), URL grameri macOS ile birebir (`Uri.Host` + ilk yol parçası; bilinmeyen komut loglanır ve yok sayılır). `SingleInstance`: `Local\TinyHand.SingleInstance` mutex + `TinyHand.Command` named pipe; ikinci süreç URL'yi çalışan örneğe iletip çıkar (macOS LaunchServices davranışı). `style/<id>` stili seçer ve overlay'i açar; `sound/on|off|toggle`; `clear`; `on/off/toggle`.
- [x] `%APPDATA%\TinyHand\state.json` yazımı (pid, aktiflik, stil, ses, styles listesi; normal çıkışta dosya silinir — sözleşme) *(14 Kasım 2026)*
  - Anahtarlar alfabetik (macOS `.sortedKeys`), 2 boşluk girinti; `styles` her stili `id/name/type` (`hand`/`custom`/`laser`) ile yayımlar. Yazım arka plan iş parçacığında debounce edilir (hook/kompozitör diskte bloklanmaz); normal çıkışta dosya silinir, çökme sonrası kalan dosya pid ile ayırt edilir.
- [x] Ayarlar penceresi (fade süresi, stil seçimi, kısayollar, interact tuşu, `interactKeepsHand` ayarı) *(14 Kasım 2026)*
  - macOS `SettingsView` bölümleri: General (kayıt edilebilir toggle/clear kısayolları, interact tuşu + keepsHand, çift Shift), Pointers (37 stil + custom eller, seçim, silme, "Make your own hand"), Appearance (boyut, gölge), Drawing (mürekkep rengi, fade, smoothing), Sound (15 tap sesi + Off, önizleme), About (sürüm, bağlantılar, sıfırlama). Kısayol kaydı sırasında global kısayollar bırakılır (macOS gibi); reddedilen kayıt eski kısayolu geri kurar. Ayarlar `%APPDATA%\TinyHand\settings.json`'a yazılır (macOS UserDefaults karşılığı; `state.json` sözleşmesi ayrı kalır).
- [x] Welcome penceresi *(14 Kasım 2026)*
  - Windows'ta Accessibility izni yok; kart "You're all set" durumunu gösterir: toggle kısayolu, Start (overlay'i açar), tray ipucu. İlk çalıştırmada bir kez (`welcomeShown`), tray menüsünden yeniden açılabilir; macOS gibi açılmadan overlay kapatılır.
- [x] HandMaker: custom PNG el ekleme akışı — PNG doğrulama + compose kodunun C# portu, macOS kuralları birebir *(14 Kasım 2026)*
  - `Logic/HandMaker.cs`: macOS `check` kuralları birebir (alfa kanalı, ≥64 px, kenar doluluk ≤ %2, tek bağlı bileşen ≥ %97, en-boy 1.05–2.8, el yüksekliği ≥ %50, üst bant ≤ %40, bilek ≥ %20, yumuşak gölge ≤ %8) ve aynı kullanıcı mesajları; compose 1024×1536, el üstte 150 px, sap 1462 px'e kadar aynı gradyan/geometri; mürekkep rengi aynı bucket/oy algoritması. `Logic/CustomHands.cs`: `%APPDATA%\TinyHand\Hands\custom-xxxxxxxx.png` + `hands.json`, `PointerStyle.All` custom elleri lazerden önce ekler; toolbar ve ayarlar anında görür. Prompt şablonu macOS ile aynı.
- **Çıkış kriteri (karşılandı, 14 Kasım 2026):** Raycast/PowerShell/URL otomasyonu ve ayarlar tam çalışıyor. Kanıt: `--m4check` **68/68** (protokol kaydı + `%1`, URL grameri, state.json içeriği/anahtar sırası/silme, on/off/toggle/clear/style/sound komutları, ikinci örnek reddi + pipe iletimi, settings.json round-trip + reset, HandMaker geçerli/6 geçersiz vaka, custom el ekleme/seçme/toolbar/silme, tüm ayar sayfaları + welcome); `scripts/m4-live.ps1` gerçek süreçle protokol→pipe→state akışını doğruladı (launch active, off/style/sound/toggle, Ctrl+Alt+Q sonrası state.json yok, settings.json var); birim testleri **130/130**; regresyonlar: `--m3check` PASS, `--inkcheck` 20/20, `--fpscheck --ink` 60 fps (draw p95 1,3 / present p95 0,6 ms), e2e-m1 10/10, cursorcheck 7/7.

### M5 — Dağıtım
- [ ] Velopack kurulumu + öz-güncelleme; güncelleme varlıkları kanal bazlı: `releases.<channel>.json` bildirimi ve ilgili `.nupkg` tam/delta paketleri Release'e dahil
- [x] Kod imzalama kararı: imzalanmayacak (sertifika alınmıyor; SmartScreen uyarısı belgelenir)
- [ ] GitHub Actions `windows-latest` işi: publish + Velopack → Windows varlıkları (`TinyHand-WindowsSetup.exe`, taşınabilir zip, kanalın `releases.<channel>.json` bildirimi ve `.nupkg` paketleri) var olan macOS release adımının açtığı **aynı GitHub Release'e** eklenir — tek yayın adımı, iki platform paketi
- [ ] Çok monitör + DPIv2 + %150 ölçek test geçişi (M1 test matrisinin genişletilmesi)
- **Çıkış kriteri:** kurulum, güncelleme ve test geçişleri tamam; sürüm iki platformun paketleriyle aynı tag'den tek Release'te çıkar.

## Açık kararlar (kullanıcı)

1. Karar: Sertifika satın alınmayacak (ücretsiz dağıtım). Sonuç: SmartScreen "bilinmeyen uygulama" uyarısı kullanıcı tarafından elle geçilir; AV false positive riski kabul edilir. Self-signed imza yapılmayacak (faydası yok). Bu karar gereği `uiAccess="true"` plandan çıkarıldı (imzalı ikili gerektirir); yükseltilmiş pencerelerin üstünde çizim yapılamaması kalıcı, belgelenmiş sınırlamadır.
2. Ok ucu için Ctrl onayı (Windows tuşu Start menüsünü açtığı için öneri Ctrl).
3. Çözüldü (macOS referansı izlenir): çift Shift click-through modunda TinyHand eli görünür kalır, gerçek imleç gizli. M1 prototipinde Windows tarafında doğrulanır.
4. Karar: Hedef işletim sistemi yalnızca **Windows 11 x64**; Windows 10 desteklenmez ve test edilmez. Sonuç: .NET 10 destek listesindeki Windows 10 edisyon ayrıntıları takip edilmez; gereksinim ve dağıtım metinleri tek hedefle yazılır.
5. Interact tuşu (M3): varsayılan **Alt** (macOS ⌥ karşılığı). Windows'ta tek başına Alt tap'i alttaki uygulamanın menü çubuğunu vurgular; modifier'ı yutmak sistem tuş durumunu kilitlediği için bu davranış kabul edilir ve M4 ayarlarından tuş değiştirilebilir (Ctrl / Shift / kapalı) *(M4'te uygulandı: Ayarlar → General → Hold to use the app)*.
