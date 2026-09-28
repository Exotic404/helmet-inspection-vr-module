# Industrial session controls

The saved HelmetDefectInspection scene contains two Blender-built controls below the instrument displays, between the instruction and findings screens. Green **START** opens an inspection; red **RESTART** clears the session and restores the existing prop placements. Aim at a cap and squeeze the controller grip (the existing XRI Select binding). Direct selection also works.

Each control uses a satin aluminum enclosure, a powder-coated faceplate, captive screws, molded mushroom cap, and mesh labels. The cap travels inward 9 mm. A mechanical click, a rising/falling confirmation tone, and a controller pulse identify accepted Start/Restart interactions. Pressing Start during an existing attempt only produces a mechanical click; it does not reset progress. Rapid repeat selections have a 250 ms cooldown.

## Editable assets

- `ArtSource/SessionControls.blend`: editable Blender source for both controls.
- `ArtSource/SessionControls-preview.png`: Blender review render.
- `Tools/SessionControls/build_session_controls.py`: reproducible Blender authoring/export script.
- `Assets/HelmetInspection/SessionControls/Source`: GLB and Unity-coordinate mesh JSON exports.
- `Assets/HelmetInspection/SessionControls/Generated`: native Unity meshes and URP materials used by the scene.

Meshes use metres and face local -Z. The separate `ButtonCap` transform moves along +Z; exported vertices include their local rest position. Each control has eight material batches and fewer than 8,000 triangles. No added realtime lights, textures, physics meshes, or runtime asset-loading dependencies are needed.

## Rebuild only these controls

Run Blender with `--background --python Tools/SessionControls/build_session_controls.py`, then choose **Helmet Inspection > Session Controls > Install Blender Industrial Buttons** in Unity. Save any open scene changes first. This installer updates only the controls and their mounting/signage, preserves existing Start/Restart component identities and session references, and compares all unrelated serialized scene components before saving. A pre-install scene backup is written under `Logs/SessionControlsSep23`.

The root BoxColliders are fitted to the cap meshes and explicitly assigned to the existing XRSimpleInteractables. Moving a whole button root moves its visual, collider and sound together. Existing helmet geometry, hotspot positions, scanner behavior, player tracking and lighting are not regenerated.

## Verification

- `HelmetInspection.Editor.SessionControlsVerifier.Verify`: saved-scene bindings, mesh budget, collider alignment and unobstructed rays.
- `HelmetInspection.Editor.SessionControlsVerifier.RunPlayMode`: actual XRI selections, session changes, press animation, audio, debounce and restoration. Run batch Unity without `-quit` for this asynchronous entry point.
- `HelmetInspection.Editor.HeldItemLocomotionVerifier.Verify`: existing interaction, scanner, ten-of-twelve completion, D09, standing height and held-item movement regressions; also omit `-quit`.
- `HelmetInspection.Editor.IndustrialSessionControlsInstaller.RenderReview`: actual Unity scene renders in `Logs/SessionControlsSep23`.

Headset audio loudness and physical comfort still need a human test with the intended headset volume.
