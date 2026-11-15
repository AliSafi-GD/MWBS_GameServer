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

### 4.1 Core سه لایه را قاطی کرده
- **کجا:** [GameServer.Core.csproj](src/GameServer.Core/GameServer.Core.csproj) به `Npgsql` و `Microsoft.AspNetCore.Authentication.JwtBearer` (و `FrameworkReference` به AspNetCore) وابسته است؛ پوشه‌ی [Postgres/](src/GameServer.Core/Postgres) داخل Core است.
- **چرا مهم:** domain/application نباید از DB و فریم‌ورک خبر داشته باشد؛ تست بدون Postgres ممکن نیست.
- **راه‌حل:** پروژه‌ی `GameServer.Infrastructure.Postgres` (همه‌ی `Postgres*`، `DatabaseMigrator`) + Core بدون Npgsql. JWT (`TokenService`) هم می‌تواند به Infrastructure/Host برود.

### 4.2 دسترسی به داده یکدست نیست
- **کجا:** سرویس‌ها مستقیم با `NpgsqlDataSource` SQL خام می‌نویسند: [ShopService.cs](src/GameServer.Core/Shop/ShopService.cs), [ProgressService.cs](src/GameServer.Core/Progression/ProgressService.cs) (296 خط), [DailyRewardService.cs](src/GameServer.Core/Progression/DailyRewardService.cs), [PhoneIdentityProvider.cs](src/GameServer.Core/Identity/PhoneIdentityProvider.cs). [AdminStore.cs](src/GameServer.Core/Admin/AdminStore.cs) هم 544 خط با query‌های زیاد است. (در مجموع 17 فایل `NpgsqlDataSource` دارند؛ 11 تایشان در `Postgres/` و درست‌اند.)
- **چرا مهم:** نقض SRP و DIP؛ منطق کسب‌وکار با SQL قاطی است و unit test نمی‌شود.
- **راه‌حل:** interfaceهای store مثل `IShopPurchaseStore`, `IProgressStore`, `IDailyRewardStore`, `IPhoneCodeStore`؛ پیاده‌سازی در Infrastructure. `AdminStore` را بر اساس حوزه تکه کن (کاربران، اقتصاد، اعلان، رقابت‌ها...).

### 4.3 `Program.cs` خیلی بزرگ — **در حال انجام** (قدم ۱)
- **کجا:** [Program.cs](src/GameServer.Host/Program.cs) 767 خط؛ endpointهای auth، account، config، version-check، wallet، leaderboards، notifications، rpc و admin + منطق داخل lambdaها. در حالی که Social/Shop/Match/Chat/Progress/Admin از قبل در [Endpoints/](src/GameServer.Host/Endpoints) هستند.
- **راه‌حل:** یک فایل Endpoints برای هر حوزه + extension method برای middlewareها؛ `Program.cs` فقط ترکیب (composition root).

### 4.4 Service locator (`IServiceProvider`)
- **کجا (بررسی شد):**
  - [HookRunner.cs](src/GameServer.Core/HookRunner.cs): برای ساختن `HookContext` سرویس‌ها را lazy می‌گیرد؛ کامنت خودش می‌گوید به‌خاطر وابستگی دایره‌ای است (chat/social خودشان از hook استفاده می‌کنند).
  - [MatchService.cs](src/GameServer.Core/Matches/MatchService.cs): `IEconomy`, `IProgress`, `ILeaderboards`, `IConfig`, transportها.
  - [ProgressService.cs](src/GameServer.Core/Progression/ProgressService.cs) و [PostgresNotifications.cs](src/GameServer.Core/Postgres/PostgresNotifications.cs): `GetService<ConnectionManager>()` (push اختیاری به کاربر آنلاین).
- **چرا مهم:** وابستگی‌ها پنهان است؛ دایره‌ها دیده نمی‌شوند.
- **راه‌حل:** دایره را صریح بشکن: `IModuleContextFactory`/`Lazy<T>` برای HookRunner؛ برای push یک `IPresence`/`IRealtimePusher` کوچک به‌جای `ConnectionManager`.

### 4.5 وابستگی به کلاس‌های concrete
- **کجا:** [ChatService.cs](src/GameServer.Core/Realtime/ChatService.cs) (`ConnectionManager`, `PartyService`)، [SocialFacade.cs](src/GameServer.Core/Social/SocialFacade.cs) (`ConnectionManager`) و `AccountCleanup` (همان فایل؛ `GroupService`, `PartyService`, `ConnectionManager`).
- **راه‌حل:** interfaceهای کوچک (مثلاً `IPresence`: آنلاین بودن/ارسال به کاربر؛ `IPartyMembership`).

### 4.6 `IUserStore` چاق است (ISP)
- **کجا:** [IUserStore.cs](src/GameServer.Core/IUserStore.cs): ورود device، هویت‌ها، پروفایل، لیست ادمین، export — در یک interface (13 متد).
- **راه‌حل:** `IAccountStore`, `IIdentityStore`, `IUserAdminStore` (Search/Export/SetDisabled). `PostgresUserStore` (422 خط) می‌تواند هر سه را پیاده کند و بعداً تکه شود.

### 4.7 زمان تست‌پذیر نیست
- **کجا:** `DateTime.UtcNow` در **17 فایل (46 مورد)**، از جمله [ShopService.cs](src/GameServer.Core/Shop/ShopService.cs), [DailyRewardService.cs](src/GameServer.Core/Progression/DailyRewardService.cs), [ProgressService.cs](src/GameServer.Core/Progression/ProgressService.cs), [PostgresCompetitions.cs](src/GameServer.Core/Postgres/PostgresCompetitions.cs), [PartyService.cs](src/GameServer.Core/Social/PartyService.cs), [RealtimeHub.cs](src/GameServer.Core/Realtime/RealtimeHub.cs).
- **راه‌حل:** `TimeProvider` تزریق شود (`TimeProvider.System` در production، `FakeTimeProvider` در تست). دوره‌های shop، جایزه روزانه و تورنومنت‌ها بدون این قابل تست نیستند.

### 4.8 پاسخ‌های untyped
- **کجا:** `ShopService.PurchasesOfAsync` → `IReadOnlyList<object>` ([ShopService.cs](src/GameServer.Core/Shop/ShopService.cs)); `MatchActions.CreateAsync/ModesAsync` → `Task<object>` ([MatchActions.cs](src/GameServer.Core/Matches/MatchActions.cs)); `LoginResultAsync` → `Task<object>` در Program.cs؛ و چندین endpoint با anonymous object.
- **راه‌حل:** recordهای response → قرارداد typed؛ مستندات و SDK نمی‌توانند از سرور جدا شوند (drift). بعداً می‌شود OpenAPI از آن‌ها ساخت.

### 4.9 کد مرده
- [InMemoryStorage.cs](src/GameServer.Core/InMemoryStorage.cs) در هیچ‌جا استفاده نشده (فقط تعریف خودش). حذف کن (یا برای تست‌ها به پروژه‌ی تست ببر).

### 4.10 unit test نداریم
- فقط e2e: [tests/GameServer.SmokeTest](tests/GameServer.SmokeTest) (سرور در حال اجرا لازم دارد) و [tests/GameServer.ArenaBots](tests/GameServer.ArenaBots).
- **راه‌حل:** `tests/GameServer.Core.Tests` (xUnit) برای منطق خالص (ProfileValidator, ConfigRules, ShopCatalog, GoalCatalog, HookRunner، دوره‌ها با FakeTimeProvider) + Testcontainers برای storeهای Postgres.

### 4.11 نسخه‌های floating
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
  - جزئی از قدم‌های بعدی هم انجام شد: `IPhoneCodeStore` (از قدم ۳) و پروژه‌ی `tests/GameServer.Core.Tests` (از قدم ۲؛ هنوز `TimeProvider` و تست‌های shop/daily نیست).
- [x] **۱.۳. مستندات نصب سرور + forwarded headers + service hosting.**
  - انجام شد: بخش جدید `docs/install/` (index، Docker Compose، Linux binary با systemd، Windows binary با Windows Service، VPS با Caddy) و حذف `getting-started/docker.md` (داخل `install/docker-compose.md` ادغام شد)؛ `run-the-server.md` حالا صریحاً «اجرا برای توسعه» است. در کد: تنظیم opt-in به نام `GameServer:ForwardedHeaders:Enabled` (+ `KnownProxies`، env: `GameServer__ForwardedHeaders__Enabled`، در compose: `FORWARDED_HEADERS`، پیش‌فرض خاموش) که `UseForwardedHeaders` را اولین middleware می‌گذارد (قبلاً پشت reverse proxy همه‌ی بازیکن‌ها یک IP و یک سطل rate limit داشتند)؛ `restart: unless-stopped` برای هر دو سرویس compose؛ `UseWindowsService()` و `UseSystemd()` (پکیج‌های `Microsoft.Extensions.Hosting.WindowsServices/Systemd` با `10.*`).
  - بررسی شد: build بدون warning، تست‌های `GameServer.Core.Tests`، `dotnet publish` و اجرای `GameServer.Host.dll` با env varهای مستندات (فقط در اتصال به DB شکست می‌خورد)، `docker compose config`. بررسی نشد: اجرای واقعی روی Docker/Linux/systemd/Windows Service/Caddy و رفتار `UseForwardedHeaders` با proxy واقعی.
  - نکته: `GameServer:ModulesPath` نسبی نسبت به working directory حل می‌شود (نه محل برنامه)؛ در سرویس‌ها حتماً مسیر مطلق بدهید.
- [ ] **۲.** پروژه‌ی `tests/GameServer.Core.Tests` (xUnit) + تزریق `TimeProvider` به جای `DateTime.UtcNow` در 17 فایل. *تمام:* هیچ `DateTime.UtcNow` در `src/` نمانده؛ تست‌های shop/daily/progress با `FakeTimeProvider`.
- [ ] **۳.** انتقال SQL از سرویس‌ها به storeها (`IShopPurchaseStore`, `IProgressStore`, `IDailyRewardStore`, `IPhoneCodeStore`) و تکه کردن `AdminStore`. *تمام:* هیچ سرویسی `NpgsqlDataSource` نمی‌گیرد.
- [ ] **۴.** ساخت `GameServer.Infrastructure.Postgres` و انتقال `Postgres/` + `DatabaseMigrator` + storeهای جدید. *تمام:* `GameServer.Core.csproj` بدون Npgsql؛ حذف `InMemoryStorage.cs`.
- [ ] **۵.** حذف `IServiceProvider` از `HookRunner`/`MatchService`/`ProgressService`/`PostgresNotifications` + recordهای typed response (`ShopService.PurchasesOfAsync`, `MatchActions`, `LoginResultAsync`...). *تمام:* هیچ `IServiceProvider` در constructor سرویس‌ها و هیچ `object` به‌عنوان نوع برگشتی در endpointها نیست.
- [ ] **۶.** تکه کردن `IUserStore` (و interfaceهای کوچک `IPresence` برای `ChatService`/`SocialFacade`/`AccountCleanup`). *تمام:* هر مصرف‌کننده فقط interface لازمش را می‌گیرد.
- [ ] **۷.** Central Package Management و pin کردن نسخه‌ها. *تمام:* هیچ `10.*` در csprojها؛ `Directory.Packages.props` موجود.
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
