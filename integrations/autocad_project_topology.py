"""Exact-context reviewed AutoCAD circuit-membership sidecars."""

from __future__ import annotations

import hashlib
import json
import math
import re
from dataclasses import asdict, dataclass, replace
from datetime import date, datetime
from pathlib import Path
from typing import Any, Sequence

from agent.exceptions import AutoCADContextError
from core.electrical_model import Circuit, ElectricalProjectModel


TOPOLOGY_VERSION = "autocad.reviewed_topology.v1"
CONNECTION_ANCHORS_VERSION = "autocad.reviewed_connection_anchors.v1"
_IDENTIFIER = re.compile(r"[a-z][a-z0-9_.:-]{0,127}")
_CONTEXT_ID = re.compile(r"[0-9a-f]{32}")
_DOCUMENT_ID = re.compile(
    r"[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}"
)
_SHA256 = re.compile(r"[0-9a-f]{64}")
_HANDLE = re.compile(r"[0-9A-F]{1,32}")
_MAX_BYTES = 65_536
ANCHOR_CAPTURE_VERSION = "autocad.connection_anchor_capture.v1"


@dataclass(frozen=True, slots=True)
class AnchorCaptureDocument:
    document_id: str
    identity_source: str
    name: str
    drawing_units: int
    layout: str
    database_modified_before: int
    database_modified_after: int

    def __post_init__(self) -> None:
        if _DOCUMENT_ID.fullmatch(self.document_id) is None:
            raise AutoCADContextError("Anchor-capture document ID is invalid.")
        if self.identity_source not in {"fingerprint_guid", "path_sha256"}:
            raise AutoCADContextError("Anchor-capture identity source is invalid.")
        _text(self.name, "drawing name", 512)
        _text(self.layout, "layout", 512)
        if not isinstance(self.drawing_units, int) or isinstance(self.drawing_units, bool) or not 0 <= self.drawing_units <= 24:
            raise AutoCADContextError("Anchor-capture drawing units are invalid.")
        for value in (self.database_modified_before, self.database_modified_after):
            if not isinstance(value, int) or isinstance(value, bool) or not 0 <= value <= 63:
                raise AutoCADContextError("Anchor-capture DBMOD is invalid.")
        if self.database_modified_before & ~(8 | 16) or self.database_modified_after & ~(8 | 16):
            raise AutoCADContextError("Anchor capture contains unsupported DBMOD bits.")


@dataclass(frozen=True, slots=True)
class AnchorCaptureSelection:
    device_object_id: str
    block_name: str
    route_object_id: str
    route_entity_type: str
    endpoint: str
    world_position: tuple[float, float, float]
    endpoint_pick_position: tuple[float, float, float]
    endpoint_pick_distance: float
    alternate_endpoint_distance: float
    endpoint_selection_method: str
    terminal_name: str

    def __post_init__(self) -> None:
        if _HANDLE.fullmatch(self.device_object_id) is None or _HANDLE.fullmatch(self.route_object_id) is None:
            raise AutoCADContextError("Anchor-capture object handle is invalid.")
        _text(self.block_name, "block name", 512)
        if self.route_entity_type not in {"LINE", "LWPOLYLINE"}:
            raise AutoCADContextError("Anchor-capture route type is unsupported.")
        if self.endpoint not in {"start", "end"}:
            raise AutoCADContextError("Anchor-capture endpoint must be start or end.")
        _point(self.world_position, "anchor-capture world position")
        _point(self.endpoint_pick_position, "anchor-capture endpoint pick position")
        for value in (self.endpoint_pick_distance, self.alternate_endpoint_distance):
            if not isinstance(value, (int, float)) or isinstance(value, bool) or not math.isfinite(value) or value < 0 or value > 1_000_000_000:
                raise AutoCADContextError("Anchor-capture endpoint distance is invalid.")
        if self.endpoint_pick_distance >= self.alternate_endpoint_distance:
            raise AutoCADContextError("Anchor-capture pick must be strictly closer to the selected endpoint.")
        if self.endpoint_selection_method not in {"engineer_pick_nearest_endpoint", "engineer_confirmed_review_request"}:
            raise AutoCADContextError("Anchor-capture endpoint selection method is unsupported.")
        _text(self.terminal_name, "terminal name", 64)


@dataclass(frozen=True, slots=True)
class ConnectionAnchorCapture:
    schema_version: int
    capture_version: str
    capture_id: str
    captured_at: str
    document: AnchorCaptureDocument
    selection: AnchorCaptureSelection
    drawing_mutation_performed: bool

    def __post_init__(self) -> None:
        if self.schema_version != 1 or self.capture_version != ANCHOR_CAPTURE_VERSION:
            raise AutoCADContextError("Connection-anchor capture version is unsupported.")
        if _CONTEXT_ID.fullmatch(self.capture_id) is None:
            raise AutoCADContextError("Connection-anchor capture ID is invalid.")
        try:
            parsed = datetime.fromisoformat(self.captured_at.replace("Z", "+00:00"))
        except (TypeError, ValueError) as exc:
            raise AutoCADContextError("Connection-anchor capture timestamp is invalid.") from exc
        if parsed.tzinfo is None or not self.captured_at.endswith("Z"):
            raise AutoCADContextError("Connection-anchor capture timestamp must be UTC.")
        if not isinstance(self.document, AnchorCaptureDocument) or not isinstance(self.selection, AnchorCaptureSelection):
            raise AutoCADContextError("Connection-anchor capture records are invalid.")
        if self.drawing_mutation_performed is not False:
            raise AutoCADContextError("Connection-anchor capture must not mutate the drawing.")


@dataclass(frozen=True, slots=True)
class TagObservation:
    object_id: str
    value: str

    def __post_init__(self) -> None:
        if _HANDLE.fullmatch(self.object_id) is None:
            raise AutoCADContextError("Reviewed topology tag handle is invalid.")
        _text(self.value, "tag value", 128)


@dataclass(frozen=True, slots=True)
class ReviewedTopology:
    schema_version: int
    topology_version: str
    topology_id: str
    document_id: str
    context_id: str
    context_evidence_sha256: str
    source_drawing_sha256: str
    convention_profile_sha256: str
    panel_name: str
    circuit_name: str
    circuit_tag_object_id: str
    device_object_ids: tuple[str, ...]
    control_tags: tuple[TagObservation, ...]
    environmental_qualifiers: tuple[TagObservation, ...]
    control_associations_performed: bool
    route_connectivity_performed: bool
    reviewed_by: str
    reviewed_on: str
    assumptions: tuple[str, ...]
    topology_sha256: str

    def __post_init__(self) -> None:
        if self.schema_version != 1 or self.topology_version != TOPOLOGY_VERSION:
            raise AutoCADContextError("Reviewed topology version is unsupported.")
        if _IDENTIFIER.fullmatch(self.topology_id) is None:
            raise AutoCADContextError("Reviewed topology ID is invalid.")
        if _DOCUMENT_ID.fullmatch(self.document_id) is None:
            raise AutoCADContextError("Reviewed topology document ID is invalid.")
        if _CONTEXT_ID.fullmatch(self.context_id) is None:
            raise AutoCADContextError("Reviewed topology context ID is invalid.")
        for value, name in (
            (self.context_evidence_sha256, "context evidence"),
            (self.source_drawing_sha256, "source drawing"),
            (self.convention_profile_sha256, "convention profile"),
            (self.topology_sha256, "topology"),
        ):
            _hash(value, name)
        _text(self.panel_name, "panel name", 128)
        _text(self.circuit_name, "circuit name", 128)
        if _HANDLE.fullmatch(self.circuit_tag_object_id) is None:
            raise AutoCADContextError("Reviewed topology circuit-tag handle is invalid.")
        _handles(self.device_object_ids, "device handles", 1, 512)
        _records(self.control_tags, "control tags", 0, 512)
        _records(self.environmental_qualifiers, "environmental qualifiers", 0, 64)
        all_tag_handles = [
            self.circuit_tag_object_id,
            *(item.object_id for item in self.control_tags),
            *(item.object_id for item in self.environmental_qualifiers),
        ]
        if len(all_tag_handles) != len(set(all_tag_handles)):
            raise AutoCADContextError("Reviewed topology tag handles must be unique.")
        if self.control_associations_performed or self.route_connectivity_performed:
            raise AutoCADContextError(
                "This topology version records circuit membership only; control and route associations must remain false."
            )
        _text(self.reviewed_by, "reviewer", 128)
        try:
            if date.fromisoformat(self.reviewed_on).isoformat() != self.reviewed_on:
                raise ValueError
        except ValueError as exc:
            raise AutoCADContextError("Reviewed topology date is invalid.") from exc
        if not isinstance(self.assumptions, tuple) or not 1 <= len(self.assumptions) <= 16:
            raise AutoCADContextError("Reviewed topology assumptions are invalid.")
        for item in self.assumptions:
            _text(item, "assumption", 512)
        if self.topology_sha256 != _topology_hash(self):
            raise AutoCADContextError("Reviewed topology SHA-256 does not match content.")

    def to_dict(self) -> dict[str, object]:
        return asdict(self)


@dataclass(frozen=True, slots=True)
class ReviewedRouteMembership:
    schema_version: int
    topology_version: str
    membership_id: str
    document_id: str
    context_id: str
    context_evidence_sha256: str
    source_drawing_sha256: str
    convention_profile_sha256: str
    panel_name: str
    circuit_name: str
    route_object_ids: tuple[str, ...]
    annotation_route_object_ids: tuple[str, ...]
    drawing_unit: str
    observed_centerline_meters: float
    annotation_centerline_meters: float
    connectivity_confirmed: bool
    installed_cable_length_confirmed: bool
    reviewed_by: str
    reviewed_on: str
    assumptions: tuple[str, ...]
    membership_sha256: str

    def __post_init__(self) -> None:
        if self.schema_version != 1 or self.topology_version != TOPOLOGY_VERSION:
            raise AutoCADContextError("Reviewed route-membership version is unsupported.")
        if _IDENTIFIER.fullmatch(self.membership_id) is None:
            raise AutoCADContextError("Reviewed route-membership ID is invalid.")
        if _DOCUMENT_ID.fullmatch(self.document_id) is None or _CONTEXT_ID.fullmatch(self.context_id) is None:
            raise AutoCADContextError("Reviewed route-membership context identity is invalid.")
        for value, name in ((self.context_evidence_sha256, "context evidence"), (self.source_drawing_sha256, "source drawing"), (self.convention_profile_sha256, "convention profile"), (self.membership_sha256, "membership")):
            _hash(value, name)
        _text(self.panel_name, "panel name", 128)
        _text(self.circuit_name, "circuit name", 128)
        _handles(self.route_object_ids, "route handles", 1, 512)
        _handles(self.annotation_route_object_ids, "annotation route handles", 0, 512)
        if set(self.route_object_ids) & set(self.annotation_route_object_ids):
            raise AutoCADContextError("Electrical and annotation route handles must be disjoint.")
        if self.drawing_unit != "millimeter":
            raise AutoCADContextError("Reviewed route membership currently requires millimeter drawing units.")
        if not isinstance(self.observed_centerline_meters, (int, float)) or isinstance(self.observed_centerline_meters, bool) or not 0 < self.observed_centerline_meters <= 1_000_000:
            raise AutoCADContextError("Reviewed route centerline length is invalid.")
        if not isinstance(self.annotation_centerline_meters, (int, float)) or isinstance(self.annotation_centerline_meters, bool) or not 0 <= self.annotation_centerline_meters <= 1_000_000:
            raise AutoCADContextError("Reviewed annotation centerline length is invalid.")
        if bool(self.annotation_route_object_ids) != (self.annotation_centerline_meters > 0):
            raise AutoCADContextError("Reviewed annotation handles and length must be present together.")
        if self.connectivity_confirmed or self.installed_cable_length_confirmed:
            raise AutoCADContextError("Route connectivity and installed cable length must remain unconfirmed in this record version.")
        _text(self.reviewed_by, "reviewer", 128)
        try:
            if date.fromisoformat(self.reviewed_on).isoformat() != self.reviewed_on:
                raise ValueError
        except ValueError as exc:
            raise AutoCADContextError("Reviewed route-membership date is invalid.") from exc
        if not isinstance(self.assumptions, tuple) or not 1 <= len(self.assumptions) <= 16:
            raise AutoCADContextError("Reviewed route-membership assumptions are invalid.")
        for item in self.assumptions:
            _text(item, "assumption", 512)
        if self.membership_sha256 != _route_membership_hash(self):
            raise AutoCADContextError("Reviewed route-membership SHA-256 does not match content.")

    def to_dict(self) -> dict[str, object]:
        return asdict(self)


@dataclass(frozen=True, slots=True)
class ConnectionAnchor:
    """One engineer-reviewed world-space terminal point on a device instance."""

    anchor_id: str
    device_object_id: str
    terminal_name: str
    world_position: tuple[float, float, float]

    def __post_init__(self) -> None:
        if _IDENTIFIER.fullmatch(self.anchor_id) is None:
            raise AutoCADContextError("Connection-anchor ID is invalid.")
        if _HANDLE.fullmatch(self.device_object_id) is None:
            raise AutoCADContextError("Connection-anchor device handle is invalid.")
        _text(self.terminal_name, "terminal name", 64)
        _point(self.world_position, "connection-anchor world position")


@dataclass(frozen=True, slots=True)
class RouteEndpointConnection:
    """An explicit reviewed association from one route endpoint to one anchor."""

    route_object_id: str
    endpoint: str
    anchor_id: str

    def __post_init__(self) -> None:
        if _HANDLE.fullmatch(self.route_object_id) is None:
            raise AutoCADContextError("Connection route handle is invalid.")
        if self.endpoint not in {"start", "end"}:
            raise AutoCADContextError("Connection route endpoint must be start or end.")
        if _IDENTIFIER.fullmatch(self.anchor_id) is None:
            raise AutoCADContextError("Connection anchor ID is invalid.")


@dataclass(frozen=True, slots=True)
class UnresolvedRouteEndpoint:
    """One explicitly reviewed route endpoint whose connection is not known."""

    route_object_id: str
    endpoint: str
    reason: str

    def __post_init__(self) -> None:
        if _HANDLE.fullmatch(self.route_object_id) is None:
            raise AutoCADContextError("Unresolved route handle is invalid.")
        if self.endpoint not in {"start", "end"}:
            raise AutoCADContextError("Unresolved route endpoint must be start or end.")
        _text(self.reason, "unresolved endpoint reason", 256)


@dataclass(frozen=True, slots=True)
class ReviewedConnectionAnchors:
    """Exact cross-context device-anchor and route-endpoint review evidence."""

    schema_version: int
    connection_version: str
    review_id: str
    document_id: str
    source_drawing_sha256: str
    device_context_id: str
    device_context_evidence_sha256: str
    device_convention_profile_sha256: str
    device_topology_sha256: str
    route_context_id: str
    route_context_evidence_sha256: str
    route_convention_profile_sha256: str
    route_membership_sha256: str
    capture_evidence_sha256s: tuple[str, ...]
    panel_name: str
    circuit_name: str
    anchors: tuple[ConnectionAnchor, ...]
    connections: tuple[RouteEndpointConnection, ...]
    unresolved_endpoints: tuple[UnresolvedRouteEndpoint, ...]
    all_route_endpoints_reviewed: bool
    reviewed_by: str
    reviewed_on: str
    assumptions: tuple[str, ...]
    review_sha256: str

    def __post_init__(self) -> None:
        if self.schema_version != 1 or self.connection_version != CONNECTION_ANCHORS_VERSION:
            raise AutoCADContextError("Reviewed connection-anchor version is unsupported.")
        if _IDENTIFIER.fullmatch(self.review_id) is None:
            raise AutoCADContextError("Reviewed connection-anchor ID is invalid.")
        if _DOCUMENT_ID.fullmatch(self.document_id) is None:
            raise AutoCADContextError("Reviewed connection-anchor document ID is invalid.")
        for value, name in (
            (self.source_drawing_sha256, "source drawing"),
            (self.device_context_evidence_sha256, "device context evidence"),
            (self.device_convention_profile_sha256, "device convention profile"),
            (self.device_topology_sha256, "device topology"),
            (self.route_context_evidence_sha256, "route context evidence"),
            (self.route_convention_profile_sha256, "route convention profile"),
            (self.route_membership_sha256, "route membership"),
            (self.review_sha256, "connection review"),
        ):
            _hash(value, name)
        if not isinstance(self.capture_evidence_sha256s, tuple) or len(self.capture_evidence_sha256s) != len(self.connections):
            raise AutoCADContextError("Connection review requires one capture evidence hash per connection.")
        for value in self.capture_evidence_sha256s:
            _hash(value, "anchor capture")
        if len(self.capture_evidence_sha256s) != len(set(self.capture_evidence_sha256s)):
            raise AutoCADContextError("Connection-anchor capture evidence hashes must be unique.")
        if _CONTEXT_ID.fullmatch(self.device_context_id) is None or _CONTEXT_ID.fullmatch(self.route_context_id) is None:
            raise AutoCADContextError("Reviewed connection-anchor context identity is invalid.")
        _text(self.panel_name, "panel name", 128)
        _text(self.circuit_name, "circuit name", 128)
        _records(self.anchors, "connection anchors", 1, 512, ConnectionAnchor)
        _records(self.connections, "route endpoint connections", 0, 1024, RouteEndpointConnection)
        _records(self.unresolved_endpoints, "unresolved route endpoints", 0, 1024, UnresolvedRouteEndpoint)
        anchor_ids = [item.anchor_id for item in self.anchors]
        anchor_id_set = set(anchor_ids)
        if len(anchor_ids) != len(anchor_id_set):
            raise AutoCADContextError("Reviewed connection-anchor IDs must be unique.")
        endpoint_keys = [(item.route_object_id, item.endpoint) for item in self.connections]
        unresolved_keys = [(item.route_object_id, item.endpoint) for item in self.unresolved_endpoints]
        if len(endpoint_keys) != len(set(endpoint_keys)) or len(unresolved_keys) != len(set(unresolved_keys)):
            raise AutoCADContextError("Reviewed route endpoints must be unique.")
        if set(endpoint_keys) & set(unresolved_keys):
            raise AutoCADContextError("A route endpoint cannot be both connected and unresolved.")
        connection_anchor_ids = [item.anchor_id for item in self.connections]
        if len(connection_anchor_ids) != len(set(connection_anchor_ids)) or set(connection_anchor_ids) != anchor_id_set:
            raise AutoCADContextError("Every connection must reference one unique reviewed anchor.")
        if self.all_route_endpoints_reviewed is not True:
            raise AutoCADContextError("Connection review must explicitly account for every route endpoint.")
        _text(self.reviewed_by, "reviewer", 128)
        try:
            if date.fromisoformat(self.reviewed_on).isoformat() != self.reviewed_on:
                raise ValueError
        except ValueError as exc:
            raise AutoCADContextError("Reviewed connection-anchor date is invalid.") from exc
        if not isinstance(self.assumptions, tuple) or not 1 <= len(self.assumptions) <= 16:
            raise AutoCADContextError("Reviewed connection-anchor assumptions are invalid.")
        for item in self.assumptions:
            _text(item, "assumption", 512)
        if self.review_sha256 != _connection_review_hash(self):
            raise AutoCADContextError("Reviewed connection-anchor SHA-256 does not match content.")

    def to_dict(self) -> dict[str, object]:
        return asdict(self)


@dataclass(frozen=True, slots=True)
class ConnectionReviewResult:
    connected_endpoint_count: int
    unresolved_endpoint_count: int
    total_endpoint_count: int
    complete_connectivity: bool


def create_reviewed_route_membership(**values: Any) -> ReviewedRouteMembership:
    """Create a self-hashed route-to-circuit membership without claiming connectivity."""
    payload = {"schema_version": 1, "topology_version": TOPOLOGY_VERSION, **values}
    digest = hashlib.sha256(_canonical(payload)).hexdigest()
    return ReviewedRouteMembership(**payload, membership_sha256=digest)


def create_reviewed_connection_anchors(**values: Any) -> ReviewedConnectionAnchors:
    """Create immutable evidence for explicit device/route endpoint decisions."""
    payload = {
        "schema_version": 1,
        "connection_version": CONNECTION_ANCHORS_VERSION,
        **values,
    }
    digest = hashlib.sha256(_canonical(payload)).hexdigest()
    return ReviewedConnectionAnchors(**payload, review_sha256=digest)


def parse_connection_anchor_capture(raw: object) -> ConnectionAnchorCapture:
    """Parse the exact read-only AutoCAD anchor-capture schema."""
    if not isinstance(raw, dict) or set(raw) != set(ConnectionAnchorCapture.__dataclass_fields__):
        raise AutoCADContextError("Connection-anchor capture must use the exact schema.")
    document = raw.get("document")
    selection = raw.get("selection")
    if not isinstance(document, dict) or set(document) != set(AnchorCaptureDocument.__dataclass_fields__):
        raise AutoCADContextError("Connection-anchor capture document must use the exact schema.")
    if not isinstance(selection, dict) or set(selection) != set(AnchorCaptureSelection.__dataclass_fields__):
        raise AutoCADContextError("Connection-anchor capture selection must use the exact schema.")
    selection_values = dict(selection)
    for name in ("world_position", "endpoint_pick_position"):
        point = selection_values[name]
        if not isinstance(point, list):
            raise AutoCADContextError(f"Anchor-capture {name} must be an array.")
        selection_values[name] = tuple(point)
    values = dict(raw)
    values["document"] = AnchorCaptureDocument(**document)
    values["selection"] = AnchorCaptureSelection(**selection_values)
    return ConnectionAnchorCapture(**values)


def load_connection_anchor_capture(
    path: Path,
    *,
    expected_sha256: str,
) -> ConnectionAnchorCapture:
    """Load one bounded non-link capture and require its command-reported digest."""
    _hash(expected_sha256, "anchor-capture evidence")
    resolved = path.resolve(strict=True)
    if path.is_symlink() or not resolved.is_file():
        raise AutoCADContextError("Connection-anchor capture must be a regular non-link file.")
    raw = resolved.read_bytes()
    if not raw or len(raw) > _MAX_BYTES:
        raise AutoCADContextError("Connection-anchor capture is empty or too large.")
    if hashlib.sha256(raw).hexdigest() != expected_sha256:
        raise AutoCADContextError("Connection-anchor capture SHA-256 does not match evidence.")
    try:
        return parse_connection_anchor_capture(json.loads(raw.decode("utf-8")))
    except (UnicodeError, json.JSONDecodeError) as exc:
        raise AutoCADContextError("Connection-anchor capture must be valid UTF-8 JSON.") from exc


def assemble_reviewed_connection_anchors(
    *,
    device_model: ElectricalProjectModel,
    route_model: ElectricalProjectModel,
    topology: ReviewedTopology,
    route_membership: ReviewedRouteMembership,
    captures: Sequence[tuple[ConnectionAnchorCapture, str]],
    unresolved_endpoints: tuple[UnresolvedRouteEndpoint, ...],
    review_id: str,
    reviewed_by: str,
    reviewed_on: str,
    assumptions: tuple[str, ...],
) -> ReviewedConnectionAnchors:
    """Assemble exact loaded captures and explicit unresolved decisions into one review."""
    if not isinstance(captures, Sequence) or isinstance(captures, (str, bytes)) or not 1 <= len(captures) <= 1024:
        raise AutoCADContextError("Connection review requires 1 through 1024 loaded captures.")
    devices = {item.source.object_id: item for item in device_model.devices}
    routes = {item.source.object_id: item for item in route_model.routes}
    anchors: dict[str, ConnectionAnchor] = {}
    connections: list[RouteEndpointConnection] = []
    evidence: list[str] = []
    endpoint_keys: set[tuple[str, str]] = set()
    for item in captures:
        if not isinstance(item, tuple) or len(item) != 2 or not isinstance(item[0], ConnectionAnchorCapture):
            raise AutoCADContextError("Connection review captures must be validated capture/hash pairs.")
        capture, digest = item
        _hash(digest, "anchor-capture evidence")
        selection = capture.selection
        if capture.document.document_id != topology.document_id or capture.document.document_id != route_membership.document_id:
            raise AutoCADContextError("Connection-anchor capture document does not match topology evidence.")
        device = devices.get(selection.device_object_id)
        route = routes.get(selection.route_object_id)
        if device is None or selection.device_object_id not in topology.device_object_ids:
            raise AutoCADContextError("Connection-anchor capture references an unknown reviewed device.")
        if route is None or selection.route_object_id not in route_membership.route_object_ids:
            raise AutoCADContextError("Connection-anchor capture references an unknown reviewed route.")
        if selection.block_name != device.block_name:
            raise AutoCADContextError("Connection-anchor capture block name does not match the exact device.")
        endpoint_key = (selection.route_object_id, selection.endpoint)
        if endpoint_key in endpoint_keys:
            raise AutoCADContextError("Connection-anchor capture repeats a reviewed route endpoint.")
        endpoint = route.points[0] if selection.endpoint == "start" else route.points[-1]
        alternate = route.points[-1] if selection.endpoint == "start" else route.points[0]
        if any(abs(left - right) > 1e-9 for left, right in zip(endpoint, selection.world_position)):
            raise AutoCADContextError("Connection-anchor capture point does not match exact route geometry.")
        selected_distance = math.dist(selection.endpoint_pick_position, endpoint)
        alternate_distance = math.dist(selection.endpoint_pick_position, alternate)
        if abs(selected_distance - selection.endpoint_pick_distance) > 1e-9 or abs(alternate_distance - selection.alternate_endpoint_distance) > 1e-9:
            raise AutoCADContextError("Connection-anchor capture pick distances do not match exact route geometry.")
        if selected_distance >= alternate_distance:
            raise AutoCADContextError("Connection-anchor capture pick is not closest to its selected endpoint.")
        anchor_id = "anchor:" + capture.capture_id
        anchor = ConnectionAnchor(
            anchor_id,
            selection.device_object_id,
            selection.terminal_name,
            selection.world_position,
        )
        anchors[anchor_id] = anchor
        connections.append(RouteEndpointConnection(*endpoint_key, anchor_id))
        evidence.append(digest)
        endpoint_keys.add(endpoint_key)
    review = create_reviewed_connection_anchors(
        review_id=review_id,
        document_id=topology.document_id,
        source_drawing_sha256=topology.source_drawing_sha256,
        device_context_id=topology.context_id,
        device_context_evidence_sha256=topology.context_evidence_sha256,
        device_convention_profile_sha256=topology.convention_profile_sha256,
        device_topology_sha256=topology.topology_sha256,
        route_context_id=route_membership.context_id,
        route_context_evidence_sha256=route_membership.context_evidence_sha256,
        route_convention_profile_sha256=route_membership.convention_profile_sha256,
        route_membership_sha256=route_membership.membership_sha256,
        capture_evidence_sha256s=tuple(evidence),
        panel_name=topology.panel_name,
        circuit_name=topology.circuit_name,
        anchors=tuple(anchors.values()),
        connections=tuple(connections),
        unresolved_endpoints=unresolved_endpoints,
        all_route_endpoints_reviewed=True,
        reviewed_by=reviewed_by,
        reviewed_on=reviewed_on,
        assumptions=assumptions,
    )
    validate_reviewed_connection_anchors(
        device_model,
        route_model,
        review,
        topology=topology,
        route_membership=route_membership,
    )
    return review


def parse_reviewed_connection_anchors(raw: object) -> ReviewedConnectionAnchors:
    """Parse an exact connection review and revalidate its content hash."""
    if not isinstance(raw, dict) or set(raw) != set(ReviewedConnectionAnchors.__dataclass_fields__):
        raise AutoCADContextError("Reviewed connection anchors must use the exact schema.")
    values = dict(raw)
    values["anchors"] = _typed_records(raw["anchors"], ConnectionAnchor, "connection anchors")
    values["connections"] = _typed_records(raw["connections"], RouteEndpointConnection, "route endpoint connections")
    values["unresolved_endpoints"] = _typed_records(raw["unresolved_endpoints"], UnresolvedRouteEndpoint, "unresolved route endpoints")
    values["capture_evidence_sha256s"] = _string_tuple(raw["capture_evidence_sha256s"], "capture evidence hashes")
    values["assumptions"] = _string_tuple(raw["assumptions"], "assumptions")
    return ReviewedConnectionAnchors(**values)


def validate_reviewed_connection_anchors(
    device_model: ElectricalProjectModel,
    route_model: ElectricalProjectModel,
    review: ReviewedConnectionAnchors,
    *,
    topology: ReviewedTopology,
    route_membership: ReviewedRouteMembership,
) -> ConnectionReviewResult:
    """Validate exact handles, endpoint geometry, and complete endpoint accounting."""
    normalized_document = "autocad:" + review.document_id.replace("-", "")
    if device_model.document_id != normalized_document or route_model.document_id != normalized_document:
        raise AutoCADContextError("Connection review does not match both AutoCAD documents.")
    if device_model.context_id != review.device_context_id or route_model.context_id != review.route_context_id:
        raise AutoCADContextError("Connection review does not match both exact contexts.")
    if review.source_drawing_sha256 != topology.source_drawing_sha256 or review.source_drawing_sha256 != route_membership.source_drawing_sha256:
        raise AutoCADContextError("Connection review source drawing does not match membership evidence.")
    if (
        review.device_context_evidence_sha256 != topology.context_evidence_sha256
        or review.device_convention_profile_sha256 != topology.convention_profile_sha256
        or review.device_topology_sha256 != topology.topology_sha256
        or review.route_context_evidence_sha256 != route_membership.context_evidence_sha256
        or review.route_convention_profile_sha256 != route_membership.convention_profile_sha256
        or review.route_membership_sha256 != route_membership.membership_sha256
    ):
        raise AutoCADContextError("Connection review does not match exact topology evidence.")
    if review.panel_name != topology.panel_name or review.panel_name != route_membership.panel_name or review.circuit_name != topology.circuit_name or review.circuit_name != route_membership.circuit_name:
        raise AutoCADContextError("Connection review panel or circuit does not match membership evidence.")
    devices = {item.source.object_id: item for item in device_model.devices}
    routes = {item.source.object_id: item for item in route_model.routes}
    if set(devices) != set(topology.device_object_ids) or set(routes) != set(route_membership.route_object_ids) | set(route_membership.annotation_route_object_ids):
        raise AutoCADContextError("Connection review models do not exactly match reviewed membership handles.")
    if any(item.device_object_id not in devices for item in review.anchors):
        raise AutoCADContextError("Connection review contains an unknown device anchor.")
    expected_endpoints = {(handle, endpoint) for handle in route_membership.route_object_ids for endpoint in ("start", "end")}
    reviewed_endpoints = {
        (item.route_object_id, item.endpoint) for item in review.connections
    } | {
        (item.route_object_id, item.endpoint) for item in review.unresolved_endpoints
    }
    if reviewed_endpoints != expected_endpoints:
        raise AutoCADContextError("Connection review must account for every exact route endpoint once.")
    anchors = {item.anchor_id: item for item in review.anchors}
    for connection in review.connections:
        route = routes[connection.route_object_id]
        endpoint = route.points[0] if connection.endpoint == "start" else route.points[-1]
        anchor = anchors[connection.anchor_id]
        if any(abs(left - right) > 1e-9 for left, right in zip(endpoint, anchor.world_position)):
            raise AutoCADContextError("Connected anchor does not exactly match the reviewed route endpoint.")
    connected = len(review.connections)
    unresolved = len(review.unresolved_endpoints)
    return ConnectionReviewResult(connected, unresolved, connected + unresolved, unresolved == 0)


def parse_reviewed_route_membership(raw: object) -> ReviewedRouteMembership:
    if not isinstance(raw, dict) or set(raw) != set(ReviewedRouteMembership.__dataclass_fields__):
        raise AutoCADContextError("Reviewed route membership must use the exact schema.")
    values = dict(raw)
    values["route_object_ids"] = _string_tuple(raw["route_object_ids"], "route handles")
    values["annotation_route_object_ids"] = _string_tuple(raw["annotation_route_object_ids"], "annotation route handles")
    values["assumptions"] = _string_tuple(raw["assumptions"], "assumptions")
    return ReviewedRouteMembership(**values)


def load_reviewed_route_membership(path: Path) -> ReviewedRouteMembership:
    """Load one bounded, regular, non-link reviewed route-membership file."""
    resolved = path.resolve(strict=True)
    if path.is_symlink() or not resolved.is_file():
        raise AutoCADContextError(
            "Reviewed route membership must be a regular non-link file."
        )
    raw = resolved.read_bytes()
    if not raw or len(raw) > _MAX_BYTES:
        raise AutoCADContextError("Reviewed route membership is empty or too large.")
    try:
        return parse_reviewed_route_membership(json.loads(raw.decode("utf-8")))
    except (UnicodeError, json.JSONDecodeError) as exc:
        raise AutoCADContextError(
            "Reviewed route membership must be valid UTF-8 JSON."
        ) from exc


def validate_reviewed_route_membership(
    model: ElectricalProjectModel,
    membership: ReviewedRouteMembership,
    *, context_evidence_sha256: str, source_drawing_sha256: str,
    convention_profile_sha256: str,
) -> float:
    """Validate exact route handles and reproduce the recorded centerline total."""
    normalized_document = "autocad:" + membership.document_id.replace("-", "")
    if model.context_id != membership.context_id or model.document_id != normalized_document or context_evidence_sha256 != membership.context_evidence_sha256 or source_drawing_sha256 != membership.source_drawing_sha256 or convention_profile_sha256 != membership.convention_profile_sha256:
        raise AutoCADContextError("Reviewed route membership does not match the exact model evidence.")
    routes = {item.source.object_id: item for item in model.routes}
    electrical_ids = set(membership.route_object_ids)
    annotation_ids = set(membership.annotation_route_object_ids)
    if set(routes) != electrical_ids | annotation_ids:
        raise AutoCADContextError("Reviewed electrical and annotation handles do not partition the imported routes.")
    total = sum(math.dist(start, end) * 0.001 for handle, route in routes.items() if handle in electrical_ids for start, end in zip(route.points, route.points[1:]))
    if abs(total - membership.observed_centerline_meters) > 1e-9:
        raise AutoCADContextError("Reviewed route centerline length does not match exact geometry.")
    annotation_total = sum(math.dist(start, end) * 0.001 for handle, route in routes.items() if handle in annotation_ids for start, end in zip(route.points, route.points[1:]))
    if abs(annotation_total - membership.annotation_centerline_meters) > 1e-9:
        raise AutoCADContextError("Reviewed annotation centerline length does not match exact geometry.")
    return total


def create_reviewed_topology(**values: Any) -> ReviewedTopology:
    """Create an immutable self-hashed circuit-membership record."""
    payload = {
        "schema_version": 1,
        "topology_version": TOPOLOGY_VERSION,
        **values,
    }
    digest = hashlib.sha256(_canonical(payload)).hexdigest()
    return ReviewedTopology(**payload, topology_sha256=digest)


def parse_reviewed_topology(raw: object) -> ReviewedTopology:
    """Parse the exact topology schema and revalidate its self-hash."""
    if not isinstance(raw, dict):
        raise AutoCADContextError("Reviewed topology must be an object.")
    expected = set(ReviewedTopology.__dataclass_fields__)
    if set(raw) != expected:
        raise AutoCADContextError("Reviewed topology must use the exact schema.")
    values = dict(raw)
    values["device_object_ids"] = _string_tuple(raw["device_object_ids"], "device handles")
    values["control_tags"] = _tag_records(raw["control_tags"], "control tags")
    values["environmental_qualifiers"] = _tag_records(
        raw["environmental_qualifiers"], "environmental qualifiers"
    )
    values["assumptions"] = _string_tuple(raw["assumptions"], "assumptions")
    return ReviewedTopology(**values)


def load_reviewed_topology(path: Path) -> ReviewedTopology:
    """Load one bounded, regular, non-link topology JSON file."""
    resolved = path.resolve(strict=True)
    if path.is_symlink() or not resolved.is_file():
        raise AutoCADContextError("Reviewed topology must be a regular non-link file.")
    raw = resolved.read_bytes()
    if not raw or len(raw) > _MAX_BYTES:
        raise AutoCADContextError("Reviewed topology is empty or too large.")
    try:
        return parse_reviewed_topology(json.loads(raw.decode("utf-8")))
    except (UnicodeError, json.JSONDecodeError) as exc:
        raise AutoCADContextError("Reviewed topology must be valid UTF-8 JSON.") from exc


def apply_reviewed_topology(
    model: ElectricalProjectModel,
    topology: ReviewedTopology,
    *,
    context_evidence_sha256: str,
    source_drawing_sha256: str,
    convention_profile_sha256: str,
) -> ElectricalProjectModel:
    """Apply exact reviewed device membership without inferring controls or routes."""
    normalized_document = "autocad:" + topology.document_id.replace("-", "")
    if (
        model.context_id != topology.context_id
        or model.document_id != normalized_document
        or context_evidence_sha256 != topology.context_evidence_sha256
        or source_drawing_sha256 != topology.source_drawing_sha256
        or convention_profile_sha256 != topology.convention_profile_sha256
    ):
        raise AutoCADContextError("Reviewed topology does not match the exact model evidence.")
    devices_by_handle = {item.source.object_id: item.device_id for item in model.devices}
    if set(devices_by_handle) != set(topology.device_object_ids):
        raise AutoCADContextError(
            "Reviewed topology device handles do not exactly match the imported devices."
        )
    matches = [
        item
        for item in model.circuits
        if item.name == topology.circuit_name
        and any(panel.panel_id == item.panel_id and panel.name == topology.panel_name for panel in model.panels)
    ]
    if len(matches) != 1:
        raise AutoCADContextError("Reviewed topology requires one exact panel/circuit match.")
    target = matches[0]
    membership = tuple(devices_by_handle[item] for item in topology.device_object_ids)
    circuits = tuple(
        replace(item, device_ids=membership) if item.circuit_id == target.circuit_id else item
        for item in model.circuits
    )
    warnings = tuple(
        item for item in model.warnings
        if "spatial circuit/control association is not inferred" not in item
    ) + (
        "Exact engineer-reviewed device membership was applied to circuit "
        f"{topology.circuit_name} / {topology.panel_name}; control tags and route connectivity remain unassociated.",
    )
    return replace(model, circuits=circuits, warnings=warnings)


def _topology_hash(value: ReviewedTopology) -> str:
    payload = asdict(value)
    del payload["topology_sha256"]
    return hashlib.sha256(_canonical(payload)).hexdigest()


def _route_membership_hash(value: ReviewedRouteMembership) -> str:
    payload = asdict(value)
    del payload["membership_sha256"]
    return hashlib.sha256(_canonical(payload)).hexdigest()


def _connection_review_hash(value: ReviewedConnectionAnchors) -> str:
    payload = asdict(value)
    del payload["review_sha256"]
    return hashlib.sha256(_canonical(payload)).hexdigest()


def _canonical(value: object) -> bytes:
    return json.dumps(
        value, ensure_ascii=False, sort_keys=True, separators=(",", ":"), default=asdict
    ).encode("utf-8")


def _text(value: object, name: str, maximum: int) -> None:
    if not isinstance(value, str) or not value.strip() or len(value.encode("utf-8")) > maximum:
        raise AutoCADContextError(f"Reviewed topology {name} is invalid.")


def _hash(value: object, name: str) -> None:
    if not isinstance(value, str) or _SHA256.fullmatch(value) is None:
        raise AutoCADContextError(f"Reviewed topology {name} SHA-256 is invalid.")


def _handles(values: object, name: str, minimum: int, maximum: int) -> None:
    if not isinstance(values, tuple) or not minimum <= len(values) <= maximum:
        raise AutoCADContextError(f"Reviewed topology {name} are invalid.")
    if any(not isinstance(item, str) or _HANDLE.fullmatch(item) is None for item in values):
        raise AutoCADContextError(f"Reviewed topology {name} are invalid.")
    if len(values) != len(set(values)):
        raise AutoCADContextError(f"Reviewed topology {name} must be unique.")


def _records(
    values: object,
    name: str,
    minimum: int,
    maximum: int,
    record_type: type = TagObservation,
) -> None:
    if (
        not isinstance(values, tuple)
        or not minimum <= len(values) <= maximum
        or any(not isinstance(item, record_type) for item in values)
    ):
        raise AutoCADContextError(f"Reviewed topology {name} are invalid.")


def _string_tuple(raw: object, name: str) -> tuple[str, ...]:
    if not isinstance(raw, list) or any(not isinstance(item, str) for item in raw):
        raise AutoCADContextError(f"Reviewed topology {name} must be an array of strings.")
    return tuple(raw)


def _typed_records(raw: object, record_type: type, name: str) -> tuple[Any, ...]:
    if not isinstance(raw, list):
        raise AutoCADContextError(f"Reviewed topology {name} must be an array.")
    fields = set(record_type.__dataclass_fields__)
    result = []
    for item in raw:
        if not isinstance(item, dict) or set(item) != fields:
            raise AutoCADContextError(f"Reviewed topology {name} must use exact records.")
        values = dict(item)
        if record_type is ConnectionAnchor:
            point = values["world_position"]
            if not isinstance(point, list):
                raise AutoCADContextError("Connection-anchor world position must be an array.")
            values["world_position"] = tuple(point)
        result.append(record_type(**values))
    return tuple(result)


def _point(value: object, name: str) -> None:
    if (
        not isinstance(value, tuple)
        or len(value) != 3
        or any(not isinstance(item, (int, float)) or isinstance(item, bool) or not math.isfinite(item) or abs(item) > 1_000_000_000 for item in value)
    ):
        raise AutoCADContextError(f"Reviewed topology {name} is invalid.")


def _tag_records(raw: object, name: str) -> tuple[TagObservation, ...]:
    if not isinstance(raw, list):
        raise AutoCADContextError(f"Reviewed topology {name} must be an array.")
    result: list[TagObservation] = []
    for item in raw:
        if not isinstance(item, dict) or set(item) != {"object_id", "value"}:
            raise AutoCADContextError(f"Reviewed topology {name} must use exact records.")
        result.append(TagObservation(**item))
    return tuple(result)
