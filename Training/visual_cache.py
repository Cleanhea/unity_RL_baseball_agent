"""Exact, bounded reuse of grayscale PNG frames in ML-Agents stacked observations.

Opt in through train_cached.py. No changes to Unity, installed packages, frame
order, normalization, observation shape, or policy inputs. Each result owns its
storage; cached frames are immutable and keyed by their complete PNG bytes.
"""
from collections import OrderedDict
from io import BytesIO
import os
import struct
import numpy as np
from PIL import Image

PNG_HEADER = b'\x89PNG\r\n\x1a\n'


def png_frames(data):
    """Split concatenated PNGs using chunk lengths, not signatures in payloads."""
    offset = 0
    while offset < len(data):
        start = offset
        if data[offset:offset + 8] != PNG_HEADER:
            raise ValueError('Not a concatenated PNG observation')
        offset += 8
        while True:
            if offset + 12 > len(data):
                raise ValueError('Truncated PNG chunk')
            size = struct.unpack_from('>I', data, offset)[0]
            kind = data[offset + 4:offset + 8]
            offset += size + 12
            if offset > len(data):
                raise ValueError('Truncated PNG payload')
            if kind == b'IEND':
                break
        yield data[start:offset]


class GrayFrameCache:
    def __init__(self, original, max_bytes=64 * 1024 * 1024, verify_first=0):
        self.original = original
        self.max_bytes = max_bytes
        self.verify_first = verify_first
        self.frames = OrderedDict()
        self.bytes = 0
        self.hits = self.misses = self.calls = self.fallbacks = self.verified = 0

    def snapshot(self):
        return {'pid': os.getpid(), 'calls': self.calls, 'hits': self.hits,
                'misses': self.misses, 'fallbacks': self.fallbacks, 'verified': self.verified,
                'entries': len(self.frames), 'bytes': self.bytes, 'max_bytes': self.max_bytes}

    def _gray(self, encoded):
        if encoded in self.frames:
            self.hits += 1
            self.frames.move_to_end(encoded)
            return self.frames[encoded]
        self.misses += 1
        with Image.open(BytesIO(encoded)) as image:
            if image.mode != 'RGB':
                raise ValueError('Only standard RGB PNG grayscale mapping is optimized')
            # Match stock float32 normalization and contiguous three-channel mean.
            channels = np.moveaxis(np.array(image, dtype=np.float32) / 255.0, -1, 0)
            gray = np.mean(np.ascontiguousarray(channels), axis=0)
        gray.setflags(write=False)
        cost = len(encoded) + gray.nbytes
        if cost <= self.max_bytes:
            while self.frames and self.bytes + cost > self.max_bytes:
                key, frame = self.frames.popitem(last=False)
                self.bytes -= len(key) + frame.nbytes
            self.frames[encoded] = gray
            self.bytes += cost
        return gray

    def __call__(self, image_bytes, expected_channels, mappings=None):
        self.calls += 1
        # All other mappings, RGB sensors and old non-mapped stacks retain stock behavior.
        standard = [channel for channel in range(expected_channels) for _ in range(3)]
        if mappings is None or list(mappings) != standard:
            self.fallbacks += 1
            return self.original(image_bytes, expected_channels, mappings)
        try:
            encoded = list(png_frames(image_bytes))
            if len(encoded) != expected_channels:
                raise ValueError('Frame/channel count mismatch')
            first = self._gray(encoded[0])
            # One output allocation. No concatenated RGB stack or per-channel groups.
            result = np.empty((expected_channels, *first.shape), dtype=np.float32)
            result[0] = first
            for i, frame in enumerate(encoded[1:], 1):
                result[i] = self._gray(frame)
        except (ValueError, OSError):
            self.fallbacks += 1
            return self.original(image_bytes, expected_channels, mappings)
        if self.verified < self.verify_first:
            stock = self.original(image_bytes, expected_channels, mappings)
            if not np.array_equal(result, stock):
                raise RuntimeError('Visual cache differs from the original pixel conversion')
            self.verified += 1
        if self.calls % 1000 == 0:
            print('[VISUAL_CACHE]', self.snapshot(), flush=True)
        return result


_installed = None


def install(verify_first=16):
    global _installed
    if _installed is None:
        from mlagents_envs import rpc_utils
        from mlagents_envs.timers import timed
        _installed = GrayFrameCache(rpc_utils.process_pixels, verify_first=verify_first)
        rpc_utils.process_pixels = timed(_installed.__call__)
        print('[VISUAL_CACHE_ENABLED]', {'pid': os.getpid(), 'limit_mb': 64,
                                       'verify_first': verify_first}, flush=True)
    return _installed


def cached_environment_factory(factory):
    """Install in the spawned worker explicitly, independent of import side effects."""
    def create(worker_id, side_channels):
        install()
        return factory(worker_id, side_channels)
    return create
