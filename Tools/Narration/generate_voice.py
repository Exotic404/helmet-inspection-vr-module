"""Generate the original Module 1 voice clips with Microsoft's Jenny Neural voice."""

import asyncio
from pathlib import Path
import sys

ROOT = Path(__file__).resolve().parents[2]
LOCAL_PACKAGES = ROOT / "Builds" / "voice-tools" / "packages"
if LOCAL_PACKAGES.is_dir():
    sys.path.insert(0, str(LOCAL_PACKAGES))

import edge_tts
import miniaudio

VOICE = "en-US-JennyNeural"
OUTPUT = ROOT / "Assets" / "HelmetInspection" / "Resources" / "Module1Voice"
LINES = {
    "welcome": "Welcome. Compare the reference helmet, A one, with the scan, A two.",
    "begin": "Point at the green start button and squeeze the grip to begin.",
    "scanner": "Use the scanner on your left wrist, or pick it up with either hand.",
    "scan": "Bring the glowing tip close to a defect on A two, and press the trigger.",
    "target": "Find ten defects. Rotate the helmet to check every side.",
    "logged": "Defect logged.",
    "complete": "Inspection complete. A2 was put on hold.",
    "restart": "Press the red restart button to try again.",
    "reset": "Session reset.",
}


async def main():
    OUTPUT.mkdir(parents=True, exist_ok=True)
    for name, text in LINES.items():
        destination = OUTPUT / f"{name}.mp3"
        temporary = destination.with_suffix(".generating.mp3")
        try:
            for attempt in range(3):
                try:
                    await edge_tts.Communicate(
                        text, VOICE, rate="+0%", pitch="+0Hz", volume="+0%"
                    ).save(str(temporary))
                    decoded = miniaudio.mp3_read_file_s16(str(temporary))
                    seconds = decoded.num_frames / decoded.sample_rate
                    if seconds < 0.25 or not any(decoded.samples):
                        raise ValueError(f"Empty or silent clip: {name}")
                    temporary.replace(destination)
                    print(f"{destination.name}: {seconds:.2f}s; {destination.stat().st_size} bytes")
                    break
                except Exception:
                    if attempt == 2:
                        raise
                    await asyncio.sleep(1 + attempt)
        finally:
            temporary.unlink(missing_ok=True)


if __name__ == "__main__":
    asyncio.run(main())
