from integrations.build_panel_sld_preview import build_preview, render_svg


def test_preview_keeps_unobserved_ways_blank_and_never_claims_mutation():
    evidence = {
        "result_sha256": "a" * 64,
        "source": {"source_drawing_sha256": "b" * 64},
        "schedule_panels": [
            {
                "panel": "DB3-GA2",
                "system": "400/230 V, 3 Phase, 4 Wire",
                "frequency_hz": None,
                "rated_ways": None,
                "ways": [
                    {"way": 1, "observed_in_lighting_drawing": False},
                    {
                        "way": 2,
                        "observed_in_lighting_drawing": True,
                        "description": "4 x lighting fixture",
                        "connected_w": 180.0,
                        "connected_va": 200.0,
                        "current_a": 0.87,
                        "status": "DRAFT-ESTIMATED-CANDIDATE",
                    },
                ],
            }
        ],
    }

    preview = build_preview(evidence, "DB3-GA2")

    assert preview["drawing_mutation_performed"] is False
    assert preview["way_count_shown"] == 2
    assert preview["observed_way_count"] == 1
    assert preview["blank_way_count"] == 1
    assert preview["geometry"][0]["description"] is None
    assert preview["geometry"][0]["status"] is None
    assert ">spare<" not in render_svg(preview).casefold()
    assert len(preview["preview_sha256"]) == 64
