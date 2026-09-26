---
name: orbiters-toolkit
description: Capture and visually inspect Unity editor tool windows such as ReFit, inspectors, or custom EditorWindows through Orbiters Toolkit and MCP for Unity, keeping Unity in the background.
---

# Unity editor window capture

Use this for actual editor UI screenshots. Scene-camera screenshots do not show
custom editor controls. The Unity project needs `orbiters.toolkit` and a connected
MCP for Unity server.

1. Read `mcpforunity://instances` and select the intended project if several Unity
   instances are connected. Read `mcpforunity://custom-tools` and find
   `orbiters_editor_window`. If missing, check installation/compilation and tool
   discovery; do not substitute a camera screenshot.
2. Call `execute_custom_tool` with `tool_name: "orbiters_editor_window"` and
   `parameters: {"action":"list"}` to identify the window's ID and exact type.
3. Capture by ID, for example:

   ```json
   {
     "tool_name": "orbiters_editor_window",
     "parameters": { "action": "capture", "window_id": 12345, "focus": false }
   }
   ```

   Replace the example ID with the listed ID. An exact `window_type` can replace
   `window_id` when only one instance is open. ReFit uses
   `Orbiters.ReFit.Editor.ReFitWizard`. `max_resolution` defaults to 0 (native
   pixels); 64–4096 caps the longest edge.
4. The server normally polls to completion. If it returns a pending job, call the
   same tool with `action: "status"` and its `job_id`. A domain reload clears jobs;
   retry the capture after Unity is ready.
5. Open the returned `fullPath` with an available local image-viewing tool and
   inspect the actual PNG. Confirm the expected window, readable UI, colors and
   framing. A successful file write alone does not establish visual correctness.

Keep `focus: false`. Background capture works for an already open window that is
the selected tab within its dock, even while another application is in front.
An inactive docked tab produces an error. Do not automatically retry with
`focus: true`: it activates Unity and interrupts the user. Only activate or open
windows when the user authorizes that interaction. Creating a missing window
requires both `open_if_missing: true` and `focus: true`; menu-specific setup may
still be required by that window.

PNGs are in `Library/OrbitersToolkit/Screenshots/`. The response identifies the
project, Unity version, window, scale and dimensions. This captures window content,
not desktop pixels, native title bars or separate popups. Headless/minimized
rendering is not guaranteed. Report capture failures rather than presenting an old
image as fresh evidence.
