# Module 1 narration

These clips read original lesson instructions using Microsoft's `en-US-JennyNeural` voice through [edge-tts](https://github.com/rany2/edge-tts). Speech uses the normal rate, pitch and volume, with no reverb or other effects. The scanner confirmation is exactly "Defect logged."

The generated MP3 files are bundled with the Unity application, so headset playback works offline. Only regenerating the clips needs an internet connection. No audio from MetaVet or the discarded UPRAISE project is used.

To regenerate with Python 3:

```powershell
python -m pip install --target Builds/voice-tools/packages edge-tts==7.3.1 miniaudio==1.71
python Tools/Narration/generate_voice.py
```

The script writes to `Assets/HelmetInspection/Resources/Module1Voice/`, fully decodes each clip to check that it contains audio, and prints its duration. Dependency packages stay in the ignored `Builds` directory. If a line changes, update the corresponding on-screen caption as well.
