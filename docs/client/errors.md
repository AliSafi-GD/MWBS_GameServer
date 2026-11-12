# Errors

All failures throw `ApiException`:

| Member | Meaning |
|---|---|
| `Code` | Stable string such as `insufficient_funds`. Use this |
| `Message` | Human text from the server. Do not parse it |
| `Status` | HTTP status, or 0 |
| `IsNetworkError` | True for connectivity problems (`Code == "network_error"`) |

```csharp
try {
    await client.Shop.BuyAsync("gems_small", 1, requestId);
}
catch (ApiException e) when (e.IsNetworkError) {
    ShowToast("No connection. Try again.");
}
catch (ApiException e) {
    ShowToast(Localize(e.Code));    // map codes to your own texts
}
```

## Rules

- Wrap every UI-triggered call in try/catch.
- Disable the button while the call is in flight (double taps cause duplicate requests).
- `client.Auth.SessionEnded` fires when the player has to log in again.
- HTTP 429 means rate limited: back off and retry later.
- `maintenance` and `app_version` errors need their own screens.

The full list of codes is in [Error codes](../reference/error-codes).
