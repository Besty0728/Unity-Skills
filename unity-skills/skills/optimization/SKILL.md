---
name: unity-optimization
description: "Project optimization utilities. Use when users want to optimize textures, meshes, or improve performance. Triggers: optimize, compression, texture size, mesh compression, performance, LOD, Unity优化, Unity压缩, Unity性能."
---

# Optimization Skills

Optimize project assets (Textures, Models).

## Skills

### `optimize_textures`
Optimize texture settings (maxSize, compression).
**Parameters:**
- `maxTextureSize` (int, optional): Max size (default 2048).
- `enableCrunch` (bool, optional): Enable crunch compression (default true).
- `compressionQuality` (int, optional): Quality 0-100 (default 50).
- `filter` (string, optional): Asset filter.

### `optimize_mesh_compression`
Set mesh compression for 3D models.
**Parameters:**
- `compressionLevel` (string): "Off", "Low", "Medium", "High".
- `filter` (string, optional): Asset filter.
