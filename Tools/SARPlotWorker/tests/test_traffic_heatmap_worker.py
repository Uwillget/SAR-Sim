import importlib.util
import copy
import pathlib
import struct
import sys
import tempfile
import unittest

import matplotlib.pyplot as plt


PROJECT_ROOT = pathlib.Path(__file__).resolve().parents[3]
MODULE_PATH = (
    PROJECT_ROOT / "Assets" / "StreamingAssets" / "SARAnalytics" /
    "TrafficHeatmapAnalysis.py"
)
SPEC = importlib.util.spec_from_file_location("sar_traffic_heatmap", MODULE_PATH)
TRAFFIC = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = TRAFFIC
SPEC.loader.exec_module(TRAFFIC)


def point(x, y, z):
    return {"x": x, "y": y, "z": z}


def sample_request():
    return {
        "schema_version": 1,
        "kind": "traffic-heatmap",
        "title": "Default Research Scene — Traffic Load",
        "visible_layer_ids": ["L0", "L1"],
        "network": {
            "AltitudeLayers": [
                {"LayerId": "L0", "DisplayName": "Lower layer", "NominalAltitude": 75, "Enabled": True},
                {"LayerId": "L1", "DisplayName": "Upper layer", "NominalAltitude": 125, "Enabled": True},
            ],
            "Edges": [
                {
                    "EdgeId": "E0",
                    "LayerId": "L0",
                    "Lanes": [
                        {"LaneId": "L0-A", "LayerId": "L0", "Centerline": [point(0, 75, 0), point(50, 75, 0), point(100, 75, 20)]},
                        {"LaneId": "L0-B", "LayerId": "L0", "Centerline": [point(0, 75, 12), point(50, 75, 12), point(100, 75, 30)]},
                    ],
                },
                {
                    "EdgeId": "E1",
                    "LayerId": "L1",
                    "Lanes": [
                        {"LaneId": "L1-A", "LayerId": "L1", "Centerline": [point(0, 125, 0), point(30, 125, 35), point(75, 125, 60)]},
                    ],
                },
            ],
            # This dramatic connector would be obvious in the output if the
            # renderer accidentally consumed it.
            "LaneConnectionCorridors": [
                {"CorridorId": "MUST-NOT-DRAW", "Centerline": [point(-999, 0, -999), point(999, 0, 999)]},
            ],
        },
        "lane_traversals": {"L0-A": 18, "L0-B": 4, "L1-A": 31},
        "resource_reservation_seconds": {
            "H1": 22.5,
            "A": 13.0,
            "AP-1": 7.0,
            "V0": 19.0,
            "C1": 12.0,
            "R1": 30.0,
            "VC-1": 5.0,
        },
        "vertiports": [
            {
                "VertiportId": "VP1",
                "Boundary": [point(130, 0, 0), point(190, 0, 0), point(190, 0, 70), point(130, 0, 70)],
                "Helipads": [
                    {"HelipadId": "H1", "Boundary": [point(145, 0, 15), point(162, 0, 15), point(162, 0, 32), point(145, 0, 32)]},
                ],
                "ApproachNet": {
                    "Nodes": [
                        {"NodeId": "A", "Position": point(130, 0, 35)},
                        {"NodeId": "B", "Position": point(105, 0, 35)},
                    ],
                    "Edges": [{"EdgeId": "AP-1", "FromNodeId": "A", "ToNodeId": "B"}],
                },
                "HoldingStructures": [
                    {
                        "HoldingStructureId": "HS1",
                        "Circles": [{"CircleId": "C1", "Center": point(170, 80, 50), "Radius": 12}],
                        "Vertices": [
                            {"VertexId": "V0", "Position": point(182, 80, 50)},
                            {"VertexId": "V1", "Position": point(170, 80, 62)},
                        ],
                        "Ramps": [
                            {"RampId": "R1", "FromVertexId": "V0", "ToVertexId": "V1", "LinkCircleId": "C1", "Direction": "CounterClockwise"},
                        ],
                    },
                ],
                "Corridors": [
                    {
                        "CorridorId": "VC-1", "CorridorType": "Entry",
                        "LinkLaneId": "L0-A", "LinkRampId": "R1",
                        "LaneLinkPoint": point(100, 75, 20),
                        "RampLinkPoint": point(182, 80, 50),
                    },
                ],
            },
        ],
    }


class TrafficHeatmapWorkerTests(unittest.TestCase):
    def test_raw_snapshot_filters_layers_and_never_collects_lane_connectors(self):
        request = sample_request()
        request["visible_layer_ids"] = ["L0"]
        prepared = TRAFFIC.prepare_traffic_data(request)
        self.assertEqual(["L0"], prepared.visible_layer_ids)
        self.assertEqual({"L0-A", "L0-B", "L1-A"}, {lane.lane_id for lane in prepared.lanes})
        self.assertNotIn("MUST-NOT-DRAW", {lane.lane_id for lane in prepared.lanes})
        visible = [lane for lane in prepared.lanes if lane.layer_id in prepared.visible_layer_ids]
        self.assertEqual({"L0-A", "L0-B"}, {lane.lane_id for lane in visible})
        self.assertTrue(any("connection corridors" in note for note in prepared.notes))

    def test_vertiport_snapshot_draws_only_reservable_paths_and_uses_bezier_corridor(self):
        prepared = TRAFFIC.prepare_traffic_data(sample_request())
        by_id = {resource.resource_id: resource for resource in prepared.resources}
        self.assertEqual({"AP-1", "R1", "VC-1"}, set(by_id))
        self.assertEqual(
            {"Approach edge", "Holding ramp", "Vertiport corridor"},
            {resource.resource_type for resource in prepared.resources},
        )
        self.assertFalse(any(resource.closed for resource in prepared.resources))
        self.assertEqual([], prepared.facility_outlines)
        self.assertGreater(len(by_id["R1"].points), 20)
        self.assertEqual(37, len(by_id["VC-1"].points))
        self.assertEqual((182.0, 50.0), by_id["VC-1"].points[0])
        self.assertEqual((100.0, 20.0), by_id["VC-1"].points[-1])
        start = by_id["VC-1"].points[0]
        end = by_id["VC-1"].points[-1]
        chord_x, chord_y = end[0] - start[0], end[1] - start[1]
        cross_distances = [
            abs(chord_x * (point[1] - start[1]) - chord_y * (point[0] - start[0]))
            for point in by_id["VC-1"].points[1:-1]
        ]
        self.assertGreater(max(cross_distances), 1.0, "Facility corridor regressed to a straight chord")
        self.assertEqual([], prepared.missing_loaded_resource_ids)

    def test_facility_corridor_matches_unity_altitude_aware_control_points(self):
        corridor = {
            "CorridorType": "Exit",
            "LaneLinkPoint": point(5, 0, 0),
            "RampLinkPoint": point(15, 10, 10),
        }
        lane = {"Centerline": [point(0, 0, 0), point(10, 0, 0)]}
        ramp = {"FromVertexId": "A", "ToVertexId": "B"}
        vertices = {
            "A": {"Position": point(10, 10, 10)},
            "B": {"Position": point(20, 10, 10)},
        }
        curve = TRAFFIC._build_facility_corridor_curve(
            corridor, lane, ramp, vertices, {}, tangent_ratio=0.3, segment_count=16,
        )
        self.assertEqual(17, len(curve))
        self.assertEqual((5.0, 0.0), curve[0])
        self.assertEqual((15.0, 10.0), curve[-1])
        handle = (300.0 ** 0.5) * 0.3  # Unity uses full XYZ endpoint distance.
        expected_x_at_quarter = 6.5625 + 0.28125 * handle
        self.assertAlmostEqual(expected_x_at_quarter, curve[4][0], places=6)
        self.assertAlmostEqual(1.5625, curve[4][1], places=6)

    def test_archived_non_default_corridor_tangent_ratio_reaches_prepared_geometry(self):
        request = sample_request()
        request["corridor_tangent_ratio"] = 0.45
        prepared = TRAFFIC.prepare_traffic_data(request)
        actual = next(
            resource.points for resource in prepared.resources
            if resource.resource_id == "VC-1"
        )

        vertiport = request["vertiports"][0]
        corridor = vertiport["Corridors"][0]
        lane = request["network"]["Edges"][0]["Lanes"][0]
        holding = vertiport["HoldingStructures"][0]
        ramp = holding["Ramps"][0]
        vertices = {
            item["VertexId"]: item for item in holding["Vertices"]
        }
        circles = {
            item["CircleId"]: item for item in holding["Circles"]
        }
        expected = TRAFFIC._build_facility_corridor_curve(
            corridor, lane, ramp, vertices, circles,
            tangent_ratio=0.45, segment_count=36,
        )
        legacy_default = TRAFFIC._build_facility_corridor_curve(
            corridor, lane, ramp, vertices, circles,
            tangent_ratio=0.3, segment_count=36,
        )

        self.assertEqual(expected, actual)
        self.assertNotEqual(legacy_default[9], actual[9])

    def test_loaded_resource_without_geometry_is_reported_not_silently_dropped(self):
        request = sample_request()
        request["resource_reservation_seconds"]["MISSING-RESOURCE"] = 42.0
        prepared = TRAFFIC.prepare_traffic_data(request)
        self.assertEqual(["MISSING-RESOURCE"], prepared.missing_loaded_resource_ids)

        figure, metadata = TRAFFIC.draw_traffic_heatmap(request)
        try:
            self.assertEqual(["MISSING-RESOURCE"], metadata["missing_loaded_resource_ids"])
            self.assertTrue(any("MISSING-RESOURCE" in warning for warning in metadata["warnings"]))
            self.assertIn("1 loaded resource ID(s) lacked geometry", metadata["caption"])
        finally:
            plt.close(figure)

    def test_render_uses_full_size_layer_and_vertiport_pages_with_consistent_typography(self):
        pages, metadata = TRAFFIC.draw_traffic_heatmap_pages(sample_request())
        try:
            self.assertEqual("sar.traffic-heatmap.pages/1", metadata["page_output_schema"])
            self.assertEqual(3, metadata["page_count"])
            self.assertEqual(3, metadata["panel_count"])
            self.assertEqual(2, metadata["network_page_count"])
            self.assertEqual(1, metadata["vertiport_page_count"])
            self.assertEqual(
                ["network-layer", "network-layer", "vertiport"],
                [page_metadata["kind"] for _, page_metadata in pages],
            )
            colorbar_axes = [
                axis for figure, _ in pages for axis in figure.axes
                if axis.get_xlabel() in {"Lane traversals", "Reservation time (s)"}
            ]
            self.assertEqual(3, len(colorbar_axes))
            weights = {axis.xaxis.label.get_fontweight() for axis in colorbar_axes}
            sizes = {axis.xaxis.label.get_fontsize() for axis in colorbar_axes}
            self.assertEqual({"bold"}, weights)
            self.assertEqual(1, len(sizes))
            map_axes = [
                axis for figure, _ in pages for axis in figure.axes
                if axis.get_ylabel() == "Y (north, m)"
            ]
            self.assertEqual(3, len(map_axes))
            self.assertTrue(all(axis.get_aspect() == 1.0 for axis in map_axes))
        finally:
            for figure, _ in pages:
                plt.close(figure)

    def test_vertiports_are_grouped_by_snapshot_containment_and_get_independent_local_bounds(self):
        request = copy.deepcopy(sample_request())
        request["vertiports"][0]["DisplayName"] = "West Hub"
        request["vertiports"].append({
            "VertiportId": "VP2",
            "DisplayName": "East Hub",
            "Boundary": [
                point(1000, 0, 1000), point(1060, 0, 1000),
                point(1060, 0, 1060), point(1000, 0, 1060),
            ],
            "Helipads": [{
                "HelipadId": "VP2-H1",
                "Boundary": [
                    point(1010, 0, 1010), point(1020, 0, 1010),
                    point(1020, 0, 1020), point(1010, 0, 1020),
                ],
            }],
            "ApproachNet": {
                "Nodes": [
                    {"NodeId": "VP2-N1", "Position": point(1060, 0, 1030)},
                    {"NodeId": "VP2-N2", "Position": point(1030, 0, 1030)},
                ],
                "Edges": [{"EdgeId": "VP2-AP", "FromNodeId": "VP2-N1", "ToNodeId": "VP2-N2"}],
            },
            "HoldingStructures": [],
            "Corridors": [],
        })
        request["resource_reservation_seconds"]["VP2-H1"] = 8
        request["resource_reservation_seconds"]["VP2-N1"] = 11
        request["resource_reservation_seconds"]["VP2-AP"] = 6

        prepared = TRAFFIC.prepare_traffic_data(request)
        self.assertEqual(["VP1", "VP2"], [group.vertiport_id for group in prepared.vertiport_groups])
        self.assertNotIn("VP2-H1", {item.resource_id for item in prepared.vertiport_groups[0].resources})
        self.assertEqual(
            {"VP2-AP"},
            {item.resource_id for item in prepared.vertiport_groups[1].resources},
        )

        pages, metadata = TRAFFIC.draw_traffic_heatmap_pages(request)
        try:
            vertiport_pages = [page for _, page in pages if page["kind"] == "vertiport"]
            self.assertEqual(2, len(vertiport_pages))
            self.assertIn("West Hub", vertiport_pages[0]["title"])
            self.assertIn("VP1", vertiport_pages[0]["title"])
            self.assertIn("East Hub", vertiport_pages[1]["title"])
            self.assertIn("VP2", vertiport_pages[1]["title"])
            self.assertEqual(
                ["vertiport-object-containment", "vertiport-object-containment"],
                [page["grouping_basis"] for page in vertiport_pages],
            )
            for page in vertiport_pages:
                data_bounds = page["data_bounds"]
                plot_bounds = page["plot_bounds"]
                self.assertLess(plot_bounds[0], data_bounds[0])
                self.assertGreater(plot_bounds[1], data_bounds[1])
                self.assertLess(plot_bounds[2], data_bounds[2])
                self.assertGreater(plot_bounds[3], data_bounds[3])
                self.assertAlmostEqual(
                    plot_bounds[1] - plot_bounds[0],
                    plot_bounds[3] - plot_bounds[2],
                )
            self.assertEqual(2, metadata["vertiport_page_count"])
        finally:
            for figure, _ in pages:
                plt.close(figure)

    def test_png_is_high_resolution_and_empty_visible_layers_are_explicit(self):
        request = sample_request()
        request["visible_layer_ids"] = []
        request["lane_traversals"] = {"L0-A": 0, "L0-B": 0, "L1-A": 0}
        request["resource_reservation_seconds"] = {}
        with tempfile.TemporaryDirectory() as temporary:
            output = pathlib.Path(temporary) / "traffic.png"
            response = TRAFFIC.render_traffic_heatmap(request, str(output))
            self.assertTrue(response["success"])
            self.assertEqual([], response["visible_layer_ids"])
            self.assertEqual(0, response["visible_lane_count"])
            self.assertEqual(str(output.resolve()), response["output_path"])
            self.assertEqual(2, response["page_count"])
            self.assertEqual(2, len(response["pages"]))
            self.assertEqual("network-layer", response["pages"][0]["kind"])
            self.assertEqual("vertiport", response["pages"][1]["kind"])
            self.assertTrue(all(pathlib.Path(page["output_path"]).exists()
                                for page in response["pages"]))
            self.assertTrue(all(pathlib.Path(page["output_path"]).parent == output.parent
                                for page in response["pages"]))
            self.assertIn("--02-vertiport-vp1", response["pages"][1]["output_path"])
            self.assertGreater(output.stat().st_size, 10000)
            with output.open("rb") as stream:
                stream.seek(16)
                width, height = struct.unpack(">II", stream.read(8))
            self.assertGreaterEqual(width, 3000)
            self.assertGreaterEqual(height, 1500)

    def test_flattened_geometry_contract_is_supported(self):
        request = {
            "kind": "traffic-heatmap",
            "visible_layer_ids": ["A"],
            "lanes": [{
                "lane_id": "lane",
                "layer_id": "A",
                "points": [[0, 0], [10, 10]],
                "traversal_count": 2,
            }, {
                "lane_id": "connector",
                "layer_id": "A",
                "geometry_role": "lane_connector",
                "points": [[0, 10], [10, 0]],
                "traversal_count": 99,
            }],
            "resources": [{
                "resource_id": "r",
                "resource_type": "Approach edge",
                "points": [[1, 2], [3, 4]],
                "reservation_seconds": 8,
            }, {
                "resource_id": "hidden-pad",
                "resource_type": "Helipad",
                "points": [[0, 0], [1, 0], [1, 1], [0, 0]],
                "reservation_seconds": 99,
            }],
        }
        prepared = TRAFFIC.prepare_traffic_data(request)
        self.assertEqual(["lane"], [lane.lane_id for lane in prepared.lanes])
        self.assertEqual(["r"], [resource.resource_id for resource in prepared.resources])


if __name__ == "__main__":
    unittest.main()
