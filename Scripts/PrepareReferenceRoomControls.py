import tempfile
from pathlib import Path

import cv2
import numpy as np
from PIL import Image


PROJECT_ROOT = Path(__file__).resolve().parents[1]
# 输入图片来自宿主剪贴板附件，使用操作系统临时目录以兼容不同用户与构建节点。
TEMP_ROOT = Path(tempfile.gettempdir())

ASSETS = {
    "T_Action_Pass_Reference.png": (
        TEMP_ROOT / "codex-clipboard-19531d6c-48ad-4787-815c-a5b5f445f77c.png",
        (768, 256),
        24,
    ),
    "T_Action_Peng_Reference.png": (
        TEMP_ROOT / "codex-clipboard-c4472740-6193-4ce9-ab58-c3945d06b18c.png",
        (768, 256),
        24,
    ),
    "T_Action_Gang_Reference.png": (
        TEMP_ROOT / "codex-clipboard-e8ccdee2-f189-4490-90ed-b43d2e5c9914.png",
        (768, 256),
        24,
    ),
    "T_Action_Hu_Reference.png": (
        TEMP_ROOT / "codex-clipboard-29d20902-67b0-48b2-b921-230f0f5e2ca5.png",
        (768, 256),
        24,
    ),
    "T_Action_Ting_Reference.png": (
        TEMP_ROOT / "codex-clipboard-b9663e27-873d-43ef-9b9d-dbb40b7ea56f.png",
        (512, 512),
        20,
    ),
    "T_Player_GoldBean_Reference.png": (
        TEMP_ROOT / "codex-clipboard-0ce4eba5-179f-4bf5-80b3-2e5946279be3.png",
        (512, 512),
        36,
    ),
}

OUTPUT_DIR = PROJECT_ROOT / "SourceArt" / "UI" / "Buttons" / "ReferenceHUD"


def largest_alpha_component_bbox(image: Image.Image) -> tuple[int, int, int, int]:
    alpha = np.asarray(image.getchannel("A"))
    mask = (alpha > 12).astype(np.uint8)
    count, _, stats, _ = cv2.connectedComponentsWithStats(mask, connectivity=8)
    if count <= 1:
        bbox = image.getchannel("A").getbbox()
        if bbox is None:
            raise RuntimeError("Image contains no visible pixels")
        return bbox

    component = 1 + int(np.argmax(stats[1:, cv2.CC_STAT_AREA]))
    x = int(stats[component, cv2.CC_STAT_LEFT])
    y = int(stats[component, cv2.CC_STAT_TOP])
    width = int(stats[component, cv2.CC_STAT_WIDTH])
    height = int(stats[component, cv2.CC_STAT_HEIGHT])
    return x, y, x + width, y + height


def normalize_asset(source: Path, destination: Path, canvas_size: tuple[int, int], padding: int) -> None:
    if not source.is_file():
        raise FileNotFoundError(source)

    image = Image.open(source).convert("RGBA")
    left, top, right, bottom = largest_alpha_component_bbox(image)
    source_padding = 10
    left = max(0, left - source_padding)
    top = max(0, top - source_padding)
    right = min(image.width, right + source_padding)
    bottom = min(image.height, bottom + source_padding)
    cropped = image.crop((left, top, right, bottom))

    available_width = canvas_size[0] - padding * 2
    available_height = canvas_size[1] - padding * 2
    scale = min(available_width / cropped.width, available_height / cropped.height)
    resized_size = (
        max(1, round(cropped.width * scale)),
        max(1, round(cropped.height * scale)),
    )
    cropped = cropped.resize(resized_size, Image.Resampling.LANCZOS)

    canvas = Image.new("RGBA", canvas_size, (0, 0, 0, 0))
    offset = (
        (canvas_size[0] - resized_size[0]) // 2,
        (canvas_size[1] - resized_size[1]) // 2,
    )
    canvas.alpha_composite(cropped, offset)
    canvas.save(destination, optimize=True)
    print(
        f"PREPARED {destination.name}: source_bbox=({left},{top},{right},{bottom}) "
        f"output={canvas_size[0]}x{canvas_size[1]} content={resized_size[0]}x{resized_size[1]}"
    )


def main() -> None:
    OUTPUT_DIR.mkdir(parents=True, exist_ok=True)
    for filename, (source, canvas_size, padding) in ASSETS.items():
        normalize_asset(source, OUTPUT_DIR / filename, canvas_size, padding)


if __name__ == "__main__":
    main()
