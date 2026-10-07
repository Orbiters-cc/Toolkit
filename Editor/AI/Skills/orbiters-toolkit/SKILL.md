---
name: orbiters-toolkit
description: Capture and visually inspect any Unity editor window (custom tool windows, inspectors, a component section far down an Inspector, inactive docked tabs, windows that are not open) through Orbiters Toolkit and MCP for Unity, always in the background — never focusing Unity, never asking the user to open or scroll anything, never touching the desktop.
---

# Unity editor window capture

This is the only way to look at Unity editor UI when the MCP for Unity bridge works. Scene-camera screenshots do not
show editor controls. Never ask the user to open, focus, dock, scroll or select a window for a screenshot, and never
use desktop screenshots, window-capture APIs or mouse/keyboard input on Unity to look at it: everything below runs in
the background and leaves the user's layout, tabs, scroll positions, selection and focus untouched.

The tool is `orbiters_editor_window` (package `orbiters.toolkit`, `Editor/MCP/EditorWindowScreenshotTool.cs`). Call it
with `execute_custom_tool` (`tool_name: "orbiters_editor_window"`, `parameters: {...}`), especially when the typed
`mcp__unityMCP__orbiters_editor_window` tool does not list a parameter yet (the typed schema is registered when the
MCP server starts and can lag behind the package).

1. If several Unity instances are connected, read `mcpforunity://instances` and select the project.
2. `{"action":"list"}` lists windows with `windowId`, exact `windowType`, and `shown` (false = inactive docked tab;
   it can still be captured).
3. Capture:

   | What | Parameters |
   |---|---|
   | A shown window as it is | `{"action":"capture","window_id":12345}` |
   | An inactive docked tab | same; it is rendered in a hidden copy automatically |
   | A window that is not open | `{"action":"capture","window_type":"Orbiters.MyAvatar.Editor.MyAvatarAboutWindow","open_if_missing":true}` |
   | A long window, whole | add `"width":560,"height":2400` (points, up to 8000) |
   | A section far down | add `"scroll_to":"Face tracking"` (text shown, element name, or USS class) |
   | Any object's Inspector, without selecting it | `{"action":"capture","inspect":"Rexouium1.6 Default Setup","height":1400,"scroll_to":"Face tracking"}` |

   `inspect` takes an instance ID, a hierarchy path (`"Root/Child"`, or `"Scene:Root/Child"`), or an asset path
   (`"Assets/…"`, `"Packages/…"`). Hidden copies (`captureMode: "hidden_copy_framebuffer"`) are shown without focus
   outside every display, keep the original window's serialized state, and are closed after the capture.
   `max_resolution` (64–8192, default 0 = native) caps the longest edge.
4. The server normally polls to completion. If it returns a pending job, call again with `{"action":"status"}`
   (and its `job_id`). A domain reload clears jobs: retry once Unity is ready (not compiling or importing).
5. Open the returned `fullPath` PNG (in `Library/OrbitersToolkit/Screenshots/`) with an image-viewing tool and look
   at it: expected window, readable UI, colors, spacing, framing. A file being written proves nothing about the UI.

If a capture fails, report the error and fix the tool (it is Orbiters' own code) rather than working around it. The
capture shows window content, not native title bars or separate OS popups.

The only desktop interaction allowed in this project is reconnecting a dropped MCP session (Start Session in the "MCP
For Unity" window), as described in the project's AGENTS.md.
