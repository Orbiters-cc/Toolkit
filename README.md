# Orbiters Toolkit

Unity editor utilities exposed through MCP for Unity. Requires a graphical Unity
2022.3 editor and MCP for Unity 9.7.1 installed in the project (including its
`MCPForUnity.Editor` assembly). Install MCP for Unity through its documented Git
URL first; this package does not install or fork the MCP server.

## Install AI integration

Open **Tools > Orbiters > Toolkit**, select **Codex** and/or **Claude Code**, and
click **Install / update AI integration**. This adds the bundled screenshot skill
to this Unity project:

- Codex: `.agents/skills/orbiters-toolkit/SKILL.md`
- Claude Code: `.claude/skills/orbiters-toolkit/SKILL.md`

The window reports installation status for each client. It does not configure MCP
connections or personal settings. Start a new chat or reload skills if the client
has not discovered the new instructions. Updates replace only toolkit-installed,
unedited copies; custom edits are preserved and reported. A small ownership receipt
is stored beside each installed skill. Existing identical skills need no rewrite.

## Releases

Repository: https://github.com/Orbiters-cc/Toolkit

**Build Release** uses the same workflow as ReFit. It packages the editor tools and
bundled AI skill, validates the archive, and publishes the ZIP and `package.json`.
The repository variable `PACKAGE_NAME` is `Toolkit`. Push a new unused
`package.json` version to `master` for an automatic release, or run the workflow
manually. The first release is `0.1.0`.

## Editor window screenshots

Call `execute_custom_tool` with `tool_name: "orbiters_editor_window"`.

List open windows:

```json
{ "action": "list" }
```

Capture the existing ReFit window:

```json
{
  "action": "capture",
  "window_type": "Orbiters.ReFit.Editor.ReFitWizard",
  "max_resolution": 0
}
```

Use `window_id` from the list instead of `window_type` to distinguish multiple
instances. By default the tool repaints the window while Unity stays in the
background. It yields to Unity's event loop before reading the window framebuffer.
It does not click any
controls, run ReFit, change the scene, or capture pixels from the desktop.

Set `open_if_missing: true` and `focus: true` with an exact `window_type` to allow
creation of a window. Opening can activate Unity, so background mode requires an
already open window. Opening calls Unity's `EditorWindow.GetWindow`, not package-specific menu
initialization. For windows requiring a menu setup routine, run that menu first.
`focus` defaults to false. The target must be the selected tab in its own dock,
but neither Unity nor the target needs keyboard focus. Inactive docked tabs return
an error instead of changing the layout or capturing a different tab. Explicit
`focus: true` permits tab selection and bringing Unity forward; it leaves the
target focused. Background mode never restores or switches application focus.

The server polls pending captures automatically. Clients without polling support
can call `{ "action": "status", "job_id": "<returned job_id>" }`. Jobs are
in-memory and expire on domain reload; request a new capture after a reload.
Only one capture runs at a time; up to 16 recent results are retained.

The completed response includes `fullPath`, output/source dimensions, scale,
window identity, Unity version and project path. Open `fullPath` with the client's
image viewer to inspect the PNG. Files use unique names under
`Library/OrbitersToolkit/Screenshots/`; they are not imported as Unity assets and
are removed if the project's Library is cleared. `max_resolution` accepts 0 for
native pixels or 64–4096 for a downscaled image.

## Boundaries

The capture is the window's rendered content, not its operating-system title bar
or other popup windows. It uses Unity's internal `GUIView.GrabPixels` API and
fails explicitly if that API is unavailable. A visible graphical editor is
required; headless, minimized and hidden-window rendering are not guaranteed.
Capture failure, closed windows and inactive docked tabs produce errors. Inspect the
returned image before treating a successful pixel read as visual verification.

After importing this package, allow compilation and MCP tool discovery to finish.
If the tool list is stale, rescan tools in MCP for Unity or reconnect the MCP client.
