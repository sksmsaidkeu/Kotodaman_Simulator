#!/usr/bin/env bash
# Docs/DEPLOY_CHECKLIST.md의 항목을 자동 검사한다.
# 단독 실행: bash Scripts/check_deploy.sh
# hook 실행: PreToolUse(Bash) — stdin으로 도구 입력 JSON을 받고, vercel deploy가 아니면 통과.
# 실패 시 exit 2 (PreToolUse에서 도구 실행을 막는 코드).
set -u

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT" || exit 0

# hook 호출이면 stdin으로 도구 입력 JSON이 온다. vercel 배포 명령일 때만 검사하고,
# 그 외 Bash 명령은 즉시 통과시킨다. read -t로 받아 stdin이 안 닫히는 환경에서도 멈추지 않는다.
payload=""
IFS= read -r -d '' -t 3 payload
case "$payload" in
  "") ;;                              # 수동 실행
  *vercel*deploy*|*vercel*--prod*) ;;
  *) exit 0 ;;
esac

# 실제 업로드되는 파일 = 추적 중이거나 ignore되지 않은 download-site 하위 파일
files="$(git ls-files --cached --others --exclude-standard -- download-site)"
[ -n "$files" ] || { echo "check_deploy: download-site에 배포할 파일이 없다" >&2; exit 2; }

failed=0
fail() { failed=1; printf '  x %s\n' "$1" >&2; }

# 배포 파일 전체에서 정규식을 찾아, 걸리면 실패로 기록한다.
scan() { # scan <regex> <설명>
  local re="$1" label="$2" hits="" m
  while IFS= read -r f; do
    [ -f "$f" ] || continue
    m="$(grep -nEH "$re" "$f")" && hits="${hits}${m}"$'\n'
  done <<< "$files"
  if [ -n "$hits" ]; then
    fail "$label"
    printf '%s' "$hits" | sed 's/^/      /' >&2
  fi
}

echo "== 1. 컨벤션 ==" >&2
scan 'console\.(log|debug)|(^|[^.[:alnum:]])debugger([^[:alnum:]]|$)' "디버그 잔재(console.log/debug, debugger)"
scan 'TODO|FIXME|XXX' "미완료 주석(TODO/FIXME/XXX)"
scan 'localhost|127\.0\.0\.1|file://' "로컬 전용 주소"

while IFS= read -r f; do
  case "$f" in *.html)
    grep -qiE 'charset=["'"'"']?utf-8' "$f" || fail "charset=utf-8 선언 없음: $f" ;;
  esac
done <<< "$files"

echo "== 2. 민감 정보 ==" >&2
scan 'vcp_[A-Za-z0-9]{20,}|ghp_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,}|sk-[A-Za-z0-9]{20,}|AKIA[0-9A-Z]{16}' "토큰/키 패턴 노출"
scan 'C:[\/]+Users|/Users/[A-Za-z0-9._-]+/|[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}' "로컬 절대경로 또는 이메일 노출"

# .env*는 존재해도 되지만 반드시 ignore되어 업로드되지 않아야 한다.
while IFS= read -r e; do
  [ -n "$e" ] || continue
  git check-ignore -q "$e" || fail "ignore되지 않은 환경파일이 업로드된다: $e"
done <<< "$(find download-site -name '.env*' -type f 2>/dev/null)"

gi="download-site/.gitignore"
[ -f "$gi" ] || fail "$gi 없음"
if [ -f "$gi" ]; then
  grep -q '^\.vercel' "$gi" || fail "$gi에 .vercel 없음"
  grep -q '^\.env'    "$gi" || fail "$gi에 .env* 없음"
fi

if [ "$failed" -ne 0 ]; then
  echo "" >&2
  echo "DEPLOY_CHECKLIST.md 점검 실패 — 배포를 중단한다. 위 항목을 고치고 다시 시도할 것." >&2
  exit 2
fi
echo "DEPLOY_CHECKLIST.md 점검 통과" >&2
exit 0
