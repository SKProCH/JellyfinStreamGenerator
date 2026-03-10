# Jellyfin Stream Generator Plugin

This plugin for Jellyfin adds a "Generate Stream URL" option to the context menu of video items. It uses `jellyfin-plugin-file-transformation` to inject a custom popup where users can select video and audio codecs, streams, and subtitle options to generate a custom HLS stream URL.

## Installation Requirements
1. **Jellyfin File Transformation Plugin**: This plugin relies heavily on [jellyfin-plugin-file-transformation](https://github.com/IAmParadox27/jellyfin-plugin-file-transformation). You must install it on your server first.
2. Download or compile the `Jellyfin.Plugin.StreamGenerator.dll`.
3. Place the `.dll` into a new folder inside your Jellyfin server's `plugins` directory. e.g., `<Jellyfin Data Folder>/plugins/StreamGenerator/Jellyfin.Plugin.StreamGenerator.dll`.
4. Restart your Jellyfin Server.

## Features
- Adds a new button next to "Copy Stream URL".
- Provides a GUI popup with selectable options:
  - Video Codec
  - Audio Codec
  - Audio Stream track selection
  - Subtitle Stream track selection
  - Subtitle burning method (HLS, Encode, Embed)
  - Copy Timestamps
- Generates a `master.m3u8` playlist URL mimicking an API call.

## Building from source
```bash
dotnet build
```
The compiled library will be available at `bin/Debug/net9.0/Jellyfin.Plugin.StreamGenerator.dll`
