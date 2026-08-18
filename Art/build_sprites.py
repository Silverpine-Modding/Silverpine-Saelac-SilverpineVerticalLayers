from pathlib import Path

from PIL import Image


ROOT = Path(__file__).resolve().parent
SOURCE = ROOT / "Generated" / "stairs_master_alpha.png"
OUTPUT = ROOT.parent / "Assets"


def trim(image: Image.Image) -> Image.Image:
    alpha = image.getchannel("A")
    bounds = alpha.getbbox()
    if bounds is None:
        raise RuntimeError("The generated staircase has no opaque pixels.")
    return image.crop(bounds)


def fit_nearest(image: Image.Image, width: int, height: int) -> Image.Image:
    scale = min(width / image.width, height / image.height)
    resized = image.resize(
        (max(1, round(image.width * scale)), max(1, round(image.height * scale))),
        Image.Resampling.NEAREST,
    )
    canvas = Image.new("RGBA", (width, height), (0, 0, 0, 0))
    canvas.alpha_composite(
        resized,
        ((width - resized.width) // 2, (height - resized.height) // 2),
    )
    return canvas


def main() -> None:
    OUTPUT.mkdir(parents=True, exist_ok=True)
    master = trim(Image.open(SOURCE).convert("RGBA"))
    up = fit_nearest(master, 32, 48)
    rotations = {
        "up": up,
        "right": up.rotate(-90, expand=True),
        "down": up.rotate(180, expand=False),
        "left": up.rotate(90, expand=True),
    }
    for direction, sprite in rotations.items():
        path = OUTPUT / f"stairs_wood_{direction}.png"
        sprite.save(path)
        print(path)

    sheet = Image.new("RGBA", (96, 96), (0, 0, 0, 0))
    placements = {
        "up": (0, 0),
        "right": (40, 8),
        "down": (64, 0),
        "left": (24, 56),
    }
    for direction, position in placements.items():
        sheet.alpha_composite(rotations[direction], position)
    sheet.save(OUTPUT / "stairs_wood_sheet.png")


if __name__ == "__main__":
    main()
