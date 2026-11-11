# Postman

1. Postman -> Import -> select both files in this folder.
2. (Optional) pick the environment "GameServer local". Collection variables already default to
   `http://localhost:5000` and the development admin key. For Docker use `http://localhost:7350`.
3. Start the server, then run **1 Auth > Guest login (device)**. It saves `token` and `userId`; every other request uses them.
4. Run the folders top to bottom, or the whole collection with the Runner. Each request has tests (green/red).

Notes
- Phone login: run "Phone: begin", copy the code from the server log line `DEV SMS to ...` into the `otp` variable, then "Phone: login".
- Google Play needs a real `serverAuthCode` from a device, so it is only for manual checks later.
- In the Runner, "Delete my account" and "Delete user" remove the account; run "Guest login" again afterwards.

## Rider / VS Code (no Postman needed)
`GameServer.http` + `http-client.env.json` work in Rider's built-in HTTP client. Open `GameServer.http`, pick the
`dev` environment at the top of the file, and run "1.1 Guest login" first (it saves `token` and `userId`).
Rider does not understand Postman variables, so use these files there instead of the Postman collection.
