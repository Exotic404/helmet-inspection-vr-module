# Metrology Studio art refresh

## Latest verified build - 2026-09-11

- Full art/gameplay acceptance passed; all 86 core Quest checks passed.
- Scanner suite: 5,642 assertions passed. Final Play Mode test registered all ten defects and passed release, held-trigger, restart and focus-recovery checks.
- Live HMD-camera preview passed with runtime SRP batching and 4x MSAA.
- Clean Android build succeeded. APK size: 52,255,694 bytes. ARM64 only, minimum SDK 29, target SDK 36; APK v2 signature verified.
- APK SHA-256: `904A3E4141F0CD016312DAFF450AAE86BFD6FDBBAFA2EC1C69ACEBDA60EAC0F7`.
- Installed successfully to the connected Quest 3; the installed base APK has the same SHA-256. Automatic launch was requested, but another VR app was active. No other app was force-closed, and in-headset play/performance has not been verified.
- Launch from the headset library's Unknown Sources: **Helmet Defect Inspection - Module 1**.

## Editable sources and scene

- `MetrologyLab.blend`: individual named lab parts, in meters.
- `InspectorScanner.blend`: individual named scanner parts.
- `../Tools/ArtRefresh/build_lab_assets.py`: reproducible Blender construction/export.
- `../Assets/HelmetInspection/ArtRefresh/Source`: exported GLB interchange files and Unity-coordinate mesh JSON.
- `../Assets/HelmetInspection/ArtRefresh/Generated`: native Unity meshes and URP materials. No runtime GLB parser is required.
- `../Assets/HelmetInspection/Scenes/HelmetDefectInspection.unity`: the playable saved scene.

The environment uses 19,710 imported triangles in 11 material batches; the scanner uses 4,386 triangles in 7 batches. Signage and existing gameplay objects are additional. This is designed for standalone Quest, not a desktop-only graphics pipeline. These mesh counts are not a headset frame-rate measurement.

## What changed

Warm ceramic wall panels, graphite service panels, bevelled inspection furniture, metal handles, storage drawers, utility cart, overhead light fixtures, copper inlays, and distinct diagnostic displays replace the previous decorative art. Text panels retain their authored positions and live session behavior. START and RESET keep their physical activation positions.

The scanner has a new ceramic body, graphite grip, optical collar, copper controls, and display. The original grab component, rigidbody, socket, optical origin, and reset behavior remain. Its existing box collider is fitted to the new longer grip so the handle cannot sink below the physical hull. The other 40 gameplay colliders are protected against changes by the installer.

The original A1 and A2 helmet meshes, poses, and authored defect data remain intact. Both use the UV-independent triplanar shader with metallic red lacquer. The reflection cubemap now stores actual six-face rendered pixels with CPU readback, instead of the prior uninitialized texture. The saved cubemap is tested after loading from disk. Emissive materials explicitly retain the emission shader variant needed for live scanner/button feedback.

## Scanner reliability fixes

- Visible hole centers remain scannable when the exterior target is behind the scanner tip at close range.
- Input follows the controller actually holding the scanner; the wrist socket retains left-trigger operation.
- A held squeeze retries while aim settles; one confirmed finding requires a release before the next.
- Beam feedback and confirmation share target selection. All ten defects compete independently; completed/disabled targets do not consume scans.
- Existing helmet-to-helmet collision, scanner-to-helmet collision exclusion, physical-head translation lock, virtual locomotion, and restart/home placement behavior remain.

## Verification and review

Run **Helmet Inspection > Metrology Studio > Verify Metrology Studio and Gameplay**. This checks the core project, original helmet hashes, authored defects, child-safe movement and reset checks, all six exterior holes, scanner reliability, material emission, scanner hull and emitter alignment, visual budgets, missing scripts, and saved reflections. Report: `../Builds/ArtRefreshValidation.txt`.

The scanner geometry suite exercises 5,642 assertions across four poses, three scales, five ranges, three approach angles, small aim offsets, both hand routes, and target-state gates. `ScannerReliabilityVerifier.VerifyRuntime` is a separate batch entry point (omit `-quit`) for real Play Mode registration of all ten findings plus held-trigger, release, restart and focus recovery.

Review images are in `../Assets/HelmetInspection/ArtRefresh/Previews/`. `ArtRefreshInstaller.RenderViews` generates inspection views. `ArtRefreshPlayPreview.Run` (omit `-quit`) captures the actual HMD camera after 60 normal Play Mode frames with SRP batching and 4x MSAA enabled. Offline editor captures temporarily bypass SRP batching to avoid stale same-frame material buffers; the runtime pipeline remains batched.

Use **Helmet Inspection > Build Quest Development APK** to make `../Builds/HelmetInspectionModule1.apk`. Actual headset performance, comfort, and real controller handling still need a Quest test. This development APK is not a signed store release.

## Preservation and regeneration

Pre-refresh scene and material snapshots are in `Backups/BeforeArtRefresh-20260910/`. Current defect-data SHA-256:

`64097A790CBF329B3BA06FCAB541AE54B588D191948FD717E0E0E43433173AFA`

Opening or building the saved scene does not require rerunning an installer. Full-module and older art-generation tools can replace authored content; do not run those for ordinary editing. The new art installer likewise replaces only its generated visual hierarchy, so put independent manual decoration outside that hierarchy.
