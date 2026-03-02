import argparse
import csv
import itertools
from pathlib import Path


def load_points(csv_path: Path):
    points_u = []
    points_v = []

    with csv_path.open("r", encoding="utf-8", newline="") as file:
        reader = csv.DictReader(file)
        if reader.fieldnames is None:
            raise ValueError(f"CSV header is missing: {csv_path}")

        required = {"u", "v"}
        if not required.issubset(set(reader.fieldnames)):
            raise ValueError(f"CSV must contain columns {required}: {csv_path}")

        for row in reader:
            points_u.append(float(row["u"]))
            points_v.append(float(row["v"]))

    if len(points_u) < 2:
        raise ValueError(f"Need at least 2 points: {csv_path}")

    return points_u, points_v


def value_to_canvas(value, minimum, scale, padding):
    return padding + (value - minimum) * scale


def build_svg(paths, width, height, show_index):
    all_u = [u for _, (u_values, _) in paths for u in u_values]
    all_v = [v for _, (_, v_values) in paths for v in v_values]
    min_u, max_u = min(all_u), max(all_u)
    min_v, max_v = min(all_v), max(all_v)

    span_u = max(max_u - min_u, 1.0)
    span_v = max(max_v - min_v, 1.0)

    padding = 40.0
    drawable_w = max(width - 2.0 * padding, 10.0)
    drawable_h = max(height - 2.0 * padding, 10.0)

    scale = min(drawable_w / span_u, drawable_h / span_v)

    colors = itertools.cycle([
        "#1f77b4",
        "#d62728",
        "#2ca02c",
        "#9467bd",
        "#ff7f0e",
        "#17becf",
    ])

    lines = [
        f'<svg xmlns="http://www.w3.org/2000/svg" width="{width}" height="{height}" viewBox="0 0 {width} {height}">',
        '<rect x="0" y="0" width="100%" height="100%" fill="white"/>',
        f'<rect x="{padding}" y="{padding}" width="{drawable_w}" height="{drawable_h}" fill="none" stroke="#cccccc" stroke-width="1"/>',
    ]

    for file_name, (u_values, v_values), color in zip(
        [p[0] for p in paths],
        [p[1] for p in paths],
        colors,
    ):
        canvas_points = []
        for u, v in zip(u_values, v_values):
            x = value_to_canvas(u, min_u, scale, padding)
            y = height - value_to_canvas(v, min_v, scale, padding)
            canvas_points.append((x, y))

        point_text = " ".join(f"{x:.3f},{y:.3f}" for x, y in canvas_points)
        lines.append(f'<polyline points="{point_text}" fill="none" stroke="{color}" stroke-width="2"/>')

        for index, (x, y) in enumerate(canvas_points):
            lines.append(f'<circle cx="{x:.3f}" cy="{y:.3f}" r="2.5" fill="{color}"/>')
            if show_index:
                lines.append(
                    f'<text x="{x + 4:.3f}" y="{y - 4:.3f}" font-size="10" fill="#333333">{index}</text>'
                )

        if canvas_points:
            lx, ly = canvas_points[0]
            lines.append(
                f'<text x="{lx + 6:.3f}" y="{ly + 14:.3f}" font-size="11" fill="{color}">{file_name}</text>'
            )

    lines.extend(
        [
            f'<text x="{padding}" y="{height - 10}" font-size="11" fill="#666666">u: [{min_u:.3f}, {max_u:.3f}]</text>',
            f'<text x="{padding + 180}" y="{height - 10}" font-size="11" fill="#666666">v: [{min_v:.3f}, {max_v:.3f}]</text>',
            '</svg>',
        ]
    )
    return "\n".join(lines)


def parse_args():
    parser = argparse.ArgumentParser(description="Visualize BuildFaceFromPath 2D CSV output as SVG")
    parser.add_argument("csv", nargs="+", help="CSV file path(s), e.g. out/debug_path2d/path2d_planar_uv_1.csv")
    parser.add_argument("--save", help="Output SVG path")
    parser.add_argument("--show-index", action="store_true", help="Draw point index labels")
    parser.add_argument("--width", type=int, default=900, help="Output SVG width")
    parser.add_argument("--height", type=int, default=700, help="Output SVG height")
    return parser.parse_args()


def main():
    args = parse_args()
    path_data = []
    for csv_file in args.csv:
        csv_path = Path(csv_file)
        u_values, v_values = load_points(csv_path)
        path_data.append((csv_path.name, (u_values, v_values)))

    if args.save:
        output_path = Path(args.save)
    else:
        output_path = Path("out") / "debug_path2d" / "path2d_plot.svg"

    output_path.parent.mkdir(parents=True, exist_ok=True)
    svg_text = build_svg(path_data, args.width, args.height, args.show_index)
    output_path.write_text(svg_text, encoding="utf-8")
    print(f"Saved: {output_path}")


if __name__ == "__main__":
    main()
