# Third-Party Notices and Open Source Licenses

This project distributes third-party open source software components under their respective licenses. This document lists the components, their licenses, and the corresponding legal obligations.

---

## 1. Component License Inventory

| Component | Version | Declared License (SPDX) | Copyright / Upstream Source |
| :--- | :--- | :--- | :--- |
| **.NET Runtime** | 10.0.12 (Host/CoreCLR) | `MIT` | © Microsoft Corporation. https://github.com/dotnet/runtime |
| **Avalonia UI** | 11.3.13 | `MIT` | © AvaloniaUI OÜ. https://github.com/AvaloniaUI/Avalonia |
| **Avalonia.Themes.Fluent** | 11.3.13 | `MIT` | © AvaloniaUI OÜ. https://github.com/AvaloniaUI/Avalonia |
| **LibVLCSharp** | 3.10.1 | `LGPL-2.1-or-later` | © VideoLAN & LibVLCSharp Authors. https://code.videolan.org/videolan/LibVLCSharp |
| **LibVLCSharp.Avalonia** | 3.10.1 | `LGPL-2.1-or-later` | © VideoLAN & LibVLCSharp Authors. https://code.videolan.org/videolan/LibVLCSharp |
| **VideoLAN.LibVLC.Windows** | 3.0.24 | `LGPL-2.1-or-later` / `GPL-2.0+` | © VideoLAN. https://code.videolan.org/videolan/vlc |
| **SkiaSharp** | 2.88.9 | `MIT` | © Microsoft Corporation. https://github.com/mono/SkiaSharp |
| **HarfBuzzSharp** | 8.3.1.1 | `MIT` | © Microsoft Corporation. https://github.com/mono/SkiaSharp |
| **Newtonsoft.Json** | 13.0.3 | `MIT` | © James Newton-King. https://github.com/JamesNK/Newtonsoft.Json |
| **MicroCom.Runtime** | 0.11.0 | `MIT` | © AvaloniaUI OÜ. https://github.com/AvaloniaUI/MicroCom |
| **Tmds.DBus.Protocol** | 0.21.2 | `MIT` | © Tom Deseyn. https://github.com/tmds/Tmds.DBus |

---

## 2. LGPL-2.1 Dynamic Linking & Relinking Obligations (Section 6)

In compliance with Section 6 of the GNU Lesser General Public License version 2.1:
1. **Dynamic Linking**: All LibVLC and LibVLCSharp binaries (`libvlc.dll`, `libvlccore.dll`, plugins tree, and `LibVLCSharp.dll`) are linked dynamically at runtime and distributed as separate files. They are NOT statically embedded into a single monolith.
2. **User Relinking & Modification**:
   - End-users have the legal right to inspect, debug, and replace `libvlc.dll`, `libvlccore.dll`, or any VLC plugin DLL with a modified or compatible version of their choice.
   - To replace the native LibVLC runtime in this distribution:
     1. Locate the `libvlc/{rid}/` directory inside the application directory (e.g., `libvlc/win-x64/` or `libvlc/win-arm64/`).
     2. Replace `libvlc.dll`, `libvlccore.dll`, and/or files in the `plugins/` subfolder with your binary-compatible build.
     3. Restart the application.

---

## 3. Source Availability & Written Offer Notice (Pending Compliance Plan)

- **Upstream Repositories**:
  - VLC / LibVLC: `https://code.videolan.org/videolan/vlc`
  - LibVLCSharp: `https://code.videolan.org/videolan/LibVLCSharp`
  - Avalonia: `https://github.com/AvaloniaUI/Avalonia`
- **Source Offer Scope & Retention Notice**:
  - LGPL-2.1 and GPL require that redistributors of binary copies make the corresponding complete machine-readable source code available, including exact matching dependencies and build configurations.
  - Simply linking to upstream project URLs does **not** automatically fulfill full binary-matching source availability obligations.
  - For current pre-release / testing candidate deliveries, the formal binary-matching source archive and written offer workflow is designated as **Pending Plan (保留待合规计划确认)**. Prior to any general public release, an exact source bundle matching the build artifacts or a binding written offer must be formalized.
  - Internal or private testing usage is acknowledged as a development phase, but does not alter the underlying licensing terms.

---

## 4. Proprietary Asset Disclaimer

- This application does NOT copy, bundle, or distribute any proprietary icons, logos, trademarks, artwork, or cloud subscription services from macOS APTV (by KimenPro).
- All visual assets, layout designs, and application styling are independently developed or derived from open source components (Avalonia Fluent Icons).
