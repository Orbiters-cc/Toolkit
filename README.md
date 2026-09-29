# Orbiters Toolkit

Unity 2022.3 editor utilities for mirror posing and optional AI integration.
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

## Mesh comparison

`Orbiters.Toolkit.Editor.Meshes` compares versions of a model without importing them: `FbxReader` reads a binary FBX
(meshes placed in the file root's space with Unity's axes and scale, blendshape signatures, bones, material names; with
`FbxReadOptions.Render` UV-split vertices and one submesh per material slot, with `FbxReadOptions.ShapeOffsets` each
blendshape's offsets). `MeshComparison` measures how far each vertex moved, index by index or to the other version's
surface when the topology changed, and describes parts as moved, reshaped, re-materialed, added or removed. Unit Git's 3D
view and MCB's version differences use it.

## Feature flags and optional packages

Tools register opt-in features with `OrbitersFeatures.Register` (key, product, label,
description, `FeatureStage`, default; stored per user in EditorPrefs). **Orbiters settings**
lists them by product with a `StageBadge` (beta, alpha, experimental) and a switch.

`Orbiters.Toolkit.Editor.Vpm` (compiled when the VRChat Package Resolver is present) reads a
package's VPM dependencies from its package.json: `vpmDependencies` plus an `"orbiters"` block
with `repositories`, `dependencyDisplayNames` and `optionalVpmDependencies` (`displayName`,
`version`, `reason`). `VpmDependencies.For("<package>")` reports status and installs, adding
missing repositories first; `DependencyPrompt` shows "this feature needs X" with an Install
button and hides once X is installed.

## Mirror posing

In XRay Gizmos, select an avatar or one of its bones and enable **Mirror** in the
Scene-view toolbar or the XRay Gizmos window. Rotate or move one paired bone in
the Scene view or Inspector; Toolkit mirrors the edited channels to its partner
across the avatar root's **local X** plane. The mirror remains scoped to that rig
when selection changes. Toggle it off and on with another avatar selected to switch.
The window reports the active rig, pair count and selected partner.

Humanoid bone mappings take priority. Generic rigs use matching hierarchy paths
whose names differ only by a side word (`Left`/`Right` in any case, or a separate
`L`/`R`: `upper_arm.L`, `J_Bip_L_Hand`, `LeftArm`, `mixamorig:LeftHand`), see
`BoneNames.Mirror`. Ambiguous paths, unpaired bones and center bones are skipped.

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

## Bone matching

`Orbiters.Toolkit.Armature` (runtime assembly `Orbiters.Toolkit`) matches clothing and accessory armatures onto an
avatar. `BoneNames` reads bone names as words: `Normalize`, `Side`, `Mirror`, `IsArmatureContainer`, `IsEnd` and
`TryInferHumanoid`, which knows Unity/VRChat, Mixamo, Blender/Rigify, VRoid, Rexouium, Biped and Unreal names,
including fingers, eyes and jaw, and leaves props (`ArmBand.L`, `Chest Pin`), Blender copies (`Hips.001`) and `_end`
leaves alone. `AvatarBoneIndex.Build` indexes an avatar's bones by name and humanoid role; `BoneMatcher.MatchHierarchy`
matches parents first by exact name, name without the clothing's affix (`DetectAffixes`: `Hips_Shirt`), humanoid role,
then contained name, and reports ambiguous matches with their alternatives. `ArmatureRest` gives bind-pose rest frames.

`Orbiters.Toolkit.Meshes.BlendShapeEvaluation.Add` applies a blendshape to mesh data the way Unity's skinning does
(checked against `BakeMesh`), clamped or not as the project's "Clamp BlendShapes" Player Setting (`ClampWeights`) says, so
X-Ray and ReFit measure the shape the user sees.

## Attachments

`Orbiters.Toolkit.Editor.VRChat.Attachments` attaches a clothing or accessory object placed under an avatar root, with
Undo and without changing the avatar. `AttachmentPlanner.Analyze(accessory, avatarRoot)` returns an `AttachmentPlan`:

- **Configured**: the creator's VRCFury Armature Links, Modular Avatar merge/bone proxy components or wired constraints
  already attach everything it shows. It is kept as is. A VRCFury or Modular Avatar prop that links nothing (dropped in
  the world, driven by its own controller) counts as configured too. A constraint only attaches what it holds when it
  is enabled and active and its source follows the avatar (an avatar object, something already attached, or one of the
  accessory's skin bones); one held by a locator of the accessory alone does not.
- **Clothing**: skinned to an armature of its own. When one recursive VRCFury Armature Link reproduces the bone matcher's
  result exactly (VRCFury only merges children by exact name, after its derived suffix), the plan uses one; otherwise
  each matched bone gets a link of the tool's own. Extra bones (hood strings, physics chains) follow their parent. A bone
  several avatar bones fit equally, and a bone below it whose match only followed that guess, is not linked:
  `AttachmentPlan.Ambiguous` keeps it with its candidates (named in a note) for AI or the user.
- **Rigid**: follows one avatar bone, chosen from its name (hat, hair, ears: head; necklace, pin: chest; bracelet,
  glove: the hand its name or position says) or the closest humanoid bone (flagged as a guess). Modelled far from the
  bone, it is placed on it.

Empty parent, position and rotation constraints (Unity or VRChat) named after avatar bones (`Head`, `Left wrist`) get
that bone as source, keeping where the object stands. Unity ones become VRChat constraints, except those an animation of
the avatar drives: the SDK could only rebind those animations by editing the clip assets in place, so they stay working
Unity constraints. Empty aim, look-at and scale constraints, object names that give instructions (`(open me)`,
`Put me in armature`), unknown scripts, missing scripts and bones no avatar bone explains become `SetupNote`s.

`AttachmentInstaller.Install(plan, options)` adds `OrbitersAttachment` (runtime assembly `Orbiters.Toolkit.VRChat`,
`IEditorOnly`) to the accessory root, the VRCFury Armature Link when exact, a saved VRCFury toggle under
`Accessories/<name>` when the accessory has no toggle or controller of its own, and copies the body's blendshape
weights to same-named shapes. `Retarget`, `Link` (AI answers), `Remove` and `Installed` complete it. VRCFury components
are created through VRCFury's public API by the optional `Orbiters.Toolkit.Editor.VRCFury` assembly; `VrcFury` reads
VRCFury components (including links saved by older VRCFury versions) without referencing it.

At build (`AttachmentBuild`, VRChat preprocess callbacks): at -10100, just before VRCFury, linked bones are placed at
their rest offset from their avatar bone (from both meshes' bind poses, so the scene pose does not matter) and parented
props stay where they are. Both are then moved under their bone, without adding constraints. When VRCFury builds the
avatar (it has a VRCFury component), VRCFury keeps the accessory's animations working with the paths it recorded
earlier. Otherwise (`AttachmentAnimationBuild`) each one goes under an offset frame on its bone, the build copy's
animator controllers are copied and their paths remapped (with proxy frames keeping animated former parents' motion and
visibility), and the copies live in `Assets/OrbitersToolkitBuildCache` until the build ends; data left by an
interrupted build is deleted on the next script reload. At -8900
animations of body blendshapes also drive the same-named accessory shapes (`BlendShapeSync`, including meshes that moved
out with their bone), and the components are removed. The scene is never changed by a build.

`AttachmentFollow.Links(avatarRoot)` lists, for every accessory, which transform follows which avatar bone and at which
offset: My Avatar attachments, VRCFury Armature Links, and the bone matcher for clothing nothing links. The build and
the posing preview share it. Armature Links follow VRCFury's own rules: the first target that resolves on the avatar
(humanoid bone, object or avatar root, then its offset path), and position, rotation and scale aligned independently.

## Follow body blendshapes (beta)

`OrbitersSurfaceFollow` (runtime assembly `Orbiters.Toolkit.VRChat`, `IEditorOnly`, menu **Orbiters › Follow Body
Blendshapes (beta)**) keeps rigid accessories on the skin when body blendshapes change: a piercing on a chest that grows
with "Muscles" moves and tilts with it. On an object it covers that object's meshes; on the avatar root in **Small
Accessories** mode, every small rigid accessory close to the skin (size and gap are settings). Rigid means a plain mesh
or a mesh on one or two bones; clothing skinned to more bones bends with the body on its own and is left alone.

`SurfaceFollow` (editor VRChat assembly) anchors each accessory to the closest triangle of the body at its current pose
and weights. For every body blendshape, the movement and tilt of that triangle (offsets through the same bone blend as
skinning, so exact at the scene pose) become a blendshape of the accessory with the same name; shapes that move the skin
there by less than 0.2 mm and 0.3° are left out. At build, after VRCFury and MCB's links (-8950), plain meshes become
single-bone skinned meshes (except renderers an animation drives, which stay as they are), the accessory's rest shape is
set so it stays where it is at the body's current weights, and `BlendShapeSync` copies the body's weights and animations
to the new shapes. The scene is not changed. The component's inspector has **Check**, a dry run listing what will follow
and which blendshapes move it. ReFit (**Keep rigid pieces on the body**) and MCB (**Keep Small Accessories On The Body**)
add it for you.

## Accessory posing preview

`Orbiters.Toolkit.Editor.VRChat.Posing.AccessoryPoseSync` (`Enable(avatarRoot)`, `Disable()`, `Sync()`, `Find`,
`Accessories`, `Changed`) keeps accessories in the avatar's pose as they will be attached once built, including props on
one bone. It is a preview: accessories go back where they were when it is switched off, before their scene is saved, on
script reload and on entering Play Mode. Bones driven by constraints are left to them; accessories Modular Avatar
attaches are shown by name match.

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
