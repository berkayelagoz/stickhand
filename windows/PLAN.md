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
| Interact tuşu basılıyken geçici click-through (davranış M3'te) | Hook yutmaz; alttaki uygulama alır | `interactKeepsHand` ayarına bağlı: false (macOS varsayılanı) ise gerçek imleç görünür, el gizlenir; true ise el kalır |
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
| `interactKey` basılıyken geçici click-through (macOS varsayılan ⌥) | Aynı mantık, ayarlanabilir tuş (önerilen varsayılan Alt) | `interactKeepsHand` ayarı: true ise el kalır, false (varsayılan) ise gerçek imleç görünür. Davranış M3, ayarı M4 |
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
  - **Uygulama (13 Kasım 2026):** `Logic/InkModel.cs` — 36 yerleşik stil + laser tablosu (`PointerStyle`), `InkChoice` çözümlemesi, `InkMetrics` (kalınlık/ok/dedup), `InkFade` (easeIn cubic-bezier), `InkGeometry` (smoothPath midpoint + `addHead`). `Overlay/InkLayer.cs` — hook→`InkJournal` stroke yaşam döngüsü (Begin/Extend/End/Clear), canlı stroke monitör boyutlu bitmap'e artımlı boyanır (kare maliyeti O(yeni nokta)), stroke bitince bbox bitmap'e dondurulur ve bağımsız solar. Gölge, 18 % siyah + radius kadar genişletilmiş offset stroke geçişiyle yaklaşıklanır (1.5 pt yarıçapta blur farkı bir pikselin altında). Prototip stili `hand_black.png` = macOS "Midnight" → `.match` = `#1D1D21`; M3'ün 36 stil seti macOS varsayılanı lavender (`#8754F2`) ile gelecek. **Yumuşatma (13 Kasım, kullanıcı isteği):** macOS'ta yalnız `smoothPath` var; Windows'ta minik mouse hareketlerinin çizgiyi bozmaması için girişe One Euro filtresi eklendi (`Logic/InkSmoothing.cs`, `minCutoff` 0.3 Hz / `beta` 0.02 / `dCutoff` 1 Hz; hız arttıkça kesim frekansı yükselir, gecikme ~8 px'te kalır). **El ucu (13 Kasım düzeltmesi):** macOS `HandAsset.measure` portu — en üst opak satır + `h/200` altındaki satırın x-merkezi `tip/tipX` olarak ölçülür ve el bu noktayla imlece çivilenir (eski üst-orta çivileme ucu ~6 px sol / ~14 px altta bırakıyordu); M3'te aynı ölçüm stil başına uygulanacak.
- [x] Ctrl basılıyken ok ucu *(başlangıçta Ctrl önerilir; onay **bekliyor**)* — ok başı `size = max(14, lineWidth × 3.2)` (macOS `addHead`) *(13 Kasım 2026; Ctrl uygulandı, kullanıcı onayı hâlâ açık — macOS ⌘)*
- [x] Fade animasyonu (`Preferences.fadeSeconds` uyumlu): per-stroke, delay `max(0.5, fadeSeconds)`, süre **0.6 s**, easeIn; bittikten sonra stroke kaynağından çıkar (macOS `fade`) *(13 Kasım 2026; varsayılan 3 s, `TH_FADE=0` başlangıç keep modu; fade sürerken kompozitör busy kadansa geçer; **Ctrl+Alt+F** fade'i runtime'da açar/kapatır)*
- [x] Ctrl+Alt+X: mürekkebi temizle; Esc: temizle + kapat *(13 Kasım 2026; `RegisterHotKey` Ctrl+Alt+X, Esc hook'tan)*
- **Çıkış kriteri (karşılandı, 13 Kasım 2026):** el ile çizim yapmak, oka metametin, tam klavye kısayol seti. Kanıt: `--inkcheck` 9/9 (çizim rengi `#1D1D21` piksel-mükemmel, Ctrl'siz ok yok / Ctrl'li ok var, fade sonrası mürekkep 0 piksel, Ctrl+Alt+X anında temizler); `--fpscheck --ink` 60,0 fps (draw 0,55 / present 0,31 ms, periyot p95 16,84 ms); birim testleri 35/35; e2e-m1 10/10; cursorcheck 7/7.

### M3 — Etkileşim kalitesi
- [ ] Çift Shift → click-through (M1 prototipinde doğrulanan davranışın ürün kalitesine taşınması)
- [ ] Basılı interact tuşu → geçici click-through (macOS `interactKey`, varsayılan ⌥ ↔ Alt); `interactKeepsHand` davranışı: tuş basılıyken el kalır ya da gerçek imleç görünür
- [ ] Toolbar: 36 stil + lazer + ses düğmesi + çıkış; hit-test / `passThroughRects` mantığı
  - Görsel sözleşme (macOS `Toolbar.swift`): darkGlass vibrancy panel'in Windows karşılığı: yarı saydam koyu arka plan + 16 px köşe radiusu, panel 300×56, yatay stack spacing 6; acrylic/blur gerekliliği ölçülerek karar verilir (GDI+ benzeri elle boyanmış gradient de bir seçenek)
- [ ] **El görsel eşliği** (macOS `ArmView`; asset PNG'ler el + sap compose'ıyla geldiğinden sap için ek iş yok): basınç/kalkma ile dinamik gölge (opacity `0.26 + 0.02 × press`, radius `lift × 0.35 + 5 × press`, offset lift'e göre), parmak ucunda kırmızı parlayan mürekkep noktası (`dotLayer`, shadow radius `r × (2.2 + press)`), stil başına `tip/tipX` pin; bunlar statik sprite'tan davranışsal sprite'a geçiş gerektirir (M1'de tek stil + tek sprite vardı)
- [ ] **Tıklayarak gösterme** (macOS `ArmView.press()` + `aim()` basınç döngüsü, `ArmView.swift:117-130`): mouse down'da el tam basınçta anında yerleşir (`pressAmount = 1`, sönümsüz); bırakınca her kare sönümle kalkış (`pressAmount += (0 − pressAmount) × 0.18` — busy ticker'ında per-frame uygulanır). Basıncın görsel faktörleri: el boyu `−6 %` (`handPts = handLenFrac × screenH × (1 − 0.06 × press)`), kalkma yüksekliği `−80 %` (`lift = liftFrac × screenH × (1 − 0.8 × press)`), lazer nokta yarıçapı `× (1 + 0.2 × press)`; gölge faktörleri yukarıdaki el görsel eşliği maddesinde. Tap/çizim ayrımı: basılma görseli **her** down'da uygulanır, mürekkep yalnız sürüklemede (hareketli down-up) oluşur (macOS `dragEvents`/`moved` ayrımı); Windows'ta down yutulan hook olayı olduğundan görüntü hook iş parçacığından kompozitör işaretlenerek (`_dirty` + busy render) tetiklenir; tap sesi de down anında çalınır (bkz. `TapSound` maddesi)
- [ ] `TapSound` (NAudio), ses aç/kapa
- [ ] Busy 120 Hz / idle 20 Hz fps yönetimi (OverlayController mantığı)
- **Çıkış kriteri:** macOS kullanım tablosundaki her davranış el ile yapılabilir, akıcı.

### M4 — Sistem entegrasyonu
- [ ] `tinyhand://` protokol kaydı + named pipe tek-instance + komut iletme (`on/off/toggle/clear/style/<id>/sound/*`)
- [ ] `%APPDATA%\TinyHand\state.json` yazımı (pid, aktiflik, stil, ses, styles listesi; normal çıkışta dosya silinir — sözleşme)
- [ ] Ayarlar penceresi (fade süresi, stil seçimi, kısayollar, interact tuşu, `interactKeepsHand` ayarı)
- [ ] Welcome penceresi
- [ ] HandMaker: custom PNG el ekleme akışı — PNG doğrulama + compose kodunun C# portu, macOS kuralları birebir
- **Çıkış kriteri:** Raycast/PowerShell/URL otomasyonu ve ayarlar tam çalışıyor.

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
