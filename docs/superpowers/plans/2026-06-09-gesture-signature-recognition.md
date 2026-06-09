# Gesture Signature Recognition Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace VeltoWin's conservative mixed shape matcher with the macOS Velto direction-signature recognizer and update timeout/scribble behavior for a more responsive feel.

**Architecture:** Keep the Windows hook and gesture lifecycle intact. Add a pure `GestureDirection` service for signature generation and distance, refactor `GestureRecognizer` to rank canonical command signatures, and update `GestureEngine` only for cancellation policy.

**Tech Stack:** .NET 8, WPF, MSTest, PowerShell build script.

---

## File Structure

- Create `src/Services/GestureDirection.cs`: pure geometry/signature helper ported from macOS `GestureDirection.swift`.
- Modify `src/Services/GestureRecognizer.cs`: command signature cache and best-match ranking.
- Modify `src/Services/GestureEngine.cs`: cumulative-turn scribble cancellation and diagnostics labels.
- Modify `src/Models/AppPreferences.cs`: default threshold, timeout, and `ScribbleCancelEnabled`.
- Modify `src/Services/ConfigStore.cs`: preference clone/load migration for the new defaults and boolean preference.
- Modify `src/UI/SettingsWindow.xaml` and `src/UI/SettingsWindow.xaml.cs`: new threshold label/slider scale text and scribble toggle if needed.
- Create `tests/Velto.Tests/Velto.Tests.csproj`: MSTest project referencing `src/Velto.csproj`.
- Create `tests/Velto.Tests/GestureDirectionTests.cs`: pure signature behavior tests.
- Create `tests/Velto.Tests/GestureRecognizerTests.cs`: runtime matching and ambiguity tests.

## Tasks

### Task 1: Test Project Baseline

**Files:**
- Create: `tests/Velto.Tests/Velto.Tests.csproj`
- Create: `tests/Velto.Tests/GestureDirectionTests.cs`

- [ ] Add MSTest project referencing `src/Velto.csproj`.
- [ ] Add one failing test for `GestureDirection.FromPoints`, expecting a mostly horizontal noisy stroke to produce a single right direction.
- [ ] Run `dotnet test tests\Velto.Tests\Velto.Tests.csproj`; expected failure: `GestureDirection` does not exist.

### Task 2: Port GestureDirection

**Files:**
- Create: `src/Services/GestureDirection.cs`
- Modify: `tests/Velto.Tests/GestureDirectionTests.cs`

- [ ] Implement `GestureDirection.Signature`, `FromPoints`, `Canonical`, `Distance`, `DisplayString`, resampling, corner splitting, bow metric, bow penalties, and Levenshtein sequence distance.
- [ ] Add tests for majority canonical selection, tie-break by last matching template, and opposite bow penalties.
- [ ] Run `dotnet test tests\Velto.Tests\Velto.Tests.csproj`; expected pass for `GestureDirectionTests`.

### Task 3: Refactor GestureRecognizer

**Files:**
- Modify: `src/Services/GestureRecognizer.cs`
- Create: `tests/Velto.Tests/GestureRecognizerTests.cs`

- [ ] Write failing tests for straight cardinal matching, ambiguous duplicate rejection, and curved bow separation.
- [ ] Replace shape-vector/template cache with canonical command signatures.
- [ ] Preserve public methods used by `GestureEngine`: `BestMatch`, `DescribeSequence`, `DescribeSimpleDirection`, and `DescribeCandidates`.
- [ ] Run `dotnet test tests\Velto.Tests\Velto.Tests.csproj`; expected pass.

### Task 4: Update Preferences and Migration

**Files:**
- Modify: `src/Models/AppPreferences.cs`
- Modify: `src/Services/ConfigStore.cs`
- Modify: `src/UI/SettingsWindow.xaml`
- Modify: `src/UI/SettingsWindow.xaml.cs`

- [ ] Change defaults to `RecognitionThreshold = 0.34`, `GestureTimeoutSeconds = 3.0`, and `ScribbleCancelEnabled = true`.
- [ ] Ensure JSON cloning and backup import preserve `ScribbleCancelEnabled`.
- [ ] Update threshold comments and UI copy to the signature-distance scale.
- [ ] Run `dotnet test tests\Velto.Tests\Velto.Tests.csproj` and `dotnet build src\Velto.csproj`; expected pass.

### Task 5: Cumulative Turn Scribble Cancellation

**Files:**
- Modify: `src/Services/GestureEngine.cs`
- Create or modify: `tests/Velto.Tests/GestureEngineScribbleTests.cs`

- [ ] Extract a small pure helper if needed so scribble turn accumulation can be tested without installing a hook.
- [ ] Write failing tests for back-and-forth cancellation and normal bent gesture non-cancellation.
- [ ] Replace path/box ratio cancellation with leg-based cumulative angle detection using `10px` leg minimum and `2π` turn threshold.
- [ ] Respect `AppPreferences.ScribbleCancelEnabled`.
- [ ] Run `dotnet test tests\Velto.Tests\Velto.Tests.csproj`; expected pass.

### Task 6: Final Build and Local Handoff

**Files:**
- Verify all touched files.

- [ ] Run `dotnet test tests\Velto.Tests\Velto.Tests.csproj`.
- [ ] Run `dotnet build src\Velto.csproj`.
- [ ] Run `powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1 -NoCopy`.
- [ ] Stop the old `Velto.exe` process if present.
- [ ] Copy `publish\Velto.exe` over `C:\dev\Velto.exe`.
- [ ] Start `C:\dev\Velto.exe` and verify the process is running.

## Self-Review

- Spec coverage: recognition, timeout defaults, scribble cancellation, diagnostics/UI text, tests, and local handoff are covered by Tasks 1-6.
- Placeholder scan: no task depends on an undefined future step.
- Type consistency: the plan consistently uses `GestureDirection.Signature`, `GestureRecognizer.BestMatch`, `AppPreferences.ScribbleCancelEnabled`, and `GestureEngine` cancellation helpers.
