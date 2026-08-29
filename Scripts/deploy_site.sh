#!/usr/bin/env bash
# download-site를 Vercel 프로덕션에 배포한다. 점검을 먼저 돌리고, 실패하면 배포하지 않는다.
# 사용법: bash deploy_site.sh
set -eu

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
TOKEN_FILE="$HOME/.vercel-token"

[ -s "$TOKEN_FILE" ] || {
  echo "토큰이 없다: $TOKEN_FILE" >&2
  echo "vercel.com/account/tokens 에서 발급 후: printf '%s' '<토큰>' > ~/.vercel-token" >&2
  exit 1
}

bash "$SCRIPT_DIR/check_deploy.sh" < /dev/null

cd "$ROOT/download-site"
vercel deploy --prod --yes --token="$(cat "$TOKEN_FILE")"
