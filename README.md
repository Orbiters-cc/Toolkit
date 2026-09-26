# Orbiters Toolkit

Unity 2022.3 editor utilities for mirror posing and optional AI integration.
Mirror posing works without MCP. The screenshot adapter compiles separately when
MCP for Unity 9.7.1 or newer is installed; this package does not install the server.

## Mirror posing

In XRay Gizmos, select an avatar or one of its bones and enable **Mirror** in the
Scene-view toolbar or the XRay Gizmos window. Rotate or move one paired bone in
the Scene view or Inspector; Toolkit mirrors the edited channels to its partner
across the avatar root's **local X** plane. The mirror remains scoped to that rig
when selection changes. Toggle it off and on with another avatar selected to switch.
The window reports the active rig, pair count and selected partner.

Humanoid bone mappings take priority. Generic rigs use matching hierarchy paths
with `Left`/`Right`, `left`/`right`, `LEFT`/`RIGHT`, or `.L`/`.R`, `_L`/`_R`,
`-L`/`-R`, and space-separated side markers (including lowercase markers).
Markers can precede or follow the bone name; namespaced humanoid names also work.
Ambiguous paths, unpaired bones and center bones are skipped.

The mesh bind pose supplies the reference frames, accounting for different local
bone axes. Pairs missing complete bind-pose data use the pose at enable time as a
relative reference, shown in the status. Enabling Mirror never changes the pose.
Editing one side subsequently replaces the opposite side's edited channels.
If both partners are edited in the same operation, both explicit edits are kept.
Mirrored changes join the source Undo operation and record prefab overrides.

Mirror is an edit-mode tool, pauses during animation preview/recording, and turns
off on play-mode changes, script reload or changes to the captured bone hierarchy.
It does not mirror scale, solve IK, bake animation or continuously drive bones.
Bone and ancestor scales must be positive and uniform. Turn Mirror off before
changing reference scale or rig structure, then enable it again.

Other editor tools can call `Orbiters.Toolkit.Editor.Posing.MirrorPoseService`
(`Enable(root, renderer)`, `Disable()`, `GetPartner(bone)`, `Changed`).
Its assembly is `Orbiters.Toolkit.Editor`; it has no XRayGizmos or MCP dependency.

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
