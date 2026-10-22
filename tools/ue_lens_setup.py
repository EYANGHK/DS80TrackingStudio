"""由 DS80 Tracking Studio 导出后，在 UE 编辑器 Tools > Execute Python Script 执行。

需要 Camera Calibration、Lens Component、Python Editor Script Plugin。
只创建未标定的 Lens File 和初始化选中的相机，不写入虚构的标定点。
"""
import json
import math
import re


def prepare(profile, unreal):
    required = ("LensFile", "LensInfo", "LensFileFactoryNew", "AssetToolsHelpers", "EditorAssetLibrary")
    missing = [name for name in required if not hasattr(unreal, name)]
    if missing:
        raise RuntimeError("Enable Camera Calibration / Python plugins; missing APIs: " + ", ".join(missing))
    if profile.get("Version") != 1 or not profile.get("FilmbackConfirmed"):
        raise ValueError("Confirm the active filmback before export.")
    for key in ("SensorWidthMm", "SensorHeightMm", "FocalLengthMm", "FocusDistanceCm", "SqueezeFactor"):
        value = profile[key]
        if not math.isfinite(value) or value <= 0:
            raise ValueError("Invalid " + key)
    path = profile["AssetPath"]
    if not re.fullmatch(r"/Game/(?:[A-Za-z0-9_]+/)*[A-Za-z0-9_]+", path):
        raise ValueError("Invalid Lens File asset path")
    if unreal.EditorAssetLibrary.does_asset_exist(path):
        raise RuntimeError("Asset already exists; select a new path to preserve calibration: " + path)

    # 构造元数据时先检查当前 UE API，避免在不兼容版本中创建半成品资源。
    info = unreal.LensInfo()
    model_name = "AnamorphicLensModel" if profile["Model"] == 1 else "SphericalLensModel"
    if not hasattr(unreal, model_name):
        raise RuntimeError("Lens model not available: " + model_name)
    info.set_editor_property("lens_model", getattr(unreal, model_name))
    info.set_editor_property("lens_model_name", profile["CameraModel"] + " / " + profile["LensModel"])
    info.set_editor_property("lens_serial_number", profile["SerialNumber"])
    info.set_editor_property("sensor_dimensions", unreal.Vector2D(profile["SensorWidthMm"], profile["SensorHeightMm"]))
    info.set_editor_property("image_dimensions", unreal.IntPoint(profile["ImageWidth"], profile["ImageHeight"]))
    info.set_editor_property("squeeze_factor", profile["SqueezeFactor"])

    folder, name = path.rsplit("/", 1)
    asset = unreal.AssetToolsHelpers.get_asset_tools().create_asset(name, folder, unreal.LensFile, unreal.LensFileFactoryNew())
    if asset is None:
        raise RuntimeError("Could not create Lens File: " + path)
    asset.set_editor_property("lens_info", info)
    unreal.EditorAssetLibrary.set_metadata_tag(asset, "DS80.Project", profile["ProjectName"])
    unreal.EditorAssetLibrary.set_metadata_tag(asset, "DS80.CalibrationStatus", "UNMEASURED - solve in Lens File Editor")
    unreal.EditorAssetLibrary.set_metadata_tag(asset, "DS80.ProjectSettings", json.dumps(profile, ensure_ascii=False))
    if not unreal.EditorAssetLibrary.save_loaded_asset(asset):
        raise RuntimeError("Lens File exists in memory but could not be saved: " + path)

    # 只修改明确选中的一个 CineCamera；不创建或移动场景中的跟踪原点。
    cameras = [actor for actor in unreal.get_editor_subsystem(unreal.EditorActorSubsystem).get_selected_level_actors()
               if isinstance(actor, unreal.CineCameraActor)]
    if len(cameras) == 1:
        camera = cameras[0].get_cine_camera_component()
        camera.modify()
        filmback = camera.get_editor_property("filmback")
        filmback.set_editor_property("sensor_width", profile["SensorWidthMm"])
        filmback.set_editor_property("sensor_height", profile["SensorHeightMm"])
        camera.set_editor_property("filmback", filmback)
        lens = camera.get_editor_property("lens_settings")
        lens.set_editor_property("min_focal_length", min(lens.get_editor_property("min_focal_length"), profile["FocalLengthMm"]))
        lens.set_editor_property("max_focal_length", max(lens.get_editor_property("max_focal_length"), profile["FocalLengthMm"]))
        camera.set_editor_property("lens_settings", lens)
        camera.set_current_focal_length(profile["FocalLengthMm"])
        focus = camera.get_editor_property("focus_settings")
        focus.set_editor_property("focus_method", unreal.CameraFocusMethod.MANUAL)
        focus.set_editor_property("manual_focus_distance", profile["FocusDistanceCm"])
        camera.set_editor_property("focus_settings", focus)
        components = cameras[0].get_components_by_class(unreal.LensComponent) if hasattr(unreal, "LensComponent") else []
        if len(components) == 1:
            component = components[0]
            component.modify()
            component.set_lens_file(asset)
            component.set_apply_nodal_offset_on_tick(profile["NodalOffsetInUe"])
            unreal.log("Lens File assigned. Set Lens Component evaluation inputs to the same manual focus/zoom values.")
        else:
            unreal.log_warning("Add one Lens Component to the selected camera, assign " + path +
                               ", and set Apply Nodal Offset on Tick to " + str(profile["NodalOffsetInUe"]))
    else:
        unreal.log_warning("Select exactly one CineCamera to initialize filmback/focal length; Lens File was created only.")
    unreal.log("Created UNCALIBRATED Lens File: " + path)
    unreal.log("Open Lens File Editor, select video feed, then solve distortion/intrinsics and nodal offset from real captures.")
    unreal.log("Disable Free-D focal length/focus updates when using manual lens inputs. Save the level after reviewing changes.")
    return asset


if __name__ == "__main__":
    import unreal
    profile = json.loads(__PROFILE_JSON__)
    prepare(profile, unreal)
