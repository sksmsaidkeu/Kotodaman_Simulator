# -*- coding: utf-8 -*-
"""Kotodama.xaml 의 글자/면 조합이 읽히는지 검사한다.

크림 전환에서 가장 흔한 실패는 '옅은 면 위 옅은 글자'다. 다크 팔레트에서 밝은
파스텔이던 글자색을 그대로 두면 크림 배경에서 대비 1.3 수준이 되어 안 보인다.
빌드는 이걸 못 잡으므로 여기서 잡는다.

기준(WCAG 2.1): 본문 4.5, 큰 글자/UI 요소 3.0, 테두리는 면과 1.5 이상이면 보인다.
프로젝트 루트에서:  python ReleaseTools/check_contrast.py
"""
import io, re, sys

DICT = "Themes/Kotodama.xaml"

# (글자, 면, 최소비, 설명)
PAIRS = [
    ("Text.Primary",   "Bg.Panel",       4.5, "본문 · 패널"),
    ("Text.Primary",   "Bg.PanelInner",  4.5, "본문 · 안쪽 패널"),
    ("Text.Primary",   "Bg.Field",       4.5, "본문 · 입력 필드"),
    ("Text.Primary",   "Bg.Ivory",       4.5, "버튼 글자"),
    ("Text.Primary",   "Bg.Backdrop",    4.5, "본문 · 창 바탕"),
    ("Text.Primary",   "Chip.Dark",      4.5, "칩 글자"),
    ("Text.Primary",   "Accent.Orange",  4.5, "선택된 탭 글자"),
    ("Text.Secondary", "Bg.Panel",       4.5, "보조 텍스트"),
    ("Text.Secondary", "Bg.Field",       4.5, "보조 텍스트 · 필드"),
    ("Text.Tertiary",  "Bg.Panel",       3.0, "비활성 텍스트"),
    ("Text.OnAccent",  "Accent.Pink",    4.5, "주 액션 버튼 글자"),
    ("Accent.BlueText","Accent.BlueFace",4.5, "부수 액션 버튼 글자"),
    ("Status.Success", "Bg.Panel",       4.5, "성공 텍스트"),
    ("Status.Info",    "Bg.Panel",       4.5, "안내 텍스트"),
    ("Status.Warn",    "Bg.Panel",       4.5, "주의 텍스트"),
    ("Status.Error",   "Bg.Panel",       4.5, "오류 텍스트"),
    ("Status.Special", "Bg.Panel",       4.5, "특수 텍스트"),
    ("Alert.Text",      "Alert.Face",      4.5, "⚠ 경고 배너"),
    ("Rose.Text",      "Rose.Face",      4.5, "× 제거 버튼"),
    ("Status.Special", "Special.Face",   4.5, "✨ 미라클 리더 배너"),
    ("Status.Success", "Success.Face",   4.5, "◆ 덱 그룹 배너"),
    ("Status.Info",    "Info.Face",      4.5, "안내 배너"),
    ("Level.Number",   "Level.Navy",     4.5, "LV 숫자"),
    ("Text.Primary",   "Rose.Face",      4.5, "글자판 특수키"),
    ("Text.Primary",   "Success.Face",   4.5, "밝은 면 위 본문"),
    ("Text.Primary",   "Info.Face",      4.5, "밝은 면 위 본문"),
    ("Text.Primary",   "Alert.Face",      4.5, "밝은 면 위 본문"),
    # 테두리는 면과 구분만 되면 된다
    ("Line.Tan",       "Bg.Panel",       1.5, "기본 테두리"),
    ("Line.TanStrong", "Bg.Panel",       1.5, "강조 테두리"),
    ("Line.Soft",      "Bg.Panel",       1.5, "구분선"),
    ("Alert.Line",      "Alert.Face",      1.5, "경고 배너 테두리"),
    ("Rose.Line",      "Rose.Face",      1.5, "제거 버튼 테두리"),
    ("Special.Line",   "Special.Face",   1.5, "미라클 배너 테두리"),
    ("Success.Line",   "Success.Face",   1.5, "덱 그룹 배너 테두리"),
    ("Info.Line",      "Info.Face",      1.5, "안내 배너 테두리"),
    ("Accent.Focus",   "Bg.Panel",       3.0, "포커스 링"),
    # P6 상태 칩. 면(Chip.Dark)이 창 바탕과 1.06 이라 알약 모양은 테두리가 세운다.
    # 이 줄이 무너지면 칩이 배경에 녹아 사라진다.
    ("Line.TanStrong", "Bg.Backdrop",    1.5, "상태 칩 테두리 · 창 바탕"),
    ("Status.Info",    "Info.Face",      4.5, "상태 칩 · 업데이트 진행"),
    ("Alert.Text",     "Alert.Face",     4.5, "상태 칩 · 업데이트 실패"),
]


def lum(hex_color):
    def channel(v):
        v /= 255.0
        return v / 12.92 if v <= 0.03928 else ((v + 0.055) / 1.055) ** 2.4
    r, g, b = (int(hex_color[i:i + 2], 16) for i in (1, 3, 5))
    return 0.2126 * channel(r) + 0.7152 * channel(g) + 0.0722 * channel(b)


def ratio(a, b):
    la, lb = lum(a), lum(b)
    hi, lo = max(la, lb), min(la, lb)
    return (hi + 0.05) / (lo + 0.05)


def main():
    text = io.open(DICT, encoding="utf-8").read()
    tok = dict(re.findall(r'x:Key="([A-Za-z.]+)"\s+Color="(#[0-9A-Fa-f]{6})"', text))

    failures = []
    for fg, bg, need, label in PAIRS:
        if fg not in tok or bg not in tok:
            failures.append("  누락 토큰: %s 또는 %s" % (fg, bg))
            continue
        got = ratio(tok[fg], tok[bg])
        if got < need:
            failures.append("  %-28s %s on %s = %.2f (필요 %.1f)"
                            % (label, tok[fg], tok[bg], got, need))

    if failures:
        print("대비 미달 %d건:" % len(failures))
        print("\n".join(failures))
        return 1
    print("OK  대비 검사 %d쌍 통과" % len(PAIRS))
    return 0


if __name__ == "__main__":
    sys.exit(main())
