
# PixelStudio CI 🎨🎮

**PixelStudio CI** is a lightweight, cross-platform pixel art editor and animation tool tailored specifically for game developers. Built from the ground up using **Godot Engine** and **C#**, it runs seamlessly both as a high-performance **PC desktop application** and directly in the **Web browser** via WebGL.

Thanks to an automated CI/CD pipeline, every single update is instantly compiled and made accessible to everyone.

🚀 **[Launch Web Version (Live on GitHub Pages)](#)** | 📦 **[Download Desktop Version (Windows/Linux)](#)**

---

## 🔥 Key Features

*   **Multi-Layer Compositing:** Full support for creating, reordering, and toggling layers with real-time blending modes.
*   **Animation Timeline:** Advanced frames management with **Onion Skinning** (previewing adjacent frames) for fluid character animations.
*   **Game-Dev Tailored Tools:**
    *   *Tile Preview Mode:* Real-time grid tiling to easily craft seamless textures (like floor/wall tiles).
    *   *Custom Pixel Crosshair:* Precision-targeted drawing cursors with zero input lag.
*   **Smart Exporting:** Pack your animations into packed **Spritesheets** (PNG) alongside auto-generated JSON sheets compatible with Unity, Godot, and Unreal Engine.
*   **Automated Workflow:** Powered by GitHub Actions to build and deploy web and desktop versions automatically on every commit.

---

## 🛠️ Tech Stack & Architecture

*   **Engine:** Godot Engine (v4.x)
*   **Language:** C# 11 / .NET 8
*   **Target Platforms:** PC (Windows, Linux) & Web (HTML5/WebGL)
*   **CI/CD:** GitHub Actions workflow for automated cross-platform matrix builds.

The codebase follows a strict modular structure, ensuring high performance for raw pixel arrays processing (`Color[]`) on the C# side.

---

## 🚀 How to Run Locally

### Prerequisites
1. Download and install [.NET 8 SDK](https://microsoft.com).
2. Download [Godot Engine (Mono/C# version)](https://godotengine.org).

### Clone & Launch
```bash
# Clone the repository
git clone https://github.com

# Navigate to the project directory
cd PixelStudio-CI

# Run the project via dotnet / godot
godot --path .
```

---

## 📄 License

This project is open-source and available under the **MIT License**. Anyone is free to download, modify, and use it for personal or commercial projects.
