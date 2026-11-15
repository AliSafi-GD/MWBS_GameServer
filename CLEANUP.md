# CLEANUP.md — نقشه راه پاکسازی معماری (Clean Architecture + SOLID)

این فایل راهنمای کار برای ادامه‌ی پاکسازی پروژه است. همه‌ی اعداد با خود کد چک شده‌اند (تاریخ بررسی: نوامبر ۲۰۲۶).

## ۱. هدف و اولویت

**اولویت اصلی: سرور (`src/`) و SDK یونیتی ([sdk/unity/com.gameserver.client](sdk/unity/com.gameserver.client)).** این دو پایه‌ی همه‌ی بازی‌های آینده هستند و باید تمیز، تست‌پذیر و بدون فرض خاصِ یک بازی باشند.

کلاینت یونیتی [samples/unity-client](samples/unity-client) (Tank Arena) و ماژول بازی [samples/HelloGame.Modules](samples/HelloGame.Modules) **نمونه** هستند: اولویت پایین، فقط وقتی ارزان است تمیز شوند.

قاعده‌ی کار:

- قدم‌های کوچک؛ در هر قدم رفتار برنامه **تغییر نکند** (refactor خالص).
- بعد از هر قدم: `dotnet build GameServer.slnx` و اجرای [tests/GameServer.SmokeTest](tests/GameServer.SmokeTest) روی سرور در حال اجرا (طبق [README.md](README.md) و [NOTES.md](NOTES.md)).
- هر قدمی که ویژگی/endpoint را عوض می‌کند، مستندات [docs/](docs/) را **در همان قدم** به‌روز کن.

## ۲. نتیجه کلی

کیفیت کد خوب است: خوانا، کامنت‌دار، validation ورودی، SQL اتمیک، pipeline تمیز hook. اما معماری **"لایه‌بندی بر اساس feature"** است، نه Clean Architecture: پروژه‌ی `GameServer.Core` هم domain است، هم application و هم infrastructure (Postgres + JWT). SOLID فقط بخشی رعایت شده (DIP و OCP در مرز ماژول‌ها خوب، SRP/ISP/DIP در داخل Core ضعیف).

## ۳. سرور — نقاط قوت

- `GameServer.Abstractions` قرارداد پایدار ماژول‌هاست؛ ماژول‌ها فقط به آن وابسته‌اند (DIP).
- Transportها پروژه‌های جدا و pluggable هستند: [Transport.WebSocket](src/GameServer.Transport.WebSocket), [Transport.Tcp](src/GameServer.Transport.Tcp), [Transport.LiteNetLib](src/GameServer.Transport.LiteNetLib) (OCP).
- Friends / Groups / Chat پشت interface هستند: `IFriendStore`, `IGroupStore`, `IChatStore`.
- `GameException` در یک middleware به JSON تبدیل می‌شود.
- سیستم hook قبل/بعد تمیز است: [HookRunner.cs](src/GameServer.Core/HookRunner.cs).

## ۴. سرور — مشکلات (به ترتیب اهمیت)

### 4.1 Core سه لایه را قاطی کرده — **Postgres جدا شد** (قدم ۴)؛ JWT هنوز در Core
- **کجا:** [GameServer.Core.csproj](src/GameServer.Core/GameServer.Core.csproj) به `Npgsql` و `Microsoft.AspNetCore.Authentication.JwtBearer` (و `FrameworkReference` به AspNetCore) وابسته است؛ پوشه‌ی [Postgres/](src/GameServer.Core/Postgres) داخل Core است.
- **چرا مهم:** domain/application نباید از DB و فریم‌ورک خبر داشته باشد؛ تست بدون Postgres ممکن نیست.
- **راه‌حل:** پروژه‌ی `GameServer.Infrastructure.Postgres` (همه‌ی `Postgres*`، `DatabaseMigrator`) + Core بدون Npgsql. JWT (`TokenService`) هم می‌تواند به Infrastructure/Host برود.

### 4.2 دسترسی به داده یکدست نیست — **انجام شد** (قدم ۳)
- **کجا:** سرویس‌ها مستقیم با `NpgsqlDataSource` SQL خام می‌نویسند: [ShopService.cs](src/GameServer.Core/Shop/ShopService.cs), [ProgressService.cs](src/GameServer.Core/Progression/ProgressService.cs) (296 خط), [DailyRewardService.cs](src/GameServer.Core/Progression/DailyRewardService.cs), [PhoneIdentityProvider.cs](src/GameServer.Core/Identity/PhoneIdentityProvider.cs). [AdminStore.cs](src/GameServer.Core/Admin/AdminStore.cs) هم 544 خط با query‌های زیاد است. (در مجموع 17 فایل `NpgsqlDataSource` دارند؛ 11 تایشان در `Postgres/` و درست‌اند.)
- **چرا مهم:** نقض SRP و DIP؛ منطق کسب‌وکار با SQL قاطی است و unit test نمی‌شود.
- **راه‌حل:** interfaceهای store مثل `IShopPurchaseStore`, `IProgressStore`, `IDailyRewardStore`, `IPhoneCodeStore`؛ پیاده‌سازی در Infrastructure. `AdminStore` را بر اساس حوزه تکه کن (کاربران، اقتصاد، اعلان، رقابت‌ها...).

### 4.3 `Program.cs` خیلی بزرگ — **در حال انجام** (قدم ۱)
- **کجا:** [Program.cs](src/GameServer.Host/Program.cs) 767 خط؛ endpointهای auth، account، config، version-check، wallet، leaderboards، notifications، rpc و admin + منطق داخل lambdaها. در حالی که Social/Shop/Match/Chat/Progress/Admin از قبل در [Endpoints/](src/GameServer.Host/Endpoints) هستند.
- **راه‌حل:** یک فایل Endpoints برای هر حوزه + extension method برای middlewareها؛ `Program.cs` فقط ترکیب (composition root).

### 4.4 Service locator (`IServiceProvider`) — **انجام شد** (قدم ۵)
- **کجا (بررسی شد):**
  - [HookRunner.cs](src/GameServer.Core/HookRunner.cs): برای ساختن `HookContext` سرویس‌ها را lazy می‌گیرد؛ کامنت خودش می‌گوید به‌خاطر وابستگی دایره‌ای است (chat/social خودشان از hook استفاده می‌کنند).
  - [MatchService.cs](src/GameServer.Core/Matches/MatchService.cs): `IEconomy`, `IProgress`, `ILeaderboards`, `IConfig`, transportها.
  - [ProgressService.cs](src/GameServer.Core/Progression/ProgressService.cs) و [PostgresNotifications.cs](src/GameServer.Core/Postgres/PostgresNotifications.cs): `GetService<ConnectionManager>()` (push اختیاری به کاربر آنلاین).
- **چرا مهم:** وابستگی‌ها پنهان است؛ دایره‌ها دیده نمی‌شوند.
- **راه‌حل:** دایره را صریح بشکن: `IModuleContextFactory`/`Lazy<T>` برای HookRunner؛ برای push یک `IPresence`/`IRealtimePusher` کوچک به‌جای `ConnectionManager`.

### 4.5 وابستگی به کلاس‌های concrete — **انجام شد** (قدم ۶)
- **کجا:** [ChatService.cs](src/GameServer.Core/Realtime/ChatService.cs) (`ConnectionManager`, `PartyService`)، [SocialFacade.cs](src/GameServer.Core/Social/SocialFacade.cs) (`ConnectionManager`) و `AccountCleanup` (همان فایل؛ `GroupService`, `PartyService`, `ConnectionManager`).
- **راه‌حل:** interfaceهای کوچک (مثلاً `IPresence`: آنلاین بودن/ارسال به کاربر؛ `IPartyMembership`).

### 4.6 `IUserStore` چاق است (ISP) — **انجام شد** (قدم ۶)
- **کجا:** `IUserStore.cs` (حالا [UserStores.cs](src/GameServer.Core/UserStores.cs)): ورود device، هویت‌ها، پروفایل، لیست ادمین، export — در یک interface (13 متد).
- **راه‌حل:** `IAccountStore`, `IIdentityStore`, `IUserAdminStore` (Search/Export/SetDisabled). `PostgresUserStore` (422 خط) می‌تواند هر سه را پیاده کند و بعداً تکه شود.

### 4.7 زمان تست‌پذیر نیست — **انجام شد** (قدم ۲)
- **کجا:** `DateTime.UtcNow` در **17 فایل (46 مورد)**، از جمله [ShopService.cs](src/GameServer.Core/Shop/ShopService.cs), [DailyRewardService.cs](src/GameServer.Core/Progression/DailyRewardService.cs), [ProgressService.cs](src/GameServer.Core/Progression/ProgressService.cs), [PostgresCompetitions.cs](src/GameServer.Core/Postgres/PostgresCompetitions.cs), [PartyService.cs](src/GameServer.Core/Social/PartyService.cs), [RealtimeHub.cs](src/GameServer.Core/Realtime/RealtimeHub.cs).
- **راه‌حل:** `TimeProvider` تزریق شود (`TimeProvider.System` در production، `FakeTimeProvider` در تست). دوره‌های shop، جایزه روزانه و تورنومنت‌ها بدون این قابل تست نیستند.

### 4.8 پاسخ‌های untyped — **موارد `object` انجام شد** (قدم ۵)؛ anonymous objectهای endpointها مانده
- **کجا:** `ShopService.PurchasesOfAsync` → `IReadOnlyList<object>` ([ShopService.cs](src/GameServer.Core/Shop/ShopService.cs)); `MatchActions.CreateAsync/ModesAsync` → `Task<object>` ([MatchActions.cs](src/GameServer.Core/Matches/MatchActions.cs)); `LoginResultAsync` → `Task<object>` در Program.cs؛ و چندین endpoint با anonymous object.
- **راه‌حل:** recordهای response → قرارداد typed؛ مستندات و SDK نمی‌توانند از سرور جدا شوند (drift). بعداً می‌شود OpenAPI از آن‌ها ساخت.

### 4.9 کد مرده — **انجام شد** (قدم ۴)
- [InMemoryStorage.cs](src/GameServer.Core/InMemoryStorage.cs) در هیچ‌جا استفاده نشده (فقط تعریف خودش). حذف کن (یا برای تست‌ها به پروژه‌ی تست ببر).

### 4.10 unit test نداریم
- فقط e2e: [tests/GameServer.SmokeTest](tests/GameServer.SmokeTest) (سرور در حال اجرا لازم دارد) و [tests/GameServer.ArenaBots](tests/GameServer.ArenaBots).
- **راه‌حل:** `tests/GameServer.Core.Tests` (xUnit) برای منطق خالص (ProfileValidator, ConfigRules, ShopCatalog, GoalCatalog, HookRunner، دوره‌ها با FakeTimeProvider) + Testcontainers برای storeهای Postgres.

### 4.11 نسخه‌های floating — **انجام شد** (قدم ۷)
- **کجا:** [GameServer.Core.csproj](src/GameServer.Core/GameServer.Core.csproj): `Version="10.*"` برای `Npgsql` و `JwtBearer` (بقیه‌ی csprojهای src را هم چک کن؛ [Directory.Build.props](Directory.Build.props) از قبل هست).
- **راه‌حل:** Central Package Management (`Directory.Packages.props`) با نسخه‌های ثابت؛ build قابل تکرار.

## ۵. SDK یونیتی

### نقاط قوت
- یک façade واحد: `GameClient` ([GameClient.cs](sdk/unity/com.gameserver.client/Runtime/Core/GameClient.cs)).
- `IHttp` ([Http.cs](sdk/unity/com.gameserver.client/Runtime/Http/Http.cs)) و `IClientStorage` ([Basics.cs](sdk/unity/com.gameserver.client/Runtime/Core/Basics.cs)) قابل تزریق‌اند؛ transportها (`IRealtimeTransport`, `IMatchTransportClient`) قابل افزودن‌اند.
- وابستگی مستقیم به Unity نیست (`#if UNITY_2019_4_OR_NEWER`، بخش Unity جدا در [UnityParts.cs](sdk/unity/com.gameserver.client/Runtime/Unity/UnityParts.cs))؛ به همین دلیل [sdk/unity/SdkSmokeTest](sdk/unity/SdkSmokeTest) بیرون Unity اجرا می‌شود.
- LiteNetLib در assembly اختیاری و پشت define ([Runtime.LiteNetLib](sdk/unity/com.gameserver.client/Runtime.LiteNetLib)).
- هیچ نام یا فرضِ مخصوص یک بازی (tank/arena) در Runtime پیدا نشد.

### مشکلات
1. **کلاس‌های API بدون interface:** `AuthApi`, `AccountApi`, `ConfigApi`, `EconomyApi`, `ShopApi`, `ProgressApi`, `CompetitionsApi`, `NotificationsApi`, `SocialApi`/`FriendsApi`/`GroupsApi`/`PartiesApi`, `ChatApi`, `MatchesApi`, `RpcApi` همه `sealed class` هستند؛ `GameClient` هم `sealed` و propertyها concrete. کد بازی نمی‌تواند در تست آنها را fake کند (در نمونه‌ی کلاینت `GameSession` مجبور است مستقیم به SDK بچسبد). **راه‌حل:** `IAuthApi`, `IShopApi`, ... و `IGameClient`.
2. **فایل‌های بزرگ:** [Models.cs](sdk/unity/com.gameserver.client/Runtime/Models/Models.cs) (443 خط، 43 کلاس در یک فایل)، [RealtimeClient.cs](sdk/unity/com.gameserver.client/Runtime/Realtime/RealtimeClient.cs) (508، شامل مدل‌های `PartyInvite`, `NotificationEvent`... + منطق اتصال)، [Transports.cs](sdk/unity/com.gameserver.client/Runtime/Realtime/Transports.cs) (434، WebSocket + TCP + helperها در یک فایل). **راه‌حل:** Models را بر اساس حوزه تکه کن (Auth/Shop/Social/...) و transportها را یکی‌یکی فایل جدا کن.
3. **وابستگی به Newtonsoft:** Api‌ها `JObject/JToken` برمی‌گردانند یا می‌گیرند و `package.json` به `com.unity.nuget.newtonsoft-json` وابسته است. برای payload آزاد بازی (rpc, config) قابل قبول است، ولی در APIهای ثابت باید modelهای typed باشند تا نشت `JObject` به کد بازی کم شود.
4. **`Dispatcher` internal:** thread اصلی فقط در همان‌جا حل می‌شود؛ برای تست و بازی‌هایی با main-loop سفارشی باید قابل تزریق (`IDispatcher`/`SynchronizationContext`) باشد.
5. **پکیج کامل نیست:** در [com.gameserver.client](sdk/unity/com.gameserver.client) پوشه‌ی `Tests/` (EditMode)، `Documentation~` و `CHANGELOG.md` وجود ندارد؛ تست فقط بیرونی است. برای استفاده در چند بازی اینها لازم‌اند. نسخه‌ی SDK هم باید با نسخه‌ی قرارداد سرور هماهنگ شود (الان `0.1.0` و بدون چک سازگاری).
6. `GameClientOptions.BaseUrl` پیش‌فرض `http://localhost:5000` دارد؛ برای SDK قابل استفاده در چند بازی بهتر است پیش‌فرض خالی باشد و نبودنش خطای واضح بدهد.

## ۶. کلاینت نمونه (اولویت پایین — کوتاه)

- **خوب:** لایه‌بندی asmdef: `Game.Core` (بدون UI) ← `Game.Meta` ← `Game.Arena`؛ صفحه‌ها از سرویس‌ها استفاده می‌کنند نه مستقیم از SDK.
- **مشکلات:**
  - god class: [SocialPage.cs](samples/unity-client/Assets/Scripts/Meta/Menu/SocialPage.cs) (701 خط، ۴ تب)، [ArenaScreen.cs](samples/unity-client/Assets/Scripts/Arena/ArenaScreen.cs) (679؛ شبکه + input + render + HUD + FX)، [MenuScreen.cs](samples/unity-client/Assets/Scripts/Meta/Menu/MenuScreen.cs) (491)، [Ui.cs](samples/unity-client/Assets/Scripts/Meta/Ui/Ui.cs) (770).
  - `GameSession.Instance` ([GameSession.cs](samples/unity-client/Assets/Scripts/Core/GameSession.cs)) service locator سراسری است (12 استفاده).
  - `async void` در 13 جا (از جمله `ArenaScreen`) — خطاها بلعیده می‌شوند؛ بهتر `async Task` + یک wrapper که لاگ کند.
  - [ChatService.cs](samples/unity-client/Assets/Scripts/Core/Services/ChatService.cs) (474 خط) وضعیت خوانده‌شده را در PlayerPrefs نگه می‌دارد و ۳۰ دوست را poll می‌کند، چون سرور endpoint «گفتگوهای من با تعداد unread» ندارد. **این یک کار سمت سرور است** (قدم ۹)، نه کلاینت.

## ۷. نقشه راه

- [ ] **۱. (کد و مستندات انجام شد، فقط SmokeTest مانده)** شکستن `Program.cs` به فایل‌های Endpoints هر حوزه + extensionهای middleware و اصلاح مستندات مربوط. *تمام:* `Program.cs` فقط composition؛ SmokeTest سبز.
  - انجام شد: `Program.cs` از 767 به 134 خط؛ `Endpoints/` (Auth, Config, Account, Economy, Competition, Notification, Rpc, Admin)، `Middleware/` (`UseGameExceptionHandler`, `UseMaintenanceMode`, `UseAdminAudit`)، `Extensions/HttpContextExtensions.cs` (`http.UserId()` به جای `CurrentUserId`/`Me`). بیلد 0 warning، لیست routeها قبل و بعد یکی (مقایسه متنی، نه runtime). مستندات `docs/` و `README.md` با کد تطبیق داده شد؛ `docs` بیلد می‌شود.
  - مانده: اجرای SmokeTest روی Postgres (Docker روی این دستگاه بالا نیامد؛ DI، binding و ترتیب middleware در runtime تست نشده).
- [ ] **۱.۱. مشکلاتی که در قدم ۱ دیده شد (عمداً درست نشد، رفتار عوض نشود):**
  - ~~`POST /v1/auth/device` هیچ rate limit ندارد (ساخت حساب بی‌حد).~~ **درست شد (قدم ۱.۲):** policy جدا با نام `device`، ۳۰ درخواست در دقیقه برای هر IP.
  - `/v1/auth/refresh` و `/v1/auth/logout` با policy `auth` (20 در دقیقه برای هر IP) محدودند؛ پشت NAT اپراتور موبایل زود پر می‌شود. پیشنهاد: کلید rate limit بر اساس کاربر/توکن، یا policy جدا.
  - `AdminAuditMiddleware` و فیلتر گروه admin هر دو به `HttpContext.Items["actor"]` و prefix مسیر تکیه دارند (coupling شل). prefix الان فقط در `AdminEndpoints.ApiPrefix` است.
  - کدهای خطای ناشناخته (مثل `invalid_version`) بی‌صدا 400 می‌شوند چون در `StatusFor` نیستند؛ بهتر است status کنار خود خطا تعریف شود.
- [x] **۱.۲. انتخاب روش‌های ورود برای هر بازی (guest / googleplay / ...) بدون دست زدن به کد هسته.**
  - انجام شد: `IIdentityProvider` و `VerifiedIdentity` به `GameServer.Abstractions` رفتند (فقط اضافه شد؛ namespace `GameServer.Abstractions`). ورود device حالا یک provider با نام `device` است (`DeviceIdentityProvider`)؛ endpoint و request/response قبلی `POST /v1/auth/device` دست‌نخورده ماند، ولی اگر `device` فعال نباشد همان خطای `404 unknown_provider` را می‌دهد، و `link/device` با `400 not_supported` رد می‌شود. تنظیم `GameServer:Auth:Providers` (env: `GameServer__Auth__Providers`، در compose: `AUTH_PROVIDERS`) لیست روش‌های فعال را مشخص می‌کند؛ خالی/بدون مقدار = رفتار قبلی عیناً (device همیشه، googleplay با credentials، phone با `Phone:Enabled`) پس ارتقا چیزی را قطع نمی‌کند. با لیست: فقط موارد لیست‌شده روشن‌اند و نام ناشناخته، provider لیست‌شده‌ی تنظیم‌نشده (مثلاً googleplay بدون ClientId/Secret) یا نام تکراری سرور را موقع startup با پیام روشن متوقف می‌کند؛ لیست کردن `phone` آن را روشن می‌کند. منطق انتخاب/اعتبارسنجی در یک کلاس خالص و تست‌پذیر است: `AuthProviderSelection`. در کد: `AddGameServerCore(..., auth: a => a.Add<T>().Use(...))` (مثل `RealtimeBuilder`)، و ماژول بازی با `IModuleInitializer.RegisterIdentityProvider(...)` (متد با پیاده‌سازی پیش‌فرض اضافه شد تا ماژول‌های قدیمی نشکنند) provider خودش را می‌آورد؛ `Program.cs` بعد از `LoadAll()` متد `IdentityProviders.Validate()` را صدا می‌زند. SQL ‌ی phone به `IPhoneCodeStore` + `PostgresPhoneCodes` رفت (رفتار یکسان). `GET /v1/auth/providers` حالا `device` را هم برمی‌گرداند (SDK/نمونه فقط non-null بودن لیست را چک می‌کنند). تست‌ها: `tests/GameServer.Core.Tests` (xUnit، ۳۴ تست، بدون DB) و به `run-tests.ps1`/`.sh` اضافه شد. مستندات (`docs/server/authentication.md` با بخش «Writing your own provider»، settings، rest-api، error-codes، README) به‌روز شد.
  - مانده: اجرای SmokeTest روی Postgres (Docker بالا نبود)؛ DI و ترتیب startup فقط با تست واحد و build بررسی شد، نه با سرور واقعی. نکته‌ی رفتاری: rate limit تازه برای device login (۳۰/دقیقه/IP) تنها تغییر عمدی رفتار است.
  - جزئی از قدم‌های بعدی هم انجام شد: `IPhoneCodeStore` (از قدم ۳، که بعداً کامل شد) و پروژه‌ی `tests/GameServer.Core.Tests` (از قدم ۲؛ `TimeProvider` بعداً در قدم ۲ انجام شد).
- [x] **۱.۳. مستندات نصب سرور + forwarded headers + service hosting.**
  - انجام شد: بخش جدید `docs/install/` (index، Docker Compose، Linux binary با systemd، Windows binary با Windows Service، VPS با Caddy) و حذف `getting-started/docker.md` (داخل `install/docker-compose.md` ادغام شد)؛ `run-the-server.md` حالا صریحاً «اجرا برای توسعه» است. در کد: تنظیم opt-in به نام `GameServer:ForwardedHeaders:Enabled` (+ `KnownProxies`، env: `GameServer__ForwardedHeaders__Enabled`، در compose: `FORWARDED_HEADERS`، پیش‌فرض خاموش) که `UseForwardedHeaders` را اولین middleware می‌گذارد (قبلاً پشت reverse proxy همه‌ی بازیکن‌ها یک IP و یک سطل rate limit داشتند)؛ `restart: unless-stopped` برای هر دو سرویس compose؛ `UseWindowsService()` و `UseSystemd()` (پکیج‌های `Microsoft.Extensions.Hosting.WindowsServices/Systemd` با `10.*`).
  - بررسی شد: build بدون warning، تست‌های `GameServer.Core.Tests`، `dotnet publish` و اجرای `GameServer.Host.dll` با env varهای مستندات (فقط در اتصال به DB شکست می‌خورد)، `docker compose config`. بررسی نشد: اجرای واقعی روی Docker/Linux/systemd/Windows Service/Caddy و رفتار `UseForwardedHeaders` با proxy واقعی.
  - نکته: `GameServer:ModulesPath` نسبی نسبت به working directory حل می‌شد (نه محل برنامه)؛ این در قدم ۱.۴ اصلاح شد (حالا نسبت به content root). برای سرویس‌ها همچنان مسیر مطلق توصیه می‌شود.
- [x] **۱.۴. کانفیگ بازی به‌صورت فایل (game config as files).**
  - انجام شد: کانفیگ بازی (کلیدهای `matches`, `shop`, `quests`, ...) حالا همراه ماژول بازی ارسال می‌شود: `modules/<Name>/config/<key>.json` با فرمت `{ "public": false, "value": {...} }` (`$schema` و فیلدهای `_comment` نادیده گرفته می‌شوند؛ `//` و trailing comma مجازند). پوشه‌ی اختیاری دوم: `GameServer:GameConfig:Path`. یک key در دو جا = توقف startup با نام هر دو فایل. در `samples/HelloGame.Modules/config/` (و csproj با `CopyToPublishDirectory`) کانفیگ Tank Arena یک فایل برای هر key است؛ توکن‌های `@now±Nd` با تاریخ ثابت جایگزین شد؛ `wwwroot/arena/arena-config.json` و کارت «Server setup (admin)» (با admin key از پیش پر شده) از صفحه‌ی arena حذف شد.
  - ترتیب startup: بارگذاری ماژول‌ها ← **خواندن و validate فایل‌ها (بدون DB، با همان parserهای سرویس‌ها؛ خطا = توقف با مسیر فایل و دلیل، همه‌ی خطاها با هم)** ← migration ← **اعمال** طبق `GameServer:GameConfig:Mode` (env `GAME_CONFIG_MODE` در compose): `seed` (پیش‌فرض: فقط keyهای غایب)، `sync` (فایل مرجع است؛ مقدار قدیمی در history با actor `config-files` و یک خط audit؛ هیچ key بدون فایل پاک نمی‌شود)، `off`. منطق خالص و تست‌شده: `GameConfigLoader/Validator/Planner/Applier` در `src/GameServer.Core/GameConfig/`. تست‌ها در `GameConfigTests.cs` (۳۰ تست جدید، بدون DB).
  - `GET /admin/api/config/export` zip از کانفیگ فعلی DB به همین فرمت می‌دهد (دکمه‌ی «Export as files» در تب Config) تا سرور موجود به git منتقل شود. `PostgresConfigStore` از قبل snapshot ۵ ثانیه‌ای داشت و با هر write/delete/rollback باطل می‌شود؛ فقط یک race (load در حال اجرا هنگام write) با شماره‌ی نسخه بسته شد. مسیرهای نسبی `ModulesPath` و `GameConfig:Path` حالا نسبت به `ContentRootPath` حل می‌شوند (نه working directory). `SmokeTest` مقدار keyهایی را که دست می‌زند در پایان برمی‌گرداند؛ `ArenaBots` دیگر کانفیگ نمی‌نویسد و فقط وجود آن را چک می‌کند.
  - مانده: seeding روی Postgres واقعی و اجرای SmokeTest/ArenaBots اجرا نشد (Docker/DB نبود)؛ فقط تست واحد، build، publish و اجرای host تا مرحله‌ی اتصال به DB (با فایل خراب) بررسی شد. cache پارس‌شده‌ی کاتالوگ (shop/quests) هنوز هر بار از JSON پارس می‌شود.
- [ ] **۱.۵. (آینده) JSON Schema برای keyهای کانفیگ.** برای هر key یک فایل `*.schema.json` (shop، quests، matches، ...) تا ویرایشگرها با `"$schema"` در فایل‌های `config/*.json` autocomplete و خطای لحظه‌ای بدهند. فیلد `$schema` الان در فرمت مجاز است و نادیده گرفته می‌شود. بهتر است schema از همان parserهای سرور درآید یا با یک تست با آنها هم‌خوان بماند تا drift نکند.
- [x] **۲.** پروژه‌ی `tests/GameServer.Core.Tests` (xUnit) + تزریق `TimeProvider` به جای `DateTime.UtcNow` در 17 فایل. *تمام:* هیچ `DateTime.UtcNow` در `src/` نمانده؛ تست‌های shop/daily/progress با `FakeTimeProvider`.
  - انجام شد: `AddGameServerCore` ساعت را با `TryAddSingleton(TimeProvider.System)` ثبت می‌کند (پس تست یا host می‌تواند قبل از آن ساعت خودش را ثبت کند). همه‌ی ۴۶ مورد در ۱۸ فایل (Core و endpointهای Host) حالا `_time.UtcNow()` هستند؛ `UtcNow()` یک extension کوچک در [TimeProviderExtensions.cs](src/GameServer.Core/TimeProviderExtensions.cs) است که `DateTime` با Kind=Utc برمی‌گرداند (همان نوعی که سرور همه‌جا دارد). کلاس‌هایی که با DI ساخته نمی‌شوند (`MatchInstance`، `Party`، تیکت matchmaker، `State` در `RealtimeHub`) زمان ساختشان را از صاحبشان می‌گیرند. endpointها `TimeProvider` را به‌عنوان پارامتر lambda می‌گیرند. در `src/` هیچ `DateTime.UtcNow/Now` یا `DateTimeOffset.UtcNow/Now` نمانده.
  - منطق تاریخ جایزه‌ی روزانه از SQL جدا شد: `DailyStreak` (`Today`, `NextDayStartUtc`, `Current`, `AfterClaim`, `RewardDay`) در [DailyRewardService.cs](src/GameServer.Core/Progression/DailyRewardService.cs)؛ رفتار عیناً همان است.
  - تست‌ها: [TimeTests.cs](tests/GameServer.Core.Tests/TimeTests.cs) (۳۳ تست، پکیج `Microsoft.Extensions.TimeProvider.Testing`): ثبت ساعت در DI (پیش‌فرض System؛ ساعت fake به `TokenService` و سرویس‌ها می‌رسد)، کلیدهای دوره (روز/هفته‌ی ISO/ماه، مرز سال)، `NextReset`، بازه‌ی فروش محصول shop، و streak جایزه‌ی روزانه با offset (یک هفته claim با یک روز جاافتاده روی ساعت fake). جمعاً ۹۷ unit test.
  - مانده: ~~تست خود سرویس‌های shop/daily/progress~~ در قدم ۳ انجام شد. ماژول‌های بازی (`samples/HelloGame.Modules`) هنوز `DateTime.UtcNow` دارند؛ `IModuleInitializer`/`HookContext` ساعتی به ماژول نمی‌دهد. اعتبارسنجی انقضای JWT (`JsonWebTokenHandler`) و `now()` های SQL هنوز ساعت خودشان را دارند.
- [x] **۳.** انتقال SQL از سرویس‌ها به storeها (`IShopPurchaseStore`, `IProgressStore`, `IDailyRewardStore`, `IPhoneCodeStore`) و تکه کردن `AdminStore`. *تمام:* هیچ سرویسی `NpgsqlDataSource` نمی‌گیرد.
  - انجام شد (2026-11-15): `ShopService`، `DailyRewardService` و `ProgressService` دیگر SQL ندارند؛ interfaceها کنار سرویس‌ها: [IShopPurchaseStore.cs](src/GameServer.Core/Shop/IShopPurchaseStore.cs)، [IDailyRewardStore.cs](src/GameServer.Core/Progression/IDailyRewardStore.cs) (claim به شکل `IDailyClaim`: قفل ردیف تا commit، dispose بدون commit = برگشت)، [IProgressStore.cs](src/GameServer.Core/Progression/IProgressStore.cs)؛ پیاده‌سازی‌ها در `Postgres/` (`PostgresShopPurchases`, `PostgresDailyRewards`, `PostgresProgress`) با همان SQL. `PurchasesOfAsync` حالا `ShopPurchaseCount` typed برمی‌گرداند (همان JSON).
  - `AdminStore` (550 خط) حذف شد: مدل‌ها و قانون‌های خالص در [AdminModels.cs](src/GameServer.Core/Admin/AdminModels.cs) (`AdminKeys`: hash، master key، کلید جدید، نام؛ `AdminRules`: redact، چک storage/broadcast، الگوی LIKE)، شش interface در [IAdminStores.cs](src/GameServer.Core/Admin/IAdminStores.cs): `IAdminAccountStore`, `IAdminAuditLog`, `IConfigHistoryStore`, `IPlayerAdminData`, `IBroadcastStore`, `IAdminMetrics` و شش کلاس `Postgres*` (Accounts, Audit, ConfigHistory, PlayerAdminData, Broadcasts, AdminMetrics). نام متدها همان ماند. چک master key از store بیرون آمد و در فیلتر admin است (`AdminKeys.IsMaster`). `PruneAsync` دو تکه شد (metrics ۷ روز، audit ۱۸۰ روز).
  - بیرون `Postgres/` فقط `ServiceCollectionExtensions.cs` هنوز Npgsql دارد (ثبت DI؛ در قدم ۴ جابه‌جا می‌شود).
  - تست‌ها: [ServiceTests.cs](tests/GameServer.Core.Tests/ServiceTests.cs) + [Fakes.cs](tests/GameServer.Core.Tests/Fakes.cs) (۳۰ تست): خود سرویس‌ها از DI واقعی با storeهای حافظه‌ای و `FakeTimeProvider` — خرید و limit روزانه و reset نیمه‌شب، برگشت limit وقتی پرداخت شکست، requestId تکراری، بازه‌ی فروش؛ claim روزانه روز به روز با روز جاافتاده، offset، شکست پرداخت؛ stat و achievement و quest روزانه و reset، claim دوباره، achievement مخفی؛ و قانون‌های admin. جمعاً ۱۲۷ unit test.
  - مانده: storeهای Postgres فقط با SmokeTest روی DB واقعی تست می‌شوند (Testcontainers نیست). `PostgresUserStore` هنوز چاق است (قدم ۶).
- [x] **۴.** ساخت `GameServer.Infrastructure.Postgres` و انتقال `Postgres/` + `DatabaseMigrator` + storeهای جدید. *تمام:* `GameServer.Core.csproj` بدون Npgsql؛ حذف `InMemoryStorage.cs`.
  - انجام شد (2026-11-15): پروژه‌ی [GameServer.Infrastructure.Postgres](src/GameServer.Infrastructure.Postgres) (namespace `GameServer.Infrastructure.Postgres`) با هر ۲۱ فایل `Postgres/` (با `git mv`، محتوا جز namespace دست نخورده). ثبت DI دو تکه شد: `AddGameServerCore` فقط سرویس‌ها (دیگر connection string لازم ندارد)، و `AddGameServerPostgres(config)` در [PostgresServiceCollectionExtensions.cs](src/GameServer.Infrastructure.Postgres/PostgresServiceCollectionExtensions.cs) data source، `DatabaseMigrator` و همه‌ی storeها را ثبت می‌کند (خطای نبودن `ConnectionStrings__Default` هم آن‌جاست). `Program.cs` هر دو را صدا می‌زند. `GameServer.Core.csproj` دیگر Npgsql ندارد؛ `InMemoryStorage.cs` حذف شد. Dockerfile عوض نشد (کل `src/` را کپی می‌کند).
  - تست‌ها: [WiringTests.cs](tests/GameServer.Core.Tests/WiringTests.cs): Core + Postgres با `ValidateOnBuild` (هر سرویس همه‌ی وابستگی‌هایش را دارد)، Core بدون connection string، پیام خطای Postgres بدون connection string، و این‌که assembly ی Core به Npgsql ارجاع ندارد. تست‌های سرویس (`TestServer`) حالا اصلاً Postgres ثبت نمی‌کنند. جمعاً ۱۳۱ unit test.
  - مانده (4.1): JWT (`TokenService`، پکیج `JwtBearer`) و `FrameworkReference` به AspNetCore هنوز در Core است.
- [x] **۵.** حذف `IServiceProvider` از `HookRunner`/`MatchService`/`ProgressService`/`PostgresNotifications` + recordهای typed response (`ShopService.PurchasesOfAsync`, `MatchActions`, `LoginResultAsync`...). *تمام:* هیچ `IServiceProvider` در constructor سرویس‌ها و هیچ `object` به‌عنوان نوع برگشتی در endpointها نیست.
  - انجام شد (2026-11-15): `ProgressService` و `PostgresNotifications` حالا `IPlayerPush` (فقط `SendToUserAsync`/`SendToAllAsync`، در [ConnectionManager.cs](src/GameServer.Core/Realtime/ConnectionManager.cs)) می‌گیرند؛ دایره‌ای نبود چون `ConnectionManager` وابستگی ندارد. دو دایره‌ی واقعی با `Lazy<T>` صریح شدند (helper داخلی `AddLazy<T>()`): `HookRunner` ← `Lazy<HookServices>` (record هفت سرویس context؛ chat و social خودشان hook اجرا می‌کنند) و `MatchService` ← `Lazy<IEnumerable<IMatchTransport>>` (transportها `MatchService` را به‌عنوان `IMatchHost` می‌گیرند). سرویس‌های match (`IEconomy`, `IProgress`, ...) مستقیم تزریق می‌شوند و یک `MatchServices` برای همه‌ی matchها ساخته می‌شود. `GET /v1/realtime` به جای `IServiceProvider` خود transportها را می‌گیرد. در `src/` فقط APIهای builder (`Add(name, sp => ...)` برای transport/provider) هنوز `IServiceProvider` دارند که جای درستشان composition root است.
  - typed: `MatchCreated`, `MatchModeView`, `MatchModesView` ([MatchActions.cs](src/GameServer.Core/Matches/MatchActions.cs))، `LoginResult` (AuthEndpoints)، `AccountView` (AccountEndpoints)؛ `PurchasesOfAsync` در قدم ۳ شد. JSON همان است (Web defaults، camelCase). تنها `object` باقی‌مانده `Transports` در `MatchModesView` است چون هر transport شکل خودش را دارد (`Describe()`).
  - تست‌ها: `ReachingAGoal_TellsThePlayer_Once` (push با `IPlayerPush` جعلی)، `MatchRecords_KeepTheirJsonNames`، و resolve شدن `Lazy<HookServices>` در `WiringTests`. جمعاً ۱۳۳ unit test.
  - مانده: anonymous objectهای خیلی از endpointها (مثلاً `new { items = ... }`)؛ می‌شود با OpenAPI بعداً record شوند.
- [x] **۶.** تکه کردن `IUserStore` (و interfaceهای کوچک `IPresence` برای `ChatService`/`SocialFacade`/`AccountCleanup`). *تمام:* هر مصرف‌کننده فقط interface لازمش را می‌گیرد.
  - انجام شد (2026-11-15): \`IUserStore\` حذف شد و سه interface جایش آمد ([UserStores.cs](src/GameServer.Core/UserStores.cs)): \`IAccountStore\` (Get, GetPublic, UpdateProfile, Delete)، \`IIdentityStore\` (ورود device، هویت‌ها، link/unlink، لیست هویت‌ها)، \`IUserAdminStore\` (Search, SetDisabled, Export). \`PostgresUserStore\` هر سه را پیاده می‌کند و یک instance پشت هر سه ثبت شده. سرویس‌های Core فقط \`IAccountStore\` می‌گیرند؛ هر endpoint فقط interface (یا دو interface) ای که واقعاً صدا می‌زند.
  - به جای کلاس‌های concrete: \`IRealtime\` (از قبل بود) در \`SocialFacade\`, \`AccountCleanup\`, \`Matchmaker\`, \`FriendsService\`, \`GroupService\`, \`PartyService\`, \`ChatService\`؛ \`IChannelSubscriptions\` (Join/Leave) برای \`ChatService\`؛ \`IPartyMembership\` (IsMember, Mine, LeaveAsync) برای \`ChatService\`/\`Matchmaker\`/\`AccountCleanup\`؛ \`IGroupCleanup\` برای \`AccountCleanup\`. \`ConnectionManager\` concrete فقط در \`RealtimeHub\` (صاحب اتصال‌ها)، \`AdminBackground\` (\`Counts()\`) و دو endpoint admin مانده؛ endpointهای گروه/پارتی همچنان خود \`GroupService\`/\`PartyService\` را می‌گیرند (API کامل همان سرویس).
  - تست: \`AccountCleanupTests\` با سه fake (ترتیب: clan، party، disconnect) و چک \`WiringTests\` که یک \`PostgresUserStore\` پشت هر سه interface است. جمعاً ۱۳۴ unit test.
  - داکیومنت سایت: صفحه‌ی جدید [docs/server/architecture.md](docs/server/architecture.md) (پروژه‌ها، wiring، storeها، push، ساعت، دو دایره، تست با fake؛ در sidebar «Server framework») و به‌روز شدن [docs/guides/testing.md](docs/guides/testing.md). قدم‌های ۲ تا ۵ قبلاً در سایت نیامده بودند؛ این صفحه آن‌ها را هم پوشش می‌دهد.
- [x] **۷.** Central Package Management و pin کردن نسخه‌ها. *تمام:* هیچ `10.*` در csprojها؛ `Directory.Packages.props` موجود.
  - انجام شد (2026-11-15): [Directory.Packages.props](Directory.Packages.props) با `ManagePackageVersionsCentrally`؛ هر ۱۰ پکیج روی **همان نسخه‌ای که قبلاً resolve می‌شد** ثابت شد (JwtBearer/Hosting.* 10.0.15، Npgsql 10.0.3، LiteNetLib 1.3.5، Test.Sdk 17.14.2، xunit 2.9.3، runner 2.8.2، TimeProvider.Testing 10.11.0، Newtonsoft 13.0.3)، پس رفتار عوض نشد (`dotnet list package` قبل و بعد یکی). از ۶ csproj (src، tests، SdkSmokeTest) `Version` حذف شد. `dotnet list package --vulnerable --include-transitive`: هیچ پکیج آسیب‌پذیری نیست.
  - Dockerfile حالا `Directory.Packages.props` را هم کپی می‌کند (بدون آن `docker build` می‌شکست).
  - داکیومنت سایت: بخش «NuGet versions» در [architecture.md](docs/server/architecture.md) و یک tip در [modules.md](docs/server/modules.md): ماژولی داخل همین repo باید نسخه را در `Directory.Packages.props` بگذارد (وگرنه NU1008).
- [ ] **۸.** SDK: interface برای APIها و `IGameClient`، تکه کردن `Models.cs`/`RealtimeClient.cs`/`Transports.cs`، `IDispatcher`، `Tests/` و CHANGELOG. *تمام:* بازی بتواند `IShopApi` را fake کند؛ فایلی بالای ~300 خط نباشد.
- [ ] **۹.** سرور: endpoint «گفتگوهای من با تعداد unread» (+ مستندات + SDK `ChatApi`). *تمام:* کلاینت دیگر ۳۰ دوست را poll نمی‌کند.
- [ ] **۱۰.** (اولویت پایین) کلاینت نمونه: تکه کردن `SocialPage`/`ArenaScreen`/`MenuScreen`/`Ui`، حذف `async void`، کم کردن `GameSession.Instance`.

## ۸. روش کار در هر قدم

- [ ] branch جدید از `main` (مثلاً `cleanup/step-2-timeprovider`)
- [ ] فقط یک تغییر مشخص، بدون تغییر رفتار
- [ ] `dotnet build GameServer.slnx` با **0 warning**
- [ ] اجرای سرور و [tests/GameServer.SmokeTest](tests/GameServer.SmokeTest) طبق [README.md](README.md) / [NOTES.md](NOTES.md) (و unit testها وقتی وجود دارند)
- [ ] به‌روزرسانی [docs/](docs/) (و [NOTES.md](NOTES.md) اگر وضعیت پروژه عوض شد) در همان قدم
- [ ] commit کوچک با پیام روشن؛ بعد قدم بعدی
