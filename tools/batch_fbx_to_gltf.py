# batch_fbx_to_gltf.py
# Usage:
#   blender.exe -b -P batch_fbx_to_gltf.py -- "INPUT_DIR" ["OUTPUT_DIR"]
#
# Converts every .fbx in INPUT_DIR to .glb (glTF-Binary).
# If OUTPUT_DIR is omitted, .glb files are written next to the .fbx files.

import bpy
import os
import sys
import pathlib

# ---------- Config you can tweak ----------
DRACO = True            # set False to disable Draco mesh compression
DRACO_LEVEL = 6         # 0 (fast) .. 10 (small)
EMBED_TEXTURES = True   # GLB embeds textures by default; keep True
EXPORT_ANIMS = True     # export animations (recommended for FBX clips)
APPLY_TRANSFORMS = True # apply object transforms on export
# ------------------------------------------

def reset_scene():
    # Reset to an empty factory scene before each file to avoid data piling up
    bpy.ops.wm.read_factory_settings(use_empty=True)

def import_fbx(fpath: str):
    bpy.ops.import_scene.fbx(
        filepath=fpath,
        automatic_bone_orientation=True,
        use_anim=True
    )

def export_glb(out_path: str):
    # Base export options
    kw = dict(
        filepath=out_path,
        export_format='GLB',
        export_yup=True,
        export_apply=APPLY_TRANSFORMS,
        export_animations=EXPORT_ANIMS,
        export_nla_strips=True,            # preserve NLA strips as separate glTF animations
        export_skins=True,
        export_morph=True,
        export_texcoords=True,
        export_normals=True,
        export_tangents=True,
        export_materials='EXPORT',         # Principled BSDF -> PBR
        export_image_format='AUTO',        # keep original texture encodings
        export_optimize_animation_size=True
    )

    # Draco mesh compression
    if DRACO:
        kw.update(
            export_draco_mesh_compression_enable=True,
            export_draco_mesh_compression_level=DRACO_LEVEL
        )

    bpy.ops.export_scene.gltf(**kw)

def ensure_dir(p: str):
    os.makedirs(p, exist_ok=True)

def main():
    # Parse args after --
    argv = sys.argv
    if "--" in argv:
        argv = argv[argv.index("--") + 1:]
    else:
        argv = []

    if not argv:
        print("Usage: blender -b -P batch_fbx_to_gltf.py -- INPUT_DIR [OUTPUT_DIR]")
        sys.exit(1)

    in_dir = os.path.abspath(argv[0])
    out_dir = os.path.abspath(argv[1]) if len(argv) > 1 else in_dir
    ensure_dir(out_dir)

    fbx_files = sorted(str(p) for p in pathlib.Path(in_dir).glob("*.fbx"))
    if not fbx_files:
        print(f"No .fbx files found in {in_dir}")
        sys.exit(0)

    print(f"Found {len(fbx_files)} FBX files in {in_dir}.")
    for i, fbx in enumerate(fbx_files, start=1):
        name = pathlib.Path(fbx).stem
        out_path = os.path.join(out_dir, f"{name}.glb")
        print(f"[{i}/{len(fbx_files)}] {name} -> {out_path}")

        reset_scene()
        import_fbx(fbx)
        # Example: set FPS if needed
        # bpy.context.scene.render.fps = 30
        export_glb(out_path)

    print("Done.")

if __name__ == "__main__":
    main()
