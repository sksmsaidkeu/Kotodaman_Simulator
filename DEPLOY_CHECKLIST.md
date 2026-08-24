# 배포 전 점검 체크리스트

`download-site/`를 Vercel에 배포하기 전에 확인한다.
점검 대상은 **실제 업로드되는 파일**(git이 추적 중이거나 ignore되지 않은 `download-site/` 하위 파일)이다.

전부 `check_deploy.sh`로 자동 검사되며, Claude Code가 `vercel deploy`를 실행하기 직전 hook으로 돌아간다.
수동 실행: `bash check_deploy.sh`

## 1. 불필요한 컨벤션 위반 코드

- [ ] `console.log` / `console.debug` / `debugger` 등 디버그 잔재가 없다 (에러 로깅용 `console.error`는 허용)
- [ ] `TODO` / `FIXME` / `XXX` 주석이 남아 있지 않다
- [ ] `localhost` / `127.0.0.1` / `file://` 같은 로컬 전용 주소가 없다
- [ ] 모든 `.html`이 `<meta charset="utf-8">`를 선언한다 (한글 깨짐 방지)

## 2. 민감 정보 노출

- [ ] 토큰/키 패턴(`vcp_`, `ghp_`, `github_pat_`, `sk-`, `AKIA…`)이 배포 파일에 없다
- [ ] `download-site/`의 모든 `.env*` 파일이 git에서 ignore된다 (= 업로드되지 않는다)
- [ ] `download-site/.gitignore`가 `.vercel`과 `.env*`를 모두 덮는다
- [ ] 로컬 절대경로(`C:\Users\…`, `/Users/…`)나 개인 이메일이 노출되지 않는다
