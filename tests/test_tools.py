"""无 UE 编辑器的脚本逻辑检查，以及真实 UDP 监视器回环。"""
import importlib.util
import json
from pathlib import Path
import socket
import subprocess
import sys
import types
import unittest

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location("ue_setup", ROOT / "tools/ue_lens_setup.py")
setup = importlib.util.module_from_spec(spec)
spec.loader.exec_module(setup)


class Properties:
    def __init__(self):
        self.values = {}

    def set_editor_property(self, name, value):
        self.values[name] = value


class LensScriptTests(unittest.TestCase):
    def setUp(self):
        self.profile = json.loads((ROOT / "profiles/FX6_24mm.example.ds80lens.json").read_text(encoding="utf-8"))
        self.profile.update(SensorHeightMm=18.8, FilmbackConfirmed=True)
        self.created = []
        self.tags = {}
        self.asset = Properties()

        def create(*args):
            self.created.append(args)
            return self.asset

        self.unreal = types.SimpleNamespace(
            LensFile=Properties, LensInfo=Properties, LensFileFactoryNew=Properties,
            SphericalLensModel=object, Vector2D=lambda *v: v, IntPoint=lambda *v: v,
            AssetToolsHelpers=types.SimpleNamespace(get_asset_tools=lambda: types.SimpleNamespace(create_asset=create)),
            EditorAssetLibrary=types.SimpleNamespace(
                does_asset_exist=lambda path: False,
                set_metadata_tag=lambda asset, key, value: self.tags.update({key: value}),
                save_loaded_asset=lambda asset: True),
            EditorActorSubsystem=object, CineCameraActor=type("Camera", (), {}),
            get_editor_subsystem=lambda kind: types.SimpleNamespace(get_selected_level_actors=lambda: []),
            log=lambda text: None, log_warning=lambda text: None)

    def test_creates_only_unmeasured_metadata(self):
        self.assertIs(setup.prepare(self.profile, self.unreal), self.asset)
        info = self.asset.values["lens_info"].values
        self.assertEqual(info["sensor_dimensions"], (35.7, 18.8))
        self.assertEqual(len(self.created), 1)
        self.assertIn("UNMEASURED", self.tags["DS80.CalibrationStatus"])
        self.assertEqual(json.loads(self.tags["DS80.ProjectSettings"])["FocalLengthMm"], 24)

    def test_existing_calibration_is_preserved(self):
        self.unreal.EditorAssetLibrary.does_asset_exist = lambda path: True
        with self.assertRaisesRegex(RuntimeError, "already exists"):
            setup.prepare(self.profile, self.unreal)
        self.assertEqual(self.created, [])

    def test_unconfirmed_filmback_is_rejected(self):
        self.profile["FilmbackConfirmed"] = False
        with self.assertRaises(ValueError):
            setup.prepare(self.profile, self.unreal)
        self.assertEqual(self.created, [])


class MonitorTests(unittest.TestCase):
    def test_freed_only_does_not_block_on_json(self):
        with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as first, socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as second:
            first.bind(("127.0.0.1", 0))
            second.bind(("127.0.0.1", 0))
            freed_port, json_port = first.getsockname()[1], second.getsockname()[1]
        process = subprocess.Popen(
            [sys.executable, "-u", str(ROOT / "tools/pose_monitor.py"), "--seconds", "1",
             "--freed-port", str(freed_port), "--json-port", str(json_port)],
            stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True, encoding="utf-8")
        try:
            ready = process.stdout.readline()
            self.assertIn("监听", ready)
            packet = bytes([0xD1]) + bytes(27) + bytes([0x6F])
            with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as sender:
                for _ in range(80):
                    sender.sendto(packet, ("127.0.0.1", freed_port))
            output, error = process.communicate(timeout=5)
            self.assertEqual(process.returncode, 0, error)
            self.assertIn("Free-D: 80 帧，错误 0", output)
            self.assertIn("JSON: 0 帧，错误 0", output)
        finally:
            if process.poll() is None:
                process.kill()
                process.communicate()
            process.stdout.close()
            process.stderr.close()


if __name__ == "__main__":
    unittest.main()
