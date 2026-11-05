"""Independent F11 state and whole-Sketch Section oracles; no NX calls."""
from __future__ import annotations

import math
from typing import Any


UF_REFERENCE_STATUS = {"reference": 0, "active": 1}
DIMENSION_STATE = {"reference": 1, "active": 0}


class OracleError(AssertionError):
    """An F11 direct-readback record did not satisfy its independent oracle."""


def _required(row: dict[str, Any], key: str, label: str) -> Any:
    if key not in row:
        raise OracleError(f"{label} missing required field {key!r}")
    return row[key]


def _finite(value: Any, label: str) -> float:
    if isinstance(value, bool) or not isinstance(value, (int, float)) or not math.isfinite(float(value)):
        raise OracleError(f"{label} must be a finite number")
    return float(value)


def _numeric_expression_value(value: Any, label: str) -> float:
    if isinstance(value, bool):
        raise OracleError(f"{label} must be a numeric expression value")
    if isinstance(value, str):
        try:
            parsed = float(value.strip())
        except ValueError as exc:
            raise OracleError(f"{label} must be a numeric expression, got {value!r}") from exc
        if not math.isfinite(parsed):
            raise OracleError(f"{label} must be finite")
        return parsed
    return _finite(value, label)


def _distance(a: list[Any], b: list[Any], label: str) -> float:
    if not isinstance(a, list) or not isinstance(b, list) or len(a) != 3 or len(b) != 3:
        raise OracleError(f"{label} points must each contain three coordinates")
    av = [_finite(item, label) for item in a]
    bv = [_finite(item, label) for item in b]
    return math.sqrt(math.fsum((av[index] - bv[index]) ** 2 for index in range(3)))


def _unit(value: list[Any], label: str) -> list[float]:
    if not isinstance(value, list) or len(value) != 3:
        raise OracleError(f"{label} vector must contain three coordinates")
    vector = [_finite(item, label) for item in value]
    norm = math.hypot(*vector)
    if norm == 0:
        raise OracleError(f"{label} vector must be nonzero")
    return [item / norm for item in vector]


def _normal_residual(a: list[Any], b: list[Any], label: str) -> float:
    av, bv = _unit(a, label), _unit(b, label)
    # A circle's normal is an unoriented plane axis, so sign does not change its locus.
    dot = min(1.0, max(-1.0, abs(math.fsum(av[index] * bv[index] for index in range(3)))))
    return math.acos(dot)


def _normalized_state(row: dict[str, Any], label: str) -> tuple[str, int | str]:
    raw_kind = _required(row, "object_kind", label)
    if not isinstance(raw_kind, str):
        raise OracleError(f"{label} object_kind must be a string")
    kind = raw_kind.lower()
    if kind == "curve":
        key = "reference_status"
        raw = _required(row, key, label)
        if (type(raw) is int and raw == UF_REFERENCE_STATUS["reference"]) or raw in ("Reference", "REFERENCE"):
            return "curve", "reference"
        if (type(raw) is int and raw == UF_REFERENCE_STATUS["active"]) or raw in ("Active", "ACTIVE"):
            return "curve", "active"
        raise OracleError(f"{label} reference_status is not UF Reference(0) or Active(1): {raw!r}")
    if kind == "dimension":
        key = "dimension_state"
        raw = _required(row, key, label)
        if (type(raw) is int and raw == DIMENSION_STATE["reference"]) or raw in ("Reference", "REFERENCE"):
            return "dimension", "reference"
        if (type(raw) is int and raw == DIMENSION_STATE["active"]) or raw in ("Driving", "DRIVING", "Active", "ACTIVE"):
            return "dimension", "active"
        raise OracleError(f"{label} dimension_state is not Reference(1) or Driving(0): {raw!r}")
    raise OracleError(f"{label} object_kind must be 'curve' or 'dimension', got {kind!r}")


def _index_records(rows: list[dict[str, Any]], label: str) -> dict[str, dict[str, Any]]:
    if not isinstance(rows, list):
        raise OracleError(f"{label} must be a list")
    indexed: dict[str, dict[str, Any]] = {}
    for index, row in enumerate(rows):
        if not isinstance(row, dict):
            raise OracleError(f"{label}[{index}] must be an object")
        tag = _required(row, "tag", f"{label}[{index}]")
        if not isinstance(tag, str) or not tag.strip():
            raise OracleError(f"{label}[{index}] Tag must be a nonempty string")
        if tag in indexed:
            raise OracleError(f"{label} contains duplicate Tag {tag!r}")
        indexed[tag] = row
    return indexed


def _validate_curve_geometry(row: dict[str, Any], label: str) -> None:
    geometry = _required(row, "geometry", label)
    if not isinstance(geometry, dict):
        raise OracleError(f"{label}.geometry must be an object")
    kind = _required(geometry, "kind", f"{label}.geometry")
    if not isinstance(kind, str):
        raise OracleError(f"{label}.geometry.kind must be a string")
    kind = kind.upper()
    if kind == "LINE":
        endpoints = _required(geometry, "endpoints_world_mm", f"{label}.geometry")
        if not isinstance(endpoints, list) or len(endpoints) != 2:
            raise OracleError(f"{label}.geometry.endpoints_world_mm must contain two 3D points")
        for point in endpoints:
            if not isinstance(point, list) or len(point) != 3:
                raise OracleError(f"{label}.geometry LINE endpoints must be 3D points")
            for component in point:
                _finite(component, f"{label}.geometry LINE endpoint")
        return
    if kind in {"ARC", "CIRCLE"}:
        center = _required(geometry, "center_world_mm", f"{label}.geometry")
        if not isinstance(center, list) or len(center) != 3:
            raise OracleError(f"{label}.geometry center_world_mm must be a 3D point")
        for component in center:
            _finite(component, f"{label}.geometry center")
        radius = _finite(_required(geometry, "radius_mm", f"{label}.geometry"), f"{label}.geometry radius")
        if radius <= 0:
            raise OracleError(f"{label}.geometry radius_mm must be positive")
        normal = _required(geometry, "normal_world", f"{label}.geometry")
        _unit(normal, f"{label}.geometry normal")
        if kind == "ARC":
            full = _required(geometry, "full_circle", f"{label}.geometry")
            if type(full) is not bool:
                raise OracleError(f"{label}.geometry full_circle must be a boolean")
            if not full:
                endpoints = _required(geometry, "endpoints_world_mm", f"{label}.geometry")
                if not isinstance(endpoints, list) or len(endpoints) != 2:
                    raise OracleError(f"{label}.geometry arc endpoints must contain two 3D points")
                for point in endpoints:
                    if not isinstance(point, list) or len(point) != 3:
                        raise OracleError(f"{label}.geometry arc endpoints must be 3D points")
                    for component in point:
                        _finite(component, f"{label}.geometry arc endpoint")
                midpoint = _required(geometry, "midpoint_world_mm", f"{label}.geometry")
                if not isinstance(midpoint, list) or len(midpoint) != 3:
                    raise OracleError(f"{label}.geometry midpoint_world_mm must be a 3D point")
                for component in midpoint:
                    _finite(component, f"{label}.geometry arc midpoint")
                sweep = _finite(_required(geometry, "sweep_rad", f"{label}.geometry"), f"{label}.geometry sweep")
                if not 0.0 < abs(sweep) < 2.0 * math.pi:
                    raise OracleError(f"{label}.geometry sweep_rad must be nonzero and less than one full turn")
        return
    if kind in {"ELLIPSE", "ELLIPTICAL_ARC"}:
        center = _required(geometry, "center_world_mm", f"{label}.geometry")
        if not isinstance(center, list) or len(center) != 3:
            raise OracleError(f"{label}.geometry center_world_mm must be a 3D point")
        major = _finite(_required(geometry, "major_radius_mm", f"{label}.geometry"), f"{label}.geometry major radius")
        minor = _finite(_required(geometry, "minor_radius_mm", f"{label}.geometry"), f"{label}.geometry minor radius")
        if major <= 0 or minor <= 0 or major < minor:
            raise OracleError(f"{label}.geometry ellipse radii must be positive and major >= minor")
        _unit(_required(geometry, "normal_world", f"{label}.geometry"), f"{label}.geometry normal")
        _unit(_required(geometry, "major_axis_world", f"{label}.geometry"), f"{label}.geometry major axis")
        if kind == "ELLIPTICAL_ARC":
            for key in ("start_world_mm", "end_world_mm", "midpoint_world_mm"):
                point = _required(geometry, key, f"{label}.geometry")
                if not isinstance(point, list) or len(point) != 3:
                    raise OracleError(f"{label}.geometry {key} must be a 3D point")
                for component in point:
                    _finite(component, f"{label}.geometry {key}")
        return
    if kind == "SPLINE":
        points = _required(geometry, "sample_points_world_mm", f"{label}.geometry")
        if not isinstance(points, list) or len(points) < 4:
            raise OracleError(f"{label}.geometry spline requires at least four sampled 3D points")
        for point in points:
            if not isinstance(point, list) or len(point) != 3:
                raise OracleError(f"{label}.geometry spline samples must be 3D points")
            for component in point:
                _finite(component, f"{label}.geometry spline sample")
        return
    raise OracleError(f"{label}.geometry.kind is unsupported by the independent oracle: {kind!r}")


def _same_value(a: Any, b: Any, label: str, tolerance: float) -> None:
    if isinstance(a, bool) or isinstance(b, bool):
        if a is not b:
            raise OracleError(f"{label} changed: {a!r} -> {b!r}")
        return
    if isinstance(a, (int, float)) and isinstance(b, (int, float)):
        av, bv = float(a), float(b)
        if not math.isfinite(av) or not math.isfinite(bv) or abs(av - bv) > tolerance:
            raise OracleError(f"{label} changed by {abs(av - bv)!r}: {a!r} -> {b!r}")
        return
    if type(a) is not type(b):
        raise OracleError(f"{label} type changed: {type(a).__name__} -> {type(b).__name__}")
    if isinstance(a, dict):
        if set(a) != set(b):
            raise OracleError(f"{label} keys changed: {sorted(a)} -> {sorted(b)}")
        for key in sorted(a):
            _same_value(a[key], b[key], f"{label}.{key}", tolerance)
        return
    if isinstance(a, list):
        if len(a) != len(b):
            raise OracleError(f"{label} item count changed: {len(a)} -> {len(b)}")
        for index, (av, bv) in enumerate(zip(a, b)):
            _same_value(av, bv, f"{label}[{index}]", tolerance)
        return
    if a != b:
        raise OracleError(f"{label} changed: {a!r} -> {b!r}")


def verify_object_transition(
    before_rows: list[dict[str, Any]],
    after_rows: list[dict[str, Any]],
    selected_object_ids: list[str],
    target_state: str,
    *,
    numeric_tolerance: float = 1.0e-12,
    allowed_expression_rhs_updates: dict[str, float] | None = None,
) -> dict[str, Any]:
    """Verify selected states and preservation using Tag as identity, JID as locator.

    Each row has `tag`, current `journal_id`, `object_kind` (`curve` or `dimension`),
    `reference_status` (curve: 0 Reference / 1 Active) or `dimension_state`
    (dimension: 0 Driving / 1 Reference), and an opaque `geometry`/`expression` payload.
    `journal_id` may change across reads; Tag identity may not.
    """
    if not isinstance(target_state, str) or target_state not in UF_REFERENCE_STATUS:
        raise OracleError(f"target_state must be 'reference' or 'active', got {target_state!r}")
    numeric_tolerance = _finite(numeric_tolerance, "numeric_tolerance")
    if numeric_tolerance < 0:
        raise OracleError("numeric_tolerance must be finite and nonnegative")
    if not isinstance(selected_object_ids, list) or not selected_object_ids:
        raise OracleError("selected_object_ids must be a nonempty explicit list")
    if any(not isinstance(item, str) or not item.strip() for item in selected_object_ids):
        raise OracleError("selected_object_ids must contain nonempty strings")
    if len(selected_object_ids) != len(set(selected_object_ids)):
        raise OracleError("selected_object_ids must be unique")
    if allowed_expression_rhs_updates is None:
        allowed_expression_rhs_updates = {}
    if not isinstance(allowed_expression_rhs_updates, dict):
        raise OracleError("allowed_expression_rhs_updates must map selected dimension Tags to numeric ComputedSize values")
    for tag, value in allowed_expression_rhs_updates.items():
        if not isinstance(tag, str) or not tag.strip():
            raise OracleError("allowed expression-sync Tag must be a nonempty string")
        _finite(value, f"allowed expression-sync value for Tag {tag}")
    before = _index_records(before_rows, "before_objects")
    after = _index_records(after_rows, "after_objects")
    if set(before) != set(after):
        raise OracleError(f"object Tag inventory changed: {sorted(before)} -> {sorted(after)}")

    id_to_tag: dict[str, str] = {}
    for tag, row in before.items():
        object_id = _required(row, "journal_id", f"before Tag {tag}")
        if not isinstance(object_id, str) or not object_id.strip():
            raise OracleError(f"before Tag {tag} Journal ID must be a nonempty string")
        if object_id in id_to_tag:
            raise OracleError(f"before object inventory maps duplicate journal ID {object_id!r}")
        id_to_tag[object_id] = tag
    missing_ids = sorted(set(selected_object_ids) - set(id_to_tag))
    if missing_ids:
        raise OracleError(f"selected IDs are not current before-snapshot JIDs: {missing_ids}")
    selected_tags = {id_to_tag[object_id] for object_id in selected_object_ids}
    unknown_sync_tags = sorted(set(allowed_expression_rhs_updates) - selected_tags)
    if unknown_sync_tags:
        raise OracleError(f"expression sync allowlist includes unselected Tags: {unknown_sync_tags}")
    rows_evidence: list[dict[str, Any]] = []

    for tag in sorted(before):
        old, new = before[tag], after[tag]
        old_kind, old_state = _normalized_state(old, f"before Tag {tag}")
        new_kind, new_state = _normalized_state(new, f"after Tag {tag}")
        new_jid = _required(new, "journal_id", f"after Tag {tag}")
        if not isinstance(new_jid, str) or not new_jid.strip():
            raise OracleError(f"after Tag {tag} Journal ID must be a nonempty string")
        if old_kind != new_kind:
            raise OracleError(f"Tag {tag} object kind changed: {old_kind} -> {new_kind}")
        if old_kind == "curve":
            _validate_curve_geometry(old, f"before Tag {tag}")
            _validate_curve_geometry(new, f"after Tag {tag}")
        else:
            for key in ("expression_tag", "expression_name", "expression_rhs", "dimension_value",
                        "dimension_unit", "expression_flags", "geometry_refs", "constraint_type"):
                value = _required(old, key, f"before Tag {tag}")
                _required(new, key, f"after Tag {tag}")
                if key in {"expression_tag", "expression_name"} and (not isinstance(value, str) or not value.strip()):
                    raise OracleError(f"before Tag {tag} {key} must be a nonempty string")
                if key == "expression_rhs" and (isinstance(value, bool) or not isinstance(value, (str, int, float))):
                    raise OracleError(f"before Tag {tag} expression_rhs must be text or numeric")
                if key == "dimension_value":
                    _finite(value, f"before Tag {tag} dimension_value")
                if key == "dimension_unit" and value != "mm":
                    raise OracleError(f"before Tag {tag} supported dimension unit must be mm")
                if key == "expression_flags" and (not isinstance(value, dict) or not value or
                        any(not isinstance(flag, str) or type(flag_value) is not bool
                            for flag, flag_value in value.items())):
                    raise OracleError(f"before Tag {tag} expression_flags must be a nonempty boolean map")
                if key == "geometry_refs" and (not isinstance(value, list) or not value
                        or any(not isinstance(item, str) or not item.strip() for item in value)):
                    raise OracleError(f"before Tag {tag} geometry_refs must contain referenced geometry IDs")
                if key == "constraint_type" and (not isinstance(value, str) or not value.strip()):
                    raise OracleError(f"before Tag {tag} constraint_type must be a nonempty string")
                if key == "constraint_type" and value not in {"HorizontalDim", "VerticalDim"}:
                    raise OracleError(f"before Tag {tag} dimension kind is outside supported HorizontalDim/VerticalDim scope")
        selected = tag in selected_tags
        expected_state = target_state if selected else old_state
        if new_state != expected_state:
            raise OracleError(
                f"Tag {tag} state mismatch: expected {expected_state}, observed {new_state}"
            )
        if old_kind == "curve":
            _same_value(old["geometry"], new["geometry"], f"Tag {tag}.geometry", numeric_tolerance)
        else:
            allowed_sync_value = allowed_expression_rhs_updates.get(tag)
            if allowed_sync_value is not None:
                if not selected or target_state != "active" or old_state != "reference" or new_state != "active":
                    raise OracleError(
                        f"Tag {tag} expression sync is allowed only for a selected Reference→Active dimension"
                    )
                if old.get("constraint_type") not in {"HorizontalDim", "VerticalDim"}:
                    raise OracleError(
                        f"Tag {tag} expression sync is allowed only for HorizontalDim/VerticalDim millimetre dimensions"
                    )
                measured_before = _finite(old["dimension_value"], f"before Tag {tag} ComputedSize")
                measured_after = _finite(new["dimension_value"], f"after Tag {tag} ComputedSize")
                rhs_after = _numeric_expression_value(new["expression_rhs"], f"after Tag {tag} expression RHS")
                if (abs(measured_before - allowed_sync_value) > numeric_tolerance
                        or abs(measured_after - allowed_sync_value) > numeric_tolerance
                        or abs(rhs_after - allowed_sync_value) > numeric_tolerance):
                    raise OracleError(
                        f"Tag {tag} expression sync must equal the recorded pre-conversion ComputedSize "
                        f"{allowed_sync_value!r}; measured={measured_before!r}/{measured_after!r}, RHS={rhs_after!r}"
                    )
            for key in ("expression_tag", "expression_name", "expression_rhs", "dimension_value",
                        "dimension_unit", "expression_flags", "geometry_refs", "constraint_type"):
                if key == "expression_rhs" and allowed_sync_value is not None:
                    continue
                _same_value(old[key], new[key], f"Tag {tag}.{key}", numeric_tolerance)
        rows_evidence.append({
            "tag": tag, "journal_id_before": old.get("journal_id"),
            "journal_id_after": new.get("journal_id"), "object_kind": old_kind,
            "selected": selected, "state_before": old_state, "state_after": new_state,
            "geometry_preserved": True,
            "expression_rhs_sync_to_computed_size": (
                allowed_expression_rhs_updates.get(tag) if old_kind == "dimension" else None
            ),
        })

    return {
        "passed": True, "target_state": target_state,
        "selected_object_count": len(selected_tags), "object_count": len(before),
        "selected_tags": sorted(selected_tags), "objects": rows_evidence,
        "numeric_tolerance": numeric_tolerance,
        "allowed_expression_rhs_updates": dict(allowed_expression_rhs_updates),
        "identity_rule": "NX Tag; current Journal ID is request locator only",
    }


def _point_pair_residuals(a: list[Any], b: list[Any], label: str) -> list[float]:
    if len(a) != 2 or len(b) != 2:
        raise OracleError(f"{label} must contain exactly two 3D points")
    direct = max(_distance(a[0], b[0], label), _distance(a[1], b[1], label))
    reversed_order = max(_distance(a[0], b[1], label), _distance(a[1], b[0], label))
    return [direct, reversed_order]


def curve_geometry_match(
    source: dict[str, Any],
    output: dict[str, Any],
    *,
    position_tolerance_mm: float = 1.0e-6,
    angular_tolerance_rad: float = 1.0e-7,
) -> dict[str, Any] | None:
    """Match a Section output copy to a curve by geometric locus, never by Tag."""
    position_tolerance_mm = _finite(position_tolerance_mm, "position_tolerance_mm")
    angular_tolerance_rad = _finite(angular_tolerance_rad, "angular_tolerance_rad")
    if position_tolerance_mm < 0 or angular_tolerance_rad < 0:
        raise OracleError("Section comparison tolerances must be nonnegative")
    kind = str(_required(source, "kind", "source curve")).upper()
    if str(_required(output, "kind", "Section output")).upper() != kind:
        return None
    try:
        if kind == "LINE":
            source_points = _required(source, "endpoints_world_mm", "source line")
            output_points = _required(output, "endpoints_world_mm", "Section line")
            residuals = _point_pair_residuals(source_points, output_points, "line endpoints")
            residual = min(residuals)
            return ({"matches": True, "max_endpoint_residual_mm": residual,
                     "endpoint_order": "reversed" if residuals[1] < residuals[0] else "same"}
                    if residual <= position_tolerance_mm else None)

        if kind in {"ARC", "CIRCLE"}:
            source_center = _required(source, "center_world_mm", f"source {kind.lower()}")
            output_center = _required(output, "center_world_mm", f"Section {kind.lower()}")
            center_residual = _distance(source_center, output_center, f"{kind.lower()} center")
            radius_source = _finite(_required(source, "radius_mm", f"source {kind.lower()}"), "source radius")
            radius_output = _finite(_required(output, "radius_mm", f"Section {kind.lower()}"), "Section radius")
            radius_residual = abs(radius_source - radius_output)
            normal_residual = _normal_residual(
                _required(source, "normal_world", f"source {kind.lower()}"),
                _required(output, "normal_world", f"Section {kind.lower()}"),
                f"{kind.lower()} normal",
            )
            if (center_residual > position_tolerance_mm or radius_residual > position_tolerance_mm
                    or normal_residual > angular_tolerance_rad):
                return None
            evidence: dict[str, Any] = {
                "matches": True, "center_residual_mm": center_residual,
                "radius_residual_mm": radius_residual, "normal_residual_rad": normal_residual,
            }
            if kind == "ARC":
                source_full = bool(_required(source, "full_circle", "source arc"))
                output_full = bool(_required(output, "full_circle", "Section arc"))
                if source_full != output_full:
                    return None
                if not source_full:
                    source_endpoints = _required(source, "endpoints_world_mm", "source arc")
                    output_endpoints = _required(output, "endpoints_world_mm", "Section arc")
                    endpoint_residuals = _point_pair_residuals(source_endpoints, output_endpoints, "arc endpoints")
                    endpoint_residual = min(endpoint_residuals)
                    source_sweep = abs(_finite(_required(source, "sweep_rad", "source arc"), "source sweep"))
                    output_sweep = abs(_finite(_required(output, "sweep_rad", "Section arc"), "Section sweep"))
                    sweep_residual = abs(source_sweep - output_sweep)
                    if endpoint_residual > position_tolerance_mm or sweep_residual > angular_tolerance_rad:
                        return None
                    source_mid = _required(source, "midpoint_world_mm", "source arc")
                    output_mid = _required(output, "midpoint_world_mm", "Section arc")
                    midpoint_residual = _distance(source_mid, output_mid, "arc interior sample")
                    if midpoint_residual > position_tolerance_mm:
                        return None
                    evidence.update({
                        "max_endpoint_residual_mm": endpoint_residual,
                        "sweep_residual_rad": sweep_residual,
                        "midpoint_residual_mm": midpoint_residual,
                    })
            return evidence
    except (KeyError, TypeError, ValueError, OracleError):
        return None
    return None


def verify_section_partition(
    sketch_curves: list[dict[str, Any]],
    section_outputs: list[dict[str, Any]],
    *,
    position_tolerance_mm: float = 1.0e-6,
    angular_tolerance_rad: float = 1.0e-7,
) -> dict[str, Any]:
    """Require Section output copies to match every Active curve exactly once."""
    position_tolerance_mm = _finite(position_tolerance_mm, "position_tolerance_mm")
    angular_tolerance_rad = _finite(angular_tolerance_rad, "angular_tolerance_rad")
    if position_tolerance_mm < 0 or angular_tolerance_rad < 0:
        raise OracleError("Section comparison tolerances must be nonnegative")
    records = _index_records(sketch_curves, "sketch_curves")
    active: list[dict[str, Any]] = []
    reference_tags: list[str] = []
    for tag, row in records.items():
        kind, state = _normalized_state({**row, "object_kind": "curve"}, f"Sketch curve Tag {tag}")
        if kind != "curve":
            raise OracleError(f"Sketch inventory Tag {tag} is not curve geometry")
        _validate_curve_geometry(row, f"Sketch curve Tag {tag}")
        if state == "active":
            active.append(row)
        else:
            reference_tags.append(tag)

    if not isinstance(section_outputs, list):
        raise OracleError("section_outputs must be a list")
    _index_records(section_outputs, "section_outputs")
    if len(section_outputs) != len(active):
        raise OracleError(
            f"whole-Sketch Section output count mismatch: expected {len(active)} active curves, "
            f"observed {len(section_outputs)}"
        )

    candidates: list[list[tuple[int, dict[str, Any]]]] = []
    for source in active:
        matches: list[tuple[int, dict[str, Any]]] = []
        for index, output in enumerate(section_outputs):
            evidence = curve_geometry_match(
                source["geometry"], output["geometry"],
                position_tolerance_mm=position_tolerance_mm,
                angular_tolerance_rad=angular_tolerance_rad,
            )
            if evidence is not None:
                matches.append((index, evidence))
        candidates.append(matches)
    order = sorted(range(len(active)), key=lambda index: len(candidates[index]))
    mapping: dict[int, tuple[int, dict[str, Any]]] = {}

    def assign(depth: int, used: set[int]) -> bool:
        if depth == len(order):
            return True
        source_index = order[depth]
        for output_index, evidence in candidates[source_index]:
            if output_index in used:
                continue
            mapping[source_index] = (output_index, evidence)
            used.add(output_index)
            if assign(depth + 1, used):
                return True
            used.remove(output_index)
            del mapping[source_index]
        return False

    if not assign(0, set()):
        unmatched = [str(active[index].get("tag")) for index in range(len(active)) if index not in mapping]
        raise OracleError(f"Section outputs do not geometrically match every Active Sketch curve: {unmatched}")

    matched: list[dict[str, Any]] = []
    for source_index, source in enumerate(active):
        output_index, evidence = mapping[source_index]
        matched.append({
            "source_tag": str(source["tag"]),
            "section_output_tag": section_outputs[output_index].get("tag"),
            "section_tag_is_source_identity": False,
            **evidence,
        })
    return {
        "passed": True,
        "active_curve_count": len(active),
        "reference_curve_count": len(reference_tags),
        "section_output_count": len(section_outputs),
        "matched_active_curves": matched,
        "reference_tags_excluded_by_count_and_locus": sorted(reference_tags),
        "position_tolerance_mm": position_tolerance_mm,
        "angular_tolerance_rad": angular_tolerance_rad,
        "identity_rule": "Section output geometry is matched one-to-one; output Tags are temporary copies",
    }
