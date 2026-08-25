# -*- coding: utf-8 -*-
"""Kotodama.xaml 의 토큰 값을 통째로 갈아끼운다.

    python ReleaseTools/set_palette.py dark     # 리디자인 이전 팔레트
    python ReleaseTools/set_palette.py cream    # 게임 UI 기반 크림 팔레트

토큰 '이름'은 그대로 두고 '값'만 바꾸므로, 앱 코드는 어느 쪽이든 손댈 필요가 없다.
PRD_UI.md 가 말한 '되돌리기는 파일 하나'가 실제로 성립하게 하는 장치다.

크림 값의 출처는 게임 스크린샷 픽셀 샘플링(PRD_UI.md §3)이고,
바꾼 뒤에는 반드시 python ReleaseTools/check_contrast.py 를 돌릴 것.
"""
import io, re, sys

DICT = "Themes/Kotodama.xaml"

DARK = {
    "Bg.Backdrop":"#10131A", "Bg.Panel":"#171C26", "Bg.PanelInner":"#222A37",
    "Bg.Field":"#1D2430", "Bg.Ivory":"#2A3342",
    "Line.Tan":"#2A3342", "Line.TanStrong":"#46546A", "Line.Soft":"#526176",
    "Text.Primary":"#F4F7FB", "Text.Secondary":"#AEB8C8", "Text.Tertiary":"#7E8B98",
    "Text.OnAccent":"#FFFFFF",
    "Accent.Orange":"#245E88", "Accent.OrangeDeep":"#1D4051",
    "Accent.Pink":"#4B8BFF", "Accent.PinkSoft":"#46546A", "Accent.PinkLine":"#4D9AF0",
    "Accent.PinkPale":"#222A37",
    "Accent.BlueFace":"#243240", "Accent.BlueLine":"#3E5A70", "Accent.BlueText":"#B8EAF5",
    "Accent.Focus":"#66D9EF",
    "Chip.Dark":"#222A37", "Level.Navy":"#1D2430", "Level.Number":"#8FE3B1",
    "Status.Success":"#8FE3B1", "Status.Info":"#B8EAF5", "Status.Warn":"#FFD08A",
    "Status.Error":"#FF9E9E", "Status.Special":"#D9C2FF",
    "Gold.Face":"#493D24", "Gold.Line":"#C9A44C", "Gold.Text":"#E0C978",
    "Rose.Face":"#4A2932", "Rose.Line":"#75404D", "Rose.Text":"#FFD8DF",
    "Special.Face":"#392E52", "Special.Line":"#9A7BD4",
    "Success.Face":"#203B35", "Success.Line":"#4E9B82",
    "Info.Face":"#1E3040", "Info.Line":"#3B7892",
    "Overlay.Scrim":"#D70B1118", "Overlay.Special":"#D7513E67", "Overlay.Gold":"#E6A66B16",
}

CREAM = {
    # 면 — 게임 모달/패널 샘플링
    "Bg.Backdrop":"#E4DED1", "Bg.Panel":"#EFEBE0", "Bg.PanelInner":"#DFDACF",
    "Bg.Field":"#FFFFFF", "Bg.Ivory":"#F5F3EC",
    # 선 — 다크에서의 대비 서열(약<강<구분선)을 뒤집어 유지.
    # Line.Tan 은 게임 원본(#CEC7B5, 대비 1.41)보다 살짝 진하다. 게임은 그림자로
    # 경계를 만들지만 앱은 장시간 보는 화면이라 테두리 자체가 보여야 한다.
    "Line.Tan":"#C9C0AB", "Line.TanStrong":"#C0B294", "Line.Soft":"#B5AB93",
    "Text.Primary":"#4A3B28", "Text.Secondary":"#6F614B", "Text.Tertiary":"#8A7C66",
    "Text.OnAccent":"#FFFFFF",
    # 선택 = 게임 주황 pill. 게임은 흰 글자를 쓰지만 대비 2.1이라 어두운 갈색 글자를 쓴다.
    "Accent.Orange":"#FFA24E", "Accent.OrangeDeep":"#F08C1E",
    # 주 액션 = 게임 핑크. 흰 글자가 읽히도록 게임 원색(#F0609E)보다 진하다.
    "Accent.Pink":"#C2306B", "Accent.PinkSoft":"#FFB0CE", "Accent.PinkLine":"#FFB0CE",
    "Accent.PinkPale":"#FCEDF5",
    "Accent.BlueFace":"#E3F1FA", "Accent.BlueLine":"#AEDAFF", "Accent.BlueText":"#1E4C9A",
    # 포커스 = 현재 시안(#66D9EF)의 크림쪽 자손. 선택/경고/주액션과 색상이 겹치지 않는다.
    "Accent.Focus":"#2E7D91",
    # LV 알약은 게임에서도 다크라 그대로 둔다.
    "Chip.Dark":"#DFD8C8", "Level.Navy":"#2B3A4C", "Level.Number":"#7BE39A",
    "Status.Success":"#17663E", "Status.Info":"#1E5F8A", "Status.Warn":"#8A5A00",
    "Status.Error":"#B32218", "Status.Special":"#5B3FA8",
    # 골드 선은 결정에 따라 #C9A44C 유지, 글자만 딥앰버로 반전.
    "Gold.Face":"#FBF0DC", "Gold.Line":"#C9A44C", "Gold.Text":"#8A5A00",
    "Rose.Face":"#FCEDF5", "Rose.Line":"#FFB0CE", "Rose.Text":"#B03A6E",
    "Special.Face":"#F0E9FA", "Special.Line":"#B49AE0",
    "Success.Face":"#E4F3EC", "Success.Line":"#6FB894",
    "Info.Face":"#E3F1FA",  "Info.Line":"#8FC4E8",
    "Overlay.Scrim":"#D70B1118", "Overlay.Special":"#D7513E67", "Overlay.Gold":"#E6A66B16",
}

PALETTES = {"dark": DARK, "cream": CREAM}


def main(argv):
    if len(argv) != 2 or argv[1] not in PALETTES:
        print(__doc__)
        return 2
    palette = PALETTES[argv[1]]
    text = io.open(DICT, encoding="utf-8").read()

    keys = set(re.findall(r'x:Key="([A-Za-z.]+)"\s+Color=', text))
    missing, extra = keys - set(palette), set(palette) - keys
    if missing or extra:
        print("token mismatch. dict-only: %s / palette-only: %s"
              % (sorted(missing), sorted(extra)))
        return 1

    def swap(m):
        return 'x:Key="%s"%sColor="%s"' % (m.group(1), m.group(2), palette[m.group(1)])

    text, n = re.subn(r'x:Key="([A-Za-z.]+)"(\s+)Color="#[0-9A-Fa-f]{6,8}"', swap, text)
    io.open(DICT, "w", encoding="utf-8", newline="\n").write(text)
    print("%s palette applied: %d tokens" % (argv[1], n))
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
