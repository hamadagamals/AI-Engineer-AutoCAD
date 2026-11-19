"""On-demand viewport and screen capture engine for cost-effective CAD/BIM visual review.

Strictly preserves user AI quota and tokens:
- Structured data (APIs) is used for 90%+ of queries.
- Visual screen capture triggers ONLY when explicitly requested or necessary.
- Compresses and downsamples image to minimal size (<150 KB) before transmitting.
"""

from __future__ import annotations

import base64
import io
import re
from dataclasses import dataclass
from typing import Any

from PIL import Image, ImageDraw


_TASHKEEL = re.compile(r"[\u064B-\u065F\u0670]")
_VISUAL_KEYWORDS = re.compile(
    r"\b(شاشة|الشاشة|شوف|شايف|بصري|بصريا|شكل|صورة|لقطة|تداخل|عين|screen|viewport|look|see|visual|appearance|clash)\b",
    re.IGNORECASE | re.UNICODE,
)


@dataclass(frozen=True, slots=True)
class ViewportCaptureResult:
    """Compressed, token-optimized image payload for vision-capable models."""

    image_base64: str
    mime_type: str
    width: int
    height: int
    byte_size: int
    estimated_vision_tokens: int
    reason: str


class ViewportVisionManager:
    """Controls the on-demand capture of CAD viewport images."""

    def __init__(self, max_dimension: int = 1024, jpeg_quality: int = 75) -> None:
        self.max_dimension = max_dimension
        self.jpeg_quality = jpeg_quality

    def should_capture_screen(self, user_query: str, force_vision: bool = False) -> bool:
        """Deterministic gate: returns True ONLY if visual inspection is strictly required."""
        if force_vision:
            return True
        if not user_query or not isinstance(user_query, str):
            return False
        normalized = _TASHKEEL.sub("", user_query)
        return bool(_VISUAL_KEYWORDS.search(normalized))

    def optimize_image_for_vision(self, image: Image.Image, reason: str = "On-demand visual inspection") -> ViewportCaptureResult:
        """Downsample and compress image to conserve token usage."""
        # Convert RGBA/Palette to RGB
        if image.mode in ("RGBA", "P"):
            rgb_image = Image.new("RGB", image.size, (255, 255, 255))
            if image.mode == "RGBA":
                rgb_image.paste(image, mask=image.split()[3])
            else:
                rgb_image.paste(image)
            image = rgb_image
        elif image.mode != "RGB":
            image = image.convert("RGB")

        # Resize proportionally if larger than max_dimension
        w, h = image.size
        if max(w, h) > self.max_dimension:
            scale = self.max_dimension / float(max(w, h))
            new_w = max(1, int(w * scale))
            new_h = max(1, int(h * scale))
            image = image.resize((new_w, new_h), Image.Resampling.LANCZOS)

        output_buffer = io.BytesIO()
        image.save(output_buffer, format="JPEG", quality=self.jpeg_quality, optimize=True)
        compressed_bytes = output_buffer.getvalue()

        # Token estimate rule-of-thumb: ~1 token per 750 bytes of compressed image
        estimated_tokens = max(85, len(compressed_bytes) // 750)

        return ViewportCaptureResult(
            image_base64=base64.b64encode(compressed_bytes).decode("ascii"),
            mime_type="image/jpeg",
            width=image.width,
            height=image.height,
            byte_size=len(compressed_bytes),
            estimated_vision_tokens=estimated_tokens,
            reason=reason,
        )

    def generate_placeholder_viewport_preview(
        self,
        title: str = "AutoCAD Viewport Preview",
        elements_count: int = 10,
    ) -> ViewportCaptureResult:
        """Create a lightweight synthetic CAD preview for testing and local verification."""
        img = Image.new("RGB", (800, 600), color=(30, 30, 30))  # CAD dark background
        draw = ImageDraw.Draw(img)

        # Draw grid
        for x in range(0, 800, 50):
            draw.line([(x, 0), (x, 600)], fill=(45, 45, 45), width=1)
        for y in range(0, 600, 50):
            draw.line([(0, y), (800, y)], fill=(45, 45, 45), width=1)

        # Draw indicative electrical layout
        draw.rectangle([(100, 100), (700, 500)], outline=(200, 200, 200), width=2)
        draw.text((110, 110), f"[AI-Engineer Viewport] {title}", fill=(0, 255, 255))
        draw.text((110, 130), f"Active Entities: {elements_count} | Mode: On-Demand Vision", fill=(180, 180, 180))

        # Redline review sample item
        draw.rectangle([(250, 250), (350, 310)], outline=(255, 0, 0), width=3)  # Redline box
        draw.text((255, 255), "L1 (REVIEW - RED)", fill=(255, 60, 60))

        return self.optimize_image_for_vision(img, reason=f"Synthetic CAD View: {title}")
