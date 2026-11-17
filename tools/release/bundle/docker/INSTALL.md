# GameServer __VERSION__ with Docker (offline)

The images are in `images.tar.gz`: the server (`gameserver:__VERSION__`) and PostgreSQL (`__POSTGRES__`). Nothing is
downloaded; only Docker (with the compose plugin) must be installed.

```bash
./load-images.sh            # Linux (on Windows: .\load-images.ps1)
cp .env.example .env        # then set JWT_SECRET (32+ characters), DB_PASSWORD and ADMIN_KEY (24+ characters, or empty)
docker compose up -d
```

Put the game module in `modules/<Name>/` and restart the server container (`docker compose restart server`).
Check `http://<server>:7350/healthz`.

PostgreSQL is not reachable from outside the containers. To reach it from this machine (for a backup tool), add
`ports: ["127.0.0.1:5432:5432"]` to the `postgres` service.
