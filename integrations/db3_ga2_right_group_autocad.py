"""Build the fixed AutoCAD overlay for the reviewed DB3-GA2 right group."""

from __future__ import annotations

import hashlib
import json
import re
from dataclasses import dataclass
from typing import Any


PROFILE = "AIENG_DB3_GA2_RIGHT_REVIEW_V1"
ROUTE_LAYER = "BLOCK003-G$0$E-Lite-Circ"
OVERLAY_LAYER = "AIENG-REVIEW-DB3-GA2-RIGHT"
NOTE_LAYER = "AIENG-REVIEW-NOTE"
EXPECTED_ROUTE_COUNT = 30
EXPECTED_BLOCK_COUNT = 28
EXPECTED_TAG_COUNT = 4
EXPECTED_ENTITY_COUNT = 65
_SHA256 = re.compile(r"[0-9a-f]{64}")


@dataclass(frozen=True, slots=True)
class RightGroupAutoCADPlan:
    script: str
    preview_sha256: str
    source_drawing_sha256: str
    entity_count: int
    route_count: int
    block_count: int


def _canonical_sha256(value: dict[str, Any]) -> str:
    return hashlib.sha256(
        json.dumps(value, ensure_ascii=False, sort_keys=True, separators=(",", ":")).encode("utf-8")
    ).hexdigest()


def build_right_group_autocad_plan(
    preview: dict[str, Any], *, expected_preview_sha256: str
) -> RightGroupAutoCADPlan:
    """Validate the exact approved preview and return one fixed overlay script."""
    if _SHA256.fullmatch(expected_preview_sha256) is None:
        raise ValueError("Expected right-group preview SHA-256 is invalid.")
    embedded = preview.get("preview_sha256")
    unsigned = dict(preview)
    unsigned.pop("preview_sha256", None)
    if embedded != expected_preview_sha256 or _canonical_sha256(unsigned) != embedded:
        raise ValueError("Right-group preview differs from the approved SHA-256.")
    if (
        preview.get("schema_version") != 1
        or preview.get("preview_type") != "db3-ga2-right-circuit-group-highlight"
        or preview.get("panel") != "DB3-GA2"
        or preview.get("selected_circuits") != [2, 4, 6, 7]
        or preview.get("absent_circuits") != [1, 3, 5]
        or preview.get("selected_route_count") != EXPECTED_ROUTE_COUNT
        or preview.get("selected_block_count") != EXPECTED_BLOCK_COUNT
        or preview.get("drawing_mutation_performed") is not False
    ):
        raise ValueError("Right-group preview does not match the reviewed profile.")
    source_sha = preview.get("source_drawing_sha256")
    if _SHA256.fullmatch(source_sha or "") is None:
        raise ValueError("Right-group source SHA-256 is invalid.")
    routes = preview.get("selected_route_handles")
    blocks = preview.get("selected_block_handles")
    tags = preview.get("selected_tag_handles")
    old_tag = preview.get("engineer_reclassification", {}).get("source_tag_handle")
    if (
        not isinstance(routes, list) or len(routes) != EXPECTED_ROUTE_COUNT
        or len(set(routes)) != EXPECTED_ROUTE_COUNT
        or not isinstance(blocks, list) or len(blocks) != EXPECTED_BLOCK_COUNT
        or len(set(blocks)) != EXPECTED_BLOCK_COUNT
        or not isinstance(tags, list) or len(tags) != EXPECTED_TAG_COUNT
        or len(set(tags)) != EXPECTED_TAG_COUNT
        or not isinstance(old_tag, str)
    ):
        raise ValueError("Right-group exact object selection is invalid.")
    for handle in [*routes, *blocks, *tags, old_tag]:
        if re.fullmatch(r"[0-9A-F]+", handle) is None:
            raise ValueError("Right-group AutoCAD handle is invalid.")

    validate_routes = "\n".join(
        f'(aieng-validate "{handle}" "ROUTE")' for handle in routes
    )
    validate_blocks = "\n".join(
        f'(aieng-validate "{handle}" "BLOCK")' for handle in blocks
    )
    validate_tags = "\n".join(
        f'(aieng-validate "{handle}" "TAG")' for handle in [*tags, old_tag]
    )
    copy_routes = "\n".join(f'(aieng-copy-route "{handle}")' for handle in routes)
    halo_blocks = "\n".join(f'(aieng-halo "{handle}" 180.0)' for handle in blocks)
    halo_tags = "\n".join(f'(aieng-halo "{handle}" 250.0)' for handle in tags)
    script = f'''(progn
(vl-load-com)
(setq aieng-valid-route-count 0 aieng-valid-block-count 0 aieng-valid-tag-count 0 aieng-created-count 0)
(defun aieng-validate (handle kind / entity data type layer)
  (setq entity (handent handle))
  (if entity
    (progn
      (setq data (entget entity) type (cdr (assoc 0 data)) layer (cdr (assoc 8 data)))
      (cond
        ((and (= kind "ROUTE") (member type '("LINE" "LWPOLYLINE" "POLYLINE")) (= layer "{ROUTE_LAYER}"))
         (setq aieng-valid-route-count (+ aieng-valid-route-count 1)))
        ((and (= kind "BLOCK") (= type "INSERT"))
         (setq aieng-valid-block-count (+ aieng-valid-block-count 1)))
        ((and (= kind "TAG") (member type '("TEXT" "MTEXT")))
         (setq aieng-valid-tag-count (+ aieng-valid-tag-count 1)))
      )
    )
  )
)
(defun aieng-copy-route (handle / entity data copy)
  (setq entity (handent handle))
  (setq data (entget entity))
  (setq data (vl-remove-if '(lambda (pair) (member (car pair) '(-1 5 330 360))) data))
  (setq data (subst (cons 8 "{OVERLAY_LAYER}") (assoc 8 data) data))
  (if (assoc 62 data)
    (setq data (subst (cons 62 256) (assoc 62 data) data))
    (setq data (append data (list (cons 62 256))))
  )
  (if (assoc 370 data)
    (setq data (subst (cons 370 -1) (assoc 370 data) data))
    (setq data (append data (list (cons 370 -1))))
  )
  (setq copy (entmakex data))
  (if copy (setq aieng-created-count (+ aieng-created-count 1)))
)
(defun aieng-halo (handle radius / point)
  (setq point (cdr (assoc 10 (entget (handent handle)))))
  (entmakex (list '(0 . "CIRCLE") (cons 8 "{OVERLAY_LAYER}") (cons 10 point) (cons 40 radius)))
  (setq aieng-created-count (+ aieng-created-count 1))
)
(setq aieng-before-selection (ssget "_X"))
(setq aieng-before-count (if aieng-before-selection (sslength aieng-before-selection) 0))
(setq aieng-before-selection nil)
(setq aieng-layer-preexisting 0)
(if (tblsearch "LAYER" "{OVERLAY_LAYER}") (setq aieng-layer-preexisting (+ aieng-layer-preexisting 1)))
(if (tblsearch "LAYER" "{NOTE_LAYER}") (setq aieng-layer-preexisting (+ aieng-layer-preexisting 1)))
{validate_routes}
{validate_blocks}
{validate_tags}
(if (and (= (getvar "INSUNITS") 4) (= (getvar "TILEMODE") 1) (= aieng-layer-preexisting 0)
         (= aieng-valid-route-count {EXPECTED_ROUTE_COUNT}) (= aieng-valid-block-count {EXPECTED_BLOCK_COUNT})
         (= aieng-valid-tag-count {EXPECTED_TAG_COUNT + 1}))
  (progn
    (entmake (list '(0 . "LAYER") '(100 . "AcDbSymbolTableRecord") '(100 . "AcDbLayerTableRecord")
      (cons 2 "{OVERLAY_LAYER}") (cons 70 0) (cons 62 4) (cons 6 "Continuous") (cons 370 50)))
    (entmake (list '(0 . "LAYER") '(100 . "AcDbSymbolTableRecord") '(100 . "AcDbLayerTableRecord")
      (cons 2 "{NOTE_LAYER}") (cons 70 0) (cons 62 30) (cons 6 "Continuous")))
{copy_routes}
{halo_blocks}
{halo_tags}
    (setq aieng-old-point (cdr (assoc 10 (entget (handent "{old_tag}")))))
    (entmakex (list '(0 . "LINE") (cons 8 "{NOTE_LAYER}")
      (cons 10 (list (- (car aieng-old-point) 200.0) (- (cadr aieng-old-point) 200.0) 0.0))
      (cons 11 (list (+ (car aieng-old-point) 200.0) (+ (cadr aieng-old-point) 200.0) 0.0))))
    (entmakex (list '(0 . "LINE") (cons 8 "{NOTE_LAYER}")
      (cons 10 (list (- (car aieng-old-point) 200.0) (+ (cadr aieng-old-point) 200.0) 0.0))
      (cons 11 (list (+ (car aieng-old-point) 200.0) (- (cadr aieng-old-point) 200.0) 0.0))))
    (entmakex (list '(0 . "TEXT") (cons 8 "{NOTE_LAYER}")
      (cons 10 (list (+ (car aieng-old-point) 300.0) (cadr aieng-old-point) 0.0))
      (cons 40 180.0) (cons 1 "REVIEW: SOURCE CCT 5 ASSIGNED TO CCT 7") (cons 7 "Standard")))
    (setq aieng-created-count (+ aieng-created-count 3))
  )
)
(setq aieng-created-selection (ssget "_X" (list (cons 8 "{OVERLAY_LAYER},{NOTE_LAYER}"))))
(setq aieng-layer-created-count (if aieng-created-selection (sslength aieng-created-selection) 0))
(setq aieng-created-selection nil)
(setq aieng-after-selection (ssget "_X"))
(setq aieng-after-count (if aieng-after-selection (sslength aieng-after-selection) 0))
(setq aieng-after-selection nil)
(setq aieng-content-valid (if (and (= aieng-created-count {EXPECTED_ENTITY_COUNT})
  (= aieng-layer-created-count {EXPECTED_ENTITY_COUNT})
  (= aieng-after-count (+ aieng-before-count {EXPECTED_ENTITY_COUNT}))) 1 0))
(if (= aieng-content-valid 1) (command-s "_.QSAVE"))
(princ (strcat "\\nAIENG_ACADVER=" (getvar "ACADVER")))
(princ (strcat "\\nAIENG_INSUNITS=" (itoa (getvar "INSUNITS"))))
(princ (strcat "\\nAIENG_TILEMODE=" (itoa (getvar "TILEMODE"))))
(princ "\\nAIENG_PROFILE={PROFILE}")
(princ "\\nAIENG_PREVIEWSHA256={embedded}")
(princ "\\nAIENG_SOURCESHA256={source_sha}")
(princ (strcat "\\nAIENG_LAYERPREEXISTING=" (itoa aieng-layer-preexisting)))
(princ (strcat "\\nAIENG_VALIDROUTECOUNT=" (itoa aieng-valid-route-count)))
(princ (strcat "\\nAIENG_VALIDBLOCKCOUNT=" (itoa aieng-valid-block-count)))
(princ (strcat "\\nAIENG_VALIDTAGCOUNT=" (itoa aieng-valid-tag-count)))
(princ "\\nAIENG_EXPECTEDCOUNT={EXPECTED_ENTITY_COUNT}")
(princ (strcat "\\nAIENG_CREATEDCOUNT=" (itoa aieng-created-count)))
(princ (strcat "\\nAIENG_LAYERCREATEDCOUNT=" (itoa aieng-layer-created-count)))
(princ (strcat "\\nAIENG_CONTENTVALID=" (itoa aieng-content-valid)))
(princ (strcat "\\nAIENG_BEFORECOUNT=" (itoa aieng-before-count)))
(princ (strcat "\\nAIENG_AFTERCOUNT=" (itoa aieng-after-count)))
(princ (strcat "\\nAIENG_DBMOD=" (itoa (getvar "DBMOD"))))
(princ "\\nAIENG_DONE=1\\n")
(princ)
)
'''
    return RightGroupAutoCADPlan(
        script, embedded, source_sha, EXPECTED_ENTITY_COUNT, EXPECTED_ROUTE_COUNT, EXPECTED_BLOCK_COUNT
    )
