# معماری فنی KOALIX

> نسخه 0.2.0 — این سند وضعیت واقعی پیاده‌سازی را توضیح می‌دهد. هر بخشی که روی ویندوز تست نشده، صراحتاً علامت خورده است.

## ۱. ساختار ماژول‌ها

| پروژه | نقش | وابستگی به ویندوز |
|---|---|---|
| `Koalix.Core` | مدل‌های پایه: `ConnectionStateMachine`، `SplitPolicy`، `AppRule`، `PathValidator`، `Redactor`، اینترفیس‌ها | ندارد |
| `Koalix.Dns` | پارس/خروجی TXT، کلاینت DNS خام (UDP + fallback به TCP)، تست ICMP، امتیازدهی، STUN، `PingMonitor`، اعمال DNS با `netsh` | فقط `WindowsDnsConfigurator` |
| `Koalix.Tunneling` | پارس لینک‌ها و Subscription، `ConnectionManager`، `EngineProcess` (Job Object)، `HealthChecker` | Job Object / Ctrl+C |
| `Koalix.SplitTunneling` | کامپایل `SplitPolicy` به قوانین `route` و `dns` در sing-box | ندارد |
| `Koalix.SingBox` | ساخت کانفیگ TUN، ادغام کانفیگ کاربر، `SingBoxConfigLinter`، Clash API | ندارد |
| `Koalix.Xray` | ساخت کانفیگ Xray و اجرای Xray پشت TUN خود sing-box | ندارد |
| `Koalix.WireGuard` | پارس `.conf`، endpoint برای sing-box، سرویس رسمی WireGuard | `wireguard.exe` |
| `Koalix.SystemIntegration` | ProcessRunner امن، اسکن پردازش‌ها، System Proxy، Journal، تعمیر شبکه، تشخیص تداخل، Kill Switch، Routing Inspector، بهینه‌سازی گیمینگ | بله |
| `Koalix.Infrastructure` | SQLite + Migration، Repositoryها، DPAPI، Serilog، گزارش عیب‌یابی، EngineManager | DPAPI |
| `Koalix.App` | رابط WPF (MVVM با CommunityToolkit، DI)، دوزبانه fa/en با RTL | بله |

## ۲. تونل‌زنی انتخابی واقعی (Split Tunneling)

**روش:** sing-box یک آداپتور **Wintun** (ورودی `tun` با `auto_route` و `strict_route`) می‌سازد. کل ترافیک سیستم وارد TUN می‌شود و sing-box برای هر اتصال TCP/UDP، پردازش مالک آن را از جدول اتصال‌های ویندوز پیدا می‌کند (`process_path` / `process_path_regex`). سپس:

- حالت **Include**: برنامه‌های انتخابی → `proxy`، بقیه → `direct` (`route.final = direct`)
- حالت **Exclude**: برنامه‌های انتخابی → `direct`، بقیه → `proxy`
- قانون **Block** → `action: reject`
- خروجی `direct` با `auto_detect_interface` روی آداپتور فیزیکی bind می‌شود؛ بنابراین حلقهٔ مسیریابی ایجاد نمی‌شود.
- حالت پوشه (`IncludeFolder`) با regex لنگرشده و case-insensitive، فایل‌های اجرایی فرزند بازی (لانچر، anti-cheat helper) را هم پوشش می‌دهد.
- `xray.exe` و پردازش‌های موتور همیشه `direct` هستند.

**بدون بازگشت پنهانی:** اگر موتور/پروفایل نتواند مسیریابی برنامه‌ای را اجرا کند، `ConnectionManager` با خطای `Split.Error.EngineNoProcessRouting` اتصال را رد می‌کند — هرگز بی‌صدا تونل کامل نمی‌شود. حالت تونل کامل WireGuard از سرویس رسمی WireGuard استفاده می‌کند؛ حالت انتخابی WireGuard از endpoint داخلی sing-box.

**محدودیت‌ها (صادقانه):**
- اتصال‌هایی که *قبل* از برقراری تونل باز شده‌اند مسیر قبلی را حفظ می‌کنند → UI لیست برنامه‌هایی را که باید ری‌استارت شوند نمایش می‌دهد.
- برنامه‌هایی که از سرویس‌های سیستمی (مثلاً `svchost`) برای شبکه استفاده می‌کنند، به نام همان سرویس دیده می‌شوند.
- ترافیک درایورهای kernel-mode (برخی anti-cheatها) از TUN عبور می‌کند اما مالک پردازشی ندارد → در Routing Inspector «نامشخص» نمایش داده می‌شود.
- جایگزین مبتنی بر **WFP** (فیلتر در سطح هسته) هنوز پیاده‌سازی نشده است.

## ۳. DNS

- **تست:** هر سرور با کوئری خام RFC 1035 روی UDP؛ در صورت TC-bit یا خطای UDP، fallback به TCP. ICMP جداگانه اندازه‌گیری می‌شود؛ **مسدود بودن ICMP امتیاز سرور را کم نمی‌کند** (در محیط تست ما ICMP مسدود بود ولی DNS کار می‌کرد — دقیقاً همین حالت).
- **جیتر:** `Σ|xᵢ − xᵢ₋₁| / (n − 1)` روی نمونه‌های موفق؛ میانه برای مقاومت در برابر نمونه‌های پرت.
- **استراتژی‌های رتبه‌بندی:** کمترین میانه، کمترین جیتر، کمترین اتلاف، بیشترین موفقیت، و متعادل گیمینگ.
- **اعمال:** `netsh interface ipv4/ipv6 set dnsservers` با شمارهٔ interface (نه نام، برای جلوگیری از injection)، قبل از آن Snapshot (شامل تشخیص DHCP از رجیستری) در Journal نوشته می‌شود، بعد از اعمال **تأیید** می‌شود و در صورت عدم تأیید خودکار بازگردانی می‌شود.
- **نشت DNS در تونل:** DNS برنامه‌های تونل‌شده از طریق `dns-remote` (DoH با detour پروکسی) و بقیه از `dns-local` حل می‌شود.

## ۴. IPv6 و UDP

- آدرس IPv6 روی TUN (`fdfe:dcba:9876::1/126`) اضافه می‌شود تا ترافیک IPv6 هم وارد TUN شود و نشت نکند. گزینهٔ «مسدودسازی IPv6 برنامه‌های تونل‌شده» برای سرورهایی که IPv6 ندارند.
- UDP (بازی‌ها، QUIC، Voice) در stack `mixed` توسط gVisor مدیریت و از طریق خروجی پروکسی منتقل می‌شود (VLESS/VMess/Trojan/SS/Hy2/TUIC/WireGuard همگی UDP را حمل می‌کنند، به شرط فعال بودن UDP روی سرور). تست STUN برای اندازه‌گیری تأخیر UDP وجود دارد. UDP-over-TCP هنوز پیاده‌سازی نشده است.

## ۵. استراتژی موتورها

| ورودی | موتور |
|---|---|
| لینک‌هایی که sing-box به‌صورت native پشتیبانی می‌کند (VLESS/VMess/Trojan/SS/Hy2/TUIC + ws/grpc/http/httpupgrade/REALITY) | sing-box مستقیم |
| xhttp، mKCP و ترنسپورت‌هایی که sing-box ندارد، یا کانفیگ JSON خود Xray | Xray (ورودی SOCKS روی 127.0.0.1) + TUN خود sing-box جلوی آن |
| WireGuard تونل کامل | سرویس رسمی WireGuard for Windows |
| WireGuard انتخابی | endpoint `wireguard` در sing-box |

**اعتبارسنجی دو مرحله‌ای:** (۱) `SingBoxConfigLinter` — بررسی معنایی ارجاع‌ها؛ چون در تست‌ها ثابت کردیم `sing-box check` قوانینی را که به outbound ناموجود اشاره می‌کنند **قبول می‌کند**. (۲) `sing-box check` / `xray run -test` واقعی.

## ۶. چرخهٔ اتصال و بازیابی

`Disconnected → Connecting → Connected ⇄ Degraded → Reconnecting → … → Disconnecting → Disconnected` (و `Error`). فقط وقتی آزمون سلامت (`generate_204` از طریق پورت سلامت داخلی) موفق شود، وضعیت `Connected` می‌شود. قطع ناگهانی موتور → اتصال مجدد با backoff نمایی؛ در صورت فعال بودن، Kill Switch (قوانین فایروال `KOALIX-KillSwitch-*`) برنامه‌های تونل‌شده را مسدود می‌کند.

**Journal و Crash Recovery:** هر تغییر سیستمی (DNS، System Proxy، قانون فایروال، تنظیم گیمینگ) *قبل از اعمال* با مقدار قبلی در `network-journal.json` (نوشتن اتمیک) ثبت می‌شود. اگر برنامه کرش کند، در اجرای بعدی فهرست تغییرات باقی‌مانده نمایش داده می‌شود و با یک تأیید بازگردانی می‌شوند. System Proxy فقط وقتی بازگردانی می‌شود که کاربر در این فاصله آن را تغییر نداده باشد.

## ۷. دسترسی مدیر (Elevation)

برنامه با `asInvoker` اجرا می‌شود. ساخت Wintun نیاز به Administrator دارد → پیشنهاد اجرای مجدد با UAC. دستورات سیستمی فقط از allow-list (`netsh`, `ipconfig`, `route`, `wireguard`, …) و با `ArgumentList` (بدون رشتهٔ shell) اجرا می‌شوند. **سرویس ممتاز با IPC احراز هویت‌شده هنوز پیاده‌سازی نشده است.**

## ۸. امنیت

- محتوای پروفایل‌ها و URL اشتراک‌ها با **DPAPI (CurrentUser + entropy)** رمز می‌شوند.
- همهٔ لاگ‌ها قبل از نوشتن از `Redactor` عبور می‌کنند (کلیدهای WG، UUID، رمزها، لینک‌ها، توکن‌های URL؛ در گزارش عیب‌یابی IPها هم پنهان می‌شوند).
- موتورها فقط از GitHub Releases رسمی، با بررسی SHA-256 (فیلد `digest` یا فایل `.dgst` در Xray) نصب می‌شوند؛ بدون hash معتبر، نصب رد می‌شود.
- Subscription فقط با https، بدون credential در URL، بدون آدرس loopback، حداکثر 5MB.

## ۹. Routing Inspector

داده از دو منبع ترکیب می‌شود: (۱) جدول TCP/UDP ویندوز با PID (`GetExtendedTcpTable/UdpTable`) و (۲) لیست اتصال‌های خود sing-box از Clash API (شامل `processPath`، `chains`، قانون منطبق). مسیر فقط زمانی «تونل/مستقیم/مسدود» گزارش می‌شود که خود موتور آن را تأیید کند؛ سوکت روی آداپتور فیزیکی که موتور از آن خبر ندارد = «خارج از TUN»؛ در غیر این صورت **«نامشخص»** — هیچ حدسی زده نمی‌شود. IP خروجی با مقایسهٔ IP مستقیم و IP از طریق پورت سلامت تونل تأیید می‌شود.
