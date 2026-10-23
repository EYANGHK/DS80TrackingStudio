"""Run in an isolated UE Python commandlet; DS80_ULENS_MANIFEST supplies cases/output.

Checks actual LensFile import/export, not optical alignment or Slate UI painting.
Never run against a production project's existing assets.
"""
import json
import math
import os
from pathlib import Path

import unreal


def rows(text):
    return [[float(value.strip()) for value in row.split(",")]
            for row in text.split(";") if row.strip()]


def close(actual, expected):
    assert math.isclose(float(actual), float(expected), rel_tol=1e-5, abs_tol=1e-5), (actual, expected)


manifest_path = Path(os.environ.get("DS80_ULENS_MANIFEST", str(
    Path(__file__).resolve().parent.parent / "artifacts/ulens-regression/manifest.json")))
manifest = json.loads(manifest_path.read_text(encoding="utf-8-sig"))
report = {"engine": unreal.SystemLibrary.get_engine_version(), "cases": []}
out_dir = Path(manifest["output"])
out_dir.mkdir(parents=True, exist_ok=True)
asset_tools = unreal.AssetToolsHelpers.get_asset_tools()

for case in manifest["cases"]:
    source = Path(case["file"])
    expected = json.loads(source.read_text(encoding="utf-8-sig"))
    task = unreal.AssetImportTask()
    task.filename = str(source)
    task.destination_path = "/Game/UlensRegression"
    task.destination_name = case["name"]
    task.automated = True
    task.replace_existing = True
    task.save = True
    asset_tools.import_asset_tasks([task])
    assert len(task.imported_object_paths) == 1, "No imported LensFile: " + str(source)
    asset = unreal.load_asset(task.imported_object_paths[0])
    assert asset.get_class().get_name() == "LensFile"

    exported = out_dir / (case["name"] + ".ulens")
    export_task = unreal.AssetExportTask()
    export_task.object = asset
    export_task.filename = str(exported)
    export_task.automated = True
    export_task.prompt = False
    export_task.replace_identical = True
    assert unreal.Exporter.run_asset_export_task(export_task), "Export failed"
    actual = json.loads(exported.read_text(encoding="utf-8-sig"))

    for dimension in ("sensorDimensions", "imageDimensions"):
        for axis in ("width", "height"):
            close(actual[dimension][axis], expected[dimension][axis])
    for group in ("cameraParameterTables", "encoderTables"):
        exported_tables = {table["parameterName"]: table for table in actual[group]}
        for table in expected[group]:
            got = exported_tables[table["parameterName"]]
            assert got["header"] == table["header"]
            a, b = rows(got["data"]), rows(table["data"])
            assert len(a) == len(b), (table["parameterName"], a, b)
            for ar, br in zip(a, b):
                assert len(ar) == len(br)
                for av, bv in zip(ar, br):
                    close(av, bv)
    report["cases"].append({"source": str(source), "asset": task.imported_object_paths[0],
                            "roundtrip": str(exported), "passed": True,
                            "tables": actual["cameraParameterTables"]})

(out_dir / "report.json").write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
unreal.log("ULENS_REGRESSION_PASS " + str(len(report["cases"])))
