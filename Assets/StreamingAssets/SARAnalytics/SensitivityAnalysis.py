#!/usr/bin/env python3
"""Headless scientific plots for the SAR runtime workbench.

The worker accepts versioned JSON requests and produces PNG files with Pandas,
NumPy, and Matplotlib. Sensitivity figures never smooth measured values or invent
uncertainty: sample standard deviation is shown only for repeated runs at one exact
parameter combination. One-factor figures explicitly identify coincident context
curves, and three-factor figures render cell values in a final foreground pass so
translucent depth planes cannot hide them. Run-comparison figures keep selected
simulation records on the X axis and give every selected output metric its own
independently scaled panel.
"""

from __future__ import annotations

import argparse
import functools
import json
import math
import os
import re
import statistics
import sys
import textwrap
from collections import defaultdict

# Some embedded Windows launchers expose SystemRoot but omit WINDIR. Matplotlib's
# system-font discovery expects WINDIR, so derive it without hard-coding a user path.
if os.name == "nt" and not os.environ.get("WINDIR"):
    os.environ["WINDIR"] = os.environ.get("SystemRoot", r"C:\Windows")

import matplotlib

matplotlib.use("Agg")
import matplotlib.pyplot as plt
import numpy as np
import pandas as pd
from matplotlib import colors as mcolors
from matplotlib import patheffects
from matplotlib.cm import ScalarMappable
from matplotlib.ticker import MaxNLocator, ScalarFormatter
from mpl_toolkits.mplot3d.art3d import Poly3DCollection

# ``importlib.util.spec_from_file_location`` (used by the source-level worker
# tests) does not add this script's directory to sys.path.  Normal CLI launches
# and PyInstaller do, but keeping the sibling-module lookup explicit makes all
# three supported entry paths deterministic.
SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))
if SCRIPT_DIR not in sys.path:
    sys.path.insert(0, SCRIPT_DIR)
from TrafficHeatmapAnalysis import (
    TRAFFIC_HEATMAP_KIND,
    render_traffic_heatmap,
)

plt.rcParams.update({
    "font.family": "sans-serif",
    "font.sans-serif": [
        "Microsoft YaHei", "SimHei", "Noto Sans CJK SC", "DejaVu Sans"
    ],
    "font.size": 12.5,
    "axes.titlesize": 15.0,
    "axes.titleweight": "bold",
    "axes.labelsize": 13.0,
    "axes.labelweight": "normal",
    "axes.linewidth": 0.85,
    "axes.edgecolor": "#53606A",
    "axes.facecolor": "#FFFFFF",
    "xtick.labelsize": 11.5,
    "ytick.labelsize": 11.5,
    "xtick.color": "#3E4A52",
    "ytick.color": "#3E4A52",
    "legend.fontsize": 10.0,
    "figure.facecolor": "#FFFFFF",
    "savefig.facecolor": "#FFFFFF",
    "savefig.dpi": 300,
    "axes.unicode_minus": False,
})

SENSITIVITY_KIND = "tested-grid-empirical-response"
COMPARISON_KIND = "run-metric-bar-comparison"
MAX_METRICS = 12
MAX_THREE_FACTOR_PANELS = 36
THREE_FACTOR_FACETS_PER_ROW = 4
THREE_FACTOR_MAX_COLUMNS = 2
METRIC_COLORMAPS = (
    # Endpoints are intentionally truncated. This keeps every scale chromatic
    # and perceptually ordered without the near-black or near-white extremes
    # that made sparse/constant heatmaps look like empty black panels.
    ("viridis", 0.24, 0.92),
    ("plasma", 0.23, 0.90),
    ("GnBu", 0.18, 0.90),
    ("YlOrRd", 0.16, 0.88),
    ("YlGnBu", 0.18, 0.88),
    ("PuRd", 0.18, 0.88),
    ("BuPu", 0.18, 0.88),
    ("PuBuGn", 0.18, 0.90),
    ("OrRd", 0.18, 0.88),
    ("Greens", 0.18, 0.88),
    ("Blues", 0.20, 0.88),
    ("YlOrBr", 0.18, 0.88),
)
INK = "#263238"
MUTED_INK = "#52616B"
GRID_COLOR = "#D8E0E5"
MISSING_COLOR = "#EEF2F4"


def finite_number(value):
    try:
        number = float(value)
    except (TypeError, ValueError):
        return None
    return number if math.isfinite(number) else None


def string_list(value):
    if value is None:
        return []
    if isinstance(value, str):
        return [value]
    return list(value)


def is_cumulative_metric(name: str) -> bool:
    value = (name or "").lower()
    if any(token in value for token in (
        "average", " avg", "avg ", "mean", "rate", "ratio", "duration",
        "per second", "/s", "variance", "proportion", "percent", "%",
    )):
        return False
    return any(token in value for token in (
        "total", "count", "collision", "booking", "sample", "successful",
        "failed", "flight", "mission", "throughput",
    ))


def context_label(names, values):
    if not names:
        return "All selected runs"
    return " · ".join(f"{name}={value:g}" for name, value in zip(names, values))


def context_legend_label(names, values):
    """Use one factor per line so long context labels remain readable."""
    if not names:
        return "All selected runs"
    return "\n".join(f"{name}={value:g}" for name, value in zip(names, values))


def response_series_fully_overlap(first_samples, second_samples,
                                  relative_tolerance=1e-7,
                                  absolute_tolerance=1e-9):
    """Return whether two complete empirical response series are coincident.

    A visual overlap includes both the mean line and its error bars.  Therefore
    the two series must have the same finite X support and matching lower/upper
    error bounds at every X.  A missing X level never creates a false overlap
    merely because the remaining common points happen to match.
    """
    if len(first_samples) != len(second_samples) or not first_samples:
        return False
    for first, second in zip(first_samples, second_samples):
        first_x, first_item = first
        second_x, second_item = second
        if not math.isclose(
                first_x, second_x,
                rel_tol=relative_tolerance, abs_tol=absolute_tolerance):
            return False
        first_mean = first_item["mean"]
        second_mean = second_item["mean"]
        first_error = first_item["std"] or 0.0
        second_error = second_item["std"] or 0.0
        comparisons = (
            (first_mean, second_mean),
            (first_mean - first_error, second_mean - second_error),
            (first_mean + first_error, second_mean + second_error),
        )
        if not all(math.isclose(
                left, right,
                rel_tol=relative_tolerance, abs_tol=absolute_tolerance)
                   for left, right in comparisons):
            return False
    return True


def coincident_series_groups(series_entries):
    """Group fully coincident one-factor series by their entry indexes."""
    remaining = set(range(len(series_entries)))
    result = []
    while remaining:
        reference_index = min(remaining)
        remaining.remove(reference_index)
        reference = series_entries[reference_index]["samples"]
        group = [reference_index]
        for candidate_index in sorted(tuple(remaining)):
            candidate = series_entries[candidate_index]["samples"]
            if response_series_fully_overlap(reference, candidate):
                group.append(candidate_index)
                remaining.remove(candidate_index)
        if len(group) > 1:
            result.append(group)
    return result


def exact_groups(runs, factors, metric, normalize):
    groups = defaultdict(list)
    for run in runs:
        parameters = run.get("parameters") or {}
        metrics = run.get("metrics") or {}
        coordinates = []
        valid = True
        for factor in factors:
            number = finite_number(parameters.get(factor))
            if number is None:
                valid = False
                break
            coordinates.append(number)
        value = finite_number(metrics.get(metric))
        duration = finite_number(run.get("duration"))
        if not valid or value is None or (normalize and (duration is None or duration <= 0)):
            continue
        groups[tuple(coordinates)].append(value / duration if normalize else value)
    result = {}
    for key, values in groups.items():
        result[key] = {
            "mean": statistics.fmean(values),
            "std": statistics.stdev(values) if len(values) > 1 else None,
            "count": len(values),
        }
    return result


def exact_parameter_design(runs, factors):
    """Return the finite parameter combinations represented by the run design.

    Response availability is deliberately not considered here.  Plot axes and
    facets must describe the experiment that was run, even when one output
    metric is absent or non-finite at a designed combination.
    """
    combinations = set()
    for run in runs:
        parameters = run.get("parameters") or {}
        coordinates = []
        for factor in factors:
            number = finite_number(parameters.get(factor))
            if number is None:
                break
            coordinates.append(number)
        else:
            combinations.add(tuple(coordinates))
    return sorted(combinations)


def empirical_grid_warning(metric, design, groups, expected_cell_count):
    """Describe grey cells without silently inventing a response surface."""
    design_keys = set(design)
    response_keys = set(groups)
    missing_responses = len(design_keys - response_keys)
    untested_cells = max(0, int(expected_cell_count) - len(design_keys))
    details = []
    if missing_responses:
        details.append(
            f"{missing_responses} designed combination(s) have no finite response"
        )
    if untested_cells:
        details.append(
            f"{untested_cells} Cartesian cell(s) were not present in the run design"
        )
    if not details:
        return ""
    return (
        f"Y metric '{metric}': " + "; ".join(details) +
        ". These cells remain grey; no interpolation or marginal mean was used."
    )


def duration_mismatch(runs):
    values = [finite_number(run.get("duration")) for run in runs]
    if any(value is None or value <= 0 for value in values):
        return True
    if len(values) < 2:
        return False
    minimum, maximum = min(values), max(values)
    # Ignore ordinary frame-boundary overshoot; only a materially different
    # simulated horizon warrants per-second normalization.
    return maximum - minimum > max(1.0, maximum * 0.01)


def subplot_grid(panel_count, maximum_columns=3):
    columns = min(maximum_columns, max(1, math.ceil(math.sqrt(panel_count))))
    rows = math.ceil(panel_count / columns)
    return rows, columns


def bar_subplot_grid(metric_count, run_count):
    # Long categorical X axes need wider panels; avoid squeezing them into three
    # columns even when many output metrics are selected.
    columns = 1 if metric_count == 1 or run_count > 12 else min(2, metric_count)
    rows = math.ceil(metric_count / columns)
    return rows, columns


def metric_scale(groups):
    values = [item["mean"] for item in groups.values() if math.isfinite(item["mean"])]
    if not values:
        return None, None
    minimum, maximum = min(values), max(values)
    if math.isclose(minimum, maximum):
        # Anchor constant metrics to a meaningful zero baseline. In particular,
        # an all-zero collision metric should use the coloured low end of its
        # scale rather than the grey midpoint of an artificial -1..1 range.
        if math.isclose(minimum, 0.0, abs_tol=1e-12):
            return 0.0, 1.0
        padding = max(abs(minimum) * 0.08, 1e-9)
        if minimum > 0.0:
            return 0.0, maximum + padding
        return minimum - padding, 0.0
    return minimum, maximum


def metric_label(metric, normalized_metrics):
    return metric + (" / s" if metric in normalized_metrics else "")


@functools.lru_cache(maxsize=None)
def metric_cmap(metric_index):
    name, lower, upper = METRIC_COLORMAPS[metric_index % len(METRIC_COLORMAPS)]
    source = plt.get_cmap(name)
    samples = source(np.linspace(lower, upper, 256))
    cmap = mcolors.LinearSegmentedColormap.from_list(
        f"sar_{metric_index % len(METRIC_COLORMAPS)}_{name}", samples,
    )
    return cmap.with_extremes(bad=MISSING_COLOR)


def metric_color(metric_index, shade=0.68):
    return metric_cmap(metric_index)(shade)


def configure_numeric_axis(axis, direction="y"):
    formatter = ScalarFormatter(useMathText=True)
    formatter.set_powerlimits((-3, 4))
    locator = MaxNLocator(nbins=7, min_n_ticks=3)
    if direction == "x":
        axis.xaxis.set_major_locator(locator)
        axis.xaxis.set_major_formatter(formatter)
    else:
        axis.yaxis.set_major_locator(locator)
        axis.yaxis.set_major_formatter(formatter)


def panel_label(index):
    if index < 0:
        raise ValueError("Panel index cannot be negative.")
    # Spreadsheet-style base-26 labels keep the advertised 36-panel limit
    # readable: a ... z, aa ... az, ba ...
    letters = []
    value = index
    while True:
        value, remainder = divmod(value, 26)
        letters.append(chr(ord("a") + remainder))
        if value == 0:
            break
        value -= 1
    return f"({''.join(reversed(letters))})"


def style_cartesian_axis(axis, grid_axis="both"):
    axis.set_facecolor("#FFFFFF")
    axis.grid(True, axis=grid_axis, color=GRID_COLOR, linestyle="--",
              linewidth=0.75, alpha=0.72)
    axis.set_axisbelow(True)
    axis.spines["top"].set_visible(False)
    axis.spines["right"].set_visible(False)
    axis.spines["left"].set_color("#7A8790")
    axis.spines["bottom"].set_color("#7A8790")
    axis.tick_params(direction="out", length=3.5, width=0.75, pad=4)


def readable_text_color(rgba):
    luminance = 0.2126 * rgba[0] + 0.7152 * rgba[1] + 0.0722 * rgba[2]
    return "#182126" if luminance > 0.55 else "#FFFFFF"


def cell_font_size(row_count, column_count):
    largest = max(row_count, column_count)
    if largest <= 5:
        return 12.0
    if largest <= 8:
        return 10.5
    return 8.8


def style_colorbar(colorbar, label):
    colorbar.set_label(label, labelpad=8, fontsize=12.0, color=INK)
    colorbar.ax.tick_params(labelsize=10.5, length=2.8, width=0.65, pad=3)
    colorbar.locator = MaxNLocator(nbins=6, min_n_ticks=3)
    colorbar.update_ticks()
    colorbar.outline.set_edgecolor("#7A8790")
    colorbar.outline.set_linewidth(0.65)


def draw_one_factor(selected, contexts, metrics, runs, normalized_metrics):
    if len(metrics) > MAX_METRICS:
        raise ValueError(f"Select at most {MAX_METRICS} Y metrics for one figure.")
    rows, columns = subplot_grid(len(metrics))
    # The plotting rows keep a fixed physical height.  Footer, X-axis title and
    # the shared fixed-context legend receive their own physical bands below the
    # axes so a wide Unity viewport cannot squeeze them into one another.
    base_height = 4.9 * rows
    figure, axes = plt.subplots(
        rows, columns,
        figsize=(5.9 * columns, base_height),
        squeeze=False,
    )
    x_name = selected[0]
    design = exact_parameter_design(runs, selected + contexts)
    context_values = sorted({key[1:] for key in design}) or [tuple()]
    context_palette = plt.get_cmap("viridis")
    context_colors = context_palette(
        np.linspace(0.18, 0.86, max(1, len(context_values))))
    series_by_metric = {}
    overlap_groups_by_metric = {}
    for metric in metrics:
        groups = exact_groups(
            runs, selected + contexts, metric, metric in normalized_metrics)
        if not groups:
            raise ValueError(f"No finite samples are available for Y metric '{metric}'.")
        entries = []
        for context_index, context in enumerate(context_values):
            samples = sorted(
                (key[0], value) for key, value in groups.items()
                if key[1:] == context
            )
            if samples:
                entries.append({
                    "context_index": context_index,
                    "context": context,
                    "label": context_legend_label(contexts, context),
                    "samples": samples,
                })
        series_by_metric[metric] = entries
        overlap_groups_by_metric[metric] = coincident_series_groups(entries)
    has_coincident_series = any(overlap_groups_by_metric.values())
    context_ids = {
        context: f"C{context_index + 1}"
        for context_index, context in enumerate(context_values)
    }
    shared_legend = {}
    annotate_points = len(context_values) <= 3
    for metric_index, metric in enumerate(metrics):
        axis = axes.flat[metric_index]
        entries = series_by_metric[metric]
        overlap_entry_groups = overlap_groups_by_metric[metric]
        overlap_context_groups = [
            [entries[entry_index]["context_index"] for entry_index in group]
            for group in overlap_entry_groups
        ]
        duplicate_annotation_contexts = {
            context_index
            for group in overlap_context_groups
            for context_index in group[1:]
        }
        # Context colours are intentionally invariant across Y panels, allowing
        # one compact shared legend below all X axes instead of duplicate labels
        # competing with each individual plotting region.
        for entry in entries:
            context_index = entry["context_index"]
            context = entry["context"]
            samples = entry["samples"]
            x = [item[0] for item in samples]
            y = [item[1]["mean"] for item in samples]
            lower = [item[1]["std"] or 0.0 for item in samples]
            label = entry["label"]
            display_label = label
            if contexts and has_coincident_series:
                display_label = f"{context_ids[context]} · {label}"
            handle = axis.errorbar(
                x, y, yerr=lower, marker="o", linewidth=2.25, markersize=7.2,
                markeredgecolor="white", markeredgewidth=0.85,
                capsize=3.5, capthick=1.0, color=context_colors[context_index],
                label=display_label if contexts else None,
            )
            if contexts and display_label not in shared_legend:
                shared_legend[display_label] = handle
            if annotate_points and context_index not in duplicate_annotation_contexts:
                for x_value, item in samples:
                    suffix = f"\nn={item['count']}" if item["count"] > 1 else ""
                    axis.annotate(
                        f"{item['mean']:.3g}{suffix}", (x_value, item["mean"]),
                        textcoords="offset points", xytext=(0, 9), ha="center",
                        fontsize=10.5, color=INK, fontweight="normal",
                    )
        axis.set_xlabel(x_name, labelpad=7)
        axis.set_ylabel(metric_label(metric, normalized_metrics), labelpad=7)
        title_lines = textwrap.wrap(
            metric_label(metric, normalized_metrics), width=43,
            break_long_words=False, break_on_hyphens=False,
        ) or [metric_label(metric, normalized_metrics)]
        axis.set_title(
            f"{panel_label(metric_index)} {title_lines[0]}" +
            ("\n" + "\n".join(title_lines[1:]) if len(title_lines) > 1 else ""),
            color=INK, pad=10,
        )
        style_cartesian_axis(axis)
        configure_numeric_axis(axis, "x")
        configure_numeric_axis(axis, "y")
        axis.margins(x=0.08, y=0.16)
        axis._sar_overlap_groups = []
        axis._sar_overlap_annotations = []
        if overlap_entry_groups:
            # Give callouts a dedicated empty band above the empirical response
            # envelope.  The arrows still point at the coincident curve, while
            # the opaque note boxes never conceal a measured segment.
            axis.relim()
            axis.autoscale_view()
            lower_limit, data_band_top = axis.get_ylim()
            span = max(data_band_top - lower_limit, 1e-9)
            group_count = len(overlap_entry_groups)
            extended_top = data_band_top + span * (0.12 + 0.15 * group_count)
            axis.set_ylim(lower_limit, extended_top)
            axis._sar_overlap_data_ceiling = data_band_top
            axis._sar_overlap_groups = [
                tuple(
                    context_ids[entries[entry_index]["context"]]
                    for entry_index in group
                )
                for group in overlap_entry_groups
            ]
            for group_index, group in enumerate(overlap_entry_groups):
                representative = entries[group[0]]
                samples = representative["samples"]
                point_index = min(
                    len(samples) - 1,
                    round((group_index + 1) * (len(samples) - 1) /
                          max(1, group_count + 1)),
                )
                anchor_x, anchor_item = samples[point_index]
                context_group = axis._sar_overlap_groups[group_index]
                callout_text = "Coincident: " + " = ".join(context_group)
                callout_y = data_band_top + span * (0.07 + 0.15 * group_index)
                annotation = axis.annotate(
                    callout_text,
                    xy=(anchor_x, anchor_item["mean"]),
                    xytext=(anchor_x, callout_y),
                    textcoords="data",
                    ha="center", va="center",
                    fontsize=9.4, color="#7B3026", fontweight="bold",
                    bbox={
                        "boxstyle": "round,pad=0.28",
                        "facecolor": "#FFF8F4",
                        "edgecolor": "#C85D48",
                        "linewidth": 0.85,
                        "alpha": 0.98,
                    },
                    arrowprops={
                        "arrowstyle": "-|>",
                        "color": "#C85D48",
                        "linewidth": 1.0,
                        "shrinkA": 4.0,
                        "shrinkB": 5.0,
                        "connectionstyle": "arc3,rad=0.08",
                    },
                    annotation_clip=False,
                    zorder=20,
                )
                annotation.set_gid(
                    "sar-coincident-" + "-".join(context_group))
                axis._sar_overlap_annotations.append(annotation)
    for axis in axes.flat[len(metrics):]:
        axis.set_visible(False)

    if contexts and shared_legend:
        shared_labels = list(shared_legend)
        shared_handles = [shared_legend[label] for label in shared_labels]
        maximum_line_length = max(
            (len(line) for label in shared_labels for line in label.splitlines()),
            default=0,
        )
        legend_columns = 1 if maximum_line_length > 46 else min(2, len(shared_labels))
        legend_rows = math.ceil(len(shared_labels) / max(1, legend_columns))
        maximum_label_lines = max(
            (len(label.splitlines()) for label in shared_labels), default=1)
        # Work in physical inches so adding contexts grows the canvas instead of
        # progressively squeezing the data axes.  The footer reserve protects
        # render_sensitivity's two-line scientific-method note.
        legend_row_height = 0.32 + 0.17 * maximum_label_lines
        legend_height = 0.56 + legend_rows * legend_row_height
        footer_height = 0.86
        axis_label_clearance = 0.72
        legend_band = footer_height + legend_height + axis_label_clearance
        total_height = base_height + legend_band
        figure.set_size_inches(5.9 * columns, total_height, forward=True)
        bottom = legend_band / total_height
        legend_center_y = (footer_height + 0.5 * legend_height) / total_height
        figure.legend(
            shared_handles, shared_labels, title="Fixed context",
            loc="center", bbox_to_anchor=(0.5, legend_center_y),
            frameon=False, ncol=max(1, legend_columns),
            title_fontsize=10.5, fontsize=9.5,
            columnspacing=2.2, handlelength=2.25, handletextpad=0.75,
            labelspacing=0.85,
        )
        figure._sar_legend_band_inches = legend_band
    else:
        # Even without a context legend the scientific-method footer is added
        # later by ``render_sensitivity``.  Grow the canvas and reserve enough
        # room for that footer plus the complete X-axis decoration.
        footer_height = 0.86
        axis_label_clearance = 0.72
        bottom_band = footer_height + axis_label_clearance
        total_height = base_height + bottom_band
        figure.set_size_inches(5.9 * columns, total_height, forward=True)
        bottom = bottom_band / total_height
    figure.subplots_adjust(
        left=0.11 if columns == 1 else 0.075,
        right=0.975, bottom=bottom, top=0.865,
        hspace=0.46, wspace=0.28,
    )
    figure._sar_plot_mode = "1d-response-lines"
    figure._sar_stack_plane_count = 0
    figure._sar_manual_layout = True
    return figure, len(metrics)


def draw_two_factors(selected, contexts, metrics, runs, normalized_metrics):
    design = exact_parameter_design(runs, selected + contexts)
    if not design:
        raise ValueError("No finite parameter combinations are available for the selected X factors.")
    grouped_by_metric = {
        metric: exact_groups(runs, selected + contexts, metric, metric in normalized_metrics)
        for metric in metrics
    }
    for metric, groups in grouped_by_metric.items():
        if not groups:
            raise ValueError(f"No finite samples are available for Y metric '{metric}'.")
    x_values = sorted({key[0] for key in design})
    y_values = sorted({key[1] for key in design})
    context_values = sorted({key[2:] for key in design}) or [tuple()]
    specs = [
        (metric, context)
        for metric in metrics
        for context in context_values
    ]
    if len(specs) > MAX_METRICS:
        raise ValueError(
            f"The requested two-factor figure needs {len(specs)} panels because unselected "
            "varying parameters are kept as fixed contexts. Select more X parameters or fewer Y metrics."
        )
    # Fixed-context panels belonging to the same metric share a row and one
    # numerical scale/colour bar.  This is deliberately the same large-facet
    # grammar previously used for three-factor slices: it keeps square cells
    # and readable labels instead of shrinking several contexts into generic
    # subplot boxes.
    row_specs = []
    for metric in metrics:
        for start in range(0, len(context_values), THREE_FACTOR_FACETS_PER_ROW):
            row_specs.append((
                metric,
                context_values[start:start + THREE_FACTOR_FACETS_PER_ROW],
            ))
    facet_columns = max(1, max((len(item[1]) for item in row_specs), default=1))
    row_count = max(1, len(row_specs))
    figure_width = max(7.4, 4.75 * facet_columns + 1.05)
    figure_height = max(6.35, 5.15 * row_count + 0.65)
    figure = plt.figure(figsize=(figure_width, figure_height))
    grid_top = 0.80 if row_count == 1 else 0.89
    grid_bottom = 0.17 if row_count == 1 else 0.095
    grid = figure.add_gridspec(
        row_count, facet_columns + 1,
        left=0.065, right=0.945, bottom=grid_bottom, top=grid_top,
        width_ratios=([1.0] * facet_columns) + [0.055],
        hspace=0.54, wspace=0.24,
    )
    metric_indexes = {metric: index for index, metric in enumerate(metrics)}
    warnings = []
    expected_cell_count = len(x_values) * len(y_values) * len(context_values)
    for metric, groups in grouped_by_metric.items():
        warning = empirical_grid_warning(metric, design, groups, expected_cell_count)
        if warning:
            warnings.append(warning)
    panel_index = 0
    for row_index, (metric, row_contexts) in enumerate(row_specs):
        groups = grouped_by_metric[metric]
        scale_min, scale_max = metric_scale(groups)
        cmap = metric_cmap(metric_indexes[metric])
        last_image = None
        for column_index, context in enumerate(row_contexts):
            axis = figure.add_subplot(grid[row_index, column_index])
            subset = {
                key[:2]: value for key, value in groups.items()
                if key[2:] == context
            }
            matrix = np.full((len(y_values), len(x_values)), np.nan, dtype=float)
            for (x_value, y_value), item in subset.items():
                matrix[y_values.index(y_value), x_values.index(x_value)] = item["mean"]
            last_image = axis.imshow(
                np.ma.masked_invalid(matrix), origin="lower", aspect="equal",
                cmap=cmap, vmin=scale_min, vmax=scale_max,
                interpolation="nearest",
            )
            # Equal data aspect makes every heatmap cell square.  The explicit
            # box aspect prevents a wide embedded viewport from stretching it.
            axis.set_box_aspect(len(y_values) / max(1, len(x_values)))
            axis.set_xticks(
                range(len(x_values)), [f"{value:g}" for value in x_values],
                fontsize=11.5,
            )
            axis.set_yticks(
                range(len(y_values)), [f"{value:g}" for value in y_values],
                fontsize=11.5,
            )
            axis.set_xticks(np.arange(-0.5, len(x_values), 1), minor=True)
            axis.set_yticks(np.arange(-0.5, len(y_values), 1), minor=True)
            axis.grid(
                which="minor", color="white", linestyle="-",
                linewidth=1.15, alpha=0.82,
            )
            axis.tick_params(which="minor", bottom=False, left=False)
            axis.tick_params(which="major", length=0, pad=5)
            axis.set_xlabel(selected[0], labelpad=7)
            axis.set_ylabel(selected[1], labelpad=7)
            title = metric_label(metric, normalized_metrics)
            if contexts:
                title += "\n" + context_label(contexts, context)
            axis.set_title(
                f"{panel_label(panel_index)} {title}", color=INK, pad=10)
            for spine in axis.spines.values():
                spine.set_color("#7A8790")
                spine.set_linewidth(0.75)
            annotation_size = cell_font_size(len(y_values), len(x_values))
            for (x_value, y_value), item in subset.items():
                xi, yi = x_values.index(x_value), y_values.index(y_value)
                text = f"{item['mean']:.3g}"
                if item["std"] is not None:
                    text += f"\n±{item['std']:.2g} (n={item['count']})"
                rgba = last_image.cmap(last_image.norm(item["mean"]))
                axis.text(
                    xi, yi, text, ha="center", va="center",
                    fontsize=annotation_size,
                    color=readable_text_color(rgba), fontweight="bold",
                )
            panel_index += 1
        for column_index in range(len(row_contexts), facet_columns):
            unused = figure.add_subplot(grid[row_index, column_index])
            unused.set_visible(False)
        colorbar_axis = figure.add_subplot(grid[row_index, -1])
        colorbar = figure.colorbar(last_image, cax=colorbar_axis)
        style_colorbar(colorbar, metric_label(metric, normalized_metrics))
    figure._sar_warnings = warnings
    figure._sar_plot_mode = "2d-square-heatmaps"
    figure._sar_stack_plane_count = 0
    figure._sar_manual_layout = True
    return figure, len(specs)


def draw_three_factors(selected, contexts, metrics, runs, normalized_metrics):
    """Draw exact 3-X responses as depth-stacked translucent heatmap planes.

    X1 and X2 remain the two square-cell dimensions of every heatmap.  Each
    tested X3 level owns a separate vertical plane along the depth axis, while
    face colour encodes the selected Y response.  This is a categorical 3-D
    stack, not an interpolated response surface: an untested or non-finite cell
    remains translucent grey and is explicitly marked with a multiplication
    sign whenever the grid is small enough to label safely.
    """
    design = exact_parameter_design(runs, selected + contexts)
    if not design:
        raise ValueError("No finite parameter combinations are available for the selected X factors.")
    grouped_by_metric = {
        metric: exact_groups(runs, selected + contexts, metric, metric in normalized_metrics)
        for metric in metrics
    }
    for metric, metric_groups in grouped_by_metric.items():
        if not metric_groups:
            raise ValueError(f"No finite samples are available for Y metric '{metric}'.")
    x_values = sorted({key[0] for key in design})
    y_values = sorted({key[1] for key in design})
    z_values = sorted({key[2] for key in design})
    context_values = sorted({key[3:] for key in design}) or [tuple()]
    specs = [
        (metric, context)
        for metric in metrics
        for context in context_values
    ]
    panel_count = len(specs)
    stack_plane_count = panel_count * len(z_values)
    if stack_plane_count > MAX_THREE_FACTOR_PANELS:
        raise ValueError(
            f"The requested three-factor figure needs {stack_plane_count} stacked heatmap planes. "
            "Select fewer Y metrics, fixed contexts, or tested levels of the third X parameter."
        )

    rows, columns = subplot_grid(panel_count, THREE_FACTOR_MAX_COLUMNS)
    figure = plt.figure(figsize=(8.1 * columns, 6.75 * rows))
    figure.subplots_adjust(
        left=0.035, right=0.955, bottom=0.105, top=0.875,
        hspace=0.27, wspace=0.20,
    )
    metric_indexes = {metric: index for index, metric in enumerate(metrics)}
    warnings = []
    expected_cell_count = (
        len(x_values) * len(y_values) * len(z_values) * len(context_values)
    )
    for metric, metric_groups in grouped_by_metric.items():
        warning = empirical_grid_warning(
            metric, design, metric_groups, expected_cell_count)
        if warning:
            warnings.append(warning)
    depth_spacing = 1.18
    label_cells = len(x_values) * len(y_values) * len(z_values) <= 64
    for panel_index, (metric, context) in enumerate(specs):
        metric_groups = grouped_by_metric[metric]
        scale_min, scale_max = metric_scale(metric_groups)
        metric_index = metric_indexes[metric]
        cmap = metric_cmap(metric_index)
        norm = mcolors.Normalize(vmin=scale_min, vmax=scale_max)
        axis = figure.add_subplot(rows, columns, panel_index + 1, projection="3d")
        axis.set_proj_type("ortho")
        axis.view_init(elev=24, azim=-58)
        plane_matrices = []
        pending_cell_annotations = []
        for depth_index, z_value in enumerate(z_values):
            depth = depth_index * depth_spacing
            subset = {
                key[:2]: value for key, value in metric_groups.items()
                if key[2] == z_value and key[3:] == context
            }
            matrix = np.full((len(y_values), len(x_values)), np.nan, dtype=float)
            for (x_value, y_value), item in subset.items():
                matrix[y_values.index(y_value), x_values.index(x_value)] = item["mean"]
            plane_matrices.append(np.ma.masked_invalid(matrix))
            vertices = []
            facecolors = []
            for y_index in range(len(y_values)):
                for x_index in range(len(x_values)):
                    vertices.append([
                        (x_index - 0.5, depth, y_index - 0.5),
                        (x_index + 0.5, depth, y_index - 0.5),
                        (x_index + 0.5, depth, y_index + 0.5),
                        (x_index - 0.5, depth, y_index + 0.5),
                    ])
                    value = matrix[y_index, x_index]
                    if math.isfinite(value):
                        rgba = list(cmap(norm(value)))
                        rgba[3] = 0.76
                    else:
                        rgba = list(mcolors.to_rgba(MISSING_COLOR, alpha=0.20))
                    facecolors.append(rgba)
            plane = Poly3DCollection(
                vertices,
                facecolors=facecolors,
                edgecolors=mcolors.to_rgba("#FFFFFF", alpha=0.76),
                linewidths=0.90,
                zsort="average",
            )
            plane.set_rasterized(True)
            axis.add_collection3d(plane)

            # A dark outline separates translucent planes without obscuring the
            # heatmap cells behind them.
            border_x = [-0.5, len(x_values) - 0.5, len(x_values) - 0.5, -0.5, -0.5]
            border_z = [-0.5, -0.5, len(y_values) - 0.5, len(y_values) - 0.5, -0.5]
            axis.plot(
                border_x, [depth] * 5, border_z,
                color=mcolors.to_rgba(INK, alpha=0.55), linewidth=0.85,
            )

            if label_cells:
                annotation_size = max(
                    7.4, cell_font_size(len(y_values), len(x_values)) - 2.8)
                for y_index in range(len(y_values)):
                    for x_index in range(len(x_values)):
                        value = matrix[y_index, x_index]
                        if math.isfinite(value):
                            rgba = cmap(norm(value))
                            text = f"{value:.3g}"
                            color = readable_text_color(rgba)
                            weight = "bold"
                        else:
                            text = "×"
                            color = "#73808A"
                            weight = "normal"
                        pending_cell_annotations.append({
                            "x": x_index,
                            "depth": depth,
                            "row": y_index,
                            "text": text,
                            "color": color,
                            "weight": weight,
                            "fontsize": annotation_size,
                            "plane_index": depth_index,
                            "value": value if math.isfinite(value) else None,
                        })

        # Add every label only after all translucent collections exist.  A
        # modest offset towards the camera prevents co-planar depth fighting;
        # explicit high z-order and a contrasting stroke keep the front plane
        # (and every visible rear plane) readable after Matplotlib's 3-D sort.
        cell_annotations = []
        annotation_depth_offset = depth_spacing * 0.075
        for pending in sorted(
                pending_cell_annotations,
                key=lambda item: item["plane_index"], reverse=True):
            foreground = pending["color"]
            stroke = "#172126" if foreground == "#FFFFFF" else "#FFFFFF"
            annotation = axis.text(
                pending["x"], pending["depth"] - annotation_depth_offset,
                pending["row"], pending["text"],
                ha="center", va="center", fontsize=pending["fontsize"],
                color=foreground, fontweight=pending["weight"],
                zorder=1000 + len(z_values) - pending["plane_index"],
                clip_on=False,
            )
            annotation.set_path_effects([
                patheffects.withStroke(linewidth=1.35, foreground=stroke),
                patheffects.Normal(),
            ])
            annotation.set_gid(
                f"sar-3d-cell-p{pending['plane_index']}-"
                f"r{pending['row']}-x{pending['x']}")
            annotation._sar_plane_index = pending["plane_index"]
            annotation._sar_plane_depth = pending["depth"]
            annotation._sar_cell_value = pending["value"]
            cell_annotations.append(annotation)

        axis._sar_stacked_heatmap_planes = len(z_values)
        axis._sar_plane_matrices = plane_matrices
        axis._sar_cell_value_annotations = cell_annotations
        axis._sar_metric_cmap_name = cmap.name
        axis._sar_metric_clim = (scale_min, scale_max)
        axis.set_xlim(-0.5, len(x_values) - 0.5)
        maximum_depth = (len(z_values) - 1) * depth_spacing
        axis.set_ylim(-0.42 * depth_spacing, maximum_depth + 0.42 * depth_spacing)
        axis.set_zlim(-0.5, len(y_values) - 0.5)
        axis.set_xticks(range(len(x_values)), [f"{value:g}" for value in x_values])
        axis.set_yticks(
            [index * depth_spacing for index in range(len(z_values))],
            [f"{value:g}" for value in z_values],
        )
        axis.set_zticks(range(len(y_values)), [f"{value:g}" for value in y_values])
        axis.set_xlabel(selected[0], labelpad=10)
        axis.set_ylabel(selected[2], labelpad=12)
        axis.set_zlabel(selected[1], labelpad=10)
        box_depth = max(1.8, maximum_depth + 1.0)
        axis.set_box_aspect((max(2.0, len(x_values)), box_depth,
                             max(2.0, len(y_values))))
        axis.tick_params(labelsize=9.6, pad=2)
        axis.xaxis.pane.set_facecolor((0.96, 0.97, 0.98, 0.42))
        axis.yaxis.pane.set_facecolor((0.96, 0.97, 0.98, 0.30))
        axis.zaxis.pane.set_facecolor((0.96, 0.97, 0.98, 0.42))
        for axis_dimension in (axis.xaxis, axis.yaxis, axis.zaxis):
            axis_dimension.pane.set_edgecolor(mcolors.to_rgba("#A7B2B9", 0.55))
        title = f"{panel_label(panel_index)} {metric_label(metric, normalized_metrics)}"
        if contexts:
            title += "\n" + context_label(contexts, context)
        axis.set_title(title, color=INK, pad=15)

        mappable = ScalarMappable(norm=norm, cmap=cmap)
        mappable.set_array([])
        colorbar = figure.colorbar(
            mappable, ax=axis, shrink=0.70, pad=0.09,
            fraction=0.042, aspect=25,
        )
        style_colorbar(colorbar, metric_label(metric, normalized_metrics))

    # Empty slots in the rectangular metric/context grid are deliberately
    # omitted instead of leaving visible 2-D placeholder axes.

    figure._sar_warnings = warnings
    figure._sar_plot_mode = "3d-stacked-heatmap-planes"
    figure._sar_stack_plane_count = stack_plane_count
    figure._sar_manual_layout = True
    return figure, panel_count


def concise_run_label(run, index, total_count):
    batch_index = int(finite_number(run.get("batch_index")) or 0)
    batch_count = int(finite_number(run.get("batch_count")) or 0)
    run_kind = str(run.get("run_kind") or "").strip().lower()
    if batch_index > 0 and (batch_count > 0 or run_kind == "batchmember"):
        return f"Batch {batch_index}/{max(batch_index, batch_count)}"

    # Older request files may only carry a human-readable label. Preserve its
    # compact Batch x/y prefix, but deliberately discard the opaque run-id suffix.
    label = str(run.get("label") or "").strip()
    batch_match = re.match(r"^Batch\s+(\d+)\s*/\s*(\d+)", label, re.IGNORECASE)
    if batch_match:
        return f"Batch {batch_match.group(1)}/{batch_match.group(2)}"
    return f"Run {index + 1}/{max(1, total_count)}"


def build_comparison_frame(metrics, runs, normalized_metrics):
    records = []
    for run_index, run in enumerate(runs):
        run_metrics = run.get("metrics") or {}
        duration = finite_number(run.get("duration"))
        for metric_index, metric in enumerate(metrics):
            value = finite_number(run_metrics.get(metric))
            if value is None:
                raise ValueError(
                    f"Selected run '{run.get('run_id') or run_index + 1}' has no finite value for Y metric '{metric}'."
                )
            if metric in normalized_metrics:
                if duration is None or duration <= 0:
                    raise ValueError(
                        f"Selected run '{run.get('run_id') or run_index + 1}' has no positive duration for normalization."
                    )
                value /= duration
            records.append({
                "run_index": run_index,
                "run_id": str(run.get("run_id") or f"run-{run_index + 1}"),
                "run_label": concise_run_label(run, run_index, len(runs)),
                "metric_index": metric_index,
                "metric": metric,
                "value": value,
            })
    return pd.DataFrame.from_records(records)


def bar_axis_limits(values):
    minimum, maximum = float(np.min(values)), float(np.max(values))
    span = maximum - minimum
    padding = max(abs(minimum), abs(maximum), 1.0) * 0.08 if math.isclose(span, 0.0) else span * 0.12
    if minimum >= 0:
        return 0.0, maximum + padding
    if maximum <= 0:
        return minimum - padding, 0.0
    return minimum - padding, maximum + padding


def draw_run_comparison(metrics, runs, normalized_metrics):
    if len(metrics) > MAX_METRICS:
        raise ValueError(f"Select at most {MAX_METRICS} Y metrics for one figure.")
    if len(runs) < 2:
        raise ValueError("Select at least two completed simulation records for comparison.")
    frame = build_comparison_frame(metrics, runs, normalized_metrics)
    rows, columns = bar_subplot_grid(len(metrics), len(runs))
    panel_width = min(12.0, max(6.4, 3.2 + 0.52 * len(runs)))
    rotation = 0 if len(runs) <= 6 else 22 if len(runs) <= 14 else 38 if len(runs) <= 22 else 52
    panel_height = 4.8 + (0.75 if rotation >= 45 else 0.3 if rotation else 0.0)
    figure, axes = plt.subplots(
        rows, columns,
        figsize=(panel_width * columns, panel_height * rows),
        squeeze=False,
    )
    x_positions = np.arange(len(runs))
    for metric_index, metric in enumerate(metrics):
        axis = axes.flat[metric_index]
        subset = frame[frame["metric"] == metric].sort_values("run_index")
        values = subset["value"].to_numpy(dtype=float)
        labels = subset["run_label"].tolist()
        palette = metric_cmap(metric_index)
        bar_colors = palette(np.linspace(0.27, 0.83, max(1, len(values))))
        bars = axis.bar(
            x_positions, values, width=0.72,
            color=bar_colors, edgecolor="#FFFFFF", linewidth=0.75, alpha=0.97,
        )
        y_label = metric_label(metric, normalized_metrics)
        axis.set_title(f"{panel_label(metric_index)} {y_label}", color=INK, pad=10)
        axis.set_xlabel("Selected simulation run", labelpad=8)
        axis.set_ylabel(y_label, labelpad=8)
        axis.set_xticks(x_positions, labels, rotation=rotation,
                        ha="right" if rotation else "center")
        axis.tick_params(axis="x", labelsize=max(9.0, 11.5 - len(runs) * 0.08))
        style_cartesian_axis(axis, "y")
        configure_numeric_axis(axis, "y")
        axis.set_ylim(*bar_axis_limits(values))
        axis.margins(x=0.025)
        if len(runs) <= 18:
            axis.bar_label(
                bars, labels=[f"{value:.3g}" for value in values],
                padding=3, fontsize=max(8.5, 11 - len(runs) // 8),
                color=INK, fontweight="normal",
            )
    for axis in axes.flat[len(metrics):]:
        axis.set_visible(False)
    return figure, len(metrics)


def save_figure(figure, output_path):
    os.makedirs(os.path.dirname(os.path.abspath(output_path)), exist_ok=True)
    maximum_inches = max(figure.get_size_inches())
    # Keep a publication-grade 300 dpi target while bounding very large
    # multi-panel exports to roughly 5.2k pixels on their longest side.
    export_dpi = max(170.0, min(300.0, 5200.0 / max(1.0, maximum_inches)))
    figure.savefig(
        output_path, dpi=export_dpi, facecolor="white",
        # Preserve the designed title/caption safety bands.  Tight bounding-box
        # cropping removed that white space and let the bitmap visually collide
        # with Unity's own analysis caption above the image viewport.
        bbox_inches=None,
    )
    plt.close(figure)


def render_sensitivity(request, output_path):
    selected = string_list(request.get("selected_factors"))
    metrics = string_list(request.get("selected_metrics"))
    all_varying = string_list(request.get("all_varying_factors") or selected)
    runs = list(request.get("runs") or [])
    if not 1 <= len(selected) <= 3:
        raise ValueError("Select between one and three input parameters (X).")
    if not metrics:
        raise ValueError("Select at least one output metric (Y).")
    contexts = [factor for factor in all_varying if factor not in selected]
    normalize = duration_mismatch(runs)
    normalized_metrics = {metric for metric in metrics if normalize and is_cumulative_metric(metric)}

    if len(selected) == 1:
        figure, panel_count = draw_one_factor(
            selected, contexts, metrics, runs, normalized_metrics)
    elif len(selected) == 2:
        figure, panel_count = draw_two_factors(
            selected, contexts, metrics, runs, normalized_metrics)
    else:
        figure, panel_count = draw_three_factors(
            selected, contexts, metrics, runs, normalized_metrics)

    title = request.get("title") or "Parameter Sensitivity Analysis"
    figure.suptitle(title, fontsize=18, fontweight="bold", color=INK, y=0.972)
    figure.text(
        0.5, 0.018,
        "Exact parameter combinations only: measured mean and sample SD;\n"
        "no synthetic smoothing. Each Y metric uses an independent scale and palette,\n"
        "shared across that metric's fixed-context panels.",
        ha="center", va="bottom", fontsize=8.9, color=MUTED_INK,
        linespacing=1.18,
    )
    if not getattr(figure, "_sar_manual_layout", False):
        # Reserve an intentionally generous top band.  Besides separating the
        # suptitle from panel titles, it leaves the embedded Unity viewer room
        # for its independent run/dimension caption without visual collision.
        figure.tight_layout(
            rect=(0.025, 0.075, 0.985, 0.895),
            pad=1.2, h_pad=2.1 if len(selected) == 1 else 1.65, w_pad=1.35,
        )
    warnings = []
    if contexts:
        warnings.append(
            "Unselected varying parameters were separated into explicit contexts; "
            "no marginal mean was used."
        )
    warnings.extend(getattr(figure, "_sar_warnings", []))
    plot_mode = getattr(figure, "_sar_plot_mode", "")
    stack_plane_count = int(getattr(figure, "_sar_stack_plane_count", 0))
    save_figure(figure, output_path)
    return {
        "success": True,
        "message": "",
        "caption": (
            f"{len(selected)} X dimension(s) · {len(metrics)} Y metric(s) · {len(runs)} run(s) · "
            f"{panel_count} panel(s)" +
            (f" · {stack_plane_count} stacked heatmap plane(s)" if stack_plane_count else "") +
            (" · cumulative metrics normalized per second because durations differ" if normalized_metrics else "")
        ),
        "plot_mode": plot_mode,
        "stack_plane_count": stack_plane_count,
        "warnings": warnings,
        "output_path": os.path.abspath(output_path),
    }


def render_comparison(request, output_path):
    metrics = string_list(request.get("selected_metrics"))
    runs = list(request.get("runs") or [])
    if not metrics:
        raise ValueError("Select at least one output metric (Y).")
    normalize = duration_mismatch(runs)
    normalized_metrics = {metric for metric in metrics if normalize and is_cumulative_metric(metric)}
    figure, panel_count = draw_run_comparison(metrics, runs, normalized_metrics)
    title = request.get("title") or "Selected Simulation Run Comparison"
    figure.suptitle(title, fontsize=18, fontweight="bold", color=INK, y=0.985)
    figure.text(
        0.5, 0.012,
        "X = selected batch members and/or independent simulation records. "
        "Every Y metric is rendered in a separate panel with its own unit label and numerical scale.",
        ha="center", fontsize=9.5, color=MUTED_INK,
    )
    figure.tight_layout(rect=(0.025, 0.065, 0.985, 0.94), pad=1.2, h_pad=1.5, w_pad=1.25)
    save_figure(figure, output_path)
    return {
        "success": True,
        "message": "",
        "caption": (
            f"{len(runs)} selected simulation record(s) · {len(metrics)} Y metric(s) · "
            f"{panel_count} panel(s)" +
            (" · cumulative metrics normalized per second because durations differ" if normalized_metrics else "")
        ),
        "warnings": [],
        "output_path": os.path.abspath(output_path),
    }


def render(request, output_path):
    kind = str(request.get("kind") or SENSITIVITY_KIND)
    if kind == SENSITIVITY_KIND:
        return render_sensitivity(request, output_path)
    if kind == COMPARISON_KIND:
        return render_comparison(request, output_path)
    if kind == TRAFFIC_HEATMAP_KIND:
        return render_traffic_heatmap(request, output_path)
    raise ValueError(f"Unsupported plotting request kind: {kind}")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--request", required=True)
    parser.add_argument("--output", required=True)
    parser.add_argument("--response", required=True)
    args = parser.parse_args()
    response = None
    exit_code = 0
    try:
        # Unity writes UTF-8 without a BOM, while Windows PowerShell 5 smoke
        # fixtures use UTF-8 with a BOM. Accepting utf-8-sig keeps both inputs
        # standards-compliant and mirrors TrafficHeatmapAnalysis.
        with open(args.request, "r", encoding="utf-8-sig") as stream:
            request = json.load(stream)
        response = render(request, args.output)
    except Exception as error:  # return a structured error to Unity
        response = {
            "success": False,
            "message": str(error),
            "caption": "",
            "warnings": [],
            "output_path": "",
        }
        exit_code = 1
    os.makedirs(os.path.dirname(os.path.abspath(args.response)), exist_ok=True)
    with open(args.response, "w", encoding="utf-8") as stream:
        json.dump(response, stream, ensure_ascii=False, indent=2)
    return exit_code


if __name__ == "__main__":
    sys.exit(main())
