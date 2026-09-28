"""Author the Session Controls in Blender, then export compact Quest meshes.

Run: blender --background --python Tools/SessionControls/build_session_controls.py
All design coordinates use Unity X right / Y up / Z forward, meters.
The mounting back is z=0, face is toward -Z. ButtonCap is exported at a zero
transform and presses inward along +Z, by 0.009m. Other meshes stay static.
"""
import bpy
import importlib.util
import json
import math
import sys
from pathlib import Path
from mathutils import Vector

ROOT = Path(__file__).resolve().parents[2]
SOURCE = ROOT / 'Assets' / 'HelmetInspection' / 'SessionControls' / 'Source'
ART = ROOT / 'ArtSource'
SOURCE.mkdir(parents=True, exist_ok=True)
spec = importlib.util.spec_from_file_location('lab_geometry', ROOT/'Tools/ArtRefresh/build_lab_assets.py')
g = importlib.util.module_from_spec(spec)
spec.loader.exec_module(g)


def mushroom_cap(name, center, mat):
    # A smooth molded mushroom profile, with an undercut grasping lip and a
    # broad domed face. The cap is one watertight lathed mesh, independently
    # animated in Unity rather than part of the stationary housing batch.
    cx, cy, cz = center
    profile = [(.000, .000), (.044, .000), (.046, -.003),
               (.046, -.008), (.061, -.008), (.065, -.011),
               (.067, -.016), (.0665, -.023), (.0635, -.031),
               (.057, -.039), (.047, -.046), (.033, -.051),
               (.017, -.054), (.000, -.055)]
    sides = 48
    verts = []
    for radius, depth in profile:
        for i in range(sides):
            angle = i * math.tau / sides
            verts.append(g.bvec((cx+radius*math.cos(angle), cy+radius*math.sin(angle), cz+depth)))
    faces = []
    for p in range(len(profile)-1):
        for i in range(sides):
            j = (i+1) % sides
            # Blender basis is a reflected version of the Unity design basis.
            faces.append((p*sides+i, p*sides+j, (p+1)*sides+j, (p+1)*sides+i))
    mesh = bpy.data.meshes.new(name)
    mesh.from_pydata(verts, [], faces)
    mesh.update()
    obj = bpy.data.objects.new(name, mesh)
    bpy.context.collection.objects.link(obj)
    g.finish(obj, mat, name, smooth=False)
    # Recalculate winding and weld pole duplicates to preserve a closed mesh.
    import bmesh
    bm = bmesh.new()
    bm.from_mesh(mesh)
    bmesh.ops.remove_doubles(bm, verts=list(bm.verts), dist=1e-7)
    bmesh.ops.recalc_face_normals(bm, faces=list(bm.faces))
    bm.to_mesh(mesh)
    bm.free()
    for poly in mesh.polygons:
        poly.use_smooth = True
    return obj


def build_unit(kind, palette):
    before = set(bpy.context.scene.objects)
    metal, face, rubber, hardware, ink, green, red = palette
    cap_mat = g.material('Control_'+kind+'_MoldedCap',
                         (.022, .245, .068) if kind == 'Start' else (.50, .018, .012),
                         .06, .43)
    g.box('Enclosure - cast aluminum body', (0, 0, -.040), (.24, .28, .080), metal, .017, 4)
    g.box('Continuous face gasket', (0, 0, -.0788), (.232, .272, .010), rubber, .015, 3)
    g.box('Faceplate - radiused powdercoated alloy', (0, 0, -.085), (.228, .268, .014), face, .013, 4)
    # One thin polished datum is enough to read as metal without a glossy case.
    g.box('Faceplate lower witness line', (0, -.099, -.0922), (.171, .0015, .0009), hardware, .0003, 1)
    g.box('Nameplate inlay', (0, .108, -.0924), (.163, .027, .0016), rubber, .003, 2)
    g.text('Engraved operation label', kind.upper(), (0, .108, -.0935),
           .0215 if kind == 'Restart' else .025, ink, align='CENTER')
    g.text('Engraved session legend', 'SESSION CONTROL', (0, -.113, -.0925), .010, ink, align='CENTER')
    # Captive steel screws with inlaid cross slots (no floating UI or decals).
    for x in (-.097, .097):
        for y in (-.117, .117):
            g.cylinder('Captive screw countersink', (x,y,-.0924), .0082, .0014, rubber, (0,0,-1), 16, .0002)
            g.cylinder('Captive screw satin head', (x,y,-.0938), .0067, .0027, hardware, (0,0,-1), 16, .0006)
            for sx, sy in ((.0080,.00125),(.00125,.0080)):
                slot=g.box('Screw recessed cross slot', (x,y,-.0953), (sx,sy,.0003), rubber, .0002, 1)
                slot.rotation_euler.y = math.radians(18 if x*y > 0 else -18)
    for x, lens, caption in ((-.047,green,'READY'),(.047,red,'RESET')):
        g.cylinder('Pilot light retaining bezel', (x,.071,-.094), .0108, .004, hardware, (0,0,-1), 24, .0006)
        g.cylinder('Pilot light sealing ring', (x,.071,-.097), .0087, .0022, rubber, (0,0,-1), 24, .0003)
        g.cylinder('Pilot light colored lens', (x,.071,-.099), .0074, .0042, lens, (0,0,-1), 24, .0013)
        g.text('Pilot legend '+caption, caption, (x,.0545,-.0925), .0072, ink, align='CENTER')
    # Concentric hardware communicates the fixed socket and the moving cap.
    g.ring('Switch socket sealing gasket', (0,-.023,-.094), .060, .039, .004, rubber, segments=48)
    g.ring('Switch socket satin retaining ring', (0,-.023,-.098), .057, .039, .006, hardware, segments=48)
    g.cylinder('Switch stem boot', (0,-.023,-.108), .041, .023, rubber, (0,0,-1), 40, .001)
    cap = mushroom_cap(kind+' - ButtonCap (animated)', (0,-.023,-.114), cap_mat)
    # Lower corner serial marker is deliberately quiet, but makes the model
    # read like manufactured equipment at hand distance.
    g.text('Stamped control designation', 'SC / 01' if kind == 'Start' else 'SC / 02',
           (0,-.127,-.0925), .0054, ink, align='CENTER')
    objects = [obj for obj in bpy.context.scene.objects if obj not in before]
    return objects, cap


def export_unit(name, objects, cap):
    bpy.context.view_layer.update()
    used_materials = {obj.data.materials[0].name for obj in objects}
    material_data = [d for d in g.MATDATA if d['name'] in used_materials]
    material_index = {d['name']: i for i,d in enumerate(material_data)}
    grouped = {}
    for obj in objects:
        material_name = obj.data.materials[0].name
        name_key = 'ButtonCap' if obj == cap else 'Static_'+material_name
        grouped.setdefault((name_key, material_name), []).append(obj)
    payload = dict(version=1, coordinateSystem='UnityLeftHandedYUp', units='meters',
                   group='session-control', materials=material_data, meshes=[],
                   animation=dict(capMesh='ButtonCap', restPosition=[0,0,0],
                                  pressOffset=[0,0,.009]),
                   collider=dict(center=[0,-.023,-.140],size=[.145,.145,.068]),
                   enclosureBounds=dict(center=[0,0,-.046],size=[.240,.280,.092]))
    runtime_objects=[]
    for (mesh_name, mat_name), parts in sorted(grouped.items()):
        pos, normals, uv, triangles = [], [], [], []
        for obj in parts:
            mesh=obj.data
            mesh.calc_loop_triangles()
            uv_layer=mesh.uv_layers.active
            normal_xform=obj.matrix_world.to_3x3().inverted().transposed()
            for tri in mesh.loop_triangles:
                for loop_idx in (tri.loops[0],tri.loops[2],tri.loops[1]):
                    loop=mesh.loops[loop_idx]
                    vertex=obj.matrix_world @ mesh.vertices[loop.vertex_index].co
                    normal=(normal_xform @ mesh.corner_normals[loop_idx].vector).normalized()
                    triangles.append(len(pos)//3)
                    pos.extend(g.uvec(vertex))
                    normals.extend(g.uvec(normal))
                    if uv_layer:
                        coords=uv_layer.data[loop_idx].uv
                        uv.extend((float(coords.x),float(coords.y)))
                    else:
                        uv.extend((float(vertex.x),float(vertex.z)))
        payload['meshes'].append(dict(name=mesh_name,materialIndex=material_index[mat_name],
                                     positions=pos,normals=normals,uv=uv,triangles=triangles))
        # Export the same material-batched geometry to GLB, without modifying
        # the editable source objects retained in the .blend file.
        mesh=bpy.data.meshes.new(name+'_'+mesh_name+'_export')
        vertices=[g.bvec(pos[i:i+3]) for i in range(0,len(pos),3)]
        faces=[(triangles[i],triangles[i+2],triangles[i+1]) for i in range(0,len(triangles),3)]
        mesh.from_pydata(vertices, [], faces)
        mesh.update()
        mesh.normals_split_custom_set_from_vertices([g.bvec(normals[i:i+3]) for i in range(0,len(normals),3)])
        mesh.materials.append(bpy.data.materials[mat_name])
        export_obj=bpy.data.objects.new(mesh_name,mesh)
        bpy.context.collection.objects.link(export_obj)
        runtime_objects.append(export_obj)
    payload['triangleCount']=sum(len(m['triangles'])//3 for m in payload['meshes'])
    payload['rendererCount']=len(payload['meshes'])
    assert payload['triangleCount'] <= 8000, payload['triangleCount']
    assert payload['rendererCount'] <= 9, payload['rendererCount']
    assert sum(m['name']=='ButtonCap' for m in payload['meshes']) == 1
    (SOURCE/(name+'.mesh.json')).write_text(json.dumps(payload,separators=(',',':')),encoding='utf-8')
    bpy.ops.object.select_all(action='DESELECT')
    for obj in runtime_objects:
        obj.select_set(True)
    bpy.context.view_layer.objects.active=runtime_objects[0]
    bpy.ops.export_scene.gltf(filepath=str(SOURCE/(name+'.glb')),export_format='GLB',
                             use_selection=True,export_yup=True,export_apply=True,
                             export_cameras=False,export_lights=False)
    for obj in runtime_objects:
        mesh=obj.data
        bpy.data.objects.remove(obj,do_unlink=True)
        bpy.data.meshes.remove(mesh)
    print('SESSION_CONTROLS_EXPORT',name,'triangles=',payload['triangleCount'],
          'renderers=',payload['rendererCount'])
    return payload


def review_scene():
    scene=bpy.context.scene
    world=bpy.data.worlds.new('Soft neutral studio')
    world.use_nodes=True
    world.node_tree.nodes['Background'].inputs['Color'].default_value=(.18,.205,.225,1)
    world.node_tree.nodes['Background'].inputs['Strength'].default_value=.32
    scene.world=world
    scene.render.engine='CYCLES'
    scene.cycles.samples=48
    scene.cycles.use_denoising=True
    scene.render.resolution_x=1600
    scene.render.resolution_y=1080
    scene.render.resolution_percentage=100
    scene.render.image_settings.file_format='PNG'
    scene.render.filepath=str(ART/'SessionControls-preview.png')
    scene.view_settings.view_transform='AgX'
    scene.render.film_transparent=False
    camera_data=bpy.data.cameras.new('Controls review camera')
    cam=bpy.data.objects.new('Controls review camera',camera_data)
    bpy.context.collection.objects.link(cam)
    cam.location=g.bvec((.46,.34,-1.20))
    cam.rotation_euler=(g.bvec((0,0,-.045))-cam.location).to_track_quat('-Z','Y').to_euler()
    cam.data.lens=64
    cam.data.clip_start=.01
    scene.camera=cam
    g.area('Large soft key',(-.42,.6,-.5),(0,0,0),36,.65,(.95,.97,1))
    g.area('Front label fill',(.25,.1,-.75),(0,0,0),8,.48,(1,.94,.85))
    g.area('Housing edge fill',(.65,.4,.10),(0,0,0),20,.55,(.8,.89,1))
    # Matte backing belongs only to the source review scene, never exports.
    backdrop=g.material('Review backdrop',(.09,.112,.125),0,.9)
    g.box('Review mounting wall',(0,0,.04),(4,4,.06),backdrop,0)


def main():
    g.reset()
    palette=[
        g.material('Control_SatinCastAluminum',(.31,.34,.35),.74,.55),
        g.material('Control_PowdercoatFace',(.105,.137,.15),.40,.59),
        g.material('Control_GraphiteGasket',(.012,.018,.02),.02,.73),
        g.material('Control_SatinHardware',(.47,.49,.48),.82,.39),
        g.material('Control_IvoryEtching',(.82,.84,.80),.0,.65),
        g.material('Control_GreenPilot',(.024,.235,.053),.02,.31,(.02,.22,.04),.12),
        g.material('Control_RedPilot',(.31,.008,.006),.02,.33,(.25,.006,.002),.08),
    ]
    metadata=[]
    for kind, offset in (('Start',-.155),('Restart',.155)):
        objects,cap=build_unit(kind,palette)
        payload=export_unit('Industrial'+kind+'Button',objects,cap)
        metadata.append(dict(name=kind,triangles=payload['triangleCount'],renderers=payload['rendererCount']))
        root=bpy.data.objects.new(kind+' button - editable meter scale',None)
        bpy.context.collection.objects.link(root)
        for obj in objects:
            obj.parent=root
        root.location.x=offset
        root['Unity runtime local origin']='Enclosure center X/Y, mounting back Z=0'
        root['Cap press travel']='Local +Z 0.009 meters'
    review_scene()
    bpy.ops.object.select_all(action='DESELECT')
    bpy.context.preferences.filepaths.save_version=0
    bpy.ops.wm.save_as_mainfile(filepath=str(ART/'SessionControls.blend'))
    if '--skip-render' not in sys.argv:
        bpy.ops.render.render(write_still=True)
    print('SESSION_CONTROLS_COMPLETE',json.dumps(metadata))


if __name__=='__main__':
    main()
