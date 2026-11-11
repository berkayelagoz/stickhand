# TinyHand — Windows Port Planı

Kaynak: macOS uygulaması (`Sources/TinyHand/`, Swift/AppKit). Hedef: özellik eşliği, tek kod tabanı iki platform.

## Kararlaştırılmış teknoloji

| Katman | Seçim |
|---|---|
| Dil / runtime | C# / .NET 8 |
| Ana süreç, tray, ayarlar | WinForms (`NotifyIcon` + mesaj pompası) |
| Overlay (monitör başına) | Ham Win32: `WS_EX_LAYERED \| WS_EX_NOACTIVATE \| WS_EX_TOPMOST` |
| Overlay çizim | GDI+ → 32bpp ARGB → `UpdateLayeredWindow`, kompozisyon timer'ı (120 Hz busy / 20 Hz idle) |
| Girdi yakalama | P/Invoke: `WH_MOUSE_LL`, `WH_KEYBOARD_LL` (`SetWindowsHookEx`) |
| Global kısayollar | `RegisterHotKey` (Ctrl+Alt+H, Ctrl+Alt+X); Esc hook'tan |
| Otomasyon | `tinyhand://` protokolü (HKCU `Software\Classes`), tek-instance named pipe, `%APPDATA%\TinyHand\state.json` |
| Ses | NAudio |
| Kurulum / öz-güncelleme | Velopack |
| CI | `windows-latest` işi: `dotnet publish -r win-x64 --self-contained` + Velopack bundle → GitHub Releases |

Elenen alternatifler: WPF overlay (`AllowsTransparency` + fullscreen topmost + 120 Hz yavaş), Rust (geliştirme hızı), WinUI/Tauri (interop yükü, kazanım yok).

## Bileşen eşlemesi (macOS → Windows)

| macOS | Windows | Not |
|---|---|---|
| `InputTap` (CGEventTap) | `WH_MOUSE_LL`/`WH_KEYBOARD_LL` | Yutma: hook'tan `1`. Kendi enjekte edilenler `LLMHF_INJECTED` ile ayıklanır, yoksa döngü. `maxWaitMs` mantığı korunur (~300 ms `LowLevelHooksTimeout` bütçesi). Accessibility izni gerekmez |
| `CursorHider` (private CGS) | Gerek yok | Overlay kendi input alabilir (hook zaten yutuyor); pencerede `Cursor = None` yeterli |
| `OverlayController` (NSWindow) | Layered window, monitör başına | `Screen.AllScreens` |
| `HotKeyCenter` (Carbon) | `RegisterHotKey` | |
| `Ink` (CAShapeLayer) | GDI+ vektör çizim, fade animasyonu timer'ı | Gölge: önceden blur'lanmış sprite (GDI+ gerçek zamanlı blur yavaş) |
| ⌘ basılı ok ucu | Ctrl (Windows tuşu Start menüsünü açar) | Açık uçlu: kullanıcı onayı |
| Çift Shift click-through | LL keyboard hook, aynı mantık | |
| NSStatusItem | `NotifyIcon` + `ContextMenuStrip` | Açık/koyu taskbar için registry teması |
| `Toolbar` | Overlay içine çizili + hit-test bölgesi | `passThroughRects` mantığı korunur |
| `Automation` | Protokol kaydı + named pipe + state.json | Protokol ikinci instance doğurur → mutex + komut iletme |
| Sparkle `Updater` | Velopack (delta dahil) | |
| `HandMaker` | Değişiklik yok | Prompt'lar ve PNG'ler platform-bağımsız |

## Windows'a özgü tuzaklar

1. **DPI**: per-monitor v2 aware manifest; el/mürekkep ölçeği monitör DPI'sına göre çarpılır. En hızlı hata kaynağı.
2. **UAC / yükseltilmiş pencereler**: overlay üstlerinde çizemez. M1-M4 için belgele; M5'te opsiyonel `uiAccess="true"` (imzalı ikili + Program Files).
3. **Antivirüs / SmartScreen**: global hook AV heuristiği tetikler; kod imzalama sertifikası fiilen şart (mümkünse EV).
4. **Hook gecikmesi**: hook içindeki iş ≤ birkaç ms.
5. **Görev çubuğu**: overlay penceresine `WS_EX_TOOLWINDOW`.

## Erken doğrulanacak varsayım (M1 spike)

4K fullscreen `UpdateLayeredWindow` @ 120 fps maliyeti. Ağırsa: kirli bölge güncellemesi ya da Direct2D + `DirectComposition` — mimari değişmez, tek render sınıfı içinde kalır.

## Proje yapısı

```
windows/
├── PLAN.md              ← bu dosya
├── PROGRESS.md          ← ilerleme izleme
├── TinyHand.sln
│   ├── TinyHand/           (WinForms tray host)
│   ├── TinyHand.Interop/   (P/Invoke: hook, layered window, protokol)
│   └── TinyHand.Tests/
└── installer/              (Velopack config)
```

## Kilometre taşları

### M1 — İskelet ve spike
- [ ] `windows/` çözüm iskeleti (sln, 3 proje, build CI'da)
- [ ] Spike: 4K fullscreen `UpdateLayeredWindow` @120 fps ölçümü; karar: GDI+ ya da Direct2D
- [ ] Tray simgesi + aç/kapa menüsü (Ctrl+Alt+H)
- [ ] `WH_MOUSE_LL`: aktifken tıklamayı yut (alttaki uygulama tıklanmaz), `LLMHF_INJECTED` ayıklama
- [ ] Overlay: monitör başına layered window, imleç konumunda el çizimi, gerçek cursor gizli
- **Çıkış kriteri:** gerçek imleç yok, el var, tıklama altta uygulamaya geçmiyor, Ctrl+Alt+H ile açılıp kapanıyor.

### M2 — Çizim
- [ ] Sürükle → mürekkep çizimi (GDI+), renk/kalınluk ölçeği
- [ ] Ctrl basılıyken ok ucu
- [ ] Fade animasyonu (`Preferences.fadeSeconds` uyumlu)
- [ ] Ctrl+Alt+X: mürekkebi temizle; Esc: temizle + kapat
- **Çıkış kriteri:** el ile çizim yapmak, oka metametin, tam klavye kısayol seti.

### M3 — Etkileşim kalitesi
- [ ] Çift Shift → click-through (hook tarafında)
- [ ] Toolbar: 36 stil + lazer + ses düğmesi + çıkış; hit-test / `passThroughRects` mantığı
- [ ] `TapSound` (NAudio), ses aç/kapa
- [ ] Busy 120 Hz / idle 20 Hz fps yönetimi (OverlayController mantığı)
- **Çıkış kriteri:** macOS kullanım tablosundaki her davranış el ile yapılabilir, akıcı.

### M4 — Sistem entegrasyonu
- [ ] `tinyhand://` protokol kaydı + named pipe tek-instance + komut iletme (`on/off/toggle/clear/style/<id>/sound/*`)
- [ ] `%APPDATA%\TinyHand\state.json` yazımı (pid, aktiflik, stil, ses)
- [ ] Ayarlar penceresi (fade süresi, stil seçimi, kısayollar)
- [ ] Welcome penceresi
- [ ] HandMaker: custom PNG el ekleme akışı
- **Çıkış kriteri:** Raycast/PowerShell/URL otomasyonu ve ayarlar tam çalışıyor.

### M5 — Dağıtım
- [ ] Velopack kurulumu + öz-güncelleme
- [ ] Kod imzalama (sertifikaya bağlı)
- [ ] GitHub Actions `windows-latest` işi: publish + Velopack → Releases (`TinyHand-WindowsSetup.exe`, taşınabilir zip)
- [ ] Çok monitör + DPIv2 + %150 ölçek test geçişi
- [ ] (Opsiyonel) `uiAccess="true"`: yükseltilmiş pencerelerin üstünde çizim
- **Çıkış kriteri:** kurulum, güncelleme ve test geçişleri tamam; sürüm macOS ile aynı tag'den çıkar.

## Açık kararlar (kullanıcı)

1. Kod imzalama sertifikası: EV mi OV mi, alınacak mı? (SmartScreen için fiilen şart)
2. Ok ucu için Ctrl onayı (Windows tuşu Start menüsünü açtığı için öneri Ctrl).
