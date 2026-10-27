# Traffic heatmap request contract

`TrafficHeatmapAnalysis.render_traffic_heatmap(request, output_path)` renders a
high-resolution PNG page collection. Each selected altitude layer receives one
full-size road-network page, followed by one independent local page for every
archived `Vertiport`. The renderer uses Matplotlib's headless `Agg` backend and
does not require a Python UI toolkit.

The exact `output_path` remains the first (network overview) page for legacy
clients. Additional pages use stable sibling names in the same directory:
`<stem>--NN-<page-id>.png`.

The request is a JSON object with `schema_version: 1` and
`kind: "traffic-heatmap"`. Recommended fields are:

- `title`: figure title.
- `visible_layer_ids`: ordered IDs to render. An empty array intentionally hides
  every road-network layer; omitting the field uses enabled topology layers.
- `network`: the archived/raw Unity `Network` snapshot. The renderer reads only
  `AltitudeLayers` and `Edges[].Lanes[].Centerline`. It never reads
  `LaneConnectionCorridors`, so lane-to-lane connector curves are excluded.
- `vertiports`: the archived/raw `List<Vertiport>` snapshot. Each vertiport page
  intentionally draws only the path resources needed for traffic-load review:
  `ApproachEdge`, explicit holding-structure `Ramp`, and
  `NetConnectedCorridor`. Approach/holding nodes, holding-circle guides,
  vertices, helipad boundaries/fills, and the outer vertiport boundary are not
  drawn. Their positive occupancy entries are treated as deliberately omitted,
  not as missing geometry. Page membership follows each raw `Vertiport` object's
  child collections; geometry proximity is never used to cluster facilities.
  Optional `DisplayName`/`Name` fields are preserved in the page title together
  with the required `VertiportId`.
- `NetConnectedCorridor` is retained because `PathPlanner` books its
  `CorridorId` for both departure and arrival paths. Its top-down geometry is
  never an endpoint chord: the worker reproduces
  `RuntimeAirspaceGeometry.BuildCorridorPolyline`, using the linked lane tangent,
  linked holding-ramp tangent, altitude-aware handle length, and a sampled cubic
  Bezier. `corridor_tangent_ratio` may be supplied when the scene overrides the
  Unity default of `0.3`.
- `lane_traversals`: map from `LaneId` to traversal count.
- `resource_reservation_seconds`: map from resource ID to total reserved time.
- `dpi`: optional export DPI, clamped to 150–600 (default 300).

For a caller that does not have raw snapshots, `lanes` and `resources` may be
supplied as flattened arrays. A lane has `lane_id`, `layer_id`, `points`, and
optional `traversal_count`. A resource has `resource_id`, `resource_type`,
`points`, optional `reservation_seconds`, optional `closed`, and optional
`vertiport_id`; only approach-edge, holding-ramp, and facility-corridor resource
types are accepted by this focused renderer. Unowned flattened resources are
placed on a clearly labelled `resource-group` page rather than being mixed into
a real vertiport. Each point may be `{ "x": ..., "y": ..., "z": ... }`,
`[x, y, z]`, or `[x, z]`.

## Page-manifest response (`sar.traffic-heatmap.pages/1`)

The top-level response keeps `output_path` as the first network page and adds:

- `page_manifest_version: 1` and
  `page_output_schema: "sar.traffic-heatmap.pages/1"`;
- `page_count`, `network_page_count`, and `vertiport_page_count`;
- `pages`, in stable display order (network layers, then vertiports). Every page
  has `page_id`, `kind`, `page_type`, `title`, and `output_path`. A
  page also carries one-based `page_number` and the collection `page_count`. A
  `network-layer` page also has `layer_id`, `visible_layer_ids`, and
  `lane_count`. A `vertiport` page also has `vertiport_id`, `display_name`,
  `resource_count`, `grouping_basis`, `data_bounds`, and padded square
  `plot_bounds`;
- `suggested_layout`, whose default is one column and optional compact mode is
  two columns. One page per viewport remains the clarity-first presentation.

The response also retains `success`, `caption`, legacy `panel_count`,
`visible_layer_ids`, `visible_lane_count`, and `resource_count`.
It also reports `missing_loaded_resource_ids` and `warnings`. Every positive
reservation load whose ID cannot be matched to drawable resource geometry is
listed there, and its count is included in `caption`; such data is never
silently discarded.

## Existing worker integration

Import `render_traffic_heatmap` in `SensitivityAnalysis.py` and add this minimal
dispatch branch before the unsupported-kind error:

```python
if kind == "traffic-heatmap":
    from TrafficHeatmapAnalysis import render_traffic_heatmap
    return render_traffic_heatmap(request, output_path)
```

PyInstaller discovers the direct import when it is at module scope. If the lazy
import above is retained, add `--hidden-import TrafficHeatmapAnalysis` (or the
same name to the `.spec` `hiddenimports`). No extra binary/data dependency is
required beyond the existing Matplotlib and NumPy runtime.
