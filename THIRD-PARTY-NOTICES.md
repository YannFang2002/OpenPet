# Third-party notices

OpenPet is an independent hobby project. It contains code and assets adapted from
two MIT-licensed projects.

## OpenTRCC

- Source: https://github.com/YannFang2002/openTRCC
- License: MIT — see [LICENSES/OpenTRCC-MIT.txt](LICENSES/OpenTRCC-MIT.txt)
- Adapted files:
  - `src/OpenPet.Devices/` — HID transport, device enumeration/catalog/profile, Thermalright Type-2 handshake and frame writes (`HidTransport.cs`, `ThermalrightType2.cs`, `DeviceEnumerator.cs`, `DeviceCatalog.cs`, `DeviceProfile.cs`, `Protocol/Handshake.cs`, `Interop/*`).
  - `src/OpenPet.Core/` — RGB565 encoder, frame assembler/chunker, BGRA image, rotation/flip, frame keep-alive pump (`Rgb565Encoder.cs`, `FrameAssembler.cs`, `FrameChunker.cs`, `FrameEncoding.cs`, `BgraImage.cs`, `ImageTransforms.cs`, `FramePump.cs`).
- Changes: namespaces renamed to `OpenPet.*`; the pet renderer, config, engine and UI are new.

## dsh-pet

- Source: https://github.com/PC2005-cloud/dsh-pet
- License: MIT — see [LICENSES/dsh-pet-MIT.txt](LICENSES/dsh-pet-MIT.txt)
- Used assets:
  - `assets/animations/` — all 106 clips converted from `dsh-pet/assets/webm/*.webm` (VP9 + alpha).
  - `assets/memes/` — the 27 sticker PNGs from `dsh-pet/assets/memes/`.
  - `assets/fonts/上首软糖体.ttf` — the "candy" font bundled by dsh-pet.
- Referenced design:
  - `assets/pet.jsonc` follows dsh-pet's `config.jsonc` for `pets`, `animations`, `animationWeights`, `physics`, `eventsRefreshSec`, `font`.
  - Random-chain weights, category `noMirror`, move distance scaling and event pools follow dsh-pet's semantics.
- Changes: all 106 animations are re-encoded to 320×180 alpha WebP frame sequences at 12 fps for the 320×240 panel;
  the 27 memes are shown as small sticker cards on screen. The config and defaults are adapted for a standalone app.

## SkiaSharp

- NuGet: `SkiaSharp` / `SkiaSharp.NativeAssets.Win32` 2.88.9
- License: MIT
- Used for all 320×240 scene rendering, text, shapes, and PNG export.

## Font note

`上首软糖体.ttf` is redistributed inside the dsh-pet asset bundle. If you plan to
publish a commercial build, verify the font's own license terms first, or switch
`font.family` to `yahei` / `system` in `assets/pet.jsonc`.
