# Gesture Signature Recognition Design

## Goal

Make VeltoWin's mouse gesture recognition feel as responsive as the current macOS Velto implementation by replacing the Win-only mixed `$1` shape matcher with the macOS-style direction signature matcher, while preserving the existing Windows global hook lifecycle.

## Current Problem

VeltoWin still combines `$1` normalized shape distance, direction compatibility filters, a special simple-direction path, and runner-up gap checks. That stack is conservative: a gesture can be visually correct but fail because a short direction run is filtered differently, a candidate is rejected before shape scoring, or a runner-up is too close under the shape-distance scale.

The adjacent macOS Velto project now uses a simpler runtime signature:

- resample each stroke to 64 points;
- split only at sharp corners;
- reduce each segment to an 8-way net direction;
- compute a signed bow metric for single-segment curved gestures;
- compare command-level canonical signatures with a normalized sequence distance plus bow penalties.

The local VeltoWin configuration also has `GestureTimeoutSeconds = 0.6`, which is much shorter than the macOS default of `3.0` and can make slow or hesitant gestures cancel before matching.

## Design

Keep `WH_MOUSE_LL` and `GestureEngine` as the Windows input lifecycle. Windows still needs the low-level hook because the app must be able to consume right-button down/up during gestures and replay a plain right click when the movement never becomes a gesture. Raw Input is not a replacement for this control path.

Add a focused `GestureDirection` helper in `src/Services` that ports the macOS signature algorithm to C#:

- `Signature` stores `int[] Sequence` and `double Bow`;
- `FromPoints` converts a runtime or template stroke into a signature;
- `Canonical` reduces multiple templates for one command to a majority direction sequence and average bow for that sequence;
- `Distance` compares two signatures, including bow penalties only for single-segment gestures.

Refactor `GestureRecognizer` to cache one canonical signature per command version, rank commands by `GestureDirection.Distance`, and accept the best match when:

- the best distance is at or below `RecognitionThreshold`;
- the runner-up margin is not ambiguous.

Use the macOS threshold scale: default `RecognitionThreshold = 0.34`, ambiguity margin `0.05`.

Update cancellation behavior in `GestureEngine`:

- keep the existing movement threshold, point sampling, and right-click replay behavior;
- replace path/box scribble detection with cumulative turn-angle detection;
- add `ScribbleCancelEnabled` to preferences with a default of `true`;
- change default `GestureTimeoutSeconds` to `3.0`.

Settings and diagnostics should describe the new scale. The old config loader should migrate the old `$1` default threshold to the new signature default while preserving user-edited values that are already in the new slider range.

## Testing

Add a lightweight test project under `tests/Velto.Tests` using MSTest. Tests should cover the pure gesture logic without starting WPF UI or installing hooks:

- straight cardinal gestures match despite slight hand-drawn noise;
- distinct cardinal commands do not cross-match;
- single-segment curved gestures with opposite bow directions are separated;
- canonical signature uses the majority template sequence and last-recorded sample as tie-break;
- ambiguous duplicate commands are rejected at runtime;
- scribble turn accumulation cancels a back-and-forth path without canceling a normal bent gesture.

Final verification:

- `dotnet test tests\Velto.Tests\Velto.Tests.csproj`;
- `dotnet build src\Velto.csproj`;
- `powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1 -NoCopy`;
- stop old `Velto.exe`, overwrite `C:\dev\Velto.exe`, relaunch it, and verify the process is running.

## Scope Boundaries

Do not change the trail overlay rendering, target-window execution, keyboard sending, tray behavior, or unrelated settings UI layout. This pass is limited to recognition, gesture cancellation, preference defaults, diagnostics text, and test coverage for those behaviors.
