"""캐릭터 이미지(RGBA PNG)를 팔레트 PNG로 압축합니다.

이미지별로 팔레트화 오차를 재서, 눈에 띄지 않을 때만 교체합니다.
임계값은 표본 300장 실측(최악 RGB 오차 3.80, 알파 오차 1.67)에 여유를
두고 잡았습니다 - 이 게임 아트(단순 셀셰이딩)에는 넉넉하지만, 그림체가
크게 다른 이미지가 섞여도 조용히 화질을 깎지 않도록 안전장치로 둡니다.

사용법: python ReleaseTools/quantize_character_images.py [--dry-run]
"""
import argparse
import io
import os
import sys

import numpy as np
from PIL import Image

DATA_DIR = os.path.join(os.path.dirname(__file__), "..", "Data", "CharacterImages")
RGB_ERROR_LIMIT = 4.0
ALPHA_ERROR_LIMIT = 3.0
PALETTE_COLORS = 255


def is_rgba_png(path: str) -> bool:
    with open(path, "rb") as f:
        head = f.read(26)
    return head[:8] == b"\x89PNG\r\n\x1a\n" and head[25] == 6


def quantize_if_safe(path: str) -> tuple[str, int, int]:
    original_size = os.path.getsize(path)
    original = Image.open(path).convert("RGBA")

    quantized = original.quantize(colors=PALETTE_COLORS, method=Image.Quantize.FASTOCTREE)
    roundtrip = quantized.convert("RGBA")

    diff = np.abs(
        np.asarray(original, dtype=np.int16) - np.asarray(roundtrip, dtype=np.int16)
    )
    rgb_error = diff[:, :, :3].mean()
    alpha_error = diff[:, :, 3].mean()

    if rgb_error > RGB_ERROR_LIMIT or alpha_error > ALPHA_ERROR_LIMIT:
        return "skipped", original_size, original_size

    buffer = io.BytesIO()
    quantized.save(buffer, "PNG", optimize=True)
    new_size = buffer.tell()

    if new_size >= original_size:
        return "skipped", original_size, original_size

    return "quantized", original_size, new_size, buffer.getvalue()


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--dry-run", action="store_true", help="파일을 바꾸지 않고 예상 절감량만 출력")
    args = parser.parse_args()

    files = sorted(f for f in os.listdir(DATA_DIR) if f.lower().endswith(".png"))
    rgba_files = [f for f in files if is_rgba_png(os.path.join(DATA_DIR, f))]

    print(f"대상: RGBA PNG {len(rgba_files)}장 / 전체 {len(files)}장")
    print(f"임계값: RGB 평균오차 <= {RGB_ERROR_LIMIT}, 알파 평균오차 <= {ALPHA_ERROR_LIMIT}\n")

    quantized_count = 0
    skipped_count = 0
    total_before = 0
    total_after = 0
    skipped_names: list[str] = []

    for index, name in enumerate(rgba_files, start=1):
        path = os.path.join(DATA_DIR, name)
        result = quantize_if_safe(path)
        status, before, after = result[0], result[1], result[2]
        total_before += before
        total_after += after

        if status == "quantized":
            quantized_count += 1
            if not args.dry_run:
                with open(path, "wb") as f:
                    f.write(result[3])
        else:
            skipped_count += 1
            skipped_names.append(name)

        if index % 300 == 0 or index == len(rgba_files):
            print(f"  {index}/{len(rgba_files)} 처리 · 팔레트화 {quantized_count} · 보류 {skipped_count}")

    print()
    print(f"팔레트화: {quantized_count}장")
    print(f"보류(임계값 초과 또는 이득 없음): {skipped_count}장")
    print(f"용량: {total_before / 1048576:.1f} MB -> {total_after / 1048576:.1f} MB "
          f"({(1 - total_after / total_before) * 100:.0f}% 절감)")
    if skipped_names:
        print("\n보류된 파일(원본 RGBA 유지):")
        for name in skipped_names:
            print(f"  {name}")
    if args.dry_run:
        print("\n--dry-run: 파일은 바뀌지 않았습니다.")


if __name__ == "__main__":
    main()
