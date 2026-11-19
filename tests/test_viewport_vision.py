"""Tests for on-demand token-conserving viewport vision engine."""

from __future__ import annotations

import pytest
from PIL import Image

from integrations.viewport_vision import ViewportVisionManager


def test_should_capture_screen_gate() -> None:
    mgr = ViewportVisionManager()

    # Visual requests should trigger
    assert mgr.should_capture_screen("شوف الرسمة على الشاشة") is True
    assert mgr.should_capture_screen("هل فيه تداخل بصري بالعين؟") is True
    assert mgr.should_capture_screen("take a look at the viewport") is True
    assert mgr.should_capture_screen("راجع الشكل بصرياً") is True

    # Pure calculation / text / data requests should NOT trigger
    assert mgr.should_capture_screen("احسب هبوط الجهد للدائرة L1") is False
    assert mgr.should_capture_screen("استخرج جدول الكميات BOQ") is False
    assert mgr.should_capture_screen("ما هو مقاس الكابل المناسب لقاطع 100A؟") is False

    # Force flag
    assert mgr.should_capture_screen("أي سؤال عادي", force_vision=True) is True


def test_optimize_image_for_vision() -> None:
    mgr = ViewportVisionManager(max_dimension=800, jpeg_quality=70)

    # Large image
    large_img = Image.new("RGB", (2000, 1500), color=(50, 50, 50))
    result = mgr.optimize_image_for_vision(large_img, reason="Test oversized view")

    # Downsampled to max dimension
    assert result.width == 800
    assert result.height == 600
    assert result.mime_type == "image/jpeg"
    assert len(result.image_base64) > 0
    # Compressed bytes should be well below 150 KB
    assert result.byte_size < 150_000
    assert result.estimated_vision_tokens > 0


def test_synthetic_viewport_preview() -> None:
    mgr = ViewportVisionManager()
    result = mgr.generate_placeholder_viewport_preview("Test Panel GA", 15)

    assert result.width <= 1024
    assert result.height <= 1024
    assert result.byte_size > 0
    assert "image_base64" in result.__dataclass_fields__

