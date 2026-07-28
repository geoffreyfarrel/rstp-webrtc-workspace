# RTSP to WebRTC Workspace

A full-stack monorepo built as a learning sandbox for exploring real-time video streaming protocols. It serves as a bridge that ingests standard RTSP streams (typically from IP cameras) and translates them into WebRTC for sub-second latency playback in the browser.

## 🏗️ Architecture & Tech Stack

- **Backend**: C# / ASP.NET Core
  - Ingests RTSP feeds and handles WebRTC signaling.
  - Core libraries: `SIPSorcery`, `SIPSorceryMedia.FFmpeg`.
- **Frontend**: Vue 3 (Composition API / Vite)
  - Renders the video feed natively using browser WebRTC APIs.
  - Core libraries: `@eyevinn/webrtc-player`.
- **Structure**: Monorepo managed via Git.

## 📂 Folder Structure

```text
rtsp-webrtc-workspace/
├── .git/                    # Single Git repository for the entire workspace
├── backend/                 # ASP.NET Core Web API project
├── frontend/                # Vue 3 project initialized via Vite
├── .gitignore               # Monorepo ignore rules (Node, VS, VS Code, .NET)
└── RtspWebRtcWorkspace.sln  # Visual Studio Solution linking the backend
```

## 🚀 Prerequisites

Before you begin, ensure you have the following installed:

- [.NET SDK](https://dotnet.microsoft.com/download) (v8.0+ recommended)
- [Node.js](https://nodejs.org/) (v18+ recommended)
- [FFmpeg](https://ffmpeg.org/download.html) (Shared binaries required by SIPSorcery for media processing)
- Visual Studio (Optimized for the C# backend)
- Visual Studio Code (Optimized for the Vue frontend)

## 💻 Getting Started

### 1. Backend (C#)

1. Open the `RtspWebRtcWorkspace.sln` file at the root of the repository using **Visual Studio**.
2. Visual Studio will automatically restore required NuGet packages.
3. Press `F5` (or click the green **Start** button) to build and launch the API server with the debugger attached.

### 2. Frontend (Vue)

1. Open the workspace root or the `frontend` folder directly in **Visual Studio Code**.
2. Open the integrated terminal (`Ctrl` + `` ` ``) and navigate to the frontend directory:
   ```bash
   cd frontend
   ```
3. Install the NPM dependencies:
   ```bash
   npm install
   ```
4. Start the Vite development server:
   ```bash
   npm run dev
   ```

## ⚙️ Development Notes

- **CORS Configuration**: The C# backend is configured to accept cross-origin requests from the Vue development server (defaulting to `http://localhost:5173`). If Vite assigns a different port, update the CORS policy in `backend/Program.cs`.
- **RTSP Source**: You will need to provide a valid RTSP URL (e.g., from a local IP camera or test stream) in the backend configuration to test the transcoding and streaming pipeline.

---

_Developed by Geoffrey Farrel._
