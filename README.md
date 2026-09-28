# Helmet Defect Inspection — Module 1

A room-scale Meta Quest 3 training application for visually comparing a reference helmet scan (A1) with a defective scan (A2), locating ten measured surface deviations, and reviewing the required disposition for each defect.

## Open and run

- Open this folder with Unity `6000.5.7f1`.
- The editor startup repair opens `Assets/HelmetInspection/Scenes/HelmetDefectInspection.unity` and also makes it the Play Mode start scene. If Unity restores an old temporary backup, use **Helmet Inspection > Repair Project and Open Training Scene**.
- Put on the Quest 3 through Quest Link for Play Mode, or use **Helmet Inspection > Build Quest Development APK** for a headset build.
- Without a headset, press Play to use the editor-only XR Interaction Simulator. Press `Tab` to cycle control modes; use `[` for the left controller, `]` for the right controller, or both keys for both controllers. The simulator shows its remaining mouse/keyboard controls on screen and is excluded from Android builds.
- Squeeze grip to pick up either helmet. The left wrist carries the inspection scanner; if you grab it with the other hand, the trigger follows that hand. Aim at A2 and squeeze the scanner hand's trigger. You can hold the trigger while settling your aim; release before confirming the next defect. Use the right stick for 30-degree snap turns. Press the right controller secondary button to recenter.
- Aim at the green **START** button beneath the two instrument displays and squeeze the controller grip. Find any **10 of the 12 defects**, then review the measurements and disposition guidance on the wall display. The red **RESTART** button clears the session and returns both helmets and the scanner to their homes, including held items. The new Blender-built controls depress, click, play distinct confirmation tones, and provide controller feedback. See [ArtSource/SESSION_CONTROLS.md](ArtSource/SESSION_CONTROLS.md) for their editable source and verification commands.

## Current artwork: Metrology Studio

The saved scene contains the Blender-built lab and precision scanner, metallic-red helmet finish, and scanner reliability fixes. Original helmet geometry and measured defect data are unchanged. See [ArtSource/ART_REFRESH.md](ArtSource/ART_REFRESH.md) for editable source files, verification, and review images.

Use **Helmet Inspection > Metrology Studio > Verify Metrology Studio and Gameplay** for the complete current regression suite. It writes `Builds/ArtRefreshValidation.txt`. Normal APK builds also run the core project validation.

## Rebuild generated content

**Do not use Build Complete Module or the legacy Environment Art Pass/Polish installers during normal editing.** Those are full-generation tools for older layouts and can replace authored scene work. Make a backup before intentionally regenerating anything.

For an intentional art-only rebuild, run `Tools/ArtRefresh/build_lab_assets.py` using Blender, then **Helmet Inspection > Metrology Studio > Install Blender Art Refresh**. This replaces generated presentation, fits the existing scanner collider to the new prop, and checks that all other collider configurations, helmet meshes, and defect data remain unchanged. It is not needed to open, play, or build the saved scene.

Use **Helmet Inspection > Validate Quest Module** before a build. The same validation automatically runs before APK/AAB builds and writes `Builds/ValidationReport.txt`.

## Release boundary

The project is configured for Android ARM64, IL2CPP, OpenGLES3, OpenXR, Meta Quest, and Touch Plus controllers. A production store upload still requires the publisher's private Android keystore, final store identity, privacy/support URLs, comfort classification, screenshots, and Meta dashboard submission. Keystore secrets must never be committed.
