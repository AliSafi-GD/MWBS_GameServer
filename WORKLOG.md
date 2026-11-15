# WORKLOG — گزارش کار و ادامه از خانه

آخرین به‌روزرسانی: 2026-11-15. همه‌ی کارها روی `main` هستند (آخرین commit کار: `f40dff1`).

این فایل خلاصه‌ی کارهای انجام‌شده، چیزهایی که هنوز تست نشده، و قدم بعدی است.
نقشه‌ی راه کامل و جزئیات هر قدم در [CLEANUP.md](CLEANUP.md) است؛ وضعیت کلی پروژه در [NOTES.md](NOTES.md).

## هدف

- پروژه را فیچر به فیچر جلو ببریم، خودم پروژه را یاد بگیرم، داکیومنت را دقیق کنیم و پروژه را تمیز کنیم.
- **اولویت: سرور (`src/`) و Unity SDK (`sdk/unity/com.gameserver.client`)**، چون پایه‌ی همه‌ی بازی‌های بعدی هستند.
  کلاینت Tank Arena و لاجیک بازی (`samples/`) اولویت پایین دارند.
- روش کار: هر قدم کوچک، بدون تغییر رفتار مگر عمدی، با build + تست + به‌روز کردن داکیومنت همان بخش.

## کارهای انجام‌شده

### ۰. بررسی معماری (Clean Architecture + SOLID)
- نتیجه: کیفیت کد خوب است، ولی معماری «لایه‌بندی بر اساس فیچر» است و SOLID نصفه.
- مهم‌ترین مشکلات سرور: Core به Npgsql وابسته است؛ چند سرویس مستقیم SQL می‌نویسند (`ShopService`, `ProgressService`,
  `DailyRewardService`, `AdminStore`)؛ `IServiceProvider` در چند سرویس؛ `DateTime.UtcNow` بدون `TimeProvider`؛ پاسخ‌های untyped.
- همه در [CLEANUP.md](CLEANUP.md) بخش ۴ و ۵ با فایل و راه‌حل.

### ۱. شکستن `Program.cs` (commit `2e5d669`)
- `Program.cs` از 767 خط به حدود 150 خط؛ فقط composition root.
- endpointها: یک فایل برای هر حوزه در `src/GameServer.Host/Endpoints/` (Auth, Config, Account, Economy, Competition,
  Notification, Rpc, Admin).
- middlewareها: `src/GameServer.Host/Middleware/` (`UseGameExceptionHandler`, `UseMaintenanceMode`, `UseAdminAudit`).
- `http.UserId()` در `Extensions/HttpContextExtensions.cs` جای `CurrentUserId` و `Me` تکراری.
- لیست routeها قبل و بعد مقایسه شد: یکی بود.

### ۲. ماژولار کردن auth (همان commit `2e5d669`)
- انتخاب روش ورود برای هر سرور با `GameServer:Auth:Providers` (در `.env`: `AUTH_PROVIDERS`):
  `device` = فقط guest، `device,googleplay`، `googleplay` = فقط Google. خالی = رفتار قبلی.
- provider اشتباه یا تنظیم‌نشده → سرور موقع start با پیام روشن متوقف می‌شود.
- `IIdentityProvider` در `GameServer.Abstractions`. provider جدید از کد (`auth: a => a.Add<T>()`) یا از ماژول بازی
  (`init.RegisterIdentityProvider(...)`).
- SQL تلفن به `IPhoneCodeStore`. اولین پروژه‌ی unit test: `tests/GameServer.Core.Tests`.
- تغییر رفتار عمدی: `POST /v1/auth/device` حالا rate limit دارد (۳۰/دقیقه/IP)؛ `/v1/auth/providers` شامل `device` است.

### ۳. داکیومنت نصب به سبک Nakama (commit `29cfde2`)
- بخش جدید `docs/install/`: Docker Compose، Linux binary (systemd)، Windows binary (Windows Service)، VPS با Caddy.
- کد: forwarded headers (`FORWARDED_HEADERS`)، `UseWindowsService()` و `UseSystemd()`، `restart: unless-stopped` در compose.
- سایت داکیومنت: https://alisafi-gd.github.io/MWBS_GameServer/ (با هر push به `main` که `docs/` را عوض کند خودکار آپدیت می‌شود).

### ۴. کانفیگ بازی به‌شکل فایل (commit `f40dff1`)
- کانفیگ هر بازی کنار ماژولش: `modules/<Name>/config/<key>.json` با فرمت `{ "public": false, "value": { ... } }`.
  نمونه: `samples/HelloGame.Modules/config/`.
- موقع start: همه‌ی فایل‌ها قبل از DB چک می‌شوند؛ خطا = توقف با مسیر فایل.
- حالت‌ها با `GAME_CONFIG_MODE`: `seed` (پیش‌فرض، فقط کلیدهای غایب)، `sync` (فایل مرجع است)، `off`.
- export کانفیگ فعلی DB به فایل: دکمه‌ی «Export as files (zip)» در تب Config پنل admin.
- دکمه‌ی دستی «Write game config» و admin key از پیش پرشده از صفحه‌ی `/arena/` حذف شد.
- مسیرهای نسبی (`ModulesPath`, `GameConfig:Path`) از content root حساب می‌شوند.
- داکیومنت: `docs/server/game-config.md`.

### ۵. ساعت قابل تست: `TimeProvider` (قدم ۲ در CLEANUP.md، 2026-11-15)
- همه‌ی `DateTime.UtcNow` در `src/` (۴۶ مورد، ۱۸ فایل) حالا از `TimeProvider` تزریق‌شده می‌خوانند: `_time.UtcNow()`.
- `AddGameServerCore` ثبتش می‌کند با `TryAddSingleton(TimeProvider.System)`؛ تست یا host می‌تواند قبلش ساعت خودش را بگذارد.
- منطق تاریخ جایزه‌ی روزانه در `DailyStreak` (بدون DB، رفتار همان).
- ۳۳ تست جدید در `tests/GameServer.Core.Tests/TimeTests.cs` با `FakeTimeProvider`. روی Postgres: unit ۹۷، SmokeTest ۲۱۰، SDK ۹۲، ArenaBots ۱۵، همه سبز.
- مانده: ماژول بازی هنوز ساعت ندارد (`HelloModule.cs`).

### ۶. SQL بیرون از سرویس‌ها + تکه کردن AdminStore (قدم ۳ در CLEANUP.md، 2026-11-15)
- `ShopService`، `DailyRewardService`، `ProgressService` حالا فقط با interface کار می‌کنند (`IShopPurchaseStore`, `IDailyRewardStore`, `IProgressStore`)؛ SQL همان است و در `Postgres/` رفت.
- `AdminStore` حذف شد → `AdminModels.cs` (مدل‌ها + `AdminKeys`/`AdminRules`) + شش interface در `IAdminStores.cs` + شش کلاس `Postgres*`.
- ۳۰ تست جدید (`ServiceTests.cs`, `Fakes.cs`): خود سرویس‌ها بدون DB. ۱۲۷ unit test.

### ۷. پروژه‌ی `GameServer.Infrastructure.Postgres` (قدم ۴ در CLEANUP.md، 2026-11-15)
- کل `Postgres/` از Core به پروژه‌ی جدید رفت؛ Core دیگر Npgsql ندارد؛ `InMemoryStorage.cs` (کد مرده) حذف شد.
- ثبت: `AddGameServerCore(...)` (سرویس‌ها) + `AddGameServerPostgres(config)` (data source، migrator، storeها). `Program.cs` هر دو را دارد.
- `WiringTests.cs`: کل گراف DI با `ValidateOnBuild` چک می‌شود. ۱۳۱ unit test.

### ۹. بدون service locator + پاسخ‌های typed (قدم ۵ در CLEANUP.md، 2026-11-15)
- `ProgressService` و `PostgresNotifications`: `IPlayerPush` به جای `IServiceProvider`.
- `HookRunner`: `Lazy<HookServices>`؛ `MatchService`: `Lazy<IEnumerable<IMatchTransport>>` + سرویس‌ها مستقیم. دایره‌ها حالا در نوع دیده می‌شوند.
- recordها: `MatchCreated`, `MatchModesView`, `LoginResult`, `AccountView` (همان JSON). ۱۳۳ unit test.

### ۸. باگ بستن WebSocket (2026-11-15)
- سرور بعد از فرستادن close فوراً `Abort()` می‌کرد؛ اتصال reset می‌شد و گاهی آخرین پیام (مثلاً خطای `unauthorized`) به کلاینت نمی‌رسید. SmokeTest یک بار به همین دلیل رد شد.
- حالا [WebSocketClosing.cs](src/GameServer.Transport.WebSocket/WebSocketClosing.cs): close زیر قفل ارسال، صبر برای جواب close کلاینت (حداکثر ۲ ثانیه)، و فقط بعد abort. برای realtime و match هر دو.
- آزمایش: ۳۰۰ اتصال با توکن غلط. قبل: ۸ پیام خطا گم شد و فقط ۵۲ close عادی. بعد: ۳۰۰/۳۰۰ هر دو.

### نحوه‌ی کار در این جلسه
- کار اجرایی را agentهای Sonnet 5.5 انجام دادند؛ نتیجه‌ی هر کدام (build، تست‌ها، مقایسه‌ی routeها، کد کلیدی) را جدا چک کردم.
- آمار فعلی: `dotnet build GameServer.slnx` بدون warning؛ ۶۴ unit test سبز؛ `npm run build` داکیومنت سبز.

## ⚠ هنوز تست نشده (روی دستگاه محل کار Docker/Postgres بالا نیامد)

هیچ‌کدام از تغییرات بالا روی سرور واقعی با Postgres اجرا نشده‌اند. اول در خانه این‌ها را چک کن:

- [x] `tests/GameServer.SmokeTest` روی سرور واقعی (DI، binding، ترتیب middleware، auth جدید). 2026-11-15: ۲۱۰ سبز.
- [x] seed و sync کانفیگ بازی روی Postgres. 2026-11-15: هر دو حالت کار کرد. دکمه‌ی export در پنل admin هنوز تست نشده.
- [x] `tests/GameServer.ArenaBots`. 2026-11-15: ۱۵ سبز (۸ بات). اول خراب بود چون `SdkSmokeTest` کانفیگ `matches`
  را با مدهای `sdk_*` عوض می‌کرد و برنمی‌گرداند؛ حالا مثل SmokeTest قبل از تست کانفیگ را نگه می‌دارد و آخر برمی‌گرداند.
  اگر DB از قبل خراب شده، یک بار سرور را با `GameServer__GameConfig__Mode=sync` اجرا کن.
- [ ] بازی یونیتی Tank Arena با سرور (کانفیگ باید خودکار seed شده باشد).
- [ ] (اختیاری) قدم‌های Linux/systemd و Windows Service و Caddy از `docs/install/`.

## شروع کار در خانه

```bash
git clone https://github.com/AliSafi-GD/MWBS_GameServer.git   # یا اگر قبلاً داری: git switch main && git pull
cd MWBS_GameServer
cp .env.example .env
```

بعد تست کامل یک‌کلیکی (Postgres با Docker، سرور، SmokeTest، SdkSmokeTest، unit testها؛ گزارش در `logs/REPORT.txt`):

```powershell
powershell -ExecutionPolicy Bypass -File .\run-tests.ps1
```

یا دستی:

```bash
docker compose up -d postgres
dotnet publish samples/HelloGame.Modules -c Release -o modules/HelloGame.Modules   # کانفیگ بازی هم کپی می‌شود
dotnet run --project src/GameServer.Host
dotnet run --project tests/GameServer.SmokeTest -- http://localhost:5000 <admin key از appsettings.Development.json>
dotnet test tests/GameServer.Core.Tests
```

- اگر پورت 5432 گرفته بود: راه‌حل در [NOTES.md](NOTES.md) بخش Set up (کانتینر روی 5433).
- در لاگ start باید خطی شبیه `Game config: 7 key(s) from 1 source(s), mode seed.` ببینی.
- داکیومنت روی سیستم خودت: داخل `docs/` دستور `npm ci` و بعد `npm run dev`.

## مشکلات شناخته‌شده (درست نشده)

- **امنیتی:** compose پورت `7350` را روی همه‌ی interfaceها باز می‌کند و Docker از کنار ufw رد می‌شود. اگر `FORWARDED_HEADERS`
  روشن باشد و پورت از بیرون باز بماند، IP جعلی ممکن است. پیشنهاد: پیش‌فرض `127.0.0.1:7350`.
- اگر guest خاموش باشد، بازیکن می‌تواند آخرین روش ورود فعالش (مثلاً Google) را unlink کند و قفل شود.
- `refresh` و `logout` با policy `auth` (۲۰/دقیقه/IP)؛ پشت NAT موبایل زود پر می‌شود.
- در حالت `sync`، فایل `maintenance.json` در پوشه‌ی config حالت تعمیرات را با هر restart برمی‌گرداند؛ آن را در config نگذار.
- چک کانفیگ بین کلیدها نیست (مثلاً quest با stat ناموجود، آیتم shop که در `economy` نیست).
- کاتالوگ shop و quest در هر درخواست دوباره از JSON parse می‌شود.
- `gh` (GitHub CLI) روی دستگاه محل کار نصب نیست؛ PR را از سایت GitHub باز کن.

## قدم بعدی (پیشنهاد، به ترتیب)

1. ~~**تست روی Postgres**~~ انجام شد (همه سبز؛ فقط دکمه‌ی export و بازی یونیتی مانده).
2. ~~**قدم ۲ در CLEANUP.md:** `TimeProvider` به جای `DateTime.UtcNow`.~~ انجام شد (بخش ۵ بالا).
3. ~~**قدم ۳:** بیرون کشیدن SQL از سرویس‌ها و تکه کردن `AdminStore`.~~ انجام شد (بخش ۶ بالا).
4. ~~**قدم ۴:** پروژه‌ی `GameServer.Infrastructure.Postgres`.~~ انجام شد (بخش ۷ بالا).
5. **قدم ۶:** تکه کردن `IUserStore` + `IPresence` برای `ChatService`/`SocialFacade`/`AccountCleanup`. (قدم ۵ انجام شد، بخش ۹.)
6. **SDK (قدم ۸):** interface برای APIها تا بازی‌ها بتوانند fake کنند؛ تکه کردن `Models.cs`، `RealtimeClient.cs`.
7. **کوچک ولی مفید:** پیش‌فرض امن پورت compose، JSON Schema برای فایل‌های کانفیگ (قدم ۱.۵)، cache کاتالوگ parse‌شده.

## نقشه‌ی فایل‌های مهم

| چیز | کجا |
|---|---|
| نقشه‌ی راه تمیزکاری | [CLEANUP.md](CLEANUP.md) |
| وضعیت پروژه و gotchaها | [NOTES.md](NOTES.md) |
| composition root سرور | [src/GameServer.Host/Program.cs](src/GameServer.Host/Program.cs) |
| endpointها | [src/GameServer.Host/Endpoints/](src/GameServer.Host/Endpoints/) |
| دیتابیس (Postgres، migration، ثبت DI) | [src/GameServer.Infrastructure.Postgres/](src/GameServer.Infrastructure.Postgres/) |
| login providerها | [src/GameServer.Core/Identity/](src/GameServer.Core/Identity/) |
| کانفیگ بازی (کد) | [src/GameServer.Core/GameConfig/](src/GameServer.Core/GameConfig/) |
| کانفیگ نمونه‌ی Tank Arena | [samples/HelloGame.Modules/config/](samples/HelloGame.Modules/config/) |
| unit testها | [tests/GameServer.Core.Tests/](tests/GameServer.Core.Tests/) |
| داکیومنت نصب | [docs/install/](docs/install/) |
| داکیومنت کانفیگ بازی | [docs/server/game-config.md](docs/server/game-config.md) |
