#!/usr/bin/env bash
# Same as run-tests.ps1 for Linux/macOS/Git Bash:  ./run-tests.sh
# Writes logs/REPORT.txt - send me that file.
cd "$(dirname "$0")" || exit 1
URL="${1:-http://localhost:5000}"
TRANSPORTS="${REALTIME_TRANSPORTS:-websocket,tcp}"
MATCH_TRANSPORTS="${MATCH_TRANSPORTS:-websocket,udp}"
mkdir -p logs && rm -f logs/*
R=logs/REPORT.txt
{ echo "GameServer test report $(date -Is)"; echo "dotnet: $(dotnet --version)"; echo; } > "$R"

[ -f .env ] || { [ -f .env.example ] && cp .env.example .env; }
if command -v docker >/dev/null 2>&1; then
  docker compose up -d postgres > logs/docker.log 2>&1
  for i in $(seq 1 30); do docker compose exec -T postgres pg_isready -U game -d game >/dev/null 2>&1 && break; sleep 2; done
fi

dotnet build GameServer.slnx -c Debug > logs/build.log 2>&1
BUILD=$?
dotnet build tests/GameServer.SmokeTest -c Debug >> logs/build.log 2>&1 || BUILD=1
dotnet build sdk/unity/SdkSmokeTest -c Debug >> logs/build.log 2>&1 || BUILD=1
dotnet build tests/GameServer.ArenaBots -c Debug >> logs/build.log 2>&1 || BUILD=1
echo "dotnet build exit code: $BUILD" >> "$R"
grep -E "error (CS|MSB|NU|NETSDK)[0-9]+" logs/build.log | sort -u >> "$R"
if [ $BUILD -ne 0 ]; then echo "Build failed, tests were not run." >> "$R"; echo "BUILD FAILED - send $R"; exit 1; fi

dotnet publish samples/HelloGame.Modules -c Release -o modules/HelloGame.Modules > logs/module.log 2>&1

ASPNETCORE_ENVIRONMENT=Development ASPNETCORE_URLS="$URL" GameServer__Realtime__Transports="$TRANSPORTS" GameServer__Match__Transports="$MATCH_TRANSPORTS" \
  dotnet run --project src/GameServer.Host --no-build --no-launch-profile > logs/server.log 2>&1 &
SERVER=$!
UP=0
for i in $(seq 1 60); do
  sleep 1
  curl -fsS "$URL/healthz" >/dev/null 2>&1 && { UP=1; break; }
  kill -0 $SERVER 2>/dev/null || break
done
echo "server started: $UP" >> "$R"
TEST=1
if [ $UP -eq 1 ]; then
  (cd logs && dotnet run --project ../tests/GameServer.SmokeTest -c Debug -- "$URL" > smoke-console.log 2>&1)
  TEST=$?
  echo "smoke test exit code: $TEST" >> "$R"
  (cd logs && dotnet run --project ../sdk/unity/SdkSmokeTest -c Debug --no-build -- "$URL" > sdk-console.log 2>&1)
  SDK=$?
  echo "sdk test exit code: $SDK" >> "$R"
  [ $SDK -ne 0 ] && TEST=$SDK
  (cd logs && dotnet run --project ../tests/GameServer.ArenaBots -c Debug --no-build -- "$URL" "dev-admin-key-do-not-use-in-production-0123456789" 8 > arena-console.log 2>&1)
  ARENA=$?
  echo "arena bots exit code: $ARENA" >> "$R"
  [ $ARENA -ne 0 ] && TEST=$ARENA
fi
pkill -P $SERVER 2>/dev/null; kill $SERVER 2>/dev/null

{ echo; echo "==== SMOKE TEST LOG ===="; cat logs/smoke-test.log 2>/dev/null || tail -n 80 logs/smoke-console.log
  echo; echo "==== SDK TEST LOG ===="; cat logs/sdk-test.log 2>/dev/null || tail -n 80 logs/sdk-console.log
  echo; echo "==== ARENA BOTS LOG ===="; cat logs/arena-bots.log 2>/dev/null || tail -n 80 logs/arena-console.log
  echo; echo "==== SERVER LOG (last 400 lines) ===="; tail -n 400 logs/server.log
  echo; echo "==== BUILD WARNINGS ===="; grep -E "warning (CS|CA)[0-9]+" logs/build.log | sort -u | head -60; } >> "$R"
[ $TEST -eq 0 ] && echo "ALL TESTS PASSED" || echo "SOME TESTS FAILED"
echo "Report: $R (send me this file)"
exit $TEST
