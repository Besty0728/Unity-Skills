# Third-Party Font Notice

This folder bundles a **subset** of a third-party open-source font, used by the
UnitySkills editor window to render CJK (Chinese) text reliably on macOS, where
the Unity editor's default dynamic font atlas drops some CJK glyphs.

## UnitySkillsCN-Regular.ttf

- **Derived from:** Maple Mono (CN variant, v7.9) — https://github.com/subframe7536/maple-font
- **Original copyright:** Copyright 2022 The Maple Mono Project Authors
- **License:** SIL Open Font License, Version 1.1 (see `OFL.txt` in this folder)
- **CN glyph base:** Maple Mono CN derives its Han glyphs from
  [Resource Han Rounded](https://github.com/CyanoHao/Resource-Han-Rounded)
  (also SIL OFL 1.1).

### Modifications made (this is a "Modified Version" under the OFL)

1. **Subsetted** to a compact glyph set — ASCII/Latin, common punctuation and UI
   symbols, and the GB2312 Han set unioned with every CJK character used by the
   UnitySkills UI strings — to keep the bundled file small (~5 MB instead of ~18 MB).
2. **OpenType layout features dropped** (coding ligatures removed) — undesirable in
   UI labels.
3. **Renamed** the internal font family to `UnitySkills CJK` so it cannot be confused
   with, or collide against, a user-installed copy of Maple Mono. Maple Mono declares
   **no Reserved Font Name**, so this rename is permitted by OFL clause 3.

### OFL compliance summary

- The full license text and the original copyright notice ship alongside the font
  in this folder (`OFL.txt`) — satisfies OFL clause 2.
- The font (this modified/subset copy and any FontAsset derived from it at runtime)
  remains licensed under the **SIL OFL 1.1 only** — it is *not* relicensed under the
  UnitySkills project license. The rest of the UnitySkills package keeps its own
  license; only the files in this `Fonts/` folder are OFL.
- The font is never sold by itself; it is only bundled with UnitySkills.
