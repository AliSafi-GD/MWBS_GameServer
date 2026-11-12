# Your first request

With the server running on `http://localhost:5000`:

## 1. Log in as a guest

```bash
curl -X POST http://localhost:5000/v1/auth/device \
  -H "Content-Type: application/json" -d '{"deviceId":"my-test-device-0001"}'
```

The first call creates the account. The answer contains `token` (a JWT valid for 60 minutes) and `refreshToken`.

```bash
TOKEN="<paste token here>"
```

## 2. Read and change the profile

```bash
curl http://localhost:5000/v1/account -H "Authorization: Bearer $TOKEN"

curl -X PUT http://localhost:5000/v1/account -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" -d '{"username":"ali_99","displayName":"Ali"}'
```

Rules: `username` is 3-20 characters of English letters, digits and `_` (stored lower-case, unique).
`displayName` is free text up to 32 characters in any language.

## 3. Call a module RPC

The sample module registers `hello`, `claim_daily_reward`, `buy_card` and `submit_score`.

```bash
curl -X POST http://localhost:5000/v1/rpc/hello -H "Authorization: Bearer $TOKEN"
curl -X POST http://localhost:5000/v1/rpc/claim_daily_reward -H "Authorization: Bearer $TOKEN"
```

## Errors

Every error looks the same:

```json
{ "error": "username_taken", "message": "That username is already used." }
```

Use `error` in code. The `message` is for humans. See [Error codes](../reference/error-codes).

## Other ways to try the API

- **Web playground**: <http://localhost:5000/playground/>, a guided tutorial with a live Send button.
- **Postman**: import `postman/GameServer.postman_collection.json` and `GameServer.local.postman_environment.json`.
- **Rider / VS Code**: open `postman/GameServer.http` and pick the `dev` environment.

## Next

[Your first Unity game](./first-unity-game)
