"""Compare exact pixel arrays, resets, mappings, ownership and bounded memory."""
from io import BytesIO
from pathlib import Path
import json
import time
import numpy as np
from PIL import Image
from mlagents_envs.rpc_utils import process_pixels
from visual_cache import GrayFrameCache, png_frames


def png(array):
    stream = BytesIO()
    Image.fromarray(array, 'RGB').save(stream, format='PNG')
    return stream.getvalue()


def main():
    rng = np.random.default_rng(20261009)
    mappings = [i for i in range(30) for _ in range(3)]
    black = png(np.zeros((256, 256, 3), dtype=np.uint8))
    frames = []
    for i in range(70):
        image = np.zeros((256, 256, 3), dtype=np.uint8)
        image[40 + i:44 + i, 90 + i:94 + i] = rng.integers(0, 256, (4, 4, 3), dtype=np.uint8)
        frames.append(png(image))
    sequences = [[black] * 30]
    sequences += [([black] * 30 + frames)[i:i + 30] for i in range(1, 71)]
    # Different agents/resets and identical PNG content are safe without ID-based state.
    sequences += [[black] * 30, frames[20:50], frames[10:40][::-1]]
    cache = GrayFrameCache(process_pixels)
    for sequence in sequences:
        data = b''.join(sequence)
        expected = process_pixels(data, 30, mappings)
        actual = cache(data, 30, mappings)
        assert np.array_equal(actual, expected) and actual.dtype == expected.dtype
        actual[:] = -123  # Returned buffers must never mutate cached history.
        assert np.array_equal(cache(data, 30, mappings), expected)
    random_frame = png(rng.integers(0, 256, (256, 256, 3), dtype=np.uint8))
    assert np.array_equal(cache(random_frame * 30, 30, mappings), process_pixels(random_frame * 30, 30, mappings))
    for mapping, channels in [(None, 1), ([0, 1, 2], 3), ([0, 0, -1], 1)]:
        assert np.array_equal(cache(random_frame, channels, mapping), process_pixels(random_frame, channels, mapping))
    small = GrayFrameCache(process_pixels, max_bytes=300000)
    for frame in frames[:8]:
        assert np.array_equal(small(frame, 1, [0, 0, 0]), process_pixels(frame, 1, [0, 0, 0]))
        assert small.bytes <= small.max_bytes
    assert len(small.frames) <= 1
    invalid = [black[:-5], black + b'bad', b'bad']
    for data in invalid:
        try:
            list(png_frames(data))
        except ValueError:
            pass
        else:
            raise AssertionError('Malformed PNG accepted by splitter')
    samples = [b''.join(frames[i:i + 30]) for i in range(30)]
    start = time.perf_counter()
    stock = [process_pixels(x, 30, mappings) for x in samples]
    stock_seconds = time.perf_counter() - start
    bench = GrayFrameCache(process_pixels)
    start = time.perf_counter()
    optimized = [bench(x, 30, mappings) for x in samples]
    cached_seconds = time.perf_counter() - start
    assert all(np.array_equal(a, b) for a, b in zip(stock, optimized))
    report = {'status': 'PASS', 'exact_comparisons': len(sequences) * 2 + 4 + 8 + len(samples),
              'stock_seconds': stock_seconds, 'cached_seconds': cached_seconds,
              'conversion_speedup': stock_seconds / cached_seconds, 'cache': bench.snapshot(),
              'note': 'Synthetic sliding PNG conversion only; not full-training throughput'}
    out = Path('Training/evaluations/visual_cache_stage3_20261009')
    out.mkdir(parents=True, exist_ok=True)
    (out / 'conversion-test.json').write_text(json.dumps(report, indent=2), encoding='utf-8')
    print(json.dumps(report, indent=2))


if __name__ == '__main__':
    main()
