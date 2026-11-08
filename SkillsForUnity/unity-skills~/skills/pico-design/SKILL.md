---
name: unity-pico-design
description: Doc-anchored design rules for PICO Unity Integration SDK v3.4.0 — XR/MR/platform services, version diffs 2.x-3.4, hallucination shields
---

# PICO Unity SDK - Design Rules (v3.4.0)

Advisory module, documentation only. Rules come from the **official PICO documentation for v3.4.0** (crawled 2026-08-13), each anchored to a checkable source URL. The SDK is closed source.

Applies to SDK v3.4.0. The doc site also covers 3.3.2 down to 2.5.0, and release notes go back to v2.0.1. Version differences are in [VERSIONS.md](./VERSIONS.md).

**Before writing PICO code, read the sub-doc for that area.** Training-corpus knowledge mostly reflects SDK 2.x and early 3.x, and many of those APIs were deprecated or renamed by 3.4.0.

## When to use

- Writing or reviewing PICO Unity Integration SDK code (`PXR_*` / `Pico.Platform`)
- Setting up a PICO XR project, or choosing the PICO XR plugin versus the Unity OpenXR plugin
- Video see-through, spatial anchors, spatial mesh, SecureMR, or platform services
- Migrating from SDK 2.x or an older 3.x version

## Critical Rule Summary

| # | Rule | Details |
|---|------|---------|
| 1 | The PICO XR plugin and the Unity OpenXR plugin are mutually exclusive. With both enabled, only PICO XR takes effect. On the OpenXR route, another vendor's plugin stops the app from running | [SETUP.md](./SETUP.md) |
| 2 | `PXR_Manager` must be on **every scene**, including loading scenes. Since 3.1.0 only 64-bit (IL2CPP, ARM64) is supported, and the menu is `PICO`, not `PXR_SDK` | [SETUP.md](./SETUP.md) |
| 3 | `PXR_MotionTracking` methods return an **int status code** (0 = success), not a bool. `SetFoveationLevel` takes two parameters, and `None = -1` | [API_REFERENCE.md](./API_REFERENCE.md) |
| 4 | The legacy haptics group (`SetControllerVibration`, `StartVibrateBy*`, `CreateHapticStream`, etc.) was deprecated in 3.2.0. Use `SendHapticImpulse` | [VERSIONS.md](./VERSIONS.md) |
| 5 | Call `StartSenseDataProvider` before any MR feature. Anchor handles die on restart, so store only the **UUID**. To delete an anchor, UnPersist it before Destroy | [MR.md](./MR.md) |
| 6 | Video see-through needs: no post-processing, no HDR under Vulkan, Clear Flags = Solid Color, background RGBA all 0. `EnableVideoSeeThrough` is delayed and must be called again after resume | [MR.md](./MR.md) |
| 7 | Controllers and bare hands **cannot be tracked at the same time**. Full-body motion capture and body trackers are mutually exclusive | [INTERACTION.md](./INTERACTION.md) |
| 8 | Check `CoreService.AsyncInitialize` at two levels (`IsError`, then `Success` or `AlreadyInitialized`). Rooms, matchmaking, leaderboards and achievements also need `GameInitialize` | [PLATFORM.md](./PLATFORM.md) |
| 9 | Face tracking is an **official documentation contradiction** in v3.4.0. Its APIs are Deprecated, and no replacement class exists in the corpus. Use it if needed, state its status, and **do not invent new APIs** | [PITFALLS.md](./PITFALLS.md) |
| 10 | Rendering conflicts: AppSW x BufferDiscards = tearing; AppSW x content protection = ghosting; LateLatching x composition layers = judder; super resolution x sharpening on one buffer: only super resolution applies | [RENDERING.md](./RENDERING.md) |

## Sub-doc Routing

| Sub-doc | Read when |
|---------|-----------|
| [SETUP.md](./SETUP.md) | Environment setup, plugin route, project settings, AndroidManifest, Project Validation, Building Blocks, AR Foundation |
| [RENDERING.md](./RENDERING.md) | Foveated rendering, super resolution, composition layers, multiview, adaptive resolution, AppSW, Late Latching, URP, spatial audio |
| [INTERACTION.md](./INTERACTION.md) | Controller input, haptics, gesture, eye, face and body tracking, body trackers, system keyboard |
| [MR.md](./MR.md) | Video see-through, spatial anchors, spatial mesh, scene calibration, plane detection, MR safety, 2.5 to 3.x MR migration |
| [SECUREMR.md](./SECUREMR.md) | SecureMR framework (3.2.0+): Tensor, Operator, Pipeline, glTF rendering, Dynamic Texture, Readback, JavaScript Operator |
| [PLATFORM.md](./PLATFORM.md) | Platform services: initialization, accounts, achievements, leaderboards, IAP, DLC, cloud save, rooms, RTC, enterprise overview |
| [API_REFERENCE.md](./API_REFERENCE.md) | Signature tables for the 9 core XR classes (with Deprecated flags), the `PXR_Enterprise` method list, Legacy API disposition |
| [VERSIONS.md](./VERSIONS.md) | Version timeline, method-level added/removed/deprecated tables, 2.x to 3.x migration. **The single source of truth for deprecations** |
| [PITFALLS.md](./PITFALLS.md) | Hallucination guard: deprecated API blacklist, signature traps, conflict matrix, build pitfalls, documentation contradictions, capability limits |

Other modules: general XR Interaction Toolkit work is in [xr](../xr/SKILL.md), architecture in [architecture](../architecture/SKILL.md), and hot-path performance in [performance](../performance/SKILL.md). This module covers only PICO-specific parts.

## Version Scope

Anchored to **v3.4.0** (released 2026-02-27). Key watersheds:

- **3.0.0**: MR interfaces reworked (event-style AnchorEntity to `*Async` + SenseDataProvider)
- **3.1.0**: menu `PXR_SDK` renamed to `PICO`; 64-bit only
- **3.2.0**: largest deprecation wave (PXR_Input and PXR_MotionTracking members, face-tracking group); SecureMR introduced
- **3.3.0**: `PXR_Over Lay` renamed to `PXR_Composition Layer`; some APIs moved into PXR_Enterprise
- **3.4.0**: plane detection, `PXR_CameraImage`, SecureMR JavaScript Operator

To refresh the corpus after a new SDK release, rerun the crawl scripts under `temp/pico-crawl/`.

## Usage Rules

1. If an API is not in the corpus, say it is "not documented in the official docs". Do not fill it in from memory.
2. The official docs contradict themselves in places. See [PITFALLS.md](./PITFALLS.md) section 5.
3. For version migration, check the added/removed tables in [VERSIONS.md](./VERSIONS.md) first.

## Pitfalls

Start with the deprecated API blacklist in [PITFALLS.md](./PITFALLS.md).
