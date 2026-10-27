import importlib.util
import pathlib
import struct
import tempfile
import unittest

import matplotlib.pyplot as plt
import numpy as np


PROJECT_ROOT = pathlib.Path(__file__).resolve().parents[3]
WORKER_PATH = (
    PROJECT_ROOT / "Assets" / "StreamingAssets" / "SARAnalytics" /
    "SensitivityAnalysis.py"
)
SPEC = importlib.util.spec_from_file_location("sar_analysis_worker", WORKER_PATH)
WORKER = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(WORKER)


def grid_runs():
    result = []
    index = 0
    for x in (1.0, 2.0):
        for z in (10.0, 20.0):
            index += 1
            result.append({
                "run_id": f"grid-{index}",
                "duration": 100.0,
                "parameters": {"x": x, "z": z},
                "metrics": {
                    "Delay (s)": x + z / 10.0,
                    "Throughput (flights)": 1000.0 * x + 100.0 * z,
                },
            })
    return result


def comparison_runs():
    return [
        {
            "run_id": "batch-member-1",
            "label": "Batch 1/2 · member-1",
            "duration": 120.0,
            "parameters": {"signalDelay": 0.2},
            "metrics": {"Delay (s)": 4.0, "Throughput (flights)": 80.0},
        },
        {
            "run_id": "independent-run-2",
            "label": "Run 2 · independent-2",
            "duration": 120.0,
            "parameters": {"signalDelay": 9.9},
            "metrics": {"Delay (s)": 7.0, "Throughput (flights)": 125.0},
        },
    ]


def volume_runs():
    result = []
    for x in (0.0, 1.0):
        for y in (0.0, 2.0):
            for z in (0.0, 3.0):
                result.append({
                    "run_id": f"volume-{len(result) + 1}",
                    "duration": 100.0,
                    "parameters": {"x": x, "y": y, "z": z},
                    "metrics": {
                        "Response": 1.0 + x + y + z,
                        "Secondary response": 20.0 - 2.0 * x + y + 0.5 * z,
                    },
                })
    return result


def long_context_runs():
    result = []
    for x in (0.0, 1.0):
        for propagation in (0.0, 0.1, 0.2, 0.3):
            for interval in (10.0, 20.0):
                result.append({
                    "run_id": f"legend-{len(result) + 1}",
                    "duration": 100.0,
                    "parameters": {
                        "x": x,
                        "signalPropagationDelaySeconds": propagation,
                        "safeSeparationIntervalSeconds": interval,
                    },
                    "metrics": {"Response": 1.0 + x + propagation + interval / 10.0},
                })
    return result


def coincident_context_runs():
    """Two coincident groups, one unique series and one partial series."""
    result = []
    context_name = "extraordinarilyLongFixedContextParameterForLegendMapping"
    for context_value in range(1, 7):
        x_values = (0.0, 1.0) if context_value == 6 else (0.0, 1.0, 2.0)
        for x_value in x_values:
            if context_value in (1, 2):
                base = x_value
                # Matching non-zero sample error ranges are part of the visual
                # overlap contract; sub-nanometre noise stays within tolerance.
                samples = (base - 0.1, base + 0.1)
                if context_value == 2:
                    samples = tuple(value + 1e-10 for value in samples)
            elif context_value in (3, 4):
                samples = (10.0 + 2.0 * x_value,)
            else:
                samples = (20.0 + x_value,)
            for sample in samples:
                result.append({
                    "run_id": f"overlap-{len(result) + 1}",
                    "duration": 100.0,
                    "parameters": {
                        "x": x_value,
                        context_name: float(context_value),
                    },
                    "metrics": {"Response": sample},
                })
    return context_name, result


class AnalysisWorkerTests(unittest.TestCase):
    def test_panel_labels_continue_after_z(self):
        self.assertEqual("(a)", WORKER.panel_label(0))
        self.assertEqual("(z)", WORKER.panel_label(25))
        self.assertEqual("(aa)", WORKER.panel_label(26))
        self.assertEqual("(ab)", WORKER.panel_label(27))
        self.assertEqual("(az)", WORKER.panel_label(51))
        self.assertEqual("(ba)", WORKER.panel_label(52))

    def test_exact_duplicate_combinations_report_mean_sample_sd_and_count(self):
        runs = [
            {
                "parameters": {"x": 1.0, "z": 2.0},
                "metrics": {"Response": 1.0},
                "duration": 100.0,
            },
            {
                "parameters": {"x": 1.0, "z": 2.0},
                "metrics": {"Response": 3.0},
                "duration": 100.0,
            },
            {
                "parameters": {"x": 9.0, "z": 8.0},
                "metrics": {"Response": 99.0},
                "duration": 100.0,
            },
        ]
        groups = WORKER.exact_groups(runs, ["x", "z"], "Response", False)
        self.assertEqual({(1.0, 2.0), (9.0, 8.0)}, set(groups))
        repeated = groups[(1.0, 2.0)]
        self.assertEqual(2, repeated["count"])
        self.assertAlmostEqual(2.0, repeated["mean"])
        self.assertAlmostEqual(2.0 ** 0.5, repeated["std"])

    def test_two_factor_multi_y_uses_distinct_colormaps_colorbars_and_scales(self):
        figure, panel_count = WORKER.draw_two_factors(
            ["x", "z"], [], ["Delay (s)", "Throughput (flights)"],
            grid_runs(), set(),
        )
        try:
            figure.canvas.draw()
            data_axes = [axis for axis in figure.axes if axis.images]
            self.assertEqual(2, panel_count)
            self.assertEqual(2, len(data_axes))
            self.assertNotEqual(
                data_axes[0].images[0].cmap.name,
                data_axes[1].images[0].cmap.name,
            )
            self.assertNotEqual(
                data_axes[0].images[0].get_clim(),
                data_axes[1].images[0].get_clim(),
            )
            colorbar_labels = {axis.get_ylabel() for axis in figure.axes}
            self.assertIn("Delay (s)", colorbar_labels)
            self.assertIn("Throughput (flights)", colorbar_labels)
            for axis in data_axes:
                self.assertGreater(len(axis.xaxis.get_minorticklocs()), 0)
                self.assertGreater(len(axis.yaxis.get_minorticklocs()), 0)
                self.assertGreaterEqual(axis.title.get_fontsize(), 15.0)
                self.assertTrue(axis.get_title().startswith("("))
                self.assertGreaterEqual(
                    min(text.get_fontsize() for text in axis.texts), 12.0,
                )
                self.assertAlmostEqual(1.0, float(axis.get_box_aspect()), places=6)
                origin = axis.transData.transform((0.0, 0.0))
                x_step = axis.transData.transform((1.0, 0.0))
                y_step = axis.transData.transform((0.0, 1.0))
                self.assertAlmostEqual(
                    abs(x_step[0] - origin[0]),
                    abs(y_step[1] - origin[1]),
                    delta=0.75,
                )
        finally:
            plt.close(figure)

    def test_two_factor_keeps_designed_levels_when_one_level_has_no_y_values(self):
        runs = grid_runs()
        for run in runs:
            if run["parameters"]["x"] == 2.0:
                run["metrics"].pop("Delay (s)")
        figure, panel_count = WORKER.draw_two_factors(
            ["x", "z"], [], ["Delay (s)"], runs, set(),
        )
        try:
            axis = next(axis for axis in figure.axes if axis.images)
            matrix = np.ma.asarray(axis.images[0].get_array())
            mask = np.ma.getmaskarray(matrix)
            self.assertEqual(1, panel_count)
            self.assertEqual((2, 2), matrix.shape)
            self.assertTrue(np.all(mask[:, 1]))
            self.assertFalse(np.any(mask[:, 0]))
            self.assertEqual(["1", "2"], [tick.get_text() for tick in axis.get_xticklabels()])
            self.assertTrue(any("remain grey" in warning
                                for warning in figure._sar_warnings))
        finally:
            plt.close(figure)

    def test_two_factor_marks_an_untested_cartesian_cell_without_compressing_axes(self):
        runs = grid_runs()[1:]
        figure, _ = WORKER.draw_two_factors(
            ["x", "z"], [], ["Delay (s)"], runs, set(),
        )
        try:
            axis = next(axis for axis in figure.axes if axis.images)
            matrix = np.ma.asarray(axis.images[0].get_array())
            self.assertEqual((2, 2), matrix.shape)
            self.assertEqual(1, int(np.count_nonzero(np.ma.getmaskarray(matrix))))
            self.assertTrue(any("not present in the run design" in warning
                                for warning in figure._sar_warnings))
        finally:
            plt.close(figure)

    def test_two_and_three_factor_modes_reject_an_entirely_missing_y_metric(self):
        with self.assertRaisesRegex(ValueError, "No finite samples.*Missing response"):
            WORKER.draw_two_factors(
                ["x", "z"], [], ["Missing response"], grid_runs(), set(),
            )
        with self.assertRaisesRegex(ValueError, "No finite samples.*Missing response"):
            WORKER.draw_three_factors(
                ["x", "y", "z"], [], ["Missing response"], volume_runs(), set(),
            )

    def test_all_zero_heatmap_uses_chromatic_non_black_scale(self):
        runs = grid_runs()
        for run in runs:
            run["metrics"] = {"Zero collisions": 0.0}
        figure, _ = WORKER.draw_two_factors(
            ["x", "z"], [], ["Zero collisions"], runs, set(),
        )
        try:
            image = next(axis.images[0] for axis in figure.axes if axis.images)
            self.assertEqual((0.0, 1.0), image.get_clim())
            rgb = image.cmap(image.norm(0.0))[:3]
            self.assertGreater(min(rgb), 0.05, "constant-zero cells must not be black")
            self.assertGreater(max(rgb) - min(rgb), 0.08,
                               "constant-zero cells must remain visibly chromatic")
        finally:
            plt.close(figure)

    def test_multi_y_bar_comparison_uses_selected_records_as_x_axis(self):
        metrics = ["Delay (s)", "Throughput (flights)"]
        runs = comparison_runs()
        figure, panel_count = WORKER.draw_run_comparison(metrics, runs, set())
        try:
            data_axes = [axis for axis in figure.axes if axis.patches]
            self.assertEqual(2, panel_count)
            self.assertEqual(2, len(data_axes))
            for metric, axis in zip(metrics, data_axes):
                labels = [label.get_text() for label in axis.get_xticklabels()]
                self.assertEqual(["Batch 1/2", "Run 2/2"], labels)
                self.assertEqual("Selected simulation run", axis.get_xlabel())
                self.assertEqual(metric, axis.get_ylabel())
                self.assertNotIn("signalDelay", labels)
                self.assertFalse(any("member-1" in label or "independent-2" in label
                                     for label in labels))
            self.assertNotEqual(
                data_axes[0].patches[0].get_facecolor(),
                data_axes[1].patches[0].get_facecolor(),
            )
        finally:
            plt.close(figure)

    def test_one_factor_context_legend_uses_a_dedicated_band_below_the_plot(self):
        line_figure, line_count = WORKER.draw_one_factor(
            ["x"], ["z"], ["Delay (s)"], grid_runs(), set(),
        )
        try:
            self.assertEqual(1, line_count)
            data_axis = next(axis for axis in line_figure.axes if axis.lines)
            self.assertEqual(1, len(line_figure.legends))
            legend = line_figure.legends[0]
            self.assertGreaterEqual(data_axis.title.get_fontsize(), 15.0)
            self.assertEqual("x", data_axis.get_xlabel())
            self.assertIsNone(data_axis.get_legend())
            self.assertLess(legend.get_bbox_to_anchor()._bbox.y0, data_axis.get_position().y0)
            legend_text = [text.get_text() for text in legend.get_texts()]
            self.assertEqual(["z=10", "z=20"], legend_text)
            self.assertEqual([], data_axis._sar_overlap_groups)
            self.assertEqual([], data_axis._sar_overlap_annotations)
        finally:
            plt.close(line_figure)

    def test_one_factor_calls_out_multiple_fully_coincident_context_groups(self):
        context_name, runs = coincident_context_runs()
        figure, panel_count = WORKER.draw_one_factor(
            ["x"], [context_name], ["Response"], runs, set(),
        )
        try:
            self.assertEqual(1, panel_count)
            axis = next(axis for axis in figure.axes if axis.get_visible())
            self.assertEqual([("C1", "C2"), ("C3", "C4")],
                             axis._sar_overlap_groups)
            self.assertEqual(2, len(axis._sar_overlap_annotations))
            self.assertEqual(
                ["Coincident: C1 = C2", "Coincident: C3 = C4"],
                [annotation.get_text()
                 for annotation in axis._sar_overlap_annotations],
            )
            for annotation in axis._sar_overlap_annotations:
                self.assertIsNotNone(annotation.arrow_patch)
                self.assertGreater(annotation.xyann[1],
                                   axis._sar_overlap_data_ceiling)
                self.assertEqual(20, annotation.get_zorder())
                self.assertTrue(annotation.get_gid().startswith("sar-coincident-"))
            legend_labels = [
                text.get_text() for text in figure.legends[0].get_texts()
            ]
            self.assertEqual(6, len(legend_labels))
            self.assertTrue(legend_labels[0].startswith("C1 · "))
            self.assertIn(context_name, legend_labels[0])
            # C5 and C6 match only at their common X levels.  Because C6 is
            # missing x=2 they are not falsely reported as a complete overlap.
            self.assertFalse(any("C5" in annotation.get_text() or
                                 "C6" in annotation.get_text()
                                 for annotation in axis._sar_overlap_annotations))
            figure.canvas.draw()
            renderer = figure.canvas.get_renderer()
            axis_bounds = axis.get_window_extent(renderer)
            for annotation in axis._sar_overlap_annotations:
                bounds = annotation.get_window_extent(renderer)
                self.assertGreaterEqual(bounds.y0, axis_bounds.y0)
                self.assertLessEqual(bounds.y1, axis_bounds.y1)
        finally:
            plt.close(figure)

    def test_one_factor_many_contexts_grow_canvas_and_fit_between_footer_and_axes(self):
        contexts = [
            "signalPropagationDelaySeconds",
            "safeSeparationIntervalSeconds",
        ]
        figure, panel_count = WORKER.draw_one_factor(
            ["x"], contexts, ["Response"], long_context_runs(), set(),
        )
        footer = figure.text(
            0.5, 0.018,
            "Exact parameter combinations only: measured mean and sample SD; no synthetic smoothing.\n"
            "Each Y metric uses an independent scale and palette, shared across fixed contexts.",
            ha="center", va="bottom", fontsize=9.5,
        )
        try:
            self.assertEqual(1, panel_count)
            self.assertEqual(1, len(figure.legends))
            self.assertGreaterEqual(figure.get_size_inches()[1], 8.0)
            figure.canvas.draw()
            renderer = figure.canvas.get_renderer()
            legend_bounds = figure.legends[0].get_window_extent(renderer)
            footer_bounds = footer.get_window_extent(renderer)
            data_axis = next(axis for axis in figure.axes if axis.get_visible())
            tight_axis_bounds = data_axis.get_tightbbox(renderer)
            axes_bottom = min(
                axis.get_window_extent(renderer).y0
                for axis in figure.axes if axis.get_visible()
            )
            figure_bounds = figure.bbox
            self.assertGreaterEqual(tight_axis_bounds.x0, figure_bounds.x0)
            self.assertLessEqual(tight_axis_bounds.x1, figure_bounds.x1)
            self.assertGreaterEqual(legend_bounds.x0, figure_bounds.x0)
            self.assertLessEqual(legend_bounds.x1, figure_bounds.x1)
            self.assertGreater(legend_bounds.y0, footer_bounds.y1)
            self.assertLess(legend_bounds.y1, axes_bottom)
        finally:
            plt.close(figure)

    def test_one_factor_long_metric_axis_and_title_stay_inside_canvas(self):
        metric = "Average Mission Completion Duration (seconds)"
        runs = long_context_runs()
        for run in runs:
            run["metrics"] = {metric: run["metrics"]["Response"]}
        figure, _ = WORKER.draw_one_factor(
            ["x"],
            ["signalPropagationDelaySeconds", "safeSeparationIntervalSeconds"],
            [metric], runs, set(),
        )
        try:
            figure.canvas.draw()
            renderer = figure.canvas.get_renderer()
            axis = next(axis for axis in figure.axes if axis.get_visible())
            figure_bounds = figure.bbox
            axis_bounds = axis.get_tightbbox(renderer)
            title_bounds = axis.title.get_window_extent(renderer)
            self.assertGreaterEqual(axis_bounds.x0, figure_bounds.x0)
            self.assertLessEqual(axis_bounds.x1, figure_bounds.x1)
            self.assertGreaterEqual(title_bounds.x0, figure_bounds.x0)
            self.assertLessEqual(title_bounds.x1, figure_bounds.x1)
            self.assertEqual(metric, axis.get_ylabel())
            self.assertIn("\n", axis.get_title())
        finally:
            plt.close(figure)

    def test_three_factor_mode_uses_depth_stacked_translucent_heatmap_planes(self):
        figure, panel_count = WORKER.draw_three_factors(
            ["x", "y", "z"], [], ["Response"], volume_runs(), set(),
        )
        try:
            figure.canvas.draw()
            data_axes = [axis for axis in figure.axes if getattr(axis, "name", "") == "3d"]
            self.assertEqual(1, panel_count)
            self.assertEqual(1, len(data_axes))
            axis = data_axes[0]
            planes = [
                artist for artist in axis.collections
                if isinstance(artist, WORKER.Poly3DCollection)
            ]
            self.assertEqual(2, len(planes))
            self.assertEqual(2, axis._sar_stacked_heatmap_planes)
            self.assertEqual("x", axis.get_xlabel())
            self.assertEqual("z", axis.get_ylabel())
            self.assertEqual("y", axis.get_zlabel())
            self.assertEqual("3d-stacked-heatmap-planes", figure._sar_plot_mode)
            self.assertEqual(2, figure._sar_stack_plane_count)
            self.assertTrue(all(np.max(plane.get_facecolors()[:, 3]) < 0.8
                                for plane in planes))
            self.assertEqual("Response", figure.axes[-1].get_ylabel())
            annotations = axis._sar_cell_value_annotations
            self.assertEqual(8, len(annotations))
            self.assertEqual(4, sum(
                annotation._sar_plane_index == 0
                for annotation in annotations
            ))
            self.assertEqual(
                8,
                sum(annotation._sar_cell_value is not None
                    for annotation in annotations),
            )
            self.assertEqual(8, len({annotation.get_gid()
                                     for annotation in annotations}))
            self.assertTrue(all(
                annotation._y < annotation._sar_plane_depth - 0.05
                for annotation in annotations
            ))
            self.assertTrue(all(
                annotation.get_zorder() > max(
                    plane.get_zorder() for plane in planes)
                for annotation in annotations
            ))
            self.assertTrue(all(annotation.get_path_effects()
                                for annotation in annotations))
        finally:
            plt.close(figure)

    def test_three_factor_keeps_a_complete_all_missing_z_facet(self):
        runs = volume_runs()
        for run in runs:
            if run["parameters"]["z"] == 3.0:
                run["metrics"].pop("Response")
        figure, panel_count = WORKER.draw_three_factors(
            ["x", "y", "z"], [], ["Response"], runs, set(),
        )
        try:
            data_axes = [axis for axis in figure.axes if getattr(axis, "name", "") == "3d"]
            self.assertEqual(1, panel_count)
            self.assertEqual(1, len(data_axes))
            self.assertEqual(["0", "3"], [tick.get_text() for tick in data_axes[0].get_yticklabels()])
            first, second = data_axes[0]._sar_plane_matrices
            self.assertEqual((2, 2), first.shape)
            self.assertEqual((2, 2), second.shape)
            self.assertFalse(np.any(np.ma.getmaskarray(first)))
            self.assertTrue(np.all(np.ma.getmaskarray(second)))
            self.assertEqual(2, data_axes[0]._sar_stacked_heatmap_planes)
            self.assertTrue(any("4 designed combination(s)" in warning
                                for warning in figure._sar_warnings))
        finally:
            plt.close(figure)

    def test_three_factor_multi_y_gives_each_metric_its_own_palette_and_shared_slice_scale(self):
        metrics = ["Response", "Secondary response"]
        figure, panel_count = WORKER.draw_three_factors(
            ["x", "y", "z"], [], metrics, volume_runs(), set(),
        )
        try:
            data_axes = [axis for axis in figure.axes if getattr(axis, "name", "") == "3d"]
            self.assertEqual(2, panel_count)
            self.assertEqual(2, len(data_axes))
            self.assertEqual(4, figure._sar_stack_plane_count)
            self.assertNotEqual(
                data_axes[0]._sar_metric_cmap_name,
                data_axes[1]._sar_metric_cmap_name,
            )
            self.assertEqual(2, data_axes[0]._sar_stacked_heatmap_planes)
            self.assertEqual(2, data_axes[1]._sar_stacked_heatmap_planes)
            colorbar_labels = [
                axis.get_ylabel() for axis in figure.axes
                if getattr(axis, "name", "") != "3d"
            ]
            self.assertIn("Response", colorbar_labels)
            self.assertIn("Secondary response", colorbar_labels)
        finally:
            plt.close(figure)

    def test_render_metadata_distinguishes_two_factor_and_three_factor_artists(self):
        with tempfile.TemporaryDirectory() as temporary:
            temporary = pathlib.Path(temporary)
            two_response = WORKER.render_sensitivity({
                "title": "Two factor",
                "selected_factors": ["x", "y"],
                "selected_metrics": ["Response"],
                "all_varying_factors": ["x", "y", "z"],
                "runs": volume_runs(),
            }, str(temporary / "two.png"))
            three_response = WORKER.render_sensitivity({
                "title": "Three factor",
                "selected_factors": ["x", "y", "z"],
                "selected_metrics": ["Response"],
                "all_varying_factors": ["x", "y", "z"],
                "runs": volume_runs(),
            }, str(temporary / "three.png"))
            self.assertEqual("2d-square-heatmaps", two_response["plot_mode"])
            self.assertEqual(0, two_response["stack_plane_count"])
            self.assertEqual("3d-stacked-heatmap-planes", three_response["plot_mode"])
            self.assertEqual(2, three_response["stack_plane_count"])
            self.assertIn("2 stacked heatmap plane(s)", three_response["caption"])
            self.assertGreater((temporary / "two.png").stat().st_size, 1000)
            self.assertGreater((temporary / "three.png").stat().st_size, 1000)

    def test_render_sensitivity_returns_grey_cell_warning(self):
        runs = grid_runs()
        runs[0]["metrics"].pop("Delay (s)")
        request = {
            "schema_version": 1,
            "kind": WORKER.SENSITIVITY_KIND,
            "title": "Missing response test",
            "selected_factors": ["x", "z"],
            "selected_metrics": ["Delay (s)"],
            "all_varying_factors": ["x", "z"],
            "runs": runs,
        }
        with tempfile.TemporaryDirectory() as temporary:
            output = pathlib.Path(temporary) / "missing-response.png"
            response = WORKER.render(request, str(output))
            self.assertTrue(response["success"])
            self.assertTrue(output.is_file())
            self.assertTrue(any("remain grey" in warning
                                for warning in response["warnings"]))

    def test_comparison_dispatch_writes_png_and_reports_all_y_metrics(self):
        request = {
            "schema_version": 1,
            "kind": WORKER.COMPARISON_KIND,
            "title": "Comparison test",
            "selected_metrics": ["Delay (s)", "Throughput (flights)"],
            "runs": comparison_runs(),
        }
        with tempfile.TemporaryDirectory() as temporary:
            output = pathlib.Path(temporary) / "comparison.png"
            response = WORKER.render(request, str(output))
            self.assertTrue(response["success"])
            self.assertTrue(output.is_file())
            self.assertGreater(output.stat().st_size, 1000)
            self.assertIn("2 Y metric(s)", response["caption"])
            self.assertIn("2 panel(s)", response["caption"])
            with output.open("rb") as stream:
                stream.seek(16)
                width, height = struct.unpack(">II", stream.read(8))
            self.assertGreaterEqual(width, 2500)
            self.assertGreaterEqual(height, 1200)

    def test_unknown_request_kind_is_rejected(self):
        with tempfile.TemporaryDirectory() as temporary:
            output = pathlib.Path(temporary) / "unknown.png"
            with self.assertRaisesRegex(ValueError, "Unsupported plotting request kind"):
                WORKER.render({"kind": "unknown"}, str(output))


if __name__ == "__main__":
    unittest.main()
