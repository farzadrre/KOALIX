# وضعیت ویژگی‌ها / Feature status (v0.2.0)

راهنما: ✅ پیاده‌سازی + تست خودکار در این محیط · 🟡 پیاده‌سازی و کامپایل شده، **روی ویندوز تست نشده** · ⏳ هنوز پیاده‌سازی نشده

| ماژول | وضعیت | توضیح |
|---|---|---|
| پارس/خروجی TXT برای DNS | ✅ | تست واحد با شمارهٔ خط خطاها |
| کوئری DNS واقعی (UDP + TCP fallback) | ✅ | تست شبکهٔ واقعی (1.1.1.1، 8.8.8.8) پاس شد |
| فرمول جیتر، میانه، امتیازدهی، ۵ استراتژی رتبه‌بندی | ✅ | |
| تست UDP با STUN | ✅ | تست واقعی مقابل stun.l.google.com پاس شد |
| ICMP | 🟡 | در محیط تست مسدود بود (به‌درستی «مسدود» گزارش شد) |
| اعمال/بازگردانی DNS روی آداپتور ویندوز | 🟡 | netsh + snapshot + تأیید پس از اعمال |
| پارس لینک‌ها: vless/vmess/trojan/ss/hy2/tuic/socks/http + subscription | ✅ | |
| ساخت کانفیگ sing-box (TUN + قوانین برنامه‌ای) | ✅ | با **sing-box 1.14.4 واقعی** (`check`) برای ۷ پروتکل × ۳ حالت + WireGuard + Xray-front + کانفیگ ادغامی تأیید شد |
| کانفیگ Xray (ws/grpc/xhttp/reality/…) | ✅ | با **Xray 26.10.31 واقعی** (`run -test`) تأیید شد |
| Linter معنایی کانفیگ | ✅ | ارجاع‌های ناموجود را می‌گیرد (sing-box check نمی‌گیرد) |
| پارس WireGuard و حذف کلیدها در لاگ | ✅ | |
| Redaction، State Machine، PathValidator | ✅ | |
| SQLite + Migration + رمزنگاری پروفایل‌ها | ✅ | (روی لینوکس با AES؛ روی ویندوز DPAPI 🟡) |
| Journal و Crash Recovery | ✅ / 🟡 | منطق Journal تست شد؛ بازگردانی واقعی روی ویندوز تست نشده |
| **تونل‌زنی انتخابی واقعی (Wintun + process rules)** | 🟡 | **تا اجرای `tests/acceptance/Run-SplitTunnelAcceptance.ps1` روی ویندوز، «کارکرد تأییدشده» محسوب نمی‌شود** |
| ConnectionManager (health check، auto-reconnect، Degraded) | 🟡 | |
| Kill Switch (فایروال per-app) | 🟡 | حالت block-all واقعی نیازمند WFP ⏳ |
| Routing Inspector (جدول TCP/UDP + Clash API + تأیید IP خروجی) | 🟡 | منطق «نامشخص به‌جای حدس» تست شد |
| بازرس DNS (از لاگ debug) | 🟡 | فرمت لاگ sing-box بین نسخه‌ها متفاوت است |
| مرکز تعمیر شبکه (۷ عملیات با توضیح/تأیید/لاگ) | 🟡 | |
| بهینه‌سازی گیمینگ برگشت‌پذیر + پروفایل بازی | 🟡 | |
| System Proxy با snapshot و بررسی تغییر | 🟡 | |
| EngineManager (دانلود از GitHub + SHA-256) | 🟡 | تجزیهٔ `.dgst` تست شد |
| رابط WPF دوزبانه فارسی/انگلیسی با RTL زنده، تم تیره/روشن، Mica/Acrylic، Tray | 🟡 | کامپایل شد و `KOALIX.exe` (win-x64، self-contained) ساخته شد؛ اجرا نشده |
| مصرف حافظه < 120MB در حالت idle | ⏳ | اندازه‌گیری نشده |
| سرویس ممتاز ویندوز با IPC احراز هویت‌شده | ⏳ | فعلاً UAC per-action |
| جایگزین WFP برای split tunneling / kill switch سراسری | ⏳ | |
| UDP-over-TCP | ⏳ | |
| نصب‌کنندهٔ Inno Setup | 🟡 | اسکریپت آماده، کامپایل نشده |
| امضای دیجیتال (Authenticode) | ⏳ | |

## 0.2.0
- قوانین پوشه‌ای در تونل‌زنی انتخابی (پیاده‌سازی + تست با sing-box واقعی).
- نصب‌کننده و به‌روزرسانی Velopack (ساخته شد؛ روی ویندوز تست نشده).
- رابط جدید (ساخته و کامپایل شد؛ بصری روی ویندوز تست نشده).
