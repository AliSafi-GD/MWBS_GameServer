# Account and config

## Account

```csharp
var me = await client.Account.GetAsync();                 // Id, Username, DisplayName, Avatar, Lang, Timezone, Identities...
await client.Account.UpdateAsync(new ProfileUpdate { Username = "ali_99", DisplayName = "Ali" });  // send only what changes
await client.Account.DeleteAsync();                       // deletes the account and everything stored for it

var profiles = await client.Account.GetUsersAsync(new[] { id1, id2 });   // public profiles of other players (max 100)
```

Rules: `username` is 3-20 characters of English letters, digits and `_` (lower-case, unique, some names reserved).
`displayName` is free text up to 32 characters. `avatar` is an id like `avatar_07` or an https URL.
Errors: `username_taken`, `invalid_username`.

## Remote config

```csharp
Dictionary<string, JToken> config = await client.Config.GetAsync();   // public keys only, cached with ETag
var price = config["my_key"];
```

## Version check (call it at startup)

```csharp
VersionCheck v = await client.Config.VersionCheckAsync(Application.version, "bazaar");
switch (v.Status) {
    case "update_required":  ShowForceUpdate(v.Url);  break;
    case "update_available": ShowSoftUpdate(v.Url);   break;
}
```

## Maintenance

While maintenance is on, every call except config and version-check throws `ApiException` with `Code == "maintenance"`.
Show `Message` and the end time (`until`). The Tank Arena Boot screen handles all of this; see `Boot/BootScreen.cs`
and `Core/Services/StartupCheck.cs`.

## A good boot order

1. Version check -> force or soft update popup
2. `Config.GetAsync()` -> maintenance screen if needed
3. Login (restore or guest)
4. Load wallet and inventory, then go to the menu
