"""Codex 등으로 만든 그림을 DrawLiar 플레이어가 마우스로 그린 것 같은 PNG로 다시 그린다.

준비: python -m pip install opencv-contrib-python
사용: python Tools/PlayerDoodle.py 입력.png 출력.png [--width 2400] [--all-colors] [--transparent] [--mess 1] [--seed 0]
입력은 흰 배경, 단색 외곽선, 단색 면 채색(그림자·그라데이션·질감 없음)일수록 잘 변환된다.
"""
import argparse, math, pathlib, re
import cv2
import numpy as np

CANVAS_WIDTH = 1200  # DrawDrawingRenderer.WIDTH
BRUSH_WIDTHS = (5, 11, 24)  # DrawApp.Drawing 굵기 선택지
BASE_COLOR_COUNT = 9  # DrawBrushSettings.BASE_COLOR_COUNT
FILL_MIN_WIDTH = 25  # 이보다 두꺼운 영역은 선이 아니라 면으로 보고 지그재그로 칠한다.
SAMPLE_STEP = 12  # 25ms 포인터 샘플 사이 마우스 이동 거리
SPUR_LENGTH = 8  # 이보다 짧게 갈라진 뼈대 가지는 버린다.
COLOR_CLUSTERS = 16  # 원본 색을 이만큼 묶고, 묶음마다 팔레트 색 하나로 바꿔 한 덩어리가 여러 색으로 갈리지 않게 한다.
COLOR_MERGE = 20  # 이보다 가까운(RGB 거리) 묶음은 같은 색으로 본다.
PAPER = (255, 255, 255)
OFFSETS = ((1, 0), (-1, 0), (0, 1), (0, -1), (1, 1), (1, -1), (-1, 1), (-1, -1))
SETTINGS_PATH = pathlib.Path(__file__).parent.parent / 'DrawLiar/Assets/DrawLiar/Scripts/UI/DrawBrushSettings.cs'

def load_palette(all_colors):
    block = SETTINGS_PATH.read_text(encoding='utf-8').split('_palette =', 1)[1].split('};', 1)[0]
    colors = [tuple(map(int, match)) if match[0] else PAPER
              for match in re.findall(r'new Color32\((\d+),(\d+),(\d+),255\)|PaperColor', block)]
    colors = colors if all_colors else colors[:BASE_COLOR_COUNT]
    return [color for color in colors if sum(color) / 3 < 235]  # 흰 종이 위에서 안 보이는 색 제외

def load_image(path):
    image = cv2.imdecode(np.fromfile(path, np.uint8), cv2.IMREAD_UNCHANGED)
    if image is None:
        raise SystemExit(f'이미지를 읽을 수 없습니다: {path}')
    if image.ndim == 2:
        image = cv2.cvtColor(image, cv2.COLOR_GRAY2BGRA)
    elif image.shape[2] == 3:
        image = cv2.cvtColor(image, cv2.COLOR_BGR2BGRA)
    alpha = image[..., 3:] / 255.0
    return (image[..., 2::-1] * alpha + 255 * (1 - alpha)).astype(np.float32)

def color_layers(rgb, palette, shape):  # 색 분리·잡티 제거는 원본 해상도에서 끝내야 가는 선이 끊기지 않는다.
    criteria = (cv2.TERM_CRITERIA_EPS + cv2.TERM_CRITERIA_MAX_ITER, 10, 1)
    _, labels, centers = cv2.kmeans(rgb.reshape(-1, 3), COLOR_CLUSTERS, None, criteria, 1, cv2.KMEANS_PP_CENTERS)
    leaders = []
    for index in np.argsort(-np.bincount(labels.ravel(), minlength=COLOR_CLUSTERS)):  # 많은 색부터 비슷한 묶음을 흡수한다.
        leader = next((leader for leader in leaders if np.linalg.norm(centers[leader] - centers[index]) < COLOR_MERGE), index)
        if leader == index:
            leaders.append(index)
        centers[index] = centers[leader]
    targets = np.float32([PAPER] + palette)
    nearest = np.argmin(((centers[:, None] - targets) ** 2).sum(-1), 1)[labels.reshape(rgb.shape[:2])]
    for index, color in enumerate(palette, 1):
        mask = cv2.morphologyEx((nearest == index).astype(np.uint8) * 255, cv2.MORPH_OPEN, np.ones((3, 3), np.uint8))
        mask = np.where(cv2.resize(mask, shape[::-1], interpolation=cv2.INTER_AREA) > 85, 255, 0).astype(np.uint8)
        if mask.any():
            yield color, mask

def scribble(mask, width, rng):
    strokes = []
    inset = cv2.getStructuringElement(cv2.MORPH_ELLIPSE, (width // 2 + 1,) * 2)
    count, labels, stats, _ = cv2.connectedComponentsWithStats(mask)
    for label in range(1, count):
        region = cv2.erode((labels == label).astype(np.uint8) * 255, inset)
        if not region.any():
            continue
        x, y, w, h = cv2.boundingRect(region)
        size = math.ceil(math.hypot(w, h)) + 2
        canvas = np.zeros((size, size), np.uint8)
        left, top = (size - w) // 2, (size - h) // 2
        canvas[top:top + h, left:left + w] = region[y:y + h, x:x + w]
        rotation = cv2.getRotationMatrix2D((size / 2, size / 2), rng.choice((-1, 1)) * rng.uniform(25, 65), 1)
        rotated = cv2.warpAffine(canvas, rotation, (size, size), flags=cv2.INTER_NEAREST)
        inverse = cv2.invertAffineTransform(rotation)
        active, done = [], []
        row = width // 3
        while row < size:
            edges = np.flatnonzero(np.diff(np.r_[0, rotated[row] > 0, 0].astype(np.int8)))
            following = []
            for start, end in zip(edges[::2], edges[1::2] - 1):
                match = next((stroke for stroke in active if stroke[1] <= end and start <= stroke[2]), None)
                if match:
                    active.remove(match)
                points = match[0] if match else []
                ends = [(start, row), (end, row)]
                if points and abs(points[-1][0] - end) < abs(points[-1][0] - start):
                    ends.reverse()
                following.append((points + ends, start, end))
            done += active
            active = following
            row += round(width * rng.uniform(.7, 1))
        offset = np.float32((x - left, y - top))
        area = stats[label, cv2.CC_STAT_AREA]
        strokes += [(area, np.float32(points) @ inverse[:, :2].T + inverse[:, 2] + offset) for points, _, _ in done + active]
    return strokes

def trace(mask):
    distance = cv2.distanceTransform(mask, cv2.DIST_L2, 3)
    ys, xs = np.nonzero(cv2.ximgproc.thinning(mask))
    pixels = set(zip(xs.tolist(), ys.tolist()))
    neighbors = lambda p: [(p[0] + dx, p[1] + dy) for dx, dy in OFFSETS if (p[0] + dx, p[1] + dy) in pixels]
    visited, paths = set(), []
    for start in sorted(pixels, key=lambda p: len(neighbors(p)) != 1):  # 끝점에서 먼저 출발
        if start in visited:
            continue
        path = [start]
        visited.add(start)
        while following := [p for p in neighbors(path[-1]) if p not in visited]:
            path.append(following[0])
            visited.add(following[0])
        joint = [p for p in neighbors(path[-1]) if p not in path[-3:]]  # 갈림길·고리에 이어 붙인다.
        if not joint or len(path) >= SPUR_LENGTH:
            thickness = 2 * np.median([distance[y, x] for x, y in path])
            paths.append((np.float32(path + joint[:1]), BRUSH_WIDTHS[int(thickness >= 6) + int(thickness >= 18)]))
    return paths

def smooth(points, window=5):
    if len(points) <= window:
        return points
    padded = np.pad(points, ((window // 2, window // 2), (0, 0)), mode='edge')
    return np.stack([np.convolve(padded[:, axis], np.ones(window) / window, 'valid') for axis in range(2)], 1)

def make_warp(shape, mess, rng):
    grid = rng.uniform(-1, 1, (3, 4, 2)).astype(np.float32)
    return cv2.resize(grid, (shape[1], shape[0]), interpolation=cv2.INTER_CUBIC) * 8 * mess

def mouse(points, warp, mess, rng):
    points = cv2.approxPolyDP(np.float32(points).reshape(-1, 1, 2), .8, False).reshape(-1, 2)
    samples = [points[0]]
    for a, b in zip(points, points[1:]):
        steps = max(1, round(np.linalg.norm(b - a) / (SAMPLE_STEP * rng.uniform(.7, 1.3))))
        samples += [a + (b - a) * t for t in np.arange(1, steps + 1) / steps]
    samples = np.float32(samples)
    if len(samples) > 1:
        distance = np.r_[0, np.cumsum(np.linalg.norm(np.diff(samples, axis=0), axis=1))]
        direction = np.gradient(samples, axis=0)
        normal = direction[:, ::-1] * (1, -1) / (np.linalg.norm(direction, axis=1, keepdims=True) + 1e-6)
        wobble = sum(rng.uniform(.5, 1) * np.sin(distance / rng.uniform(40, 160) + rng.uniform(0, 2 * np.pi)) for _ in range(2))
        samples += normal * wobble[:, None] * 1.2 * mess
    index = np.round(samples).astype(int)
    return samples + warp[np.clip(index[:, 1], 0, warp.shape[0] - 1), np.clip(index[:, 0], 0, warp.shape[1] - 1)]

def render(strokes, shape, scale, transparent):
    size = (round(shape[0] * scale), round(shape[1] * scale))
    color = np.full(size + (3,), 255, np.uint8)
    alpha = np.zeros(size, np.uint8)
    for points, rgb, width in strokes:
        if len(points) == 1:
            points = np.repeat(points, 2, 0)
        pixels = np.round(points * scale * 16).astype(np.int32).reshape(-1, 1, 2)
        thickness = max(1, round(width * scale))
        cv2.polylines(color, [pixels], False, tuple(map(int, rgb[::-1])), thickness, cv2.LINE_AA, 4)
        cv2.polylines(alpha, [pixels], False, 255, thickness, cv2.LINE_AA, 4)
    if not transparent:
        return color
    coverage = alpha[..., None] / 255.0
    color = np.clip((color - 255 * (1 - coverage)) / np.maximum(coverage, 1e-3), 0, 255).astype(np.uint8)
    return np.dstack([color, alpha])

def main():
    parser = argparse.ArgumentParser(description='그림을 DrawLiar 플레이어 낙서처럼 다시 그린다.')
    parser.add_argument('input')
    parser.add_argument('output')
    parser.add_argument('--width', type=int, default=2400, help='출력 가로 픽셀. 붓 굵기는 게임 캔버스 1200 기준 비율을 유지한다.')
    parser.add_argument('--all-colors', action='store_true', help='구독자 팔레트 프리셋 색까지 사용')
    parser.add_argument('--transparent', action='store_true', help='배경을 투명하게 저장')
    parser.add_argument('--mess', type=float, default=1, help='손떨림·비뚤어짐 정도')
    parser.add_argument('--seed', type=int, default=0, help='같은 값이면 같은 결과')
    args = parser.parse_args()
    rng = np.random.default_rng(args.seed)
    cv2.setRNGSeed(args.seed)
    rgb = load_image(args.input)
    shape = (round(rgb.shape[0] * CANVAS_WIDTH / rgb.shape[1]), CANVAS_WIDTH)
    fills, lines = [], []
    fill_core = cv2.getStructuringElement(cv2.MORPH_ELLIPSE, (FILL_MIN_WIDTH,) * 2)
    for color, mask in color_layers(rgb, load_palette(args.all_colors), shape):
        thick = cv2.morphologyEx(mask, cv2.MORPH_OPEN, fill_core)
        thin = cv2.subtract(mask, cv2.dilate(thick, np.ones((5, 5), np.uint8)))
        _, labels, stats, _ = cv2.connectedComponentsWithStats(thin)
        touching = np.unique(labels[cv2.dilate(thick, np.ones((9, 9), np.uint8)) > 0])
        thin[np.isin(labels, [label for label in touching if label and stats[label, cv2.CC_STAT_AREA] < FILL_MIN_WIDTH ** 2])] = 0  # 면 가장자리 자투리
        fills += [(area, points, color) for area, points in scribble(thick, BRUSH_WIDTHS[2], rng)]
        lines += [(smooth(points), color, width) for points, width in trace(thin)]
    fills.sort(key=lambda fill: -fill[0])  # 큰 면부터 칠해야 작은 면이 덮이지 않는다.
    lines.sort(key=lambda line: -sum(line[1]))  # 진한 선을 마지막에 그어야 윤곽이 묻히지 않는다.
    warp = make_warp(shape, args.mess, rng)
    strokes = [(points, color, BRUSH_WIDTHS[2]) for _, points, color in fills] + lines
    strokes = [(mouse(points, warp, args.mess, rng), color, width) for points, color, width in strokes]
    image = render(strokes, shape, args.width / CANVAS_WIDTH, args.transparent)
    cv2.imencode('.png', image)[1].tofile(args.output)

if __name__ == '__main__':
    main()
