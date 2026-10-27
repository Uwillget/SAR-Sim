# Import the Unity TestArea whitebox into SZ_TestArea.
# UE 4.27, from the editor's Python console:
#   py "<repo>/UnrealHost/SZ_TestArea/Content/Python/import_testarea.py"
# Override the source mesh with SAR_UE_IMPORT_DIR, or pass the OBJ path as argv[1].
import os
import sys
import unreal

_REPO_ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", "..", "..", ".."))
_DEFAULT_OBJ = os.path.join(
    os.environ.get("SAR_UE_IMPORT_DIR", os.path.join(_REPO_ROOT, "ue_import")),
    "testarea",
    "TestArea.obj",
)
OBJ = sys.argv[1] if len(sys.argv) > 1 else _DEFAULT_OBJ
MAP_PATH = "/Game/Maps/TestArea"
MESH_DIR = "/Game/TestArea"
# Unity Mat_Building_Gray is sRGB 0.784. Material constants are linear.
BUILDING_LINEAR = unreal.LinearColor(0.585, 0.585, 0.585, 1.0)
GROUND_LINEAR = unreal.LinearColor(0.170, 0.170, 0.170, 1.0)
# TestArea bounds in Unity metres: east, up, north.
EAST_MIN, EAST_MAX = -1041.2, 4637.0
NORTH_MIN, NORTH_MAX = 2564.3, 8605.5


def _lit(name, color, roughness):
    path = "/Game/Materials/" + name
    if unreal.EditorAssetLibrary.does_asset_exist(path):
        unreal.EditorAssetLibrary.delete_asset(path)
    factory = unreal.MaterialFactoryNew()
    mat = unreal.AssetToolsHelpers.get_asset_tools().create_asset(
        name, "/Game/Materials", unreal.Material, factory
    )
    mat.set_editor_property("two_sided", True)
    color_expr = unreal.MaterialEditingLibrary.create_material_expression(
        mat, unreal.MaterialExpressionConstant3Vector, -400, 0
    )
    color_expr.set_editor_property("constant", color)
    unreal.MaterialEditingLibrary.connect_material_property(
        color_expr, "", unreal.MaterialProperty.MP_BASE_COLOR
    )
    rough = unreal.MaterialEditingLibrary.create_material_expression(
        mat, unreal.MaterialExpressionConstant, -400, 180
    )
    rough.set_editor_property("r", roughness)
    unreal.MaterialEditingLibrary.connect_material_property(
        rough, "", unreal.MaterialProperty.MP_ROUGHNESS
    )
    emit = unreal.MaterialEditingLibrary.create_material_expression(
        mat, unreal.MaterialExpressionConstant3Vector, -400, 320
    )
    emit.set_editor_property("constant", unreal.LinearColor(0.08, 0.08, 0.08, 1.0))
    unreal.MaterialEditingLibrary.connect_material_property(
        emit, "", unreal.MaterialProperty.MP_EMISSIVE_COLOR
    )
    unreal.MaterialEditingLibrary.recompile_material(mat)
    unreal.EditorAssetLibrary.save_asset(mat.get_path_name())
    return mat


def _import_mesh():
    task = unreal.AssetImportTask()
    task.filename = OBJ
    task.destination_path = MESH_DIR
    task.destination_name = "TestArea"
    task.replace_existing = True
    task.automated = True
    task.save = True
    unreal.AssetToolsHelpers.get_asset_tools().import_asset_tasks([task])
    asset_path = MESH_DIR + "/TestArea"
    mesh = unreal.EditorAssetLibrary.load_asset(asset_path)
    if mesh is None:
        unreal.log_error("[TestArea] import failed")
        return None
    body = mesh.get_editor_property("body_setup")
    if body is not None:
        body.set_editor_property(
            "collision_trace_flag", unreal.CollisionTraceFlag.CTF_USE_COMPLEX_AS_SIMPLE
        )
    unreal.EditorAssetLibrary.save_asset(asset_path)
    return mesh


def _spawn(cls, label, location, rotation):
    actor = unreal.EditorLevelLibrary.spawn_actor_from_class(cls, location, rotation)
    if actor:
        actor.set_actor_label(label)
    return actor


def run():
    if not os.path.exists(OBJ):
        unreal.log_error("[TestArea] missing " + OBJ)
        return
    unreal.EditorAssetLibrary.make_directory("/Game/Maps")
    unreal.EditorAssetLibrary.make_directory("/Game/Materials")
    unreal.EditorAssetLibrary.make_directory(MESH_DIR)
    building = _lit("M_BuildingGray", BUILDING_LINEAR, 0.7)
    ground_mat = _lit("M_Ground", GROUND_LINEAR, 0.9)
    mesh = _import_mesh()

    unreal.EditorLevelLibrary.new_level(MAP_PATH)

    city = None
    if mesh is not None:
        city = unreal.EditorLevelLibrary.spawn_actor_from_object(
            mesh, unreal.Vector(0.0, 0.0, 0.0), unreal.Rotator(0.0, 0.0, 0.0)
        )
    mirrored = False
    if city is not None:
        city.set_actor_label("TestArea")
        component = city.get_component_by_class(unreal.StaticMeshComponent)
        if component:
            component.set_material(0, building)
            component.set_editor_property("ld_max_draw_distance", 0.0)
            bounds = component.get_editor_property("static_mesh").get_bounding_box()
            center_y = (bounds.min.y + bounds.max.y) * 0.5
            # A positive TestArea easting must land on Unreal +Y. If the OBJ
            # importer mirrored Y, the mesh centre is negative and one scale flip
            # puts it back on the AirSim east axis.
            if center_y < 0.0:
                city.set_actor_scale3d(unreal.Vector(1.0, -1.0, 1.0))
                mirrored = True
            unreal.log("[TestArea] mesh center Y cm = %.1f mirror=%s" % (center_y, mirrored))

    east_span_m = (EAST_MAX - EAST_MIN) + 400.0
    north_span_m = (NORTH_MAX - NORTH_MIN) + 400.0
    center_east_cm = (EAST_MIN + EAST_MAX) * 0.5 * 100.0
    center_north_cm = (NORTH_MIN + NORTH_MAX) * 0.5 * 100.0
    plane = unreal.EditorAssetLibrary.load_asset("/Engine/BasicShapes/Plane")
    if plane is not None:
        ground = unreal.EditorLevelLibrary.spawn_actor_from_object(
            plane, unreal.Vector(center_north_cm, center_east_cm, 0.0), unreal.Rotator(0.0, 0.0, 0.0)
        )
        if ground:
            ground.set_actor_label("Ground")
            # Engine plane is 100 cm on a side. Scale is in metres of coverage.
            ground.set_actor_scale3d(unreal.Vector(north_span_m, east_span_m, 1.0))
            ground_component = ground.get_component_by_class(unreal.StaticMeshComponent)
            if ground_component:
                ground_component.set_material(0, ground_mat)
                ground_component.set_editor_property("ld_max_draw_distance", 0.0)

    _spawn(unreal.PlayerStart, "PlayerStart", unreal.Vector(0.0, 0.0, 300.0), unreal.Rotator(0.0, 0.0, 0.0))
    sun = _spawn(
        unreal.DirectionalLight,
        "Sun",
        unreal.Vector(0.0, 0.0, 50000.0),
        unreal.Rotator(-50.0, 30.0, 0.0),
    )
    if sun:
        light = sun.get_component_by_class(unreal.DirectionalLightComponent)
        if light:
            light.set_editor_property("intensity", 10.0)
            light.set_editor_property("light_color", unreal.Color(255, 255, 255, 255))
    _spawn(unreal.SkyAtmosphere, "SkyAtmosphere", unreal.Vector(0.0, 0.0, 0.0), unreal.Rotator(0.0, 0.0, 0.0))
    sky = _spawn(unreal.SkyLight, "SkyLight", unreal.Vector(0.0, 0.0, 20000.0), unreal.Rotator(0.0, 0.0, 0.0))
    if sky:
        sky_light = sky.get_component_by_class(unreal.SkyLightComponent)
        if sky_light:
            sky_light.set_editor_property("intensity", 1.5)
            sky_light.set_editor_property("lower_hemisphere_is_black", False)
            try:
                sky_light.set_editor_property("real_time_capture", True)
            except Exception:
                pass
    volume = _spawn(unreal.PostProcessVolume, "Exposure", unreal.Vector(0.0, 0.0, 0.0), unreal.Rotator(0.0, 0.0, 0.0))
    if volume:
        volume.set_editor_property("unbound", True)
        settings = volume.get_editor_property("settings")
        settings.set_editor_property("override_auto_exposure_method", True)
        settings.set_editor_property("auto_exposure_method", unreal.AutoExposureMethod.AEM_MANUAL)
        settings.set_editor_property("override_auto_exposure_bias", True)
        settings.set_editor_property("auto_exposure_bias", 1.0)
        volume.set_editor_property("settings", settings)

    unreal.EditorLevelLibrary.save_current_level()
    unreal.log("[TestArea] map saved")


run()
