# Industrial session controls

These are original Blender assets for the Module 1 start/restart controls.
The molded mushroom profile, cast enclosure, captive screws, status lenses,
and etched labels are modeled geometry. They use no external textures.

Run from the project root:

```powershell
& 'C:\Program Files\Blender Foundation\Blender 5.2\blender.exe' --background --python Tools/SessionControls/build_session_controls.py
```

Outputs:

- `ArtSource/SessionControls.blend`: both controls, individually editable parts,
  and the lit review scene. The two parent roots are offset for presentation;
  runtime exports have no presentation offset.
- `ArtSource/SessionControls-preview.png`: review render.
- `Assets/HelmetInspection/SessionControls/Source/IndustrialStartButton.mesh.json`
- `Assets/HelmetInspection/SessionControls/Source/IndustrialRestartButton.mesh.json`
- Same-name `.glb` files for interchange.

The JSON follows the existing art-refresh v1 mesh format, uses meters, and has
Unity left-handed X-right / Y-up / Z-forward coordinates baked into vertices.
Its origin is the center of the mounting back (X/Y center, back at Z=0).
The button faces toward -Z. No object translation or scale is needed on import.
The GLB follows the existing art-refresh Blender glTF Y-up convention.

Each unit is below 8,000 triangles and has exactly eight material batches:
seven `Static_...` meshes and one `ButtonCap`. Keep that cap as a separate
zero-transform child, and animate its local position from `(0,0,0)` to
`(0,0,0.009)` for a press. Do not translate it by its mesh bounds center.

The enclosure is 0.240m wide and 0.280m tall. Its faceplate ends at Z=-0.092m;
the domed cap extends to Z=-0.169m. The cap's center in X/Y is `(0,-0.023)`.
A suggested cap interaction box has center `(0,-0.023,-0.140)` and size
`(0.145,0.145,0.068)`. This describes the visible mushroom rather than an
offset legacy button. An implementation can make a deliberately larger hit
area if needed, while keeping it centered on the cap.

Static pilot renderers are `Static_Control_GreenPilot` and
`Static_Control_RedPilot`. Their source material emission is deliberately
subtle. Active/idle changes may use renderer property blocks; other renderer
materials do not need mutation for interaction feedback.

The rebuild script reuses the established project's geometry helpers without
invoking the lab or scanner regeneration. It writes only these new session
control assets. Unity's scene and all gameplay wiring remain the installer's
responsibility.
