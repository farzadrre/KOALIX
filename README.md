<p align="center"><img src="installer/assets/koalix-512.png" width="128" alt="KOALIX"></p>
<h1 align="center">KOALIX</h1>
<p align="center">
  <a href="https://github.com/farzadrre/KOALIX/releases/latest"><img src="https://img.shields.io/github/v/release/farzadrre/KOALIX?label=download" alt="release"></a>
  <img src="https://img.shields.io/badge/Windows-10%20%7C%2011-0078D6" alt="windows">
  <img src="https://img.shields.io/badge/.NET-10-512BD4" alt=".NET 10">
  <a href="LICENSE"><img src="https://img.shields.io/badge/license-MIT-green" alt="MIT"></a>
</p>

**فارسی** | [English](#english)

ابزار شبکهٔ گیمینگ برای ویندوز ۱۰/۱۱: مدیریت و تست DNS، کلاینت sing-box / Xray / WireGuard / OpenVPN، **تونل‌زنی انتخابی واقعی برای هر برنامه** (بدون Proxifier)، بازرس مسیریابی، پایش پینگ، آمار مصرف، مرکز تعمیر شبکه، بهینه‌سازی گیمینگ، ویجت شناور، حالت شیشه‌ای و تم‌ها. رابط دوزبانه (فارسی راست‌به‌چپ / English) با تعویض زنده.

### دانلود
از صفحهٔ **[Releases](https://github.com/farzadrre/KOALIX/releases/latest)**:

| فایل | توضیح |
|---|---|
| `KoalixApp-win-Setup.exe` | نصب‌کننده — به‌روزرسانی خودکار از داخل برنامه |
| `KOALIX-<نسخه>-portable-win-x64.exe` | قابل‌حمل، تک‌فایل، بدون نصب |

فایل‌ها امضای دیجیتال ندارند؛ در پیام SmartScreen روی «More info ← Run anyway» بزنید. به‌روزرسانی درون‌برنامه (تنظیمات ← به‌روزرسانی) به‌طور پیش‌فرض همین مخزن را می‌خواند.

### اولین اجرا
1. تنظیمات ← موتورها ← نصب sing-box (و در صورت نیاز Xray). دانلود فقط از GitHub رسمی با تأیید SHA-256.
2. تونل‌ها ← لینک / Subscription / کانفیگ را وارد کنید.
3. تونل‌زنی انتخابی ← حالت و برنامه‌ها را انتخاب کنید.
4. داشبورد ← اتصال (برای TUN دسترسی Administrator لازم است).

### ساخت از سورس
```powershell
# پیش‌نیاز: .NET SDK 10
dotnet build KoalixSuite.slnx -c Release
dotnet test tests/Koalix.Tests
# فایل اجرایی تکی:
dotnet publish src/Koalix.App/Koalix.App.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o output
```
یا `BUILD-KOALIX.bat` را اجرا کنید.

### انتشار نسخهٔ جدید
نسخه را در `Directory.Build.props` بالا ببرید، بخش آن را در `CHANGELOG.md` بنویسید، سپس:
```powershell
git tag v0.4.39
git push origin v0.4.39
```
GitHub Actions (`.github/workflows/release.yml`) تست‌ها را اجرا می‌کند، نصب‌کننده + نسخهٔ قابل‌حمل + فید به‌روزرسانی را می‌سازد و Release را منتشر می‌کند.

---

## English

Gaming network suite for Windows 10/11: DNS manager & benchmark, sing-box / Xray / WireGuard / OpenVPN client, **real per-application split tunneling** via sing-box TUN (Wintun) process rules — no Proxifier — Routing Inspector, ping monitor, usage statistics, network repair center, reversible gaming tweaks, floating widget, glass mode and style presets. Bilingual UI (Persian RTL / English) with live switching.

### Download
Grab the latest build from **[Releases](https://github.com/farzadrre/KOALIX/releases/latest)**: `KoalixApp-win-Setup.exe` (installer, in-app updates) or `KOALIX-<version>-portable-win-x64.exe` (single-file portable). Binaries are unsigned (SmartScreen › More info › Run anyway). Data lives in `%LOCALAPPDATA%\KOALIX` (portable mode: create `portable.flag` next to `KOALIX.exe`).

### Build
.NET SDK 10 on Windows — see the commands above. Releasing: bump `Directory.Build.props`, add a `CHANGELOG.md` section, push a `vX.Y.Z` tag; the workflow tests, builds and publishes the release.

Docs: [docs/FEATURES.md](docs/FEATURES.md) · [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) · [CHANGELOG.md](CHANGELOG.md)

### Repository layout
```
src/Koalix.Core            state machine, split policy, redaction, abstractions
src/Koalix.Dns             DNS TXT format, raw DNS client, ICMP/STUN, scoring, ping monitor, Windows DNS apply
src/Koalix.Tunneling       share links, subscriptions, connection manager, engine processes, health checks
src/Koalix.SplitTunneling  SplitPolicy → sing-box route/dns rules
src/Koalix.SingBox         config builder, linter, Clash API, engine
src/Koalix.Xray            Xray config + engine (behind sing-box TUN)
src/Koalix.WireGuard       .conf parser, official service / sing-box endpoint
src/Koalix.SystemIntegration  process runner, scanner, system proxy, journal, repair, conflicts, kill switch, routing inspector, gaming
src/Koalix.Infrastructure  SQLite + migrations, repositories, DPAPI, Serilog, diagnostics, engine manager
src/Koalix.App             WPF UI (MVVM, DI, fa/en)
tests/Koalix.Tests         unit + real-engine + network tests
tests/acceptance          Windows split-tunnel acceptance script
installer/KOALIX.iss       Inno Setup script
```

### License
MIT — see [LICENSE](LICENSE). sing-box (GPL-3.0), Xray-core (MPL-2.0), WireGuard and WireSock are not bundled; KOALIX downloads them from their official releases at runtime.
