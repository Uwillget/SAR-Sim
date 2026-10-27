#!/usr/bin/env python3
"""Headless, publication-quality traffic heatmap renderer for SAR.

The road panels deliberately consume *authored lane centerlines* and never consume
``LaneConnectionCorridors``.  Vertiport pages are path-focused: only reservable
approach edges, explicit holding ramps, and booked facility-to-network corridors
are drawn.  Facility corridors are reconstructed with the same tangent cubic
Bezier used by Unity's 3D scene; point resources, helipad fills, and facility
boundaries are intentionally omitted.  A request may contain either raw Unity
``Network``/``Vertiport`` snapshots or already flattened geometry.

The public entry point is :func:`render_traffic_heatmap`.  It is importable by
the existing SAR plotting worker and also has a small CLI for QA and tests.
"""

from __future__ import annotations

import argparse
import json
import math
import os
import sys
import traceback
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any, Iterable, Mapping, Sequence

# Match the main plotting worker's Windows font discovery behavior.  This also
# keeps Chinese scene titles renderable in the localized desktop build.
if os.name == "nt" and not os.environ.get("WINDIR"):
    os.environ["WINDIR"] = os.environ.get("SystemRoot", r"C:\Windows")

import matplotlib

matplotlib.use("Agg")

import matplotlib.pyplot as plt
from matplotlib.cm import ScalarMappable
from matplotlib.collections import LineCollection
from matplotlib.colors import Normalize
from matplotlib.ticker import FuncFormatter, MaxNLocator


TRAFFIC_HEATMAP_KIND = "traffic-heatmap"
SCHEMA_VERSION = 1
PAGE_MANIFEST_VERSION = 1
DEFAULT_DPI = 300
_MISSING = object()


@dataclass(frozen=True)
class LayerSpec:
    layer_id: str
    display_name: str
    altitude: float | None = None
    enabled: bool = True


@dataclass(frozen=True)
class LaneGeometry:
    lane_id: str
    layer_id: str
    points: tuple[tuple[float, float], ...]
    traversals: float = 0.0


@dataclass(frozen=True)
class ResourceGeometry:
    resource_id: str
    resource_type: str
    points: tuple[tuple[float, float], ...]
    reservation_seconds: float = 0.0
    closed: bool = False
    owner_vertiport_id: str = ""


@dataclass
class VertiportGeometry:
    vertiport_id: str
    display_name: str
    resources: list[ResourceGeometry] = field(default_factory=list)
    outlines: list[tuple[tuple[float, float], ...]] = field(default_factory=list)


@dataclass
class PreparedTrafficData:
    title: str
    layers: list[LayerSpec] = field(default_factory=list)
    visible_layer_ids: list[str] = field(default_factory=list)
    lanes: list[LaneGeometry] = field(default_factory=list)
    resources: list[ResourceGeometry] = field(default_factory=list)
    facility_outlines: list[tuple[tuple[float, float], ...]] = field(default_factory=list)
    vertiport_groups: list[VertiportGeometry] = field(default_factory=list)
    missing_loaded_resource_ids: list[str] = field(default_factory=list)
    notes: list[str] = field(default_factory=list)


def _field(value: Any, *names: str, default: Any = None) -> Any:
    if isinstance(value, Mapping):
        for name in names:
            if name in value:
                return value[name]
        lowered = {str(key).lower(): item for key, item in value.items()}
        for name in names:
            if name.lower() in lowered:
                return lowered[name.lower()]
    return default


def _has_field(value: Any, *names: str) -> bool:
    return _field(value, *names, default=_MISSING) is not _MISSING


def _items(value: Any) -> list[Any]:
    if value is None:
        return []
    if isinstance(value, (str, bytes, Mapping)):
        return []
    try:
        return list(value)
    except TypeError:
        return []


def _finite_number(value: Any, default: float = 0.0) -> float:
    try:
        number = float(value)
    except (TypeError, ValueError):
        return default
    return number if math.isfinite(number) else default


def _optional_number(value: Any) -> float | None:
    if value is None:
        return None
    number = _finite_number(value, float("nan"))
    return number if math.isfinite(number) else None


def _truthy(value: Any, default: bool = False) -> bool:
    if value is None:
        return default
    if isinstance(value, str):
        return value.strip().lower() not in {"", "0", "false", "no", "off"}
    return bool(value)


def _point2(value: Any) -> tuple[float, float] | None:
    """Convert Unity X/Y/Z or a flat X/Z pair to top-down plot coordinates."""
    if isinstance(value, Mapping):
        x = _field(value, "x", "X", default=_MISSING)
        z = _field(value, "z", "Z", default=_MISSING)
        if x is _MISSING or z is _MISSING:
            return None
        px = _finite_number(x, float("nan"))
        pz = _finite_number(z, float("nan"))
    elif isinstance(value, Sequence) and not isinstance(value, (str, bytes)):
        if len(value) >= 3:
            px = _finite_number(value[0], float("nan"))
            pz = _finite_number(value[2], float("nan"))
        elif len(value) >= 2:
            px = _finite_number(value[0], float("nan"))
            pz = _finite_number(value[1], float("nan"))
        else:
            return None
    else:
        return None
    if not math.isfinite(px) or not math.isfinite(pz):
        return None
    return px, pz


def _point3(value: Any) -> tuple[float, float, float] | None:
    """Convert Unity coordinates while retaining altitude for Bezier handles."""
    if isinstance(value, Mapping):
        raw_x = _field(value, "x", "X", default=_MISSING)
        raw_y = _field(value, "y", "Y", default=0.0)
        raw_z = _field(value, "z", "Z", default=_MISSING)
        if raw_x is _MISSING or raw_z is _MISSING:
            return None
        point = (
            _finite_number(raw_x, float("nan")),
            _finite_number(raw_y, float("nan")),
            _finite_number(raw_z, float("nan")),
        )
    elif isinstance(value, Sequence) and not isinstance(value, (str, bytes)):
        if len(value) >= 3:
            point = tuple(_finite_number(value[index], float("nan")) for index in range(3))
        elif len(value) >= 2:
            point = (
                _finite_number(value[0], float("nan")), 0.0,
                _finite_number(value[1], float("nan")),
            )
        else:
            return None
    else:
        return None
    return point if all(math.isfinite(component) for component in point) else None


def _polyline3(value: Any) -> tuple[tuple[float, float, float], ...]:
    return tuple(point for point in (_point3(item) for item in _items(value)) if point is not None)


def _vector_add(a: tuple[float, float, float], b: tuple[float, float, float]) -> tuple[float, float, float]:
    return tuple(a[index] + b[index] for index in range(3))


def _vector_subtract(a: tuple[float, float, float], b: tuple[float, float, float]) -> tuple[float, float, float]:
    return tuple(a[index] - b[index] for index in range(3))


def _vector_scale(value: tuple[float, float, float], scalar: float) -> tuple[float, float, float]:
    return tuple(component * scalar for component in value)


def _vector_dot(a: tuple[float, float, float], b: tuple[float, float, float]) -> float:
    return sum(a[index] * b[index] for index in range(3))


def _vector_length(value: tuple[float, float, float]) -> float:
    return math.sqrt(_vector_dot(value, value))


def _vector_normalize(value: tuple[float, float, float]) -> tuple[float, float, float]:
    length = _vector_length(value)
    return _vector_scale(value, 1.0 / length) if length > 1e-6 else (0.0, 0.0, 0.0)


def _project_point_on_segment3(point: tuple[float, float, float],
                               start: tuple[float, float, float],
                               end: tuple[float, float, float]) -> tuple[float, float, float]:
    segment = _vector_subtract(end, start)
    length_squared = _vector_dot(segment, segment)
    if length_squared < 1e-6:
        return start
    t = min(1.0, max(0.0, _vector_dot(_vector_subtract(point, start), segment) / length_squared))
    return _vector_add(start, _vector_scale(segment, t))


def _tangent_on_polyline3(points: Sequence[tuple[float, float, float]],
                          target: tuple[float, float, float]) -> tuple[float, float, float]:
    if len(points) < 2:
        return (0.0, 0.0, 0.0)
    tangent = _vector_subtract(points[1], points[0])
    best_distance = float("inf")
    for index in range(1, len(points)):
        projection = _project_point_on_segment3(target, points[index - 1], points[index])
        distance = _vector_dot(_vector_subtract(target, projection),
                               _vector_subtract(target, projection))
        if distance < best_distance:
            best_distance = distance
            tangent = _vector_subtract(points[index], points[index - 1])
    return _vector_normalize(tangent)


def _evaluate_bezier3(p0: tuple[float, float, float], p1: tuple[float, float, float],
                      p2: tuple[float, float, float], p3: tuple[float, float, float],
                      t: float) -> tuple[float, float, float]:
    u = 1.0 - t
    return tuple(
        u * u * u * p0[index] + 3.0 * u * u * t * p1[index] +
        3.0 * u * t * t * p2[index] + t * t * t * p3[index]
        for index in range(3)
    )


def _polyline(value: Any, *, close: bool = False) -> tuple[tuple[float, float], ...]:
    points = tuple(point for point in (_point2(item) for item in _items(value)) if point is not None)
    if close and len(points) >= 3 and points[0] != points[-1]:
        points += (points[0],)
    return points


def _numeric_map(value: Any) -> dict[str, float]:
    if not isinstance(value, Mapping):
        return {}
    result: dict[str, float] = {}
    for key, raw in value.items():
        name = str(key or "").strip()
        if name:
            result[name] = max(0.0, _finite_number(raw))
    return result


def _layer_specs(network: Any, inferred_ids: Iterable[str]) -> list[LayerSpec]:
    result: list[LayerSpec] = []
    seen: set[str] = set()
    raw_layers = _field(network, "altitude_layers", "AltitudeLayers", "layers", default=[])
    for index, raw in enumerate(_items(raw_layers)):
        layer_id = str(_field(raw, "layer_id", "LayerId", "id", default=f"L{index}") or f"L{index}")
        if layer_id in seen:
            continue
        seen.add(layer_id)
        result.append(LayerSpec(
            layer_id=layer_id,
            display_name=str(_field(raw, "display_name", "DisplayName", "name", default=layer_id) or layer_id),
            altitude=_optional_number(_field(raw, "nominal_altitude", "NominalAltitude", "altitude")),
            enabled=_truthy(_field(raw, "enabled", "Enabled", default=True), True),
        ))
    for layer_id in inferred_ids:
        if layer_id and layer_id not in seen:
            seen.add(layer_id)
            result.append(LayerSpec(layer_id, layer_id))
    return result


def _looks_like_connector(raw: Any) -> bool:
    if _truthy(_field(raw, "is_connector", "IsConnector", default=False)):
        return True
    role = str(_field(raw, "geometry_role", "GeometryRole", "kind", "Kind", default="") or "").lower()
    return "connector" in role or "laneconnectioncorridor" in role.replace("_", "")


def _collect_lanes(request: Mapping[str, Any], network: Any,
                   traversals: Mapping[str, float]) -> list[LaneGeometry]:
    lanes: list[LaneGeometry] = []
    explicit = _field(request, "lanes", "Lanes", default=_MISSING)
    if explicit is not _MISSING:
        lane_sources = [(raw, "") for raw in _items(explicit)]
    else:
        lane_sources: list[tuple[Any, str]] = []
        # Intentionally inspect only Edges[].Lanes.  The sibling
        # LaneConnectionCorridors collection is never read.
        for edge in _items(_field(network, "edges", "Edges", default=[])):
            edge_layer = str(_field(edge, "layer_id", "LayerId", default="") or "")
            lane_sources.extend((raw, edge_layer) for raw in _items(_field(edge, "lanes", "Lanes", default=[])))

    seen: set[str] = set()
    for index, (raw, edge_layer) in enumerate(lane_sources):
        if _looks_like_connector(raw):
            continue
        lane_id = str(_field(raw, "lane_id", "LaneId", "id", default=f"lane-{index + 1}") or f"lane-{index + 1}")
        if lane_id in seen:
            continue
        points = _polyline(_field(raw, "points", "Points", "centerline", "Centerline", default=[]))
        if len(points) < 2:
            continue
        seen.add(lane_id)
        layer_id = str(_field(raw, "layer_id", "LayerId", default=edge_layer) or edge_layer or "Unassigned")
        embedded = _field(raw, "traversal_count", "TraversalCount", "traversals", "Traversals", default=_MISSING)
        value = traversals.get(lane_id, 0.0) if embedded is _MISSING else max(0.0, _finite_number(embedded))
        lanes.append(LaneGeometry(lane_id, layer_id, points, value))
    return lanes


def _arc_points(center_raw: Any, radius_raw: Any, start_raw: Any, end_raw: Any,
                direction: Any, segment_count: int = 72) -> tuple[tuple[float, float], ...]:
    center = _point2(center_raw)
    start = _point2(start_raw)
    end = _point2(end_raw)
    radius = max(0.0, _finite_number(radius_raw))
    if center is None or start is None or end is None or radius <= 0.0:
        return tuple(point for point in (start, end) if point is not None)
    a0 = math.atan2(start[1] - center[1], start[0] - center[0])
    a1 = math.atan2(end[1] - center[1], end[0] - center[0])
    diff = (a1 - a0) % (2.0 * math.pi)
    direction_text = str(direction or "Shortest").strip().lower()
    direction_number = int(_finite_number(direction, -1)) if not isinstance(direction, str) else -1
    clockwise = direction_text in {"clockwise", "cw"} or direction_number == 1
    counter_clockwise = direction_text in {"counterclockwise", "counter-clockwise", "ccw"} or direction_number == 2
    if clockwise and diff > 0.0:
        diff -= 2.0 * math.pi
    elif not counter_clockwise and diff > math.pi:
        diff -= 2.0 * math.pi
    return tuple((
        center[0] + radius * math.cos(a0 + diff * index / segment_count),
        center[1] + radius * math.sin(a0 + diff * index / segment_count),
    ) for index in range(segment_count + 1))


def _arc_points3(center_raw: Any, radius_raw: Any, start_raw: Any, end_raw: Any,
                 direction: Any, segment_count: int = 48) -> tuple[tuple[float, float, float], ...]:
    """Mirror ``RuntimeAirspaceGeometry.BuildRampPolyline`` in Unity."""
    center = _point3(center_raw)
    start = _point3(start_raw)
    end = _point3(end_raw)
    radius = max(0.0, _finite_number(radius_raw))
    if center is None or start is None or end is None or radius <= 0.0:
        return tuple(point for point in (start, end) if point is not None)
    start_angle = math.atan2(start[2] - center[2], start[0] - center[0])
    end_angle = math.atan2(end[2] - center[2], end[0] - center[0])
    delta = (end_angle - start_angle) % (2.0 * math.pi)
    direction_text = str(direction or "Shortest").strip().lower()
    direction_number = int(_finite_number(direction, -1)) if not isinstance(direction, str) else -1
    clockwise = direction_text in {"clockwise", "cw"} or direction_number == 1
    counter_clockwise = direction_text in {"counterclockwise", "counter-clockwise", "ccw"} or direction_number == 2
    if clockwise and delta > 0.0:
        delta -= 2.0 * math.pi
    elif not counter_clockwise and delta > math.pi:
        delta -= 2.0 * math.pi
    count = max(4, int(segment_count))
    return tuple((
        center[0] + radius * math.cos(start_angle + delta * index / count),
        start[1] + (end[1] - start[1]) * index / count,
        center[2] + radius * math.sin(start_angle + delta * index / count),
    ) for index in range(count + 1))


def _network_lane_lookup(network: Any) -> dict[str, Any]:
    result: dict[str, Any] = {}
    for edge in _items(_field(network, "edges", "Edges", default=[])):
        for lane in _items(_field(edge, "lanes", "Lanes", default=[])):
            lane_id = str(_field(lane, "lane_id", "LaneId", "id", default="") or "").strip()
            if lane_id and lane_id not in result:
                result[lane_id] = lane
    return result


def _build_facility_corridor_curve(corridor: Any, lane: Any, ramp: Any,
                                   vertices: Mapping[str, Any], circles: Mapping[str, Any],
                                   tangent_ratio: float = 0.3,
                                   segment_count: int = 36) -> tuple[tuple[float, float], ...]:
    """Sample the same tangent cubic Bezier used by Unity's 3D scene.

    Unlike an endpoint chord, the curve uses the linked lane tangent and the
    linked holding-ramp tangent.  Altitude participates in handle length and
    tangent direction before samples are projected onto X/Z for this map.
    """
    lane_point = _point3(_field(corridor, "lane_link_point", "LaneLinkPoint"))
    ramp_point = _point3(_field(corridor, "ramp_link_point", "RampLinkPoint"))
    if lane_point is None or ramp_point is None:
        return ()

    lane_line = _polyline3(_field(lane, "centerline", "Centerline", "points", "Points", default=[]))
    lane_tangent = _tangent_on_polyline3(lane_line, lane_point)

    ramp_line: tuple[tuple[float, float, float], ...] = ()
    if ramp is not None:
        from_id = str(_field(ramp, "from_vertex_id", "FromVertexId", default="") or "")
        to_id = str(_field(ramp, "to_vertex_id", "ToVertexId", default="") or "")
        start = _field(vertices.get(from_id, {}), "position", "Position")
        end = _field(vertices.get(to_id, {}), "position", "Position")
        circle_id = str(_field(ramp, "link_circle_id", "LinkCircleId", default="") or "")
        circle = circles.get(circle_id)
        if circle is None:
            ramp_line = tuple(point for point in (_point3(start), _point3(end)) if point is not None)
        else:
            ramp_line = _arc_points3(
                _field(circle, "center", "Center"), _field(circle, "radius", "Radius"),
                start, end, _field(ramp, "direction", "Direction", default="Shortest"), 48,
            )
    ramp_tangent = _tangent_on_polyline3(ramp_line, ramp_point)

    unity_forward = (0.0, 0.0, 1.0)
    if _vector_length(lane_tangent) < 1e-4:
        lane_tangent = unity_forward
    if _vector_length(ramp_tangent) < 1e-4:
        ramp_tangent = unity_forward

    entry = str(_field(corridor, "corridor_type", "CorridorType", default="") or "") == "Entry"
    p0, p3 = (ramp_point, lane_point) if entry else (lane_point, ramp_point)
    ratio = min(0.75, max(0.05, _finite_number(tangent_ratio, 0.3)))
    handle = _vector_length(_vector_subtract(p3, p0)) * ratio
    start_tangent = ramp_tangent if entry else lane_tangent
    end_tangent = lane_tangent if entry else ramp_tangent
    travel = _vector_subtract(p3, p0)
    if _vector_dot(start_tangent, travel) < 0.0:
        start_tangent = _vector_scale(start_tangent, -1.0)
    if _vector_dot(end_tangent, travel) < 0.0:
        end_tangent = _vector_scale(end_tangent, -1.0)
    p1 = _vector_add(p0, _vector_scale(_vector_normalize(start_tangent), handle))
    p2 = _vector_subtract(p3, _vector_scale(_vector_normalize(end_tangent), handle))

    count = max(8, int(segment_count))
    samples = (_evaluate_bezier3(p0, p1, p2, p3, index / count)
               for index in range(count + 1))
    return tuple((point[0], point[2]) for point in samples)


def _collect_intentionally_omitted_resource_ids(vertiports: Iterable[Any]) -> set[str]:
    """IDs with time windows which the focused path-only figure intentionally hides."""
    result: set[str] = set()

    def add(raw_id: Any) -> None:
        resource_id = str(raw_id or "").strip()
        if resource_id:
            result.add(resource_id)

    for vp in vertiports:
        for pad in _items(_field(vp, "helipads", "Helipads", default=[])):
            add(_field(pad, "helipad_id", "HelipadId", "id"))
        approach = _field(vp, "approach_net", "ApproachNet", default={})
        for node in _items(_field(approach, "nodes", "Nodes", default=[])):
            add(_field(node, "node_id", "NodeId", "id"))
        for holding in _items(_field(vp, "holding_structures", "HoldingStructures", default=[])):
            add(_field(holding, "holding_structure_id", "HoldingStructureId", "id"))
            for circle in _items(_field(holding, "circles", "Circles", default=[])):
                add(_field(circle, "circle_id", "CircleId", "id"))
            for vertex in _items(_field(holding, "vertices", "Vertices", default=[])):
                add(_field(vertex, "vertex_id", "VertexId", "id"))
    return result


def _collect_single_vertiport_geometry(vp: Any, reservations: Mapping[str, float],
                                       fallback_index: int, lane_lookup: Mapping[str, Any],
                                       tangent_ratio: float) -> VertiportGeometry:
    vertiport_id = str(_field(
        vp, "vertiport_id", "VertiportId", "id", "Id",
        default=f"Vertiport-{fallback_index + 1}",
    ) or f"Vertiport-{fallback_index + 1}").strip()
    display_name = str(_field(
        vp, "display_name", "DisplayName", "name", "Name", "title", "Title",
        default=vertiport_id,
    ) or vertiport_id).strip()
    group = VertiportGeometry(vertiport_id, display_name)

    def add(resource_id: Any, resource_type: str, points: Iterable[Any] | tuple[tuple[float, float], ...],
            *, closed: bool = False) -> None:
        rid = str(resource_id or "").strip()
        polyline = points if isinstance(points, tuple) else _polyline(points, close=closed)
        if not rid or len(polyline) < 2:
            return
        if closed and polyline[0] != polyline[-1]:
            polyline += (polyline[0],)
        group.resources.append(ResourceGeometry(
            rid, resource_type, tuple(polyline), max(0.0, reservations.get(rid, 0.0)), closed,
            vertiport_id,
        ))

    approach = _field(vp, "approach_net", "ApproachNet", "approach_network", "ApproachNetwork", default={})
    approach_nodes = {
        str(_field(node, "node_id", "NodeId", "id", default="")):
            _field(node, "position", "Position")
        for node in _items(_field(approach, "nodes", "Nodes", default=[]))
    }
    for edge in _items(_field(approach, "edges", "Edges", default=[])):
        from_id = str(_field(edge, "from_node_id", "FromNodeId", default=""))
        to_id = str(_field(edge, "to_node_id", "ToNodeId", default=""))
        add(_field(edge, "edge_id", "EdgeId", "id"), "Approach edge",
            [approach_nodes.get(from_id), approach_nodes.get(to_id)])

    all_vertices: dict[str, Any] = {}
    all_circles: dict[str, Any] = {}
    all_ramps: dict[str, Any] = {}
    for holding in _items(_field(vp, "holding_structures", "HoldingStructures", default=[])):
        vertices = {
            str(_field(vertex, "vertex_id", "VertexId", "id", default="")): vertex
            for vertex in _items(_field(holding, "vertices", "Vertices", default=[]))
        }
        all_vertices.update(vertices)
        circles = {
            str(_field(circle, "circle_id", "CircleId", "id", default="")): circle
            for circle in _items(_field(holding, "circles", "Circles", default=[]))
        }
        all_circles.update(circles)
        for ramp in _items(_field(holding, "ramps", "Ramps", default=[])):
            ramp_id = str(_field(ramp, "ramp_id", "RampId", "id", default="") or "")
            if ramp_id:
                all_ramps[ramp_id] = ramp
            from_id = str(_field(ramp, "from_vertex_id", "FromVertexId", default=""))
            to_id = str(_field(ramp, "to_vertex_id", "ToVertexId", default=""))
            start = _field(vertices.get(from_id, {}), "position", "Position")
            end = _field(vertices.get(to_id, {}), "position", "Position")
            circle_id = str(_field(ramp, "link_circle_id", "LinkCircleId", default="") or "")
            circle = circles.get(circle_id)
            if circle is not None:
                points = _arc_points(
                    _field(circle, "center", "Center"), _field(circle, "radius", "Radius"),
                    start, end, _field(ramp, "direction", "Direction", default="Shortest"),
                )
            else:
                points = _polyline([start, end])
            add(ramp_id, "Holding ramp", points)

    for corridor in _items(_field(vp, "corridors", "Corridors", default=[])):
        # These facility-to-network corridors are explicitly booked by PathPlanner.
        # Rebuild the same tangent cubic Bezier shown by RuntimeAirspaceGeometry;
        # a straight endpoint chord would misrepresent both path and heat location.
        lane_id = str(_field(corridor, "link_lane_id", "LinkLaneId", default="") or "")
        ramp_id = str(_field(corridor, "link_ramp_id", "LinkRampId", default="") or "")
        points = _build_facility_corridor_curve(
            corridor, lane_lookup.get(lane_id), all_ramps.get(ramp_id),
            all_vertices, all_circles, tangent_ratio,
        )
        add(_field(corridor, "corridor_id", "CorridorId", "id"),
            "Vertiport corridor", points)

    return group


def _collect_vertiport_geometry(vertiports: Iterable[Any], reservations: Mapping[str, float],
                                lane_lookup: Mapping[str, Any], tangent_ratio: float) -> tuple[
        list[ResourceGeometry], list[tuple[tuple[float, float], ...]], list[VertiportGeometry]]:
    groups = [
        _collect_single_vertiport_geometry(vp, reservations, index, lane_lookup, tangent_ratio)
        for index, vp in enumerate(vertiports)
    ]
    resources = [resource for group in groups for resource in group.resources]
    outlines = [outline for group in groups for outline in group.outlines]
    return resources, outlines, groups


def _collect_explicit_resources(request: Mapping[str, Any],
                                reservations: Mapping[str, float]) -> list[ResourceGeometry]:
    result: list[ResourceGeometry] = []
    explicit = _field(request, "resources", "Resources", default=[])
    for index, raw in enumerate(_items(explicit)):
        resource_id = str(_field(raw, "resource_id", "ResourceId", "id", default=f"resource-{index + 1}") or f"resource-{index + 1}")
        resource_type = str(_field(
            raw, "resource_type", "ResourceType", "type", "Type", default="Resource",
        ) or "Resource")
        type_key = "".join(character for character in resource_type.lower() if character.isalnum())
        if type_key not in {
                "approachedge", "holdingramp", "ramp", "vertiportcorridor",
                "facilitycorridor", "netconnectedcorridor"}:
            continue
        points = _polyline(_field(raw, "points", "Points", "centerline", "Centerline", default=[]),
                           close=_truthy(_field(raw, "closed", "Closed", default=False)))
        if len(points) < 2:
            continue
        embedded = _field(raw, "reservation_seconds", "ReservationSeconds", default=_MISSING)
        seconds = reservations.get(resource_id, 0.0) if embedded is _MISSING else max(0.0, _finite_number(embedded))
        result.append(ResourceGeometry(
            resource_id,
            resource_type,
            points,
            seconds,
            _truthy(_field(raw, "closed", "Closed", default=False)),
            str(_field(raw, "vertiport_id", "VertiportId", "owner_vertiport_id",
                       "OwnerVertiportId", default="") or "").strip(),
        ))
    return result


def prepare_traffic_data(request: Mapping[str, Any]) -> PreparedTrafficData:
    if not isinstance(request, Mapping):
        raise TypeError("Traffic heatmap request must be a JSON object.")
    kind = str(_field(request, "kind", "Kind", default=TRAFFIC_HEATMAP_KIND) or TRAFFIC_HEATMAP_KIND)
    if kind != TRAFFIC_HEATMAP_KIND:
        raise ValueError(f"Unsupported traffic heatmap request kind: {kind}")

    network = _field(request, "network", "Network", "full_network", "FullNetwork", default={})
    traversals = _numeric_map(_field(request, "lane_traversals", "LaneTraversals", default={}))
    reservations = _numeric_map(_field(
        request, "resource_reservation_seconds", "ResourceReservationSeconds", default={},
    ))
    lanes = _collect_lanes(request, network, traversals)
    layers = _layer_specs(network, (lane.layer_id for lane in lanes))

    visible_names = ("visible_layer_ids", "VisibleLayerIds")
    if _has_field(request, *visible_names):
        raw_visible = _field(request, *visible_names, default=[])
        visible = [str(item) for item in _items(raw_visible) if str(item).strip()]
    else:
        visible = [layer.layer_id for layer in layers if layer.enabled]
    visible = list(dict.fromkeys(visible))
    # Preserve authored stack order, then append any requested legacy IDs.
    requested = set(visible)
    visible = [layer.layer_id for layer in layers if layer.layer_id in requested] + [
        item for item in visible if item not in {layer.layer_id for layer in layers}
    ]

    vertiports = _items(_field(
        request, "vertiports", "Vertiports", "vertiport_snapshot", "VertiportSnapshot", default=[],
    ))
    lane_lookup = _network_lane_lookup(network)
    tangent_ratio = _finite_number(_field(
        request, "corridor_tangent_ratio", "CorridorTangentRatio", default=0.3,
    ), 0.3)
    resources, outlines, vertiport_groups = _collect_vertiport_geometry(
        vertiports, reservations, lane_lookup, tangent_ratio,
    )
    explicit_resources = _collect_explicit_resources(request, reservations)
    if explicit_resources:
        original_group_keys = {
            id(group): {(item.resource_id, item.resource_type) for item in group.resources}
            for group in vertiport_groups
        }
        owners_by_key: dict[tuple[str, str], set[str]] = {}
        for item in resources:
            owners_by_key.setdefault((item.resource_id, item.resource_type), set()).add(
                item.owner_vertiport_id
            )
        normalized_explicit: list[ResourceGeometry] = []
        for item in explicit_resources:
            owner = item.owner_vertiport_id
            candidate_owners = owners_by_key.get((item.resource_id, item.resource_type), set())
            if not owner and len(candidate_owners) == 1:
                owner = next(iter(candidate_owners))
            normalized_explicit.append(ResourceGeometry(
                item.resource_id, item.resource_type, item.points,
                item.reservation_seconds, item.closed, owner,
            ))
        explicit_resources = normalized_explicit
        explicit_keys = {(item.resource_id, item.resource_type) for item in explicit_resources}
        resources = [item for item in resources if (item.resource_id, item.resource_type) not in explicit_keys]
        resources.extend(explicit_resources)

        known_group_ids = {group.vertiport_id for group in vertiport_groups}
        for owner in dict.fromkeys(
                item.owner_vertiport_id for item in explicit_resources if item.owner_vertiport_id):
            if owner not in known_group_ids:
                vertiport_groups.append(VertiportGeometry(owner, owner))
                known_group_ids.add(owner)
        if any(not item.owner_vertiport_id for item in explicit_resources):
            unassigned_id = "Unassigned-resources"
            vertiport_groups.append(VertiportGeometry(unassigned_id, "Unassigned resources"))
            resources = [
                item if item.owner_vertiport_id else ResourceGeometry(
                    item.resource_id, item.resource_type, item.points,
                    item.reservation_seconds, item.closed, unassigned_id,
                )
                for item in resources
            ]
        for group in vertiport_groups:
            allowed_keys = set(original_group_keys.get(id(group), set()))
            allowed_keys.update(
                (item.resource_id, item.resource_type)
                for item in explicit_resources
                if (item.owner_vertiport_id or "Unassigned-resources") == group.vertiport_id
            )
            group.resources = [
                item for item in resources
                if item.owner_vertiport_id == group.vertiport_id and
                (item.resource_id, item.resource_type) in allowed_keys
            ]

    drawable_resource_ids = {item.resource_id for item in resources}
    loaded_resource_ids = {resource_id for resource_id, seconds in reservations.items() if seconds > 0.0}
    intentionally_omitted_resource_ids = _collect_intentionally_omitted_resource_ids(vertiports)
    missing_loaded_resource_ids = sorted(
        loaded_resource_ids - drawable_resource_ids - intentionally_omitted_resource_ids
    )

    notes: list[str] = []
    omitted = len(lanes) - sum(1 for lane in lanes if lane.layer_id in set(visible))
    if omitted > 0:
        notes.append(f"{omitted} lane(s) hidden by the altitude-layer filter")
    if missing_loaded_resource_ids:
        notes.append(
            f"{len(missing_loaded_resource_ids)} loaded resource ID(s) lack drawable geometry; "
            "see response warnings"
        )
    notes.append("Lane-to-lane connection corridors are intentionally excluded")
    notes.append("Vertiport pages show approach edges, holding ramps, and Bezier facility corridors only")
    return PreparedTrafficData(
        title=str(_field(request, "title", "Title", default="Traffic Load Heatmap") or "Traffic Load Heatmap"),
        layers=layers,
        visible_layer_ids=visible,
        lanes=lanes,
        resources=resources,
        facility_outlines=outlines,
        vertiport_groups=vertiport_groups,
        missing_loaded_resource_ids=missing_loaded_resource_ids,
        notes=notes,
    )


def _normalization(values: Iterable[float]) -> Normalize:
    maximum = max([max(0.0, _finite_number(value)) for value in values] + [0.0])
    return Normalize(vmin=0.0, vmax=maximum if maximum > 0.0 else 1.0)


def _format_number(value: float, _position: int | None = None) -> str:
    if abs(value - round(value)) < 1e-8:
        return str(int(round(value)))
    if abs(value) >= 1000.0:
        return f"{value:,.0f}"
    return f"{value:.2f}".rstrip("0").rstrip(".")


def _style_map_axis(axis: plt.Axes, has_geometry: bool = True) -> None:
    # A square axes box keeps layer facets visually aligned.  Equal data aspect
    # is retained by expanding the shorter coordinate range, never by stretching
    # the authored geometry.
    axis.set_box_aspect(1.0)
    if has_geometry:
        axis.set_aspect("equal", adjustable="datalim")
    else:
        axis.set_aspect("auto")
    axis.set_xlabel("X (east, m)", fontweight="bold")
    axis.set_ylabel("Y (north, m)", fontweight="bold")
    axis.grid(True, color="#DCE3E8", linewidth=0.55, alpha=0.75, zorder=0)
    axis.set_axisbelow(True)
    axis.xaxis.set_major_locator(MaxNLocator(nbins=6))
    axis.yaxis.set_major_locator(MaxNLocator(nbins=6))
    axis.xaxis.set_major_formatter(FuncFormatter(_format_number))
    axis.yaxis.set_major_formatter(FuncFormatter(_format_number))
    axis.tick_params(direction="out", length=3.0, width=0.7, colors="#43515B")
    for spine in axis.spines.values():
        spine.set_color("#AEB8BF")
        spine.set_linewidth(0.75)


def _layer_title(layer: LayerSpec | None, lane_count: int) -> str:
    if layer is None:
        return "Road network — selected layers hidden"
    altitude = "" if layer.altitude is None else f" · {layer.altitude:g} m"
    return f"Road network — {layer.display_name}{altitude}\n{lane_count} lane centerline(s)"


def _draw_network_panel(axis: plt.Axes, layer: LayerSpec | None,
                        lanes: list[LaneGeometry], norm: Normalize, cmap: Any) -> None:
    axis.set_facecolor("#FBFCFD")
    if not lanes:
        axis.text(0.5, 0.5, "No lane centerlines in this visible layer",
                  transform=axis.transAxes, ha="center", va="center",
                  color="#6C7880", fontsize=11, fontweight="bold")
        axis.set_xlim(0.0, 1.0)
        axis.set_ylim(0.0, 1.0)
    else:
        segments = [list(lane.points) for lane in lanes]
        values = [lane.traversals for lane in lanes]
        axis.add_collection(LineCollection(
            segments, colors="#CBD2D7", linewidths=3.0, alpha=0.82,
            capstyle="round", joinstyle="round", zorder=1,
        ))
        axis.add_collection(LineCollection(
            segments, array=values, cmap=cmap, norm=norm, linewidths=1.75,
            alpha=0.98, capstyle="round", joinstyle="round", zorder=2,
        ))
        axis.autoscale_view(tight=True)
        axis.margins(0.035)
    axis.set_title(_layer_title(layer, len(lanes)), pad=10, fontweight="bold", color="#26343D")
    _style_map_axis(axis, bool(lanes))


def _draw_resource_panel(axis: plt.Axes, resources: list[ResourceGeometry],
                         outlines: list[tuple[tuple[float, float], ...]],
                         norm: Normalize, cmap: Any,
                         panel_title: str | None = None) -> None:
    axis.set_facecolor("#FBFCFD")
    if outlines:
        axis.add_collection(LineCollection(
            [list(points) for points in outlines], colors="#2D3941", linewidths=1.35,
            linestyles="--", alpha=0.72, zorder=1,
        ))
    if resources:
        segments = [list(item.points) for item in resources]
        values = [item.reservation_seconds for item in resources]
        axis.add_collection(LineCollection(
            segments, colors="#CBD2D7", linewidths=4.2, alpha=0.82,
            capstyle="round", joinstyle="round", zorder=2,
        ))
        axis.add_collection(LineCollection(
            segments, array=values, cmap=cmap, norm=norm, linewidths=2.35,
            alpha=0.99, capstyle="round", joinstyle="round", zorder=3,
        ))
        axis.autoscale_view(tight=True)
        axis.margins(0.06)
    elif outlines:
        axis.autoscale_view(tight=True)
        axis.margins(0.06)
        axis.text(0.5, 0.04, "No reservable path-segment geometry was available",
                  transform=axis.transAxes, ha="center", va="bottom", color="#6C7880")
    else:
        axis.text(0.5, 0.5, "No approach-edge, holding-ramp, or corridor geometry available",
                  transform=axis.transAxes, ha="center", va="center",
                  color="#6C7880", fontsize=11, fontweight="bold")
        axis.set_xlim(0.0, 1.0)
        axis.set_ylim(0.0, 1.0)
    axis.set_title(panel_title or
                   f"Vertiport reservable paths\n{len(resources)} drawable segment(s)",
                   pad=10, fontweight="bold", color="#26343D")
    _style_map_axis(axis, bool(resources or outlines))


def _geometry_bounds(resources: Iterable[ResourceGeometry],
                     outlines: Iterable[tuple[tuple[float, float], ...]]) -> tuple[float, float, float, float] | None:
    points = [
        point
        for geometry in [*(item.points for item in resources), *outlines]
        for point in geometry
    ]
    if not points:
        return None
    xs = [point[0] for point in points]
    ys = [point[1] for point in points]
    return min(xs), max(xs), min(ys), max(ys)


def _padded_square_bounds(bounds: tuple[float, float, float, float] | None,
                          padding_ratio: float = 0.09) -> tuple[float, float, float, float] | None:
    if bounds is None:
        return None
    minimum_x, maximum_x, minimum_y, maximum_y = bounds
    center_x = (minimum_x + maximum_x) * 0.5
    center_y = (minimum_y + maximum_y) * 0.5
    span = max(maximum_x - minimum_x, maximum_y - minimum_y, 10.0)
    half_span = span * (0.5 + max(0.0, padding_ratio))
    return (
        center_x - half_span, center_x + half_span,
        center_y - half_span, center_y + half_span,
    )


def _apply_bounds(axis: plt.Axes, bounds: tuple[float, float, float, float] | None) -> None:
    if bounds is None:
        return
    axis.set_xlim(bounds[0], bounds[1])
    axis.set_ylim(bounds[2], bounds[3])


def _style_colorbar(colorbar: Any, label: str) -> None:
    colorbar.set_label(label, fontweight="bold", fontsize=11, labelpad=7)
    colorbar.ax.tick_params(labelsize=9, length=2.5, width=0.65, colors="#43515B")
    colorbar.ax.xaxis.set_major_formatter(FuncFormatter(_format_number))
    colorbar.outline.set_linewidth(0.65)
    colorbar.outline.set_edgecolor("#8E9AA2")


def _plot_style() -> dict[str, Any]:
    return {
        "font.family": "sans-serif",
        "font.sans-serif": [
            "Microsoft YaHei", "SimHei", "Noto Sans CJK SC", "DejaVu Sans",
        ],
        "font.size": 10,
        "axes.titlesize": 12,
        "axes.labelsize": 10.5,
        "xtick.labelsize": 9,
        "ytick.labelsize": 9,
        "figure.facecolor": "white",
        "savefig.facecolor": "white",
    }


def _warning_metadata(data: PreparedTrafficData) -> list[str]:
    if not data.missing_loaded_resource_ids:
        return []
    return [
        "Reservation load has no drawable geometry for resource ID(s): " +
        ", ".join(data.missing_loaded_resource_ids)
    ]


def _unique_page_id(base: str, used: set[str]) -> str:
    candidate = base or "page"
    suffix = 2
    while candidate in used:
        candidate = f"{base}-{suffix}"
        suffix += 1
    used.add(candidate)
    return candidate


def draw_traffic_heatmap_pages(request: Mapping[str, Any]) -> tuple[
        list[tuple[plt.Figure, dict[str, Any]]], dict[str, Any]]:
    """Create one full-size figure per network layer and per vertiport.

    Vertiport pages are grouped by the archived ``Vertiport`` object's own
    child collections.  No spatial-nearest-neighbour clustering is performed,
    so neighboring facilities cannot be accidentally merged.
    """
    data = prepare_traffic_data(request)
    layer_lookup = {layer.layer_id: layer for layer in data.layers}
    selected_layers = [layer_lookup.get(layer_id, LayerSpec(layer_id, layer_id))
                       for layer_id in data.visible_layer_ids]
    if not selected_layers:
        selected_layers = [None]

    cmap = matplotlib.colormaps.get_cmap("RdYlGn_r")
    lane_norm = _normalization(lane.traversals for lane in data.lanes
                               if lane.layer_id in set(data.visible_layer_ids))
    resource_norm = _normalization(item.reservation_seconds for item in data.resources)
    pages: list[tuple[plt.Figure, dict[str, Any]]] = []
    used_page_ids: set[str] = set()

    with plt.rc_context(_plot_style()):
        for layer in selected_layers:
            lanes = [] if layer is None else [
                lane for lane in data.lanes if lane.layer_id == layer.layer_id
            ]
            figure, axis = plt.subplots(figsize=(12.8, 9.2))
            _draw_network_panel(axis, layer, lanes, lane_norm, cmap)
            figure.suptitle(data.title, fontsize=17, fontweight="bold",
                            color="#1D2B34", y=0.985)
            figure.subplots_adjust(left=0.085, right=0.965, top=0.88, bottom=0.20)
            lane_bar = figure.colorbar(
                ScalarMappable(norm=lane_norm, cmap=cmap), ax=[axis],
                orientation="horizontal", fraction=0.045, pad=0.10, aspect=48,
            )
            _style_colorbar(lane_bar, "Lane traversals")
            figure.text(0.5, 0.022, " · ".join(data.notes), ha="center", va="bottom",
                        fontsize=8.5, color="#66737B")
            layer_id = layer.layer_id if layer is not None else "hidden"
            page_id = _unique_page_id(f"network-{layer_id}", used_page_ids)
            pages.append((figure, {
                "page_id": page_id,
                "kind": "network-layer",
                "page_type": "network-layer",
                "title": _layer_title(layer, len(lanes)).replace("\n", " — "),
                "layer_id": None if layer is None else layer.layer_id,
                "visible_layer_ids": [] if layer is None else [layer.layer_id],
                "lane_count": len(lanes),
                "resource_count": 0,
                "suggested_columns": 1,
            }))

        for group in data.vertiport_groups:
            figure, axis = plt.subplots(figsize=(11.8, 9.2))
            identity = (
                group.vertiport_id if group.display_name == group.vertiport_id
                else f"{group.display_name} · {group.vertiport_id}"
            )
            _draw_resource_panel(
                axis, group.resources, group.outlines, resource_norm, cmap,
                panel_title=(
                    f"Vertiport — {identity}\n"
                    f"{len(group.resources)} reservable path segment(s)"
                ),
            )
            data_bounds = _geometry_bounds(group.resources, group.outlines)
            plot_bounds = _padded_square_bounds(data_bounds)
            _apply_bounds(axis, plot_bounds)
            figure.suptitle(data.title, fontsize=17, fontweight="bold",
                            color="#1D2B34", y=0.985)
            figure.subplots_adjust(left=0.09, right=0.96, top=0.88, bottom=0.20)
            resource_bar = figure.colorbar(
                ScalarMappable(norm=resource_norm, cmap=cmap), ax=[axis],
                orientation="horizontal", fraction=0.045, pad=0.10, aspect=48,
            )
            _style_colorbar(resource_bar, "Reservation time (s)")
            figure.text(
                0.5, 0.022,
                "Reservation scale is shared across all vertiport pages · "
                "approach edges, holding ramps, and Bezier facility corridors only",
                ha="center", va="bottom", fontsize=8.5, color="#66737B",
            )
            page_id = _unique_page_id(f"vertiport-{group.vertiport_id}", used_page_ids)
            page_type = (
                "resource-group" if group.vertiport_id == "Unassigned-resources"
                else "vertiport"
            )
            pages.append((figure, {
                "page_id": page_id,
                "kind": page_type,
                "page_type": page_type,
                "title": f"Vertiport — {identity}",
                "vertiport_id": None if page_type == "resource-group" else group.vertiport_id,
                "display_name": group.display_name,
                "lane_count": 0,
                "resource_count": len(group.resources),
                "grouping_basis": "vertiport-object-containment",
                "data_bounds": list(data_bounds) if data_bounds is not None else None,
                "plot_bounds": list(plot_bounds) if plot_bounds is not None else None,
                "suggested_columns": 1,
            }))

    for index, (_, page_metadata) in enumerate(pages):
        page_metadata["page_number"] = index + 1
        page_metadata["page_count"] = len(pages)

    warnings = _warning_metadata(data)
    visible_lane_count = sum(
        1 for lane in data.lanes if lane.layer_id in set(data.visible_layer_ids)
    )
    network_page_count = len(selected_layers)
    vertiport_page_count = len(data.vertiport_groups)
    missing_caption = (
        f" · {len(data.missing_loaded_resource_ids)} loaded resource ID(s) lacked geometry"
        if data.missing_loaded_resource_ids else ""
    )
    metadata = {
        "success": True,
        "kind": TRAFFIC_HEATMAP_KIND,
        "page_manifest_version": PAGE_MANIFEST_VERSION,
        "page_output_schema": "sar.traffic-heatmap.pages/1",
        "page_count": len(pages),
        "panel_count": len(pages),
        "network_page_count": network_page_count,
        "vertiport_page_count": vertiport_page_count,
        "visible_layer_ids": list(data.visible_layer_ids),
        "visible_lane_count": visible_lane_count,
        "resource_count": len(data.resources),
        "missing_loaded_resource_ids": list(data.missing_loaded_resource_ids),
        "warnings": warnings,
        "suggested_layout": {
            "default_columns": 1,
            "optional_columns": 2,
            "ordering": "network layers, then vertiports",
            "clarity_priority": True,
        },
        "pages": [page_metadata.copy() for _, page_metadata in pages],
        "caption": (
            f"{network_page_count} full-size network page(s) · "
            f"{vertiport_page_count} vertiport page(s) · "
            f"{visible_lane_count} visible lane(s) · "
            f"{len(data.resources)} reservation resource(s)"
            f"{missing_caption}"
        ),
    }
    return pages, metadata


def draw_traffic_heatmap(request: Mapping[str, Any]) -> tuple[plt.Figure, dict[str, Any]]:
    """Compatibility helper returning the first page and collection metadata."""
    pages, metadata = draw_traffic_heatmap_pages(request)
    if not pages:
        raise ValueError("Traffic heatmap did not produce any pages.")
    for figure, _ in pages[1:]:
        plt.close(figure)
    return pages[0][0], metadata


def _save_figure(figure: plt.Figure, output_path: str, request: Mapping[str, Any]) -> None:
    absolute = os.path.abspath(output_path)
    os.makedirs(os.path.dirname(absolute), exist_ok=True)
    dpi = int(min(600, max(150, _finite_number(_field(request, "dpi", "Dpi", default=DEFAULT_DPI), DEFAULT_DPI))))
    figure.savefig(absolute, dpi=dpi, bbox_inches="tight", pad_inches=0.10,
                   facecolor="white", metadata={"Software": "SAR Python analytics worker"})


def _filename_slug(value: Any) -> str:
    text = str(value or "page").strip().lower()
    slug = "".join(character if character.isascii() and character.isalnum() else "-"
                   for character in text)
    slug = "-".join(part for part in slug.split("-") if part)
    return (slug or "page")[:80]


def _page_output_path(primary_output_path: str, page_index: int,
                      page_metadata: Mapping[str, Any]) -> str:
    primary = Path(os.path.abspath(primary_output_path))
    if page_index == 0:
        return str(primary)
    suffix = primary.suffix or ".png"
    page_slug = _filename_slug(_field(page_metadata, "page_id", "kind", default="page"))
    return str(primary.with_name(
        f"{primary.stem}--{page_index + 1:02d}-{page_slug}{suffix}"
    ))


def render_traffic_heatmap(request: Mapping[str, Any], output_path: str) -> dict[str, Any]:
    if not output_path:
        raise ValueError("Traffic heatmap output path is required.")
    pages, metadata = draw_traffic_heatmap_pages(request)
    if not pages:
        raise ValueError("Traffic heatmap did not produce any pages.")
    rendered_pages: list[dict[str, Any]] = []
    try:
        for index, (figure, page_metadata) in enumerate(pages):
            page_path = _page_output_path(output_path, index, page_metadata)
            _save_figure(figure, page_path, request)
            rendered = page_metadata.copy()
            rendered["output_path"] = page_path
            rendered_pages.append(rendered)
    finally:
        for figure, _ in pages:
            plt.close(figure)
    metadata["pages"] = rendered_pages
    metadata["output_path"] = rendered_pages[0]["output_path"]
    return metadata


def _write_response(path: str | None, response: Mapping[str, Any]) -> None:
    text = json.dumps(response, ensure_ascii=False, indent=2)
    if path:
        Path(path).parent.mkdir(parents=True, exist_ok=True)
        Path(path).write_text(text, encoding="utf-8")
    print(text)


def main(argv: Sequence[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="Render an SAR traffic heatmap PNG.")
    parser.add_argument("--request", required=True)
    parser.add_argument("--output", required=True)
    parser.add_argument("--response")
    args = parser.parse_args(argv)
    try:
        with open(args.request, "r", encoding="utf-8-sig") as stream:
            request = json.load(stream)
        response = render_traffic_heatmap(request, args.output)
        _write_response(args.response, response)
        return 0
    except Exception as exc:  # pragma: no cover - exercised by packaged worker smoke test
        response = {
            "success": False,
            "kind": TRAFFIC_HEATMAP_KIND,
            "error": str(exc),
            "traceback": traceback.format_exc(),
            "output_path": "",
        }
        _write_response(args.response, response)
        return 1


if __name__ == "__main__":
    sys.exit(main())
