# Rear Fisheye Risk Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add configurable rear fisheye risk prediction so `CAM_BACK` uses front-camera-style corridor and trend logic while limiting edge detections to warning-level risk.

**Architecture:** Extend camera configuration with a `CameraRiskOptions` value object parsed from `config.toml`. Pass enabled camera risk options into `SafetyDecisionEngine`, and let the rear camera score each detection with fisheye angle mapping, center danger gating, edge warning gating, and the existing 10-second trend window.

**Tech Stack:** C#/.NET 10, Tomlyn config parsing, xUnit.

---

### Task 1: Parse Camera Risk Options

**Files:**
- Modify: `src/utils/RideManagerOptions.cs`
- Modify: `src/utils/ConfigLoader.cs`
- Test: `tests/CameraPipelineFactoryTests.cs`
- Modify: `config.toml`

- [ ] **Step 1: Write failing config parsing test**

Add a test that writes a `CAM_BACK` TOML camera with:

```toml
fisheye_fov_degrees = 180
fisheye_strength = 0.75
rear_center_danger_angle_degrees = 50
rear_edge_warning_min_score = 0.22
```

Assert these values are available on `camera.Risk`.

- [ ] **Step 2: Run test and verify RED**

Run:

```bash
dotnet test --filter CameraPipelineFactoryTests
```

Expected: compile failure or test failure because `CameraOptions.Risk` does not exist.

- [ ] **Step 3: Implement minimal config model**

Add `CameraRiskOptions` to `RideManagerOptions.cs`, add TOML properties to `ConfigLoader.CameraToml`, clamp values, and set defaults. Use defaults: front non-fisheye 90 degrees, rear fisheye 180 degrees, fisheye strength 1.0, center danger angle 45 degrees, edge warning min score 0.18.

- [ ] **Step 4: Run targeted test and verify GREEN**

Run:

```bash
dotnet test --filter CameraPipelineFactoryTests
```

Expected: tests pass.

### Task 2: Wire Risk Options Into Decisions

**Files:**
- Modify: `src/core/SafetyDecisionEngine.cs`
- Modify: `Program.cs`
- Modify: `src/camera/CameraLiveTester.cs`
- Test: `tests/SafetyDecisionEngineTests.cs`

- [ ] **Step 1: Write failing rear-center danger test**

Construct `SafetyDecisionEngine` with a `CAM_BACK` risk map using `fisheye_fov_degrees = 180`, `fisheye_strength = 1.0`, `rear_center_danger_angle_degrees = 45`. Feed three centered, growing `motorcycle` boxes inside 10 seconds. Assert the rear assessment becomes `Danger`.

- [ ] **Step 2: Write failing rear-edge warning test**

Construct the same engine and feed a large high-confidence `car` at the far edge. Assert overall risk is `Warning`, not `Danger`.

- [ ] **Step 3: Run tests and verify RED**

Run:

```bash
dotnet test --filter SafetyDecisionEngineTests
```

Expected: tests fail because rear scoring still uses area-only logic and has no configurable risk map.

- [ ] **Step 4: Implement minimal engine changes**

Add optional `IReadOnlyDictionary<CameraId, CameraRiskOptions>` to `SafetyDecisionEngine`. Replace rear scoring with fisheye angle scoring:

- Map `centerX` to `absoluteAngle = abs(centerX - 0.5) * fovDegrees`.
- `centerWeight` is strong inside half of `rear_center_danger_angle_degrees`.
- `bottomGate`, area proximity, and trend thresholds reuse the front camera shape.
- Edge samples are capped below danger threshold but can pass warning threshold when above `rear_edge_warning_min_score`.

- [ ] **Step 5: Wire runtime construction**

Pass `options.Cameras.ToDictionary(camera => camera.Id, camera => camera.Risk)` into `SafetyDecisionEngine` in `Program.cs` and `CameraLiveTester`.

- [ ] **Step 6: Run targeted test and verify GREEN**

Run:

```bash
dotnet test --filter SafetyDecisionEngineTests
```

Expected: tests pass.

### Task 3: Full Verification

**Files:**
- All touched source and tests.

- [ ] **Step 1: Run complete suite**

Run:

```bash
dotnet test
```

Expected: all tests pass.

- [ ] **Step 2: Review diff**

Run:

```bash
git diff -- src tests config.toml docs/superpowers/plans/2026-06-21-rear-fisheye-risk.md
```

Expected: only rear fisheye risk configuration and algorithm changes are present.
