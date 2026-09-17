"""Build authored, meter-scale lab/scanner source assets for the Quest art refresh.

Run with Blender --background --python Tools/ArtRefresh/build_lab_assets.py.
All design coordinates below use Unity's X right, Y up, Z forward convention.
Exports GLB for interchange plus a lossless Unity-coordinate mesh/material JSON.
"""
import bpy
import bmesh
import json
import math
import sys
from pathlib import Path
from mathutils import Vector, Matrix

ROOT = Path(__file__).resolve().parents[2]
SOURCE = ROOT / 'Assets' / 'HelmetInspection' / 'ArtRefresh' / 'Source'
ART = ROOT / 'ArtSource'
COMPARE_EXPORT = '--compare-export' in sys.argv
LIVE_SOURCE = SOURCE
if COMPARE_EXPORT:
    SOURCE = ART / 'ExportComparison'
SOURCE.mkdir(parents=True, exist_ok=True)
ART.mkdir(parents=True, exist_ok=True)
MATDATA = []
MATINDEX = {}
FONT = None
FONT_PATH = Path('C:/Windows/Fonts/bahnschrift.ttf')


def bvec(v):
    return Vector((v[0], v[2], v[1]))


def uvec(v):
    return [float(v.x), float(v.z), float(v.y)]


def reset():
    global MATDATA, MATINDEX, FONT
    bpy.ops.object.select_all(action='SELECT')
    bpy.ops.object.delete(use_global=False)
    for datablocks in (bpy.data.meshes, bpy.data.curves, bpy.data.materials,
                       bpy.data.cameras, bpy.data.lights):
        for item in list(datablocks):
            if item.users == 0:
                datablocks.remove(item)
    MATDATA, MATINDEX = [], {}
    if FONT_PATH.exists():
        FONT = bpy.data.fonts.load(str(FONT_PATH), check_existing=True)


def material(name, color, metal=0.0, rough=.45, emission=None, strength=0.0):
    mat = bpy.data.materials.new(name)
    mat.use_nodes = True
    shader = mat.node_tree.nodes.get('Principled BSDF')
    shader.inputs['Base Color'].default_value = (*color, 1)
    shader.inputs['Metallic'].default_value = metal
    shader.inputs['Roughness'].default_value = rough
    if emission is not None:
        shader.inputs['Emission Color'].default_value = (*emission, 1)
        shader.inputs['Emission Strength'].default_value = strength
    mat.diffuse_color = (*color, 1)
    MATINDEX[name] = len(MATDATA)
    MATDATA.append(dict(name=name, baseColor=[*color, 1], metallic=metal,
                        roughness=rough, emission=list(emission or (0, 0, 0)),
                        emissionStrength=strength))
    return mat


def finish(obj, mat, name, bevel=0.0, segments=2, smooth=True):
    obj.name = name
    obj.data.materials.append(mat)
    bpy.context.view_layer.objects.active = obj
    if bevel:
        mod = obj.modifiers.new('Machined edge radii', 'BEVEL')
        mod.width = bevel
        mod.segments = segments
        mod.affect = 'EDGES'
        mod.limit_method = 'ANGLE'
        bpy.ops.object.modifier_apply(modifier=mod.name)
    if smooth:
        for poly in obj.data.polygons:
            poly.use_smooth = True
        normals = obj.modifiers.new('Area weighted surface normals', 'WEIGHTED_NORMAL')
        normals.keep_sharp = True
        normals.weight = 60
        bpy.ops.object.modifier_apply(modifier=normals.name)
    return obj


def box(name, center, size, mat, bevel=.006, segments=2, angle=0):
    bpy.ops.mesh.primitive_cube_add(size=1, location=bvec(center))
    obj = bpy.context.object
    obj.scale = bvec(size)
    bpy.ops.object.transform_apply(location=False, rotation=False, scale=True)
    if angle:
        obj.rotation_euler.x = math.radians(angle)
    return finish(obj, mat, name, min(bevel, min(size) * .42), segments)


def cylinder(name, center, radius, depth, mat, axis=(0, 1, 0), vertices=24, bevel=.001):
    bpy.ops.mesh.primitive_cylinder_add(vertices=vertices, radius=radius, depth=depth,
                                      end_fill_type='NGON', location=bvec(center))
    obj = bpy.context.object
    obj.rotation_mode = 'QUATERNION'
    obj.rotation_quaternion = Vector((0, 0, 1)).rotation_difference(bvec(axis))
    return finish(obj, mat, name, bevel, 1)


def ring(name, center, outer, inner, depth, mat, axis=(0, 0, 1), segments=32):
    verts, faces = [], []
    rotation = Vector((0, 0, 1)).rotation_difference(bvec(axis))
    origin = bvec(center)
    for z in (-depth / 2, depth / 2):
        for radius in (outer, inner):
            for i in range(segments):
                angle = i * math.tau / segments
                verts.append(origin + rotation @ Vector((math.cos(angle)*radius,
                                                        math.sin(angle)*radius, z)))
    for i in range(segments):
        j = (i+1) % segments
        faces.extend([(i, j, 2*segments+j, 2*segments+i),
                      (segments+j, segments+i, 3*segments+i, 3*segments+j),
                      (j, i, segments+i, segments+j),
                      (2*segments+i, 2*segments+j, 3*segments+j, 3*segments+i)])
    mesh = bpy.data.meshes.new(name)
    mesh.from_pydata(verts, [], faces)
    mesh.update()
    obj = bpy.data.objects.new(name, mesh)
    bpy.context.collection.objects.link(obj)
    return finish(obj, mat, name, 0, 1)


def text(name, value, center, size, mat, face='front', align='LEFT'):
    curve = bpy.data.curves.new(name, 'FONT')
    curve.body = value
    curve.size = size
    curve.align_x = align
    curve.align_y = 'CENTER'
    curve.resolution_u = 2
    curve.extrude = 0
    if FONT:
        curve.font = FONT
    obj = bpy.data.objects.new(name, curve)
    bpy.context.collection.objects.link(obj)
    obj.location = bvec(center)
    if face == 'front':
        obj.rotation_euler = (math.pi/2, 0, 0)
    elif face == 'right':
        obj.rotation_euler = (math.pi/2, 0, -math.pi/2)
    elif face == 'left':
        obj.rotation_euler = (math.pi/2, 0, math.pi/2)
    elif face == 'top':
        obj.rotation_euler = (0, 0, 0)
    bpy.ops.object.select_all(action='DESELECT')
    obj.select_set(True)
    bpy.context.view_layer.objects.active = obj
    bpy.ops.object.convert(target='MESH')
    obj.data.materials.append(mat)
    return obj


def fastener(name, point, mat, axis=(0, 0, -1), radius=.006):
    return cylinder(name, point, radius, .0025, mat, axis, 8, 0)


def make_lab():
    reset()
    white = material('Lab_WarmCeramic', (.70, .715, .68), .08, .32)
    navy = material('Lab_DeepPetrolPowdercoat', (.031, .055, .066), .30, .34)
    graphite = material('Lab_GraphiteElastomer', (.017, .023, .026), .04, .62)
    alloy = material('Lab_SatinAluminum', (.48, .52, .53), .87, .27)
    copper = material('Lab_BurntOrange', (.69, .185, .060), .40, .30)
    ash = material('Lab_PaleAsh', (.43, .31, .20), .0, .46)
    grain = material('Lab_AshGrain', (.32, .22, .14), .0, .48)
    floor = material('Lab_EpoxyFloor', (.14, .18, .19), .15, .44)
    light = material('Lab_WarmDiffuser', (.80, .79, .69), .0, .35, (.84, .91, 1), 2.2)
    led = material('Lab_InstrumentTeal', (.025, .30, .25), .05, .30, (.10, .83, .67), 1.8)
    marking = material('Lab_IvoryMarkings', (.74, .78, .75), .0, .58)

    # Thin decorative panels preserve the existing room's shell and colliders.
    for index in range(5):
        x = -2.0 + index
        box('Back acoustic panel %02d' % index, (x, 1.91, 2.426), (.986, 1.72, .030), white, .009)
        box('Back low service panel %02d' % index, (x, .535, 2.426), (.986, .96, .030), navy, .009)
    box('Back service datum rail', (0, 1.04, 2.407), (4.93, .028, .012), alloy, .005)
    box('Back wall skirting shadow', (0, .075, 2.412), (4.94, .075, .014), graphite, .005)
    box('Back ceiling reveal', (0, 2.79, 2.418), (4.93, .055, .020), navy, .006)
    # A narrow color stroke gives the header a deliberate visual anchor.
    box('Module datum copper', (-1.035, 2.54, 2.404), (.016, .22, .010), copper, .003)
    for side in (-1, 1):
        for index, z in enumerate((-1.88, -.63, .62, 1.87)):
            box('Side upper ceramic %s %s' % (side,index), (side*2.426, 1.91, z),
                (.030, 1.72, 1.234), white, .009)
            box('Side lower service %s %s' % (side,index), (side*2.426, .535, z),
                (.030, .96, 1.234), navy, .009)
        box('Side aluminum datum %s' % side, (side*2.407, 1.04, 0), (.012, .028, 4.93), alloy, .004)
        box('Side skirting %s' % side, (side*2.412, .075, 0), (.014, .075, 4.94), graphite, .004)
        box('Side ceiling reveal %s' % side, (side*2.418, 2.79, 0), (.020, .055, 4.93), navy, .005)
    # Large floor inset and restrained working-area boundary.
    box('Working bay inset floor', (0, .008, .21), (2.29, .012, 2.62), floor, .005)
    for x in (-1.17, 1.17):
        box('Inlaid copper bay line', (x, .014, .21), (.019, .005, 2.59), copper, .002)
        for z in (-1.07, 1.49):
            box('Inlaid boundary return', (x*.94, .014, z), (.16, .005, .019), copper, .002)
    for x in (-1.80, 1.80):
        box('Epoxy expansion joint', (x, .003, 0), (.006, .003, 4.90), graphite, .001)
    text('Floor bay identification', '01  /  INSPECTION', (0, .017, -1.0), .075, marking, 'top', 'CENTER')

    # A properly engineered bench: radiused perimeter, thin inlaid top, feet,
    # structural uprights and accessible instrument fascia.
    bench_body = box('Inspection bench beveled body', (0, .793, .60), (1.66, .114, .86), navy, .037, 3)
    # The inset surface below covers this entire flat face at the same Y=.85.
    # Keeping both faces produces severe depth fighting in the Quest's view.
    # Remove only the hidden flat face; retain every bevel, side, and dimension.
    body_mesh = bmesh.new()
    try:
        body_mesh.from_mesh(bench_body.data)
        hidden_top = [face for face in body_mesh.faces
                      if all(abs((bench_body.matrix_world @ v.co).z - .85) < 1e-6
                             for v in face.verts)]
        if len(hidden_top) != 1 or len(hidden_top[0].verts) != 4:
            raise RuntimeError('Inspection bench hidden top face changed; review table geometry')
        expected_corners = {(round(x, 6), round(z, 6))
                            for x in (-.793, .793) for z in (.207, .993)}
        actual_corners = {(round((bench_body.matrix_world @ v.co).x, 6),
                           round((bench_body.matrix_world @ v.co).y, 6))
                          for v in hidden_top[0].verts}
        if actual_corners != expected_corners:
            raise RuntimeError('Inspection bench top is no longer fully covered by the inset')
        bmesh.ops.delete(body_mesh, geom=hidden_top, context='FACES_ONLY')
        body_mesh.to_mesh(bench_body.data)
        bench_body.data.update()
    finally:
        body_mesh.free()
    box('Inspection bench aluminum surface', (0, .842, .60), (1.60, .016, .80), alloy, .016, 3)
    box('Inspection bench front bumper', (0, .770, .158), (1.43, .026, .020), graphite, .009)
    box('Inspection fascia inset', (0, .814, .167), (1.27, .037, .007), graphite, .004)
    text('Bench technical badge', 'M1 / SURFACE METROLOGY', (-.60, .814, .162), .021, marking)
    for x in (-.74, .74):
        fastener('Bench fascia fastener', (x, .812, .161), alloy, radius=.008)
    for x in (-.64, .64):
        box('Bench formed support', (x, .408, .64), (.135, .688, .39), white, .025, 3)
        box('Bench upright service insert', (x, .409, .436), (.082, .532, .010), navy, .010)
        box('Bench stabilizer foot', (x, .065, .62), (.22, .095, .70), navy, .025, 3)
        for z in (.37, .89):
            cylinder('Bench leveling foot', (x, .014, z), .061, .026, graphite, vertices=16)
        box('Bench copper datum strip', (x, .62, .429), (.051, .065, .006), copper, .007)
    box('Bench rear crossmember', (0, .37, .825), (1.29, .074, .065), navy, .010)
    # Pale ash underside lip warms up the otherwise clinical metal table.
    box('Bench ash trim', (0, .728, .189), (1.47, .027, .029), ash, .007)
    for k in range(3):
        box('Bench fine ash grain', (0, .724+k*.007, .173), (1.42, .0010, .001), grain, 0)

    # Left cabinet accurately follows its existing physical footprint.
    box('Calibration cabinet case', (-2.18, .835, 1.678), (.48, 1.61, .555), navy, .026, 3)
    box('Calibration cabinet plinth', (-2.18, .079, 1.678), (.43, .15, .505), graphite, .012)
    box('Calibration cabinet ceramic lid', (-2.18, 1.630, 1.678), (.482, .020, .558), white, .008)
    for k in range(3):
        y = .30+k*.455
        box('Cabinet ceramic drawer %s' % k, (-2.18, y, 1.389), (.438, .422, .027), white, .012)
        box('Cabinet handle recess %s' % k, (-2.18, y+.131, 1.371), (.237, .041, .009), graphite, .006)
        box('Cabinet aluminum handle %s' % k, (-2.18, y+.143, 1.359), (.213, .018, .018), alloy, .006)
        text('Drawer index %s' % k, ['01 / GAUGES','02 / OPTICS','03 / FIXTURES'][k],
             (-2.36, y-.106, 1.373), .018, navy)
    box('Cabinet vertical orange indicator', (-1.949, 1.02, 1.490), (.008, .153, .026), copper, .004)
    # One instrument case on top, sized small enough to keep the upper sightline.
    instrument_case('Cabinet optics transit case', (-2.18, 1.701, 1.67), (.34, .12, .38), graphite, alloy, copper)

    # Right mobile cart: complete shell, three solid shelves and restrained props.
    for x in (1.84, 2.40):
        for z in (1.46, 2.41):
            box('Cart anodized upright', (x, .38, z), (.031, .62, .031), alloy, .007)
            cylinder('Cart caster tyre', (x, .049, z), .047, .025, graphite, axis=(1,0,0), vertices=16)
    for y in (.125, .386, .680):
        box('Cart formed tray', (2.12, y, 1.94), (.60, .030, 1.00), navy, .013)
        box('Cart shelf rubber insert', (2.12, y+.016, 1.94), (.550, .004, .940), graphite, .002)
        box('Cart shelf back upstand', (2.12, y+.048, 2.429), (.60, .079, .016), navy, .008)
    box('Cart ash upper worktop', (2.12, .696, 1.94), (.550, .008, .94), ash, .008)
    for k in range(5):
        box('Cart worktop grain', (1.91+k*.088, .7005, 1.94), (.0012, .001, .90), grain, 0)
    box('Cart ceramic lower drawer', (2.12, .251, 1.445), (.51, .20, .025), white, .013)
    box('Cart lower drawer pull', (2.12, .301, 1.424), (.19, .017, .020), alloy, .006)
    instrument_case('Cart calibration case', (2.12, .477, 1.86), (.40, .144, .43), graphite, alloy, copper)
    box('Cart top folded cloth base', (2.27, .714, 1.88), (.18, .024, .25), white, .009)
    box('Cart top folded cloth fold', (2.266, .727, 1.88), (.17, .008, .22), white, .003)
    # Slim instrument dock resting flat on the cart, not a second handheld tool.
    box('Cart diagnostic tablet body', (2.034, .716, 2.031), (.20, .026, .28), graphite, .017)
    box('Cart diagnostic tablet display', (2.034, .731, 2.035), (.170, .004, .207), navy, .006)
    for k in range(3):
        box('Cart diagnostic line', (2.013, .7338, 1.983+k*.037), (.099-k*.021, .001, .007), led, .001)
    text('Cart etched identification', 'CAL / 01', (1.880, .676, 1.423), .022, marking)
    # Finished wall mounted rack above the cart, clear of instructional signs.
    box('Wall utility rack back', (2.412, 2.065, 1.85), (.038, .73, .77), navy, .016)
    for y in (1.755, 2.070, 2.375):
        box('Wall utility shelf', (2.303, y, 1.85), (.255, .029, .77), alloy, .007)
    for z in (1.520, 2.180):
        box('Wall utility vertical rail', (2.275, 2.065, z), (.036, .65, .029), graphite, .006)
    for k, z in enumerate((1.70, 1.99)):
        box('Wall rack calibration bin %s' % k, (2.308, 1.865, z), (.22, .19, .225), white, .010)
        box('Wall rack bin handle %s' % k, (2.191, 1.910, z), (.015, .025, .083), graphite, .005)
        box('Wall rack bin orange badge %s' % k, (2.194, 1.839, z), (.007, .035, .066), copper, .003)
    box('Wall rack folded protective sheets', (2.312, 2.122, 1.94), (.20, .061, .30), white, .010)
    box('Wall rack cased optical standard', (2.309, 2.147, 1.66), (.19, .12, .17), graphite, .015)

    # Suspended linear lighting has genuinely visible underside diffusers.
    for x in (-1.28, 1.28):
        box('Linear luminaire housing', (x, 2.912, .15), (.29, .098, 2.63), navy, .018, 3)
        box('Linear luminaire satin rim', (x, 2.860, .15), (.259, .012, 2.586), alloy, .005)
        box('Linear luminaire emissive diffuser', (x, 2.851, .15), (.213, .012, 2.532), light, .005)
        for z in (-.79, .15, 1.09):
            box('Linear fixture diffuser separator', (x, 2.844, z), (.22, .005, .012), navy, .002)
        for z in (-.81, 1.11):
            box('Luminaire ceiling mount', (x, 2.969, z), (.064, .055, .09), alloy, .006)
    # Ash baffle pair: a limited warm accent, not repetitive visual noise.
    for x in (-.47, .47):
        box('Ceiling pale ash acoustic baffle', (x, 2.944, .34), (.045, .11, 2.99), ash, .010)
        box('Ceiling baffle edge grain', (x-.023, 2.934, .34), (.001, .026, 2.80), grain, .0004)

    # Back entry trim gives a believable room behind the standing position.
    box('Entry graphite surround', (0, 1.20, -2.419), (1.34, 2.40, .055), navy, .021)
    box('Entry ceramic door face', (0, 1.175, -2.383), (1.18, 2.28, .020), white, .012)
    box('Entry vertical observation inset', (.29, 1.60, -2.367), (.16, .71, .014), navy, .025)
    box('Entry latch strike', (-.43, 1.03, -2.362), (.044, .18, .019), alloy, .008)
    box('Entry horizontal lever', (-.35, 1.075, -2.336), (.18, .025, .030), alloy, .009)
    box('Entry copper signage marker', (-.414, 1.91, -2.367), (.019, .23, .014), copper, .004)
    export_asset('MetrologyLab', 'environment', 25000)
    render_preview('MetrologyLab', (-.15, 1.65, -2.12), (.05, 1.45, .8), True)


def instrument_case(name, center, size, shell, alloy, accent):
    x,y,z = center
    sx,sy,sz = size
    box(name+' molded lower', (x,y-sy*.08,z), (sx,sy*.81,sz), shell, .016)
    box(name+' lid seam', (x,y+sy*.35,z), (sx*1.012,.009,sz*1.012), alloy, .004)
    box(name+' lid', (x,y+sy*.425,z), (sx,sy*.20,sz), shell, .013)
    for dx in (-sx*.31,sx*.31):
        box(name+' toggle latch', (x+dx,y,z-sz*.51), (.032,.048,.009), alloy, .005)
    box(name+' serial badge', (x,y+sy*.54,z), (sx*.31,.003,sz*.18), accent, .003)
    box(name+' molded handle', (x,y,z-sz*.53), (sx*.26,.020,.024), shell, .006)


def make_scanner():
    reset()
    shell = material('Scanner_CeramicShell', (.76, .775, .73), .10, .29)
    grip = material('Scanner_GraphiteGrip', (.016, .023, .028), .06, .52)
    alloy = material('Scanner_MachinedAluminum', (.45, .50, .53), .91, .22)
    orange = material('Scanner_CopperAccent', (.78, .208, .069), .52, .27)
    lens = material('Scanner_OpticalTeal', (.008, .17, .16), .30, .13, (.025, .66, .62), .9)
    screen = material('Scanner_DisplayGlass', (.006, .016, .020), .20, .20)
    graphics = material('Scanner_DisplayGraphics', (.45, .88, .74), .05, .32, (.20, .84, .66), 1.0)
    # Actual body within original .078 x .105 x .158 collider envelope. Grip is
    # cosmetic and remains within the existing controller hand silhouette.
    box('Scanner main ceramic shell', (0, .008, .002), (.070, .054, .146), shell, .012, 3)
    box('Scanner graphite lower seam', (0, -.016, .002), (.065, .015, .136), grip, .006, 3)
    box('Scanner rear impact boot', (0, .002, -.065), (.063, .045, .020), grip, .008, 3)
    box('Scanner top recessed panel', (0, .036, .006), (.054, .006, .109), grip, .005, 3)
    box('Scanner display glass separate mesh', (0, .0398, .015), (.044, .002, .065), screen, .004, 3)
    text('Scanner display QA01', 'QA-01', (-.018, .0411, .030), .009, graphics, 'top')
    text('Scanner display READY', 'READY', (-.018, .0411, .012), .006, graphics, 'top')
    for i in range(4):
        box('Scanner display battery bar %s' % i, (.011+i*.002, .04115, .028), (.0012,.00035,.004), graphics, .0001, 1)
    box('Scanner display baseline', (0, .0412, -.007), (.033, .0004, .001), graphics, 0)
    for i in range(5):
        box('Scanner display waveform %s' % i, (-.010+i*.005, .0412, -.003), (.001,.0004,.002+i*.001), graphics, 0)
    # A durable grip shaped around the holding hand, with inset ribs.
    box('Scanner angled pistol grip', (0, -.055, -.034), (.030, .083, .046), grip, .008, 3, angle=-12)
    box('Scanner grip heel cap', (0, -.094, -.042), (.035, .014, .044), alloy, .005, 2, angle=-12)
    for side in (-1,1):
        box('Scanner grip side ceramic cheek', (side*.016, -.048, -.035), (.005, .045, .030), shell, .002, 2, angle=-12)
        for k in range(4):
            box('Scanner grip rib %s %s' % (side,k), (side*.017, -.069+k*.008, -.048+k*.0015),
                (.0035,.0022,.020), grip, .001, 1, angle=-12)
        # Copper inlay runs partway down either housing side.
        box('Scanner side copper accent %s' % side, (side*.0348, .010, -.006), (.0025,.008,.086), orange, .001, 2)
        for z in (-.045,.038):
            fastener('Scanner shell captive screw', (side*.0352, .022, z), alloy,
                     axis=(side,0,0), radius=.0022)
    box('Scanner trigger paddle', (0, -.034, -.003), (.021,.026,.010), orange, .004, 3, angle=-12)
    box('Scanner top tactile button', (0, .040, -.041), (.015,.006,.012), orange, .004, 3)
    # Lens architecture centers precisely on the existing emitter transform.
    cylinder('Scanner front optical collar', (0, 0, .074), .029, .014, grip, axis=(0,0,1), vertices=32, bevel=.0017)
    ring('Scanner machined optical ring', (0, 0, .082), .0268, .022, .005, alloy, segments=32)
    ring('Scanner optical copper witness ring', (0, 0, .084), .0227, .0211, .003, orange, segments=32)
    cylinder('Scanner optical emitter window', (0, 0, .085), .0209, .002, lens, axis=(0,0,1), vertices=32, bevel=.0003)
    ring('Scanner emitter glass bevel', (0, 0, .086), .0207, .0193, .001, lens, segments=32)
    # Small mechanical witness notches on front ring, kept clear of the beam.
    for a in (0,90,180,270):
        ang=math.radians(a)
        fastener('Scanner optical ring fastener', (.0245*math.cos(ang),.0245*math.sin(ang),.085),
                 grip, axis=(0,0,1), radius=.0013)
    export_asset('InspectorScanner', 'scanner', 6000)
    render_preview('InspectorScanner', (.32,.25,.36), (0,-.025,.005), False)


def export_asset(name, group, tri_limit):
    # Keep all named parts independently editable in the authored Blender source.
    # Only the exported runtime assets use material batching.
    if not COMPARE_EXPORT:
        bpy.ops.wm.save_as_mainfile(filepath=str(ART/(name+'.blend')))
    for mat_info in MATDATA:
        meshes = [o for o in bpy.context.scene.objects if o.type == 'MESH']
        selected = [o for o in meshes if o.data.materials and o.data.materials[0].name == mat_info['name']]
        if not selected:
            continue
        bpy.ops.object.select_all(action='DESELECT')
        for obj in selected:
            obj.select_set(True)
        bpy.context.view_layer.objects.active = selected[0]
        bpy.ops.object.join()
        obj = bpy.context.object
        obj.name = group + '_' + mat_info['name']
        bpy.ops.object.transform_apply(location=True, rotation=True, scale=True)
    bpy.context.view_layer.update()
    payload = dict(version=1, coordinateSystem='UnityLeftHandedYUp', group=group,
                   units='meters', materials=MATDATA, meshes=[])
    tris_total=0
    for obj in sorted(bpy.context.scene.objects, key=lambda x: x.name):
        if obj.type != 'MESH':
            continue
        mesh=obj.data
        mesh.calc_loop_triangles()
        pos, normals, uv, triangles = [], [], [], []
        uv_layer=mesh.uv_layers.active
        normal_xform=obj.matrix_world.to_3x3().inverted().transposed()
        # One vertex per triangle corner preserves all authored hard normals and
        # UV seams, and avoids dependency on any third-party glTF importer.
        for tri in mesh.loop_triangles:
            for loop_idx in (tri.loops[0],tri.loops[2],tri.loops[1]):
                loop=mesh.loops[loop_idx]
                vertex=obj.matrix_world @ mesh.vertices[loop.vertex_index].co
                normal=(normal_xform @ mesh.corner_normals[loop_idx].vector).normalized()
                triangles.append(len(pos)//3)
                pos.extend(uvec(vertex))
                normals.extend(uvec(normal))
                if uv_layer:
                    coords=uv_layer.data[loop_idx].uv
                    uv.extend((float(coords.x), float(coords.y)))
                else:
                    uv.extend((float(vertex.x), float(vertex.y)))
        mat_index=MATINDEX[mesh.materials[0].name]
        payload['meshes'].append(dict(name=obj.name, materialIndex=mat_index,
                                     positions=pos, normals=normals, uv=uv,
                                     triangles=triangles))
        tris_total+=len(triangles)//3
    payload['triangleCount']=tris_total
    payload['rendererCount']=len(payload['meshes'])
    if COMPARE_EXPORT:
        current=json.loads((LIVE_SOURCE/(name+'.mesh.json')).read_text(encoding='utf-8'))
        for saved_mesh, reference_mesh in zip(current['meshes'],payload['meshes']):
            differences={key:max((abs(a-b) for a,b in zip(saved_mesh[key],reference_mesh[key])),default=0)
                         for key in ('positions','normals','uv','triangles')}
            print('EXPORT_SAVE_COMPARISON', name, saved_mesh['name'], differences)
        print('EXPORT_MATERIAL_IDENTITY', name, current['materials']==payload['materials'])
    if tris_total>tri_limit:
        raise RuntimeError(f'{name}: {tris_total} triangles exceeds {tri_limit}')
    if len(payload['meshes'])>(14 if group=='environment' else 7):
        raise RuntimeError('Renderer budget exceeded')
    json_path=SOURCE/(name+'.mesh.json')
    json_text=json.dumps(payload,separators=(',',':'))
    if not json_path.exists() or json_path.read_text(encoding='utf-8') != json_text:
        json_path.write_text(json_text,encoding='utf-8')
    bpy.ops.object.select_all(action='SELECT')
    bpy.ops.export_scene.gltf(filepath=str(SOURCE/(name+'.glb')), export_format='GLB',
                              use_selection=True, export_yup=True, export_apply=True,
                              export_cameras=False, export_lights=False)
    print(f'ART_REFRESH_EXPORT {name}: triangles={tris_total}, renderers={len(payload["meshes"])}')


def area(name, location, target, energy, size, color=(1,.92,.82)):
    data=bpy.data.lights.new(name,'AREA')
    data.energy=energy
    data.shape='DISK'
    data.size=size
    data.color=color
    obj=bpy.data.objects.new(name,data)
    bpy.context.collection.objects.link(obj)
    obj.location=bvec(location)
    direction=bvec(target)-obj.location
    obj.rotation_euler=direction.to_track_quat('-Z','Y').to_euler()


def render_preview(name, location, target, lab):
    if COMPARE_EXPORT:
        return
    scene=bpy.context.scene
    world=bpy.data.worlds.new(name+' Review World')
    world.use_nodes=True
    world.node_tree.nodes['Background'].inputs['Color'].default_value=(.13,.16,.18,1)
    world.node_tree.nodes['Background'].inputs['Strength'].default_value=.38
    scene.world=world
    scene.render.engine='CYCLES'
    scene.cycles.samples=32
    scene.cycles.use_denoising=True
    scene.render.resolution_x=1440
    scene.render.resolution_y=1080
    scene.render.resolution_percentage=100
    scene.render.image_settings.file_format='PNG'
    scene.render.filepath=str(ART/(name+'-preview.png'))
    scene.view_settings.view_transform='AgX'
    cam_data=bpy.data.cameras.new('Review Camera')
    cam=bpy.data.objects.new('Review Camera',cam_data)
    bpy.context.collection.objects.link(cam)
    cam.location=bvec(location)
    cam.rotation_euler=(bvec(target)-cam.location).to_track_quat('-Z','Y').to_euler()
    cam.data.lens=22 if lab else 55
    cam.data.clip_start=.01
    scene.camera=cam
    if lab:
        area('Review key', (0,2.75,-.65), (0,.8,.8), 390, 3.0)
        area('Review cool fill', (-1.2,2.55,1.7), (0,1,0), 210, 2.0, (.69,.84,1))
        area('Review front softbox', (0,2,-3), (0,1,1), 170, 3.0)
        # Hide only near-side walls in this open dollhouse preview.
        for obj in scene.objects:
            if obj.type=='MESH' and 'Lab_' in obj.name:
                pass
    else:
        area('Review key', (-.20,.32,.12), (0,0,0), 13, .24)
        area('Review edge', (.20,.07,-.12), (0,0,0), 9, .18, (.65,.80,1))
        area('Review face', (0,-.02,.30), (0,0,0), 4, .15)
        scene.render.film_transparent=True
    bpy.ops.render.render(write_still=True)


if __name__ == '__main__':
    make_lab()
    make_scanner()
