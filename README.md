<p align="center"><img src="installer/assets/koalix-512.png" width="140" alt="KOALIX"></p>
<h1 align="center">KOALIX</h1>
<p align="center"><b>ابزار شبکهٔ گیمینگ برای ویندوز — اتصال، تونل‌زنی انتخابی، DNS، پینگ و بهینه‌سازی، همه در یک برنامه</b></p>
<p align="center">
  <a href="https://github.com/farzadrre/KOALIX/releases/latest"><img src="https://img.shields.io/github/v/release/farzadrre/KOALIX?label=download&color=8b5cf6" alt="release"></a>
  <a href="https://github.com/farzadrre/KOALIX/releases"><img src="https://img.shields.io/github/downloads/farzadrre/KOALIX/total?color=22c55e" alt="downloads"></a>
  <img src="https://img.shields.io/badge/Windows-10%20%7C%2011-0078D6" alt="windows">
  <img src="https://img.shields.io/badge/.NET-10-512BD4" alt=".NET 10">
  <a href="LICENSE"><img src="https://img.shields.io/badge/license-MIT-green" alt="MIT"></a>
</p>

<p align="center"><b>فارسی</b> | <a href="#english">English</a></p>

---

## فهرست
- [KOALIX چیست؟](#koalix-چیست)
- [دانلود و نصب](#دانلود-و-نصب)
- [شروع سریع](#شروع-سریع)
- [معرفی کامل بخش‌ها](#معرفی-کامل-بخشها)
- [ویجت شناور، حالت تمرکز و سینی ویندوز](#ویجت-شناور-حالت-تمرکز-و-سینی-ویندوز)
- [میان‌برهای صفحه‌کلید](#میانبرهای-صفحهکلید)
- [امنیت و حریم خصوصی](#امنیت-و-حریم-خصوصی)
- [رفع مشکلات رایج](#رفع-مشکلات-رایج)
- [ساخت از سورس](#ساخت-از-سورس)
- [مجوز](#مجوز)

---

## KOALIX چیست؟

KOALIX یک برنامهٔ ویندوزی است که هر چیزی که یک گیمر یا کاربر اینترنت برای یک اتصال پایدار و کم‌پینگ لازم دارد را در یک جا جمع کرده است:

- **کلاینت تونل** با موتورهای **sing-box**، **Xray**، **WireGuard** و **OpenVPN**. لینک‌های `vless` / `vmess` / `trojan` / `ss` / `hysteria2` / `tuic`، لینک Subscription، فایل‌های WireGuard (`.conf`) و OpenVPN (`.ovpn`) را می‌پذیرد.
- **تونل‌زنی انتخابی واقعی برای هر برنامه:** مثلاً فقط بازی یا فقط مرورگر از تونل رد شود و بقیه مستقیم بروند. بدون Proxifier، و بدون اینکه بی‌صدا همه‌چیز را تونل کند.
- **مدیریت و تست DNS**، **پایش پینگ و جیتر**، **بازرس مسیریابی**، **آمار مصرف ماندگار**، **مرکز تعمیر شبکه** و **بهینه‌سازی‌های برگشت‌پذیر گیمینگ**.
- رابط کاربری **دوزبانه (فارسی راست‌به‌چپ / English)** با تعویض زنده، تم روشن/تیره، پالت‌های رنگی، حالت شیشه‌ای، ویجت شناور و صداهای قابل‌تنظیم.

---

## دانلود و نصب

آخرین نسخه را از صفحهٔ **[Releases](https://github.com/farzadrre/KOALIX/releases/latest)** بگیرید:

| فایل | برای چه کسی؟ |
|---|---|
| **`KoalixApp-win-Setup.exe`** | پیشنهادی. نصب می‌شود و **خودش از داخل برنامه به‌روزرسانی می‌شود**. برای کاربر جاری نصب می‌شود (`%LOCALAPPDATA%\KoalixApp`) و برای نصب دسترسی Admin لازم نیست. |
| **`KOALIX-<نسخه>-portable-win-x64.exe`** | قابل‌حمل، تک‌فایل، بدون نصب. روی فلش هم کار می‌کند. به‌روزرسانی خودکار ندارد و نسخهٔ جدید را باید دستی دانلود کنید. |

**پیش‌نیاز:** ویندوز ۱۰ (نسخهٔ 1809 به بعد) یا ویندوز ۱۱، ۶۴ بیتی. نصب جداگانهٔ .NET لازم نیست (داخل فایل هست).

> **پیام SmartScreen:** فایل‌ها امضای دیجیتال پولی ندارند، به همین خاطر ویندوز ممکن است هشدار بدهد. روی **More info** و بعد **Run anyway** بزنید. همهٔ فایل‌ها مستقیم توسط GitHub Actions از همین سورس ساخته می‌شوند.

**حالت قابل‌حمل کامل:** اگر کنار `KOALIX.exe` یک فایل خالی به اسم `portable.flag` بسازید، همهٔ تنظیمات و داده‌ها کنار خود برنامه ذخیره می‌شوند، نه در `%LOCALAPPDATA%\KOALIX`.

---

## شروع سریع

1. **نصب موتور:** تنظیمات ← موتورها ← روی «نصب / به‌روزرسانی» sing-box (و در صورت نیاز Xray) بزنید. دانلود فقط از GitHub رسمی موتورها و با بررسی SHA-256 انجام می‌شود.
2. **وارد کردن کانفیگ:** به بخش **تونل‌ها** بروید و لینک کانفیگ یا Subscription را Paste کنید (چند لینک را هم با هم قبول می‌کند). فایل را هم می‌توانید روی پنجره بکشید و رها کنید.
3. **تست سرورها:** «پینگ همه» را بزنید تا سرورهای سالم و سریع مشخص شوند.
4. **(اختیاری) انتخاب برنامه‌ها:** در **تونل‌زنی انتخابی** مشخص کنید کدام برنامه‌ها از تونل رد شوند.
5. **اتصال:** در **داشبورد** سرور را انتخاب کنید و دکمهٔ بزرگ اتصال را بزنید. برای حالت TUN (تونل‌زنی برنامه‌ای) برنامه باید **با دسترسی Administrator** اجرا شود؛ اگر نباشد، دکمهٔ «اجرای مجدد با دسترسی Admin» نمایش داده می‌شود.

> بار اول یک **تور راهنمای قدم‌به‌قدم** نمایش داده می‌شود. هر وقت خواستید، از تنظیمات یا صفحهٔ «درباره» دوباره ببینیدش.

---

## معرفی کامل بخش‌ها

### 🏠 داشبورد
صفحهٔ اصلی: وضعیت اتصال، سرعت و سرورها در یک نگاه.

- **دکمهٔ بزرگ اتصال / قطع** با هاله و انیمیشن ضربان. شکلش (دایره یا مربع گرد) از تنظیمات قابل تغییر است.
- **کارت وضعیت:** نشان می‌دهد وصل هستید، در حال اتصال، وصل ولی کند، یا قطع؛ همراه با توضیح و پیشنهاد.
- **کارت سرورها:**
  - هر سرور با پرچم کشور، پروتکل، موتور و پینگ نمایش داده می‌شود.
  - ترتیب کارت‌ها دستی (کشیدن و رها کردن)، بر اساس پینگ، نام یا موتور است.
  - با کلیدهای **W/A/S/D** بینشان جابه‌جا می‌شوید.
  - دکمه‌های «پینگ همه» و «تست انتخاب‌شده».
- **اتصال هوشمند (Auto):** خودش سریع‌ترین سرور را انتخاب می‌کند و اگر سرور از کار بیفتد، **خودکار به سرور بعدی سوییچ می‌کند**.
- **تأیید IP خروجی:** IP مستقیم (ISP) و IP تونل را جدا نشان می‌دهد تا مطمئن شوید واقعاً از تونل خارج می‌شوید.
- **سرعت زندهٔ تونل:** نمودار دانلود/آپلود و مصرف همین جلسه، امروز و این هفته.
- **مصرف هر برنامه:** وقتی تونل با sing-box کار می‌کند، مصرف هر برنامه جدا ثبت می‌شود.
- **آمار مصرف ماندگار:** امروز / ۳ روز / هفته / ماه / کل. بعد از بستن برنامه هم حفظ می‌شود.
- **نقشهٔ سرورها:** سرورها بر اساس کشور گروه‌بندی می‌شوند.
- **سوییچ‌های سریع:** حالت گیم، مسیریابی ایران (سایت‌های ایرانی مستقیم)، Kill Switch و پروکسی سیستم.
- **تشخیص تداخل:** قبل از هر اتصال این موارد بررسی می‌شوند: VPN یا پروکسی دیگر، آداپتور TUN اضافه، پورت اشغال و پروکسی سیستم.
- **پس‌زمینهٔ زنده:** ذرات و موج‌های نرم که با اتصال جان می‌گیرند (قابل خاموش کردن).

### 🔌 تونل‌ها
همهٔ کانفیگ‌ها و Subscriptionها اینجا مدیریت می‌شوند.

- **وارد کردن:**
  - Paste لینک (`vless`, `vmess`, `trojan`, `ss`, `hy2`/`hysteria2`, `tuic`)
  - چند Subscription با هم
  - فایل JSON، فایل `.conf` وایرگارد و `.ovpn`
  - کشیدن و رها کردن فایل روی پنجره
- **Subscriptionها:** نام‌گذاری، به‌روزرسانی دستی یا خودکار هنگام باز شدن برنامه، و نمایش **حجم باقی‌مانده و تاریخ انقضا** (اگر پنل اعلام کند).
- **انتخاب موتور برای هر کانفیگ یا هر Subscription:** خودکار / sing-box / Xray. با یک کلیک بین sing-box و Xray جابه‌جا کنید.
- **تست پینگ واقعی (Real delay):** زمان پاسخ یک درخواست HTTP واقعی که از خود تونل رد می‌شود، نه فقط پینگ TCP.
- **گروه‌بندی و فیلتر:** کانفیگ‌ها را در گروه بگذارید، فیلتر کنید، یا چندتا را با هم انتخاب، جابه‌جا یا حذف کنید.
- **مرتب‌سازی:** دستی (کشیدن)، نام، پینگ اندازه‌گیری‌شده یا موتور.
- **پیش‌نمایش امن:** محتوای کانفیگ با **رمزها و کلیدهای مخفی‌شده** نمایش داده می‌شود.
- **اعتبارسنجی با موتور:** قبل از اتصال، خود موتور کانفیگ را بررسی می‌کند.
- **اشتراک‌گذاری با QR:** ساخت کد QR برای اسکن با گوشی، کپی لینک یا ذخیرهٔ تصویر QR.
- **OpenVPN:** نام کاربری و رمز رمزنگاری‌شده ذخیره می‌شوند. OpenVPN فقط در حالت تونل کامل کار می‌کند.

### 🔀 تونل‌زنی انتخابی
مهم‌ترین قابلیت KOALIX: **مسیر هر برنامه را جداگانه تعیین کنید.** sing-box یک آداپتور مجازی Wintun می‌سازد و ترافیک را بر اساس نام برنامه مسیریابی می‌کند.

- **سه حالت:**
  - **تونل کامل:** همه از تونل رد می‌شوند، به‌جز برنامه‌هایی که «مسدود» یا «مستقیم» کرده‌اید.
  - **فقط برنامه‌های انتخابی:** فقط برنامه‌هایی که «تونل» زده‌اید از تونل می‌روند و بقیه مستقیم.
  - **همه به‌جز انتخابی‌ها:** همه از تونل، به‌جز برنامه‌هایی که «مستقیم» زده‌اید.
- **قانون برای هر برنامه:** تونل / مستقیم / مسدود. از لیست برنامه‌های در حال اجرا انتخاب کنید یا فایل `.exe` را اضافه کنید.
- **قانون پوشه‌ای:** کل پوشهٔ یک بازی یا لانچر را اضافه کنید تا همهٔ برنامه‌ها و پردازش‌های فرزندش همان قانون را بگیرند.
- **قوانین دامنه:** هر خط یک دامنه، با گزینهٔ «همیشه از تونل» یا «همیشه مستقیم». زیردامنه‌ها هم شامل می‌شوند.
- **ایران مستقیم:** سایت‌ها و IPهای ایرانی مستقیم، بقیه از تونل.
- **ضد نشتی:** DNS برنامه‌های تونل‌شده از داخل تونل حل می‌شود، و IPv6 و QUIC (UDP 443) برای جلوگیری از نشت و قطعی مسدود می‌شوند.
- **پیش‌نمایش مسیریابی:** قبل از اتصال می‌بینید چه چیزی تونل، مستقیم یا مسدود می‌شود.

### 🔍 بازرس مسیریابی
برای اطمینان از اینکه قوانین **واقعاً** اجرا می‌شوند.

- برای هر برنامه و هر اتصال TCP/UDP، **مسیر تنظیم‌شده** را با **مسیر واقعی** مقایسه می‌کند.
- اگر برنامه‌ای برخلاف قانون مسیریابی شده باشد، هشدار قرمز می‌دهد. گزینهٔ «فقط مغایرت‌ها» هم دارد.
- برای هر اتصال دامنه، سرور، پروتکل، قانون تطبیق‌خورده و خروجی را نشان می‌دهد. پرس‌وجوهای DNS هم در حالت لاگ تفصیلی دیده می‌شوند.

### 🌐 مدیریت DNS
تست، رتبه‌بندی و اعمال DNS روی هر کارت شبکه.

- **تست همه:** میانهٔ زمان پاسخ، جیتر، درصد موفقیت و Loss هر DNS اندازه‌گیری و به آن **امتیاز** داده می‌شود. بهترین DNS برای گیم هم پیشنهاد می‌شود.
- **اعمال و بازگردانی:** DNS را روی کارت شبکهٔ انتخابی اعمال و تأیید کنید. قبل از اعمال، وضعیت قبلی ذخیره می‌شود تا با «بازگردانی» برگردد.
- **وارد کردن و خروجی TXT:** لیست DNSها را با فرمت ساده (نام در یک خط، بعد آدرس‌ها) وارد یا خارج کنید. موارد تکراری خودکار رد می‌شوند.
- افزودن، ویرایش و کپی پروفایل DNS.

### 📶 پایش پینگ
پینگ پیوستهٔ ICMP به هر میزبان یا IP.

- نمودار زندهٔ ۲ دقیقهٔ آخر؛ بسته‌های گم‌شده با علامت قرمز مشخص می‌شوند.
- میانگین، کمینه، بیشینه، مقدار فعلی، **جیتر** و **درصد Packet Loss**.
- ارزیابی کیفیت: عالی برای گیم / خوب / ضعیف (احتمال لگ).

### 🎮 گیمینگ
پروفایل هر بازی و بهینه‌سازی‌های سیستمی که **همگی برگشت‌پذیرند**.

- **بازی‌ها:**
  - بازی را اضافه کنید؛ وقتی اجرا شود، پروفایلش **خودکار فعال می‌شود**.
  - «مسیر بازی از تونل» با یک کلیک پوشهٔ بازی را به قوانین تونل اضافه می‌کند.
  - «افزایش اولویت بازی در حال اجرا».
- **بهینه‌سازی‌های سیستم** (برای هر کدام توضیح کامل داده می‌شود که چه می‌کند):
  - طرح انرژی High/Ultimate Performance و Game Mode ویندوز
  - بیدار نگه داشتن همهٔ هسته‌های CPU (غیرفعال کردن Core Parking)
  - تنظیم زمان‌بندی پردازنده به نفع برنامهٔ جلو و بیشترین اولویت برای بازی‌ها
  - حذف محدودیت شبکهٔ چندرسانه‌ای (Network Throttling)
  - کاهش تأخیر ورودی آنلاین (غیرفعال کردن Nagle و Delayed ACK)
  - خاموش کردن ضبط پس‌زمینهٔ Game Bar/DVR
  - توقف موقت Windows Search و SuperFetch
  - خاموش کردن ثبت زمان آخرین دسترسی NTFS و شمارنده‌های Perflib
  - تکرار سریع‌تر صفحه‌کلید
- **اعمال همه / برگرداندن همه:** با گزارش دقیق اینکه چه اعمال شد، چه از قبل فعال بود و چه نیاز به Admin دارد. گزینهٔ «برگرداندن خودکار هنگام خروج» هم در تنظیمات هست.
- **تست گیمینگ:** وضعیت فعلی همهٔ تنظیمات را بررسی می‌کند.
- **اتصال کنسول:** پروکسی KOALIX را روی شبکهٔ خانگی به اشتراک بگذارید تا کنسول یا گوشی هم از تونل استفاده کند. برای PS4، PS5، Xbox Series و Nintendo Switch راهنما دارد.

### 🛠️ مرکز تعمیر شبکه
ابزارهای رفع مشکل شبکه. **هر کار با توضیح کامل** (چه می‌کند، چه عواقبی دارد، اگر مشکلی پیش آمد چه کنید) و فقط با تأیید شما اجرا می‌شود.

- پاک کردن کش DNS (`flushdns`)
- تمدید IP از DHCP
- پاک کردن کش ARP
- ریست Winsock و ریست TCP/IP
- ریست پروکسی WinHTTP
- تست Gateway و نمایش اطلاعات شبکه (فقط خواندنی)
- **بررسی خودکار سیستم:** آیا DNS سیستم مسموم (فیلترشده) است؟ اینترنت مستقیم در دسترس است؟ VPN یا پروکسی دیگری تداخل دارد؟

### 🧭 پروکسی سیستم
برای برنامه‌هایی که TUN لازم ندارند.

- پروکسی سیستم ویندوز (WinINet) را روی `127.0.0.1` با پورت دلخواه فعال کنید. فقط برنامه‌هایی که از پروکسی سیستم پیروی می‌کنند (مثل مرورگرها) اثر می‌گیرند.
- وضعیت قبلی ذخیره می‌شود و با «بازگردانی» برمی‌گردد. اگر برنامهٔ دیگری پروکسی را عوض کند، هشدار می‌دهد.

### 📜 گزارش‌ها
- نمایش لاگ برنامه و موتورها.
- **خروجی گزارش عیب‌یابی** با یک کلیک. **رمزها، کلیدها و IPها خودکار حذف می‌شوند**، پس می‌توانید بدون نگرانی برای کسی بفرستید.

### ⚙️ تنظیمات
- **عمومی:** زبان (فارسی/English)، اجرا با ویندوز، کوچک شدن به سینی هنگام بستن، کلید میان‌بر سراسری **Ctrl+Alt+K**.
- **ظاهر:**
  - تم روشن/تیره با انیمیشن موجی
  - **پالت‌های آماده** (بنفش کوالا، زمردی، نارنجی غروب، آبی یخی، رز، کهربایی و **رنگین‌کمان ARGB**)، رنگ دلخواه، رنگ تصادفی و ذخیرهٔ پالت شخصی
  - **سبک‌ها:** پیش‌فرض، نئون، مینیمال، رترو گیمینگ
  - **حالت شیشه‌ای کامل** (پس‌زمینهٔ Acrylic و کارت‌های مات و شفاف)، جنس پنجره و شکل دکمهٔ اتصال
  - **ARGB واکنشی:** سرعت چرخش رنگ‌ها با سرعت دانلود یا پینگ تغییر می‌کند
  - روشن/خاموش کردن انیمیشن‌ها برای سیستم‌های ضعیف یا هنگام بازی
- **صداها:** برای اتصال، قطع، خطا، اتصال مجدد، جهش پینگ، سوییچ خودکار و حالت تمرکز یکی از ۳۰ صدای داخلی یا فایل wav/mp3 خودتان را انتخاب کنید. حجم همه با یک اسلایدر تنظیم می‌شود.
- **اتصال:**
  - حالت پیش‌فرض تونل‌زنی، اتصال مجدد خودکار، به‌روزرسانی خودکار Subscriptionها
  - **Kill Switch:** اگر تونل قطع شود، برنامه‌های تونل‌شده مسدود می‌شوند تا IP واقعی لو نرود
  - **سپر ضد مسمومیت DNS:** قبل از هر اتصال، نام سرور از دو مسیر پرسیده می‌شود تا پاسخ‌های جعلی فیلترینگ شناسایی شوند
  - **TLS Fragment (ضد DPI)**، **Mux**، MTU تونل و پشتهٔ TUN، Strict Routing و پشتیبانی IPv6
  - بستن خودکار برنامه‌های مزاحم (VPN و پروکسی‌های دیگر) قبل از اتصال
- **موتورها:** نصب و به‌روزرسانی sing-box و Xray از GitHub رسمی با تأیید SHA-256، و نمایش وضعیت WireGuard، WireSock و OpenVPN.
- **پشتیبان‌گیری و بازیابی:** همهٔ تنظیمات، پروفایل‌ها، Subscriptionها و قوانین در یک فایل **رمزنگاری‌شده با رمز دلخواه شما**.
- **به‌روزرسانی:** بررسی نسخهٔ جدید (به‌طور پیش‌فرض از همین مخزن GitHub) و دانلود و نصب با یک کلیک. تنظیمات و پروفایل‌ها حفظ می‌شوند.
- **بازگشت به پیش‌فرض** و دیدن دوبارهٔ تور راهنما.

### ℹ️ درباره
نسخهٔ برنامه، نسخهٔ Runtime، پوشهٔ داده، وضعیت موتورهای نصب‌شده و دسترسی Admin را نشان می‌دهد. دکمه‌هایی هم برای باز کردن پوشهٔ داده، دیدن دوبارهٔ تور و کپی اطلاعات سیستم (برای گزارش مشکل) دارد.

---

## ویجت شناور، حالت تمرکز و سینی ویندوز

- **ویجت شناور (شبیه MSI Afterburner):**
  - یک پنل کوچک که همیشه روی صفحه می‌ماند، حتی روی بازی.
  - پنج حالت دارد: فشرده، شبکه، سخت‌افزار، کامل و OSD.
  - وضعیت اتصال، پینگ، سرعت دانلود/آپلود، مصرف CPU/GPU و دمای CPU/GPU (و Hotspot) را نشان می‌دهد.
  - شفافیتش قابل تنظیم است. می‌توانید جایش را قفل کنید، یا Click-through کنید تا کلیک‌ها از آن رد شوند.
- **حالت تمرکز:** صفحهٔ مینیمال گیمینگ با وضعیت اتصال، پینگ زنده، نمودار و هشدار جهش پینگ. با **Esc** خارج می‌شوید.
- **سینی ویندوز (Tray):** آیکونی که رنگش بر اساس پینگ عوض می‌شود، به‌همراه اتصال/قطع سریع، لیست سرورها، ویجت و باز کردن برنامه.
- **جست‌وجوی سریع (Ctrl+K):** صفحه‌ها، سرورها و دستورها را جست‌وجو و اجرا کنید؛ مثل اتصال، قطع، پینگ همه، تعویض زبان یا تم، و گزارش عیب‌یابی.

## میان‌برهای صفحه‌کلید

| کلید | کار |
|---|---|
| `Ctrl+Alt+K` | اتصال / قطع سراسری (حتی وقتی پنجره بسته است) |
| `Ctrl+K` | جست‌وجوی سریع و دستورها |
| `W` `A` `S` `D` | جابه‌جایی بین کارت‌های سرور در داشبورد |
| `Esc` | خروج از حالت تمرکز |

---

## امنیت و حریم خصوصی

- **بدون سرور مرکزی و بدون ارسال آمار:** KOALIX هیچ اطلاعاتی برای سازنده نمی‌فرستد.
- **کانفیگ‌ها رمزنگاری‌شده** با DPAPI ویندوز ذخیره می‌شوند؛ فقط حساب کاربری شما می‌تواند بازشان کند.
- **موتورها فقط از GitHub رسمی** sing-box و Xray دانلود و با SHA-256 بررسی می‌شوند.
- **لاگ‌ها و گزارش‌ها** پیش از ذخیره یا خروجی، رمزها، UUIDها، کلیدها و IPها را حذف می‌کنند.
- **هر تغییر سیستمی** (DNS، پروکسی، فایروال، تنظیمات گیمینگ) در یک ژورنال ثبت می‌شود. اگر برنامه ناگهان بسته شود، در اجرای بعد پیشنهاد بازگردانی می‌دهد.
- محل داده‌ها: `%LOCALAPPDATA%\KOALIX`

---

## رفع مشکلات رایج

| مشکل | راه‌حل |
|---|---|
| ویندوز اجازهٔ اجرا نمی‌دهد | در SmartScreen روی More info و بعد Run anyway بزنید. |
| تونل‌زنی برنامه‌ای کار نمی‌کند | برنامه را با Admin اجرا کنید (دکمهٔ «اجرای مجدد با Admin»). |
| وصل می‌شود ولی سایت‌ها باز نمی‌شوند یا نیمه‌کاره لود می‌شوند | در تنظیمات ← اتصال، MTU تونل را کم کنید (مثلاً 1400 یا 1280). Strict Routing و سپر DNS هم روشن باشند. |
| با اتصال، اینترنت کلاً قطع می‌شود | پشتهٔ TUN را روی gvisor بگذارید و VPNهای دیگر را ببندید (بخش تداخل‌ها در داشبورد). |
| فقط بعضی سایت‌ها (مثل یوتیوب) باز نمی‌شوند | مطمئن شوید QUIC مسدود است، سرور دیگری را امتحان کنید، یا موتور را بین sing-box و Xray عوض کنید. |
| پینگ واقعی نشان داده نمی‌شود | sing-box را از تنظیمات ← موتورها نصب کنید. |
| بعد از قطع ناگهانی، اینترنت مشکل دارد | KOALIX را دوباره باز کنید و «بازگردانی» را بزنید، یا از مرکز تعمیر شبکه استفاده کنید. |

اگر مشکل حل نشد، از بخش **گزارش‌ها** خروجی عیب‌یابی بگیرید (اطلاعات حساس خودکار حذف می‌شود) و در بخش **[Issues](https://github.com/farzadrre/KOALIX/issues)** بفرستید.

---

## ساخت از سورس

```powershell
# پیش‌نیاز: ویندوز + .NET SDK 10
git clone https://github.com/farzadrre/KOALIX.git
cd KOALIX
dotnet build KoalixSuite.slnx -c Release
dotnet test tests/Koalix.Tests
# فایل اجرایی تکی در پوشهٔ output:
dotnet publish src/Koalix.App/Koalix.App.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o output
```
یا فقط `BUILD-KOALIX.bat` را اجرا کنید.

**انتشار نسخهٔ جدید:** نسخه را در `Directory.Build.props` بالا ببرید، بخش آن را در `CHANGELOG.md` بنویسید، سپس تگ را push کنید:
```powershell
git tag v0.4.40
git push origin v0.4.40
```
GitHub Actions (`.github/workflows/release.yml`) تست‌ها را اجرا می‌کند، نصب‌کننده، نسخهٔ قابل‌حمل و فید به‌روزرسانی را می‌سازد و Release را منتشر می‌کند.

### ساختار مخزن
```
src/Koalix.Core               state machine, split policy, redaction, abstractions
src/Koalix.Dns                DNS client & benchmark, ICMP/STUN, ping monitor, Windows DNS apply
src/Koalix.Tunneling          share links, subscriptions, connection manager, health checks
src/Koalix.SplitTunneling     SplitPolicy → sing-box route/dns rules
src/Koalix.SingBox            config builder, linter, Clash API, engine
src/Koalix.Xray               Xray config + engine (behind sing-box TUN)
src/Koalix.WireGuard          .conf parser, official service / sing-box endpoint
src/Koalix.SystemIntegration  processes, system proxy, journal, repair, conflicts, kill switch, gaming
src/Koalix.Infrastructure     SQLite + migrations, repositories, DPAPI, logging, engine manager
src/Koalix.App                WPF UI (MVVM, DI, fa/en)
tests/Koalix.Tests            unit + real-engine + network tests
installer/                    icons, splash, Inno Setup script
```
مستندات بیشتر: [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) · [CHANGELOG.md](CHANGELOG.md)

---

## مجوز
MIT؛ فایل [LICENSE](LICENSE) را ببینید. موتورهای sing-box (GPL-3.0)، Xray-core (MPL-2.0)، WireGuard و WireSock همراه برنامه نیستند. KOALIX آن‌ها را هنگام اجرا از انتشارهای رسمی‌شان دانلود می‌کند و تحت مجوز خودشان هستند.

<br>

---

<a id="english"></a>
## English

**KOALIX** is a gaming network suite for Windows 10/11 that puts everything you need for a stable, low-latency connection in one app.

### Highlights
- **Tunnel client** with **sing-box**, **Xray**, **WireGuard** and **OpenVPN** engines. It accepts `vless` / `vmess` / `trojan` / `ss` / `hysteria2` / `tuic` links, subscriptions, WireGuard `.conf` and OpenVPN `.ovpn` files.
- **Real per-application split tunneling** through a sing-box Wintun TUN adapter: no Proxifier, and no silent full-tunnel fallback.
- DNS manager and benchmark, ping/jitter monitor, routing inspector, persistent usage stats, network repair center and reversible gaming tweaks.
- Bilingual UI (Persian RTL / English) with live switching, light/dark themes, palettes, glass mode, a floating widget and custom sounds.

### Download
From **[Releases](https://github.com/farzadrre/KOALIX/releases/latest)**:
- **`KoalixApp-win-Setup.exe`**: the installer (recommended). It updates itself from inside the app and needs no admin rights to install.
- **`KOALIX-<version>-portable-win-x64.exe`**: a single-file portable build with no auto-update.

Requires Windows 10 1809+ / 11 (x64); .NET is bundled. The binaries are unsigned, so on SmartScreen choose **More info → Run anyway**. Create `portable.flag` next to `KOALIX.exe` to keep all data beside it.

### Quick start
1. Settings → Engines → install sing-box (and Xray if needed). Engines come only from official GitHub releases and are SHA-256 verified.
2. Tunnels → paste links or subscriptions, or drag files onto the window.
3. Click **Ping all** to find healthy, fast servers.
4. (Optional) Split Tunneling → pick which apps use the tunnel.
5. Dashboard → **Connect**. TUN mode needs Administrator rights; the app offers a one-click "restart as admin" button.

### Pages
- **Dashboard:**
  - Connect button and status card.
  - Server cards with flag, protocol, engine and ping (drag to reorder, W/A/S/D to move between them).
  - Smart auto-connect with failover.
  - Direct vs tunnel egress IP check.
  - Live speed chart, plus per-app and persistent usage stats (today / 3 days / week / month / all-time).
  - Server map, quick switches (game mode, Iran-direct routing, kill switch, system proxy) and a pre-connect conflict check.
- **Tunnels:**
  - Import links, subscriptions (with remaining volume and expiry), JSON, `.conf` and `.ovpn` files.
  - Per-profile or per-subscription engine (Auto / sing-box / Xray).
  - Real-delay HTTP tests through the tunnel; groups, filters, multi-select and sorting.
  - Redacted preview, engine validation, QR sharing, and encrypted OpenVPN credentials.
- **Split Tunneling:**
  - Modes: full tunnel, only selected apps, or all except selected.
  - Per-app rules (tunnel / direct / block) and folder rules covering every child process.
  - Domain rules and an Iran-direct preset.
  - Leak protection: DNS through the tunnel, IPv6 block, QUIC block.
  - A routing preview before you connect.
- **Routing Inspector:** compares configured vs observed routes for every app and TCP/UDP connection, with mismatch alerts, DNS queries and matched rules.
- **DNS Manager:**
  - Benchmarks DNS servers (median, jitter, success rate, loss) and scores them.
  - Applies the chosen server per adapter with verification, saving the previous state so you can restore it.
  - TXT import/export.
- **Ping Monitor:** continuous ICMP with a 2-minute chart, min/avg/max, jitter, packet loss and a gaming-quality verdict.
- **Gaming:**
  - Per-game profiles that auto-activate when the game launches; route a game folder through the tunnel in one click; raise a running game's priority.
  - Reversible system tweaks, each fully explained:
    - power plan and Game Mode
    - core parking and CPU scheduling priority
    - network throttling, Nagle and delayed ACK
    - Game DVR, Search and SuperFetch
    - NTFS last-access timestamps and Perflib counters
    - keyboard repeat
  - Apply-all and revert-all, with a report.
  - LAN proxy sharing for PS4/PS5/Xbox/Switch and phones.
- **Network Repair Center:**
  - Flush DNS, renew DHCP, clear ARP, reset Winsock, TCP/IP and the WinHTTP proxy, test the gateway.
  - Each action shows its effect, consequences and recovery steps, and runs only after you confirm.
  - A system self-check detects poisoned DNS, a missing direct internet connection and conflicting VPNs.
- **System Proxy:** a WinINet `127.0.0.1` proxy for apps that don't need TUN, with restore and change detection.
- **Logs:** a one-click diagnostic report with secrets and IPs redacted.
- **Settings:**
  - Language, autostart, tray and the global hotkey.
  - Themes, palettes (including ARGB), style presets, full glass mode, reactive ARGB and animations.
  - 30 built-in sounds or your own files.
  - Connection options: auto-reconnect, kill switch, DNS-poisoning shield, TLS fragment, mux, MTU and TUN stack, strict routing.
  - Engine install, encrypted backup/restore, in-app updates (default source: this repository) and reset to defaults.
- **About:** version, runtime, data folder, engine status and admin state, with buttons to replay the tour and copy system info.

### Extras
- **Floating widget:** an always-on-top overlay in Afterburner style, with compact / network / hardware / full / OSD modes. It shows ping, speeds, CPU/GPU load and temperatures, and supports opacity, position lock and click-through.
- **Focus mode:** a minimal gaming screen with a ping-spike alert.
- **Tray:** a ping-coloured icon with quick connect and the server list.
- **Ctrl+K:** a command palette for pages, servers and commands.
- **Ctrl+Alt+K:** global connect/disconnect.

### Privacy & security
- No telemetry and no central server.
- Configs are encrypted with Windows DPAPI.
- Engines are SHA-256 verified.
- Logs and reports redact secrets.
- Every system change is journaled and can be restored after a crash.
- Data lives in `%LOCALAPPDATA%\KOALIX`.

### Build from source
Windows + .NET SDK 10: see the commands in the Persian section above, or run `BUILD-KOALIX.bat`. To release, bump `Directory.Build.props`, add a `CHANGELOG.md` section and push a `vX.Y.Z` tag. The workflow then runs the tests, builds the installer, the portable exe and the update feed, and publishes the release.

### License
MIT — see [LICENSE](LICENSE). sing-box (GPL-3.0), Xray-core (MPL-2.0), WireGuard and WireSock are not bundled; they are downloaded from their official releases at runtime.

Found a bug? Open an **[issue](https://github.com/farzadrre/KOALIX/issues)** and attach the redacted diagnostic report from the Logs page.
