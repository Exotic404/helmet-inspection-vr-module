# Install Module 1 on your Quest

APK: `C:\Users\gamin\Documents\GitHub\VRmodule1\Builds\HelmetInspectionModule1.apk`

1. Connect the Quest to this PC with USB. Turn on Developer Mode if necessary, put on the headset, and accept **Allow USB debugging**.
2. Open PowerShell and paste:

```powershell
$adb = 'C:\Users\gamin\AppData\Local\Unity\Hub\Editor\6000.5.7f1\Editor\Data\PlaybackEngines\AndroidPlayer\SDK\platform-tools\adb.exe'
& $adb devices
& $adb install -r 'C:\Users\gamin\Documents\GitHub\VRmodule1\Builds\HelmetInspectionModule1.apk'
```

3. Wait for **Success**, then open **Helmet Defect Inspection - Module 1** from the headset's **Unknown Sources** app list. Turn on both controllers. This updates the original Module 1 app. Narration works offline.

If `devices` reports `unauthorized`, accept the USB debugging prompt inside the headset and run the commands again.

Alternatively, use Meta Quest Developer Hub's connected device app installation control to select the same APK.

Reference: [Meta's ADB installation guide](https://developers.meta.com/horizon/documentation/native/android/ts-adb/).

## Remove the rejected UPRAISE copy

The automated deletion was blocked. To remove it manually:

- Open [UPRAISE-module1 repository settings](https://github.com/Exotic404/UPRAISE-module1/settings), scroll to **Danger Zone > Delete this repository**, and confirm the repository name.
- In File Explorer, delete only `C:\Users\gamin\Documents\GitHub\UPRAISE module1`. Keep `VRmodule1`.
