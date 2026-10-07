# Orbiters Toolkit

## 0.3.17 — 2026-10-07

- **Drawing pen** held where it was grabbed: it no longer jumps into a set pose in the hand, but follows the hand from
  the position and rotation it had when grabbed, for the owner and for guests. Back in front of the chest, it sits on
  its grip again. Pens built before keep the old hold until rebuilt (a new gallery version for gallery pens).
- Item pictures show props hidden until their menu toggle turns them on (the drawing pen) instead of an empty square.
- `orbiters_editor_window` (MCP) captures everything in the background: inactive docked tabs, windows that are not open
  (`open_if_missing`) and long windows (`width`, `height`, `scroll_to`) are rendered in a hidden copy outside every
  display, and `inspect` captures any object's Inspector without selecting it. Unity is never brought forward; the
  `focus` option is gone.
- `MaterialSides`: whether a material shows the back of its faces and whether the mesh part it covers needs that (open
  surfaces such as fur cards and feathers). `MaterialSurfaceMaps.OriginalShader`, `StrippedTexture` and `EnableEmission`
  for locked (optimized) materials and emission maps.
- The clothing pose preview leaves a custom base's own objects alone (its `CustomBaseFootprint`, e.g. MCB's logic).
- `BoneFrameRetarget`: animations written for a skeleton whose bones rest in other orientations (a model re-exported with
  other bone rolls) turn each bone the same way from its rest pose on the current skeleton; clips already made for it
  stay. `AttachmentAnimationBuild.IsBuildData` tells build copies (in memory, the build cache, VRCFury's temporary
  builds) from source assets.
- `OrbitersAboutWindow`: the About window of the Orbiters tools (logo, version, license, then each third-party project
  with its license, read from the tool's notices file). MCB and My Avatar use it. It recognises the SIL Open Font License (OFL) and keeps
  a notice's list items on their own lines.
- The photoshoot uses two columns once it is wide enough (820 points): the shot and its framing on the left, its
  style beside them. A host can put its own content first in it (`PhotoshootPanel.SetLead`, e.g. My Avatar's card).
- Photoshoot **text**: a host that passes fonts (`PhotoshootOptions.TextFonts`) gets a line of text on the thumbnail
  (`PhotoshootState.Text`), edited on its previews (`PhotoshootTextLayer`: move, resize, write in place, while the rest
  of the preview still frames the avatar) and in a Text tab, and drawn into captured thumbnails by the same renderer
  that draws the preview (`PhotoshootTextRenderer`: glyphs from the font, then outline, shadow or glow).
- Photoshoot **poses of your own**: a + swatch on the Pose tab (or animations dropped on the poses) adds poses from
  humanoid animations in the project: an .anim, or a model's clips (a clip dragged from a model adds just that one).
  Each pose is kept for every project, also once the animation is gone, and can be removed like your own backgrounds;
  both share one store (`PhotoshootLibrary`).
- `AvatarParameterBudget.BuildRemovedParameters`: a build step that leaves parameters of a VRCFury component out (My
  Avatar's face tracking features others don't see) makes every budget count them as uploaded.

## 0.3.16 — 2026-10-07

- **Tools › Orbiters › Animation Extractor**: drop FBX files, from the Project window or your computer (copied into
  `Assets/Animations/Imported`, or a folder you choose), and save their animations as standalone .anim clips you can use
  anywhere: pick the clips and their names, keep or force Loop Time, and replace earlier extractions in place so
  controllers using them keep working (or keep both, or skip). Humanoid curves, events and clip settings come along.
- Photoshoot **ref sheet**: the avatar from the front, the back and the side, side by side at one scale on a 1920×1080
  sheet, each view named above it in italic grey on a dark background. Same pose, light and expression as the
  photoshoot; the side view faces left or right; drag, scroll or Fit the views; captured at full size. Hosts open it
  with their own button.
- Photoshoot **effects**, stackable, each with its strength: bloom, comic halftone (flat colours in 2 to 12 tones, ink
  outlines, and an optional print screen of dots whose size you choose), ambient occlusion, depth of field (sharp at
  the avatar's view position from its VRChat avatar descriptor), chromatic aberration, grain, lens distortion and
  vignette (below zero it brightens the edges). Bloom, occlusion, depth of field and chromatic aberration go up to 300%.
- Photoshoot **backgrounds of your own**: a + swatch (or pictures dropped on the backgrounds) adds PNG or JPEG pictures,
  kept for every project; each can be removed. Background pictures are now cropped to the frame instead of stretched.
- Photoshoot Light tab: an environment light slider (0 to 300% of the preset's).
- The photoshoot's Framing card folds under its header; it is open by default and stays as you leave it.
- Photoshoot pictures no longer show helpers other tools hang on the avatar without saving them, such as XRay Gizmos'
  armature.
- The photoshoot sphere's stylesheet no longer makes Unity warn about an unknown `outline-width` property on every import.

## 0.3.15 — 2026-10-06

- Photoshoot: changing where the avatar looks no longer zooms in when a framing preset is chosen; like turning, it keeps the zoom and placement.

## 0.3.14 — 2026-10-06

- Photoshoot: **Look at the camera** makes the avatar look straight at the camera; a dial splits the turn between the head and the eyes, from the head alone to the eyes alone. Eyes skinned partly to the head turn further, so what shows still meets the camera.
- Photoshoot: a sphere turns and tilts the avatar (`OrbitSphere`, also for other tools). The avatar turns around whichever of its hips, chest and head is nearest the middle of the view, and the camera is framed on the unturned avatar, so turning no longer slides it around or changes the zoom. Eyes are the humanoid eye bones, else the ones set in the VRChat Avatar Descriptor's eye look (`PhotoshootLook.EyeFallback`).

## 0.3.13 — 2026-10-05

- `AvatarBudget`: an avatar's parameters, bones, PhysBones and contacts against VRChat's PC limits, read from the SDK's performance levels, split between the avatar and its custom base. Custom base providers describe what they add and what their build changes with `CustomBaseFootprint`. XRay Gizmos shows it over the Scene view.
- Parameter estimates count the bits of the custom base's objects separately, and estimate what VRCFury's parameter compression leaves after build: which parameters it compresses, the bits they take and the time a full sync takes.
- Build copies: `AttachmentAnimationBuild.Keep` makes objects created during a build assets until it is released; `Moved` keeps the animations of bones a tool moved before the build.
- Uploads keep the meshes and materials a build step created in memory (twist splits, accessories following the body): the SDK saves the build copy as a prefab, which dropped them and uploaded those renderers empty. A last build step saves them with the build, meshes in a binary file (a body with its blendshapes is several GB as text).
- Drawing pen: a thumbs up (index down, thumb up) draws too, like a squeezed fist. Pens created before keep their controller until created again.
- `BlendShapePruning`: the blendshapes a built avatar uses on a renderer (its controllers' animations, visemes, jaw flap, eyelids, standard MMD morphs on Body), and a copy of the mesh without the others, baked at their weight.

## 0.3.12 — 2026-10-05

- Shared building blocks for asset galleries and versioned content, moved out of MCB: version records and the version timeline, gallery card styles, safe archive extraction with size budgets, `.unitypackage` hashing, copying and import previews, content trust prompts, and resumable transfers checked against their SHA-256.
- Recognise original avatar bases as well as custom ones from a model fingerprint.
- The drawing pen is an avatar-independent prefab (`DrawingPenInstaller.CreatePrefab`) that fits itself to the avatar it is attached to; `AttachmentHooks` lets props adapt when a tool attaches them.
- VPM dependency plans: the complete transitive plan, conflicts included, is shown before any package changes; VRChat SDK packages and unrelated tools are never upgraded silently.
- Other packages can add sections to the Orbiters settings window; add Key, External, Grid, Download, Broom and Plus icons.
- The local development API uses 127.0.0.1 (faster than localhost on Windows); Orbiters inspectors no longer cause a horizontal scrollbar; sizes under 1 MB read in KB.

## 0.3.11 — 2026-10-05

- Add the shared searchable dropdown field and the blendshape picker used by MCB and ReFit (labels, search tooltip, selection refresh).
- Add Check, Sparkle, Male and Female icons.
- Keep notice boxes and status hosts from shrinking under long content.

## 0.3.10 — 2026-10-03

- Preserve clothing pose, proportions and skinning across attachment, ReFit, previews and avatar builds; improve residual body coverage.
- Repair broken imported materials, map roughness and smoothness correctly, preserve authored packed maps, and default missing surface maps to matte.
- Add the shared drawing pen installer and Play Mode mirror overrides; capture thumbnails safely with trail and line renderers.
- Cache original-body preparation and reduce repeated fitting work.

Unity 2022.3 editor utilities for mirror posing and optional AI integration.
Mirror posing works without MCP. The screenshot adapter compiles separately when
MCP for Unity 9.7.1 or newer is installed; this package does not install the server.

## Shared editor services

From 0.2.1, MCB and My Avatar share account storage and Magic Sync through
`AuthenticationService`, environment/API URL handling through
`OrbitersEnvironment`, and authenticated requests through `OrbitersApi`.
The per-environment account in the Unity preferences folder is the common store, readable by the signed-in OS user only
(DPAPI on Windows, a `chmod 600` file elsewhere); disconnecting in either tool disconnects the shared account. A browser
login saves only if it is still the newest, uncancelled one and the tools are still on the server it started on.

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
Unity constraints. On an object the tool did not create (`AttachmentOptions.Created = false`) the user's Unity constraints
stay in the scene and are converted on the build copy only (claimed through the SDK's `IsUnityConstraintAutoConverted`, so
its build panel does not flag them). Empty aim, look-at and scale constraints, object names that give instructions (`(open me)`,
`Put me in armature`), unknown scripts, missing scripts and bones no avatar bone explains become `SetupNote`s.

`AttachmentInstaller.Install(plan, options)` adds `OrbitersAttachment` (runtime assembly `Orbiters.Toolkit.VRChat`,
`IEditorOnly`) to the accessory root, the VRCFury Armature Link when exact, a saved VRCFury toggle under
`Accessories/<name>` when the accessory has no toggle or controller of its own, and copies the body's blendshape
weights to same-named shapes. `Retarget`, `Link` (AI answers), `Remove` and `Installed` complete it. `Remove` deletes an
object the tool created; from one it did not create, it removes what it added and puts back what it changed (wired
constraints, the place it was moved to, blendshape weights copied from the body), recorded on the attachment, where
the user did not change it since. VRCFury components
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
out with their bone; shapes a refit already links are left to it), and the components are removed. The scene is never
changed by a build.

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

## Refits to a custom base

Clothing made for an avatar's original base does not follow the blendshapes a custom base adds (MCB versions: muscles,
flexing…). The Orbiters tools share one refit layer for it; ReFit (`orbiters.refit`) computes the geometry.

- **Engine** (`Orbiters.Toolkit.Editor.Refit`): `RefitEngine.Current` is the installed engine (`IRefitEngine`); ReFit
  registers itself when its editor code loads, so a tool never references it and only needs it once the user asks for a
  refit. A `RefitJob` is one mesh: `Fit` fits it from the original base body to this body and adds the body's shapes,
  `Shapes` only adds the shapes to a mesh that already fits. `RefitPreferences.Tightness` (0 loose … 1 tight, saved in
  `ProjectSettings/OrbitersRefit.json`) is shared by MCB's ReFit panel and My Avatar.
- **Custom bases**: `CustomBases.Describe(avatarRoot)` asks the registered `ICustomBaseProvider`s (MCB registers one) for
  the avatar's custom base: its body, its blendshapes (the declared ones on the body, then every body shape containing
  "flex") and, when the provider can, the original base to fit from (`ResolveOriginal`, disposable: it may be a temporary
  import). `CustomBaseFingerprint` recognises a custom base without a provider: the SHA-256 of the body's model file is
  looked up on the Orbiters server (`POST mcb/custom-bases/identify`), cached in `Library/Orbiters`. It cannot fit from
  the original. `CustomBaseDetection.DetectAsync(avatarRoot)` does both once per avatar in the background, then measures
  where each custom shape moves the body (`BodyShapeMap`, read a few shapes per editor update); it starts over when a
  provider calls `CustomBases.NotifyChanged` (a version applied) or the body mesh changes.
- **Fit check**: `FitCheck.CheckAsync(item, state)` says whether an item needs anything: only its skinned meshes lying
  within 5 cm of skin a custom shape moves count (`RefitRelevance`, on a worker thread), so rigid pieces and far away
  items are left alone. Shapes a mesh already has, under the same or a normalized name, are its creator's and are never
  replaced. `FitAdvice`: `AddShapes` (it fits, or its creator adapted part of it), `AskFit` (unknown: ask), `Refit`
  (`OrbitersFitInfo` says it was made for the original base). An item's creator adds **Orbiters › Fit Info**
  (`OrbitersFitInfo`) to say which base and custom base it was made for, and which body shapes it must never get.
- **Records**: `RefitRunner.Run`/`RunAsync(RefitBatch)` refits meshes and records each on its renderer
  (`OrbitersRefit`, runtime `Orbiters.Toolkit.VRChat`, `IEditorOnly`): the renderer's state before (mesh, bones, pose,
  weights), the generated mesh and the body shape each generated shape follows. Refitting a refitted mesh starts again
  from its original instead of stacking; adding shapes to it keeps its fit; a failed refit keeps the previous one. The
  same inputs (mesh, pose, body and its weights, the original body's pose and weights when fitting from it, shapes,
  tightness) reuse an earlier result from `Assets/Orbiters/ReFit/Cache`, with its warnings (a rough fit stays rough). `RefitRecords` restores (`Remove`, `RemoveAll`), lists and syncs them; the record's
  inspector shows what follows the body and restores the original mesh.
- **Build**: `RefitBuild` takes the applied records from the build copy at -10110, before attachments and VRCFury move
  or merge meshes, and at -8960 (after VRCFury and MCB's correctives, before Follow Body Blendshapes and attachments)
  makes every animation of a body shape also drive its generated shapes, whatever animates it (exact curve copies,
  `BlendShapeSync`). A generated shape removed during the build while its body shape is animated fails the build.
  Without VRCFury, the controllers are copied as for attachments. Attachments leave the shapes a refit links alone.
- `RefitGhost.Show(original, originalBody, avatarBody)` shows the original base see-through over the body, to line
  clothing up with it before a `Fit`; `RefitCandidates` lists the meshes a refit can adapt (never editor helpers such as
  X-Ray gizmos).

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
instances. Everything happens in the background: Unity is never brought forward and
the user's layout, tabs, scroll positions, selection and focus are left alone. The
tool does not click any controls, change the scene, or capture pixels from the desktop.

- A shown window (the selected tab of its dock) is repainted and its framebuffer read.
- An inactive docked tab, a window that is not open (`"open_if_missing": true` with an
  exact `window_type`), or a capture with `width`, `height` (points, up to 8000) or
  `scroll_to` is rendered in a hidden copy: shown without focus outside every display,
  with the original window's serialized state, and closed after the capture
  (`captureMode: "hidden_copy_framebuffer"`). Opening a window type uses only its
  `CreateInstance`; windows that need a menu setup routine may show their empty state.
- `scroll_to` scrolls the hidden copy so the first element whose name, USS class or
  text matches is at the top.
- `"inspect": "<instance ID | Root/Child | Scene:Root/Child | Assets/... path>"`
  captures a hidden Inspector locked on that object, without selecting it:

```json
{ "action": "capture", "inspect": "Rexouium1.6 Default Setup", "height": 1400, "scroll_to": "Face tracking" }
```

The server polls pending captures automatically. Clients without polling support
can call `{ "action": "status", "job_id": "<returned job_id>" }`. Jobs are
in-memory and expire on domain reload; request a new capture after a reload.
Only one capture runs at a time; up to 16 recent results are retained.

The completed response includes `fullPath`, output/source dimensions, scale,
window identity, Unity version and project path. Open `fullPath` with the client's
image viewer to inspect the PNG. Files use unique names under
`Library/OrbitersToolkit/Screenshots/`; they are not imported as Unity assets and
are removed if the project's Library is cleared. `max_resolution` accepts 0 for
native pixels or 64–8192 for a downscaled image.

## Boundaries

The capture is the window's rendered content, not its operating-system title bar
or other popup windows. It uses Unity's internal `GUIView.GrabPixels` API and
fails explicitly if that API is unavailable. A visible graphical editor is
required; headless, minimized and hidden-window rendering are not guaranteed.
Capture failure, closed windows and inactive docked tabs produce errors. Inspect the
returned image before treating a successful pixel read as visual verification.

After importing this package, allow compilation and MCP tool discovery to finish.
If the tool list is stale, rescan tools in MCP for Unity or reconnect the MCP client.
