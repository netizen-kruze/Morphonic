#!/usr/bin/env python3
"""Writes the unit tests' tiny checkpoint, src/Morphonic.Tests/Fixtures/tiny_voice.pth,
and the weight-norm reference beside it (tiny_weightnorm.txt).

The file has torch.save's zip layout (archive/data.pkl naming every
tensor, one raw little-endian storage per tensor under archive/data/),
written here without PyTorch: the pickle is produced by Python's own
pickler with the same persistent ids and rebuild calls torch emits, so
the app's tensor-only reader (TorchCheckpoint) sees exactly what a
checkpoint from torch looks like. Values are fixed, so the file and the
reference are reproducible byte for byte.

    python3 -I tools/make_test_fixture.py
"""
import io
import os
import pickle
import struct
import sys
import types
import zipfile
from collections import OrderedDict

import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
FIXTURES = os.path.join(HERE, "..", "src", "Morphonic.Tests", "Fixtures")


# ── stand-ins for the three torch globals a checkpoint pickle names ──
# pickle looks the globals up by module and name, so the modules exist
# as empty shells for the duration of this script only.
torch_mod = types.ModuleType("torch")
torch_utils = types.ModuleType("torch._utils")


def _rebuild_tensor_v2(*args):  # never called: only its name is pickled
    raise RuntimeError("stand-in")


_rebuild_tensor_v2.__module__ = "torch._utils"
torch_utils._rebuild_tensor_v2 = _rebuild_tensor_v2
for name in ("FloatStorage", "HalfStorage", "LongStorage"):
    cls = type(name, (), {})
    cls.__module__ = "torch"
    setattr(torch_mod, name, cls)
torch_mod._utils = torch_utils
sys.modules["torch"] = torch_mod
sys.modules["torch._utils"] = torch_utils

STORAGE_TYPE = {np.dtype("float32"): "FloatStorage", np.dtype("float16"): "HalfStorage", np.dtype("int64"): "LongStorage"}


class Storage:
    def __init__(self, key, array):
        self.key = key
        self.array = np.ascontiguousarray(array)
        self.type_name = STORAGE_TYPE[self.array.dtype]


class Tensor:
    def __init__(self, storage):
        self.storage = storage

    def __reduce_ex__(self, protocol):
        a = self.storage.array
        stride = tuple(int(s // a.itemsize) for s in a.strides)
        return (_rebuild_tensor_v2, (self.storage, 0, tuple(a.shape), stride, False, OrderedDict()))


class TorchPickler(pickle.Pickler):
    def persistent_id(self, obj):
        if isinstance(obj, Storage):
            return ("storage", getattr(torch_mod, obj.type_name), obj.key, "cpu", int(obj.array.size))
        return None


def main():
    rng = np.random.default_rng(1234)
    v = rng.standard_normal((2, 3, 5)).astype(np.float16)
    g = np.array([[[0.75]], [[1.25]]], dtype=np.float16)

    storages = [
        Storage("0", np.arange(12, dtype=np.float32) * np.float32(0.1)),            # a.weight [3,4]: 0.0 .. 1.1
        Storage("1", np.array([1.5, -2.0, 3.25], dtype=np.float16)),               # a.bias
        Storage("2", np.array([7, -8, 9], dtype=np.int64)),                        # i.weight
        Storage("3", g),                                                           # n.weight_g [2,1,1]
        Storage("4", v),                                                           # n.weight_v [2,3,5]
    ]
    storages[0].array = storages[0].array.reshape(3, 4)
    weight = OrderedDict([
        ("a.weight", Tensor(storages[0])),
        ("a.bias", Tensor(storages[1])),
        ("i.weight", Tensor(storages[2])),
        ("n.weight_g", Tensor(storages[3])),
        ("n.weight_v", Tensor(storages[4])),
    ])
    root = {
        "weight": weight,
        "config": [1025, 32, 192, "1", [3, 7, 11], [[1, 3, 5], [1, 3, 5], [1, 3, 5]], 0.0, 40000],
        "info": "tiny fixture for Morphonic's tests",
        "sr": "40k",
        "f0": 1,
        "version": "v2",
    }

    buf = io.BytesIO()
    TorchPickler(buf, protocol=2).dump(root)

    os.makedirs(FIXTURES, exist_ok=True)
    path = os.path.join(FIXTURES, "tiny_voice.pth")
    with zipfile.ZipFile(path, "w", compression=zipfile.ZIP_STORED) as z:
        info = zipfile.ZipInfo("archive/data.pkl", date_time=(1980, 1, 1, 0, 0, 0))
        z.writestr(info, buf.getvalue())
        for s in storages:
            info = zipfile.ZipInfo("archive/data/" + s.key, date_time=(1980, 1, 1, 0, 0, 0))
            z.writestr(info, s.array.astype(s.array.dtype.newbyteorder("<")).tobytes())
        z.writestr(zipfile.ZipInfo("archive/byteorder", date_time=(1980, 1, 1, 0, 0, 0)), b"little")
        z.writestr(zipfile.ZipInfo("archive/version", date_time=(1980, 1, 1, 0, 0, 0)), b"3\n")

    # weight_norm(v, g, dim=0): w = g * v / ||v|| with the norm over every
    # axis but 0, in float64, each result rounded once to float32 — the
    # definition ModelAssembler.WeightNorm implements.
    v64 = v.astype(np.float64)
    g64 = g.astype(np.float64).reshape(2)
    norm = np.sqrt((v64 ** 2).sum(axis=(1, 2)))
    w = (v64 * (g64 / norm)[:, None, None]).astype(np.float32).reshape(-1)
    with open(os.path.join(FIXTURES, "tiny_weightnorm.txt"), "w", newline="\n") as f:
        for x in w:
            f.write(np.format_float_positional(np.float32(x), unique=True, trim="-") + "\n")
    print(f"wrote {path} ({os.path.getsize(path)} bytes) and tiny_weightnorm.txt ({w.size} values)")


if __name__ == "__main__":
    main()
