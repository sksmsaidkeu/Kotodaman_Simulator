#!/bin/sh
# 테마 토큰 가드. 프로젝트 루트에서 실행합니다.
#   sh ReleaseTools/check_theme.sh
#
# 1) Theme.cs 가 참조하는 키가 Kotodama.xaml 에 전부 있는가 (오타는 런타임에야 터짐)
# 2) Kotodama.xaml 의 토큰이 전부 Theme.cs 로 노출되는가 (죽은 토큰 방지)
# 3) 이관 완료 파일에 하드코딩 #RRGGBB 가 되살아나지 않았는가
# 4) BrushFromHex 사본이 되살아나지 않았는가 (단계 0.5 완료 후 활성화)

DICT=Themes/Kotodama.xaml
MIRROR=Themes/Theme.cs

# 이관이 끝난 파일만 여기 추가합니다. .xaml 과 .xaml.cs 를 반드시 함께 넣으세요.
# (코드비하인드에 색이 더 많습니다 — PRD_UI.md §1)
#
# 단계 3 완료: .xaml 8개 + .xaml.cs 8개 전부 이관됨. 앱 전체가 토큰을 지납니다.
# Themes/ 는 검사 대상이 아닙니다 — Kotodama.xaml 이 색의 출처이고,
# DataPalette.cs 는 테마가 아닌 게임 데이터 색(속성·확률 램프)이라 의도적으로 리터럴입니다.
MIGRATED="$(ls *.xaml *.xaml.cs 2>/dev/null | tr '
' ' ')"

fail=0

keys=$(grep -oE 'x:Key="[A-Za-z.]+"' "$DICT" | sed 's/x:Key="//;s/"//' | sort -u)
refs=$(grep -oE 'B\("[A-Za-z.]+"\)' "$MIRROR" | sed 's/B("//;s/")//' | sort -u)

for r in $refs; do
  echo "$keys" | grep -qx "$r" || { echo "FAIL  $MIRROR 가 없는 키를 참조: $r"; fail=1; }
done

for k in $keys; do
  case "$k" in Radius.*) continue;; esac
  echo "$refs" | grep -qx "$k" || { echo "WARN  쓰이지 않는 토큰: $k"; }
done

for f in $MIGRATED; do
  hits=$(grep -oE '#[0-9A-Fa-f]{6,8}' "$f" | sort -u | tr '\n' ' ')
  [ -n "$hits" ] && { echo "FAIL  $f 에 하드코딩 색이 남음: $hits"; fail=1; }
done

# 내장 브러시는 hex 가 아니라서 위 검사를 통과한다. 토큰 체계를 우회하므로 따로 막는다.
# (Brushes.White 가 크림 배경에서 안 보이는 사고가 실제로 있었다 - PRD_UI.md 단계 4)
builtin=$(grep -l 'Brushes\.' *.xaml.cs 2>/dev/null | tr '
' ' ')
[ -n "$builtin" ] && { echo "FAIL  내장 브러시(Brushes.*) 사용: $builtin"; fail=1; }
named=$(grep -lE '(Foreground|Background|BorderBrush|Fill|Stroke)="(White|Black|Gray|Red|Blue|Green|Yellow)"|Value="(White|Black|Gray|Red|Blue|Green|Yellow)"' *.xaml 2>/dev/null | tr '
' ' ')
[ -n "$named" ] && { echo "FAIL  내장 색 이름 사용: $named"; fail=1; }

if [ -n "$MIGRATED" ]; then
  dup=$(grep -l 'BrushFromHex(string' *.xaml.cs 2>/dev/null | tr '\n' ' ')
  [ -n "$dup" ] && { echo "FAIL  BrushFromHex 사본 부활: $dup"; fail=1; }
fi

if [ $fail -eq 0 ]; then
  echo "OK  토큰 $(echo "$keys" | grep -vc '^Radius\.')개, 참조 $(echo "$refs" | wc -w)개 일치"
fi
exit $fail
