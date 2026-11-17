# Game config files

Put one `<key>.json` file per config key here, for example `shop.json`, `quests.json`, `matches.json`. They are copied next to
the module DLL on `dotnet publish`, and the server writes them into its database at start (mode `seed`: only keys that are
missing). Format of every file:

```json
{ "public": false, "value": { ... } }
```

What each key contains: the server docs, "Game config files". A broken file stops the server at start with the file name
and the reason, so mistakes do not reach players.
