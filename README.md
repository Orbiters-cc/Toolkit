# Orbiters Toolkit

Unity 2022.3 editor utilities for avatar photoshoots, mirror posing and optional AI integration.
Mirror posing works without MCP. The screenshot adapter compiles separately when
MCP for Unity 9.7.1 or newer is installed; this package does not install the server.

## Shared editor services

From 0.2.1, MCB and My Avatar share account storage and Magic Sync through
`AuthenticationService`, environment/API URL handling through
`OrbitersEnvironment`, and authenticated requests through `OrbitersApi`.
The existing per-environment Unity preferences account remains the common store;
disconnecting in either tool disconnects the shared account.

`OrbitersAccountView`, `OrbitersSignInElement` and `OrbitersAccountElement` provide
common account controls. `OrbitersGlow` and `OrbitersGlowSurfaceElement` render
the animated background; `OrbitersVectorLogo` draws absolute SVG M/L/C/Z paths.
These editor-guarded services live in `Orbiters.Toolkit`, allowing components with
editor-only helpers to reference them. Player builds contain none of this editor
logic. `Orbiters.Toolkit.Editor` continues to own posing and screenshot utilities.
The package also depends on Unity's Newtonsoft JSON package.

## Avatar photoshoot

From 0.2.2, `Orbiters.Toolkit.Editor.Photoshoot` holds the photoshoot used by MCB for
custom base asset thumbnails and banners, and by My Avatar for VRChat avatar
thumbnails. A host keeps a `PhotoshootState` (selections, live preview scene, icon
caches) so the photoshoot survives Inspector rebuilds, and adds a `PhotoshootPanel`
built from `PhotoshootOptions`: the avatar, whether a banner is produced, the
thumbnail and banner sizes, and callbacks that receive captured or browsed images.

The panel shows a preview (the banner with the thumbnail over it, or the thumbnail
alone), one row per shot with **Capture**/**Retake** and **Browse**, a framing card and
one style picker with Pose, Light, Background and Expression tabs. A host that presents
the thumbnail in its own context sets `ThumbnailPreview`: the panel then shows no
preview and sends it the thumbnail image, captured or live, whenever it changes.
Changes are rendered on the next editor tick, so drags and quick picks coalesce into
one render.

Framing happens on the preview itself: drag to move the avatar, scroll to zoom,
Shift-drag to turn, double-click to reset. A host showing the thumbnail elsewhere
calls `AttachFraming` on that element (and `DetachFraming` when it stops following
the live preview). The framing card offers **Portrait**, **Half body** and
**Full body**, measured on the posed avatar in any pose: the vertices the head moves
(ears and hair included, down to the chest), those the spine moves (without forearms
and hands, down to the hips), or the whole outline, fitted in height and width with
headroom and kept fitted when the pose or rotation changes. The Turn dial loops all
the way round and the Zoom dial goes up to 20×; both snap at their rest value, step
with the arrow keys and reset on double-click; **Reset**
returns to the default framing. Choosing a preset glides there with a light
overshoot. Placement pans the camera, so off-centre framings stay undistorted.

Framing input renders in the same event and reuses the posed avatar copy (about 2 ms
per frame); only a new pose or expression bakes the skinned meshes again, and a new
pose re-poses the existing copy instead of duplicating the avatar. A render that posed
the copy or changed its expression is followed by two more on the next editor frames,
after an editor update, so every skinned mesh (hair, accessories) shows the new pose.

Light presets set their own ambient on the avatar copy (through its light probe data,
so the user's scene lighting is never touched) and may add a second rim light:
**Cinematic Rim** and **Neon Night** are dark looks lit from behind on both sides,
next to **High Key**, **Low Key** and the earlier studio looks. The first background
is a plain colour chosen with an inline picker (area, hue bar, curated colours, hex
entry, reset to the default grey).

The live scene is an additive, unsaved scene with the avatar copy, three lights and
the background. Poses (`BodyPoses/`), backgrounds (`Backgrounds/`) and the banner blur
shader ship with the package. Framing ignores particle, trail and line renderers, and
renders compile shaders synchronously, so previews and captures never show the cyan
placeholder of a shader that is still compiling.

`ButtonInteraction.RegisterImmediateClick` (buttons that act on press) and
`SkinnedMeshBounds.Refresh` (posed culling bounds) are shared editor helpers.

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
