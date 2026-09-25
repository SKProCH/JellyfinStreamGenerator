# Jellyfin Stream Generator Plugin

This plugin for Jellyfin adds a "Generate Stream URL" option to the context menu of video items.  
It allows to generate urls with custom parameters and separate tokens.

Find this plugin useful? Consider starring this repository.

## Why?

Sometimes you need to play video from a Jellyfin in a dumb player (like in games) and this is becoming a pain to do.  

Jellyfin has the "Copy Stream URL" option, but it doesn't allow for customizing. 
More importantly, it just exposes your token, so everyone who has a link can see it and use it.

This library provides the ability to generate custom HLS stream URLs with separate tokens, allowing for more control and security.

Inspired by [vrchat-jellyfin](https://github.com/orcachillin/vrchat-jellyfin) but implemented as a Jellyfin plugin.

## Installation

### Requirements

- **Jellyfin 12.0.x**
- **Jellyfin File Transformation Plugin**: This plugin relies on [jellyfin-plugin-file-transformation](https://github.com/IAmParadox27/jellyfin-plugin-file-transformation). You must install it on your server first.

### Via Plugin Repository (recommended)

1. In Jellyfin, go to **Dashboard -> Plugins -> Repositories**
2. Add a new repository manifest URL:
   ```
   https://skproch.github.io/JellyfinStreamGenerator/manifest.json
   ```
3. Go to **Catalog**, find **Stream Generator** and install it
4. Restart your Jellyfin Server

### Manual Installation

1. Download the latest plugin zip from [GitHub Releases](https://github.com/SKProCH/JellyfinStreamGenerator/releases)
2. Extract the zip into a new folder inside your Jellyfin server's `plugins` directory, e.g. `<Jellyfin Data Folder>/plugins/StreamGenerator/`
3. Restart your Jellyfin Server

## My other plugins

[JellyfinReplayGain](https://github.com/SKProCH/JellyfinReplayGain) - measures and adjusts volume on the fly during transcoding without modifying media files

## Versioning

Plugin versions use the format `x.y.z.N`. The last digit (`N`) is the preview build number — `0` indicates a stable release.

## Features
- Adds a new button "Generate Stream URL" for a video item  
  <img width="239" height="128" alt="image" src="https://github.com/user-attachments/assets/229c4263-4050-45ba-be9f-4decf36497ac" />
- Provides a GUI popup with selectable options:
  - Video/audio reencoding for compatibility
  - Video/audio/subtitle stream track selection
  - Bitrate selection
  - Subtitle burning method (HLS, Encode, Embed)
  - Container selection (fMP4/TS)
  - Copy Timestamps
  - Remember watch progress for your account when you watching something via StreamGenerator links
  - etc  
  <img width="1119" height="751" alt="image" src="https://github.com/user-attachments/assets/8c41e555-3768-4385-ad54-06320af9c617" />
- Generates a `master.m3u8` playlist URL mimicking an API call
- Generates a unique token for each generated URL, without exposing your Jellyfin token  
  <img width="616" height="659" alt="image" src="https://github.com/user-attachments/assets/36785259-834f-4f92-94c0-d77eb898a777" />
- Serves the already transcoded video parts if available to avoid unnecessary transcoding for multiple users

## Building from source
```bash
dotnet build
```
The compiled library will be available at `bin/Debug/net10.0/Jellyfin.Plugin.StreamGenerator.dll`
