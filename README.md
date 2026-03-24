# Jellyfin Stream Generator Plugin

This plugin for Jellyfin adds a "Generate Stream URL" option to the context menu of video items.  
It allows to generate urls with custom parameters and separate tokens.

## Why?

Sometimes you need to play video from a Jellyfin in a dumb player (like in games) and this is becoming a pain to do.  

Jellyfin has the "Copy Stream URL" option, but it doesn't allow for customizing. 
More importantly, it just exposes your token, so everyone who has a link can see it and use it.

This library provides the ability to generate custom HLS stream URLs with separate tokens, allowing for more control and security.

Inspired by [vrchat-jellyfin](https://github.com/orcachillin/vrchat-jellyfin) but implemented as a Jellyfin plugin.

## Installation Requirements
1. **Jellyfin File Transformation Plugin**: This plugin relies heavily on [jellyfin-plugin-file-transformation](https://github.com/IAmParadox27/jellyfin-plugin-file-transformation). You must install it on your server first.
2. Download or compile the `Jellyfin.Plugin.StreamGenerator.dll`.
3. Place the `.dll` into a new folder inside your Jellyfin server's `plugins` directory. e.g., `<Jellyfin Data Folder>/plugins/StreamGenerator/Jellyfin.Plugin.StreamGenerator.dll`.
4. Restart your Jellyfin Server.

## Features
- Adds a new button next to "Copy Stream URL" for a video item  
  <img width="239" height="128" alt="image" src="https://github.com/user-attachments/assets/229c4263-4050-45ba-be9f-4decf36497ac" />
- Provides a GUI popup with selectable options:
  - Video Codec
  - Audio Codec
  - Audio Stream track selection
  - Subtitle Stream track selection
  - Subtitle burning method (HLS, Encode, Embed)
  - Bitrate selection
  - Copy Timestamps  
  <img width="464" height="671" alt="image" src="https://github.com/user-attachments/assets/22661870-cbf6-4f8a-9ad9-189d3470529e" />
- Generates a `master.m3u8` playlist URL mimicking an API call
- Generates a unique token for each generated URL, without exposing your Jellyfin token  
  <img width="616" height="659" alt="image" src="https://github.com/user-attachments/assets/36785259-834f-4f92-94c0-d77eb898a777" />
## Building from source
```bash
dotnet build
```
The compiled library will be available at `bin/Debug/net9.0/Jellyfin.Plugin.StreamGenerator.dll`
