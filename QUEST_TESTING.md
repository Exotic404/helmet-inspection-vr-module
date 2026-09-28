# Meta Quest 3 testing guide

Download the current test APK from the [v1.1.0 test release](https://github.com/Exotic404/helmet-inspection-vr-module/releases/tag/v1.1.0-test). The Android package is `org.adfl.training.helmetinspection.module1`.

## Install with Meta Quest Developer Hub

1. Enable Developer Mode for the Quest headset.
2. Install [Meta Quest Developer Hub](https://developers.meta.com/horizon/documentation/spatial-sdk/meta-quest-developer-hub/) and sign in.
3. Connect the powered-on headset with a USB-C data cable.
4. Put on the headset and choose **Always allow from this computer** when USB debugging appears.
5. Open **Device Manager** in MQDH and drag `HelmetInspectionModule1.apk` onto the connected headset.
6. In the headset, open **Apps > Unknown Sources**, then launch **Helmet Inspection Module 1**.

MQDH overwrites an older build with the same package ID. If the headset shows a controller-required screen, wake both Touch controllers and continue.

## Basic test

1. Confirm the player starts at a comfortable standing height and can see over the table.
2. Aim at the green **START** control and squeeze the controller grip. Confirm the cap moves and the click/confirmation sound is audible.
3. Pick up both helmets and the scanner. Confirm movement stays stable while holding each item.
4. Scan several cyan defect targets, including hole defects and **A2-D09 Material Bulge**. Detected targets should turn green.
5. Confirm any 10 of the 12 independent defects completes the exercise.
6. Move or hold the helmets and scanner, then use the red **RESTART** control. Confirm its distinct sound plays and every item, target, and progress value resets.
7. Remove and put the headset back on, or relaunch from a different physical position. Confirm the room remains visible and usable.

Please report the headset model and Horizon OS version, the step that failed, and a short recording or screenshot when possible.

## ADB alternative

With Android platform-tools installed and the headset authorized:

```powershell
adb devices
adb install -r HelmetInspectionModule1.apk
adb shell am start -n org.adfl.training.helmetinspection.module1/com.unity3d.player.UnityPlayerGameActivity
```
