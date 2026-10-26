# Orbiters Toolkit

## 0.4.8 — 2026-10-26

- **Pictures at the mesh's own scale**: skinned meshes scaled on their object (N4N0's ball) were pictured 1/scale too
  large, so Mixer's Hierarchy showed an avatar root as N4N0's sphere; ReFit's relevance and bounds measure bone-less
  renderers at their scale too.
- **Never refit**: `OrbitersAttachment.neverRefit` (from the creator's gallery setup, or the Inspector) and props the
  Toolkit fits itself (N4N0, the drawing pen, the hand screen) never get a fit question (`FitCheck.NeverRefit`).
- **Stay there** (N4N0 › Light › Stay there, `N4N0/Light/Stay`, one synced bit, not saved): N4N0 and its light stay
  where they are in the world, a steady light, until it is turned off. N4N0 hangs from a stay frame under the head
  (`Head/Stay`, a VRC Parent Constraint on the head that the light's animator layer freezes to the world).
- **N4N0 stays upright**: a VRC Rotation Constraint turns N4N0 with its stay frame, whatever way the swinging tether
  tilts its tip, so it no longer nods as its owner walks.
- **Expressions without clipping**: each expression has in-between frames at 25, 50 and 75 % that follow the sphere a
  little above it, and N4N0's states change one expression at a time (each state has an intro: the old expression
  blends out, then the new one ramps in), so the face's patterns never cut through the ball on their way. Listening no
  longer blinks.
- **N4N0's folder icon**: the N4N0 folder in the expressions menu shows the N4N0 mark in its aqua-to-lime gradient
  (`N4N0 icon.png` in N4N0's folder).
- **Screen version 9**: the Music page's timeline (16 segments lit to where the track is; tap or drag along it to move
  in the track) and Take back card on the Results and Music pages while the link card is out (Orbiters - N4N0 0.3.2);
  the screen's back shows N4N0's face logo. N4N0's language files have the new text (`card.takeBack`).
- **Full pictures' diagnostics**: with Orbiters - N4N0's N4N0 › Testing › Picture diagnostics on, the screen (which
  also reads CRT A now) frames each picture in the colour of why it shows what it shows and draws the textures' state
  in a row of cells at its bottom left (contracts `Docs/orb-picture.md`, Diagnostics). A picture shows once CRT B ran:
  B's heartbeat is no longer compared with the screen's clock (Custom Render Texture updates and cameras need not share
  `_Time`).
- N4N0's options folder is `Menu 16`, its screen revision 13.

## 0.4.7 — 2026-10-26

- **VRCFury 1.1434**: VRCFury now builds controllers that reuse the avatar's own clips, blend trees and masks where it
  changed nothing; a build step right after it (`VrcFuryBuiltAnimations`, order -9999) copies them into its
  controllers, so the build steps after it (blendshape links, face tracking, ReFit, MCB) never edit project assets.
  Hidden builds run each VRCFury step inside its build context (`VrcFury.BuildContext`, `VrcFury.RunMain` for direct
  callers), and Armature Link targets fall back to related bones and resolve offsets from the avatar root as VRCFury
  does. Needs VRCFury 1.1434 or later where VRCFury is installed.
- **N4N0's new look** (`OrbInstaller.Model`): a black sphere with white patterns, its face on the front (two wide eyes
  wrapping round its sides and a small mouth) and the N4N0 mark under a triangle's three corners on the back. Its
  expressions are the model's blendshapes (Surprised, Annoyed, Happy, Thinking, Sleeping, Blink, Talk), which N4N0's
  states set: it blinks now and then, its mouth moves while it talks, its eyes widen with its owner's voice while it
  listens; everyone sees it face ahead, its owner sees it turn to them. One VRChat/Mobile/Standard Lite material (two
  4 × 4 pictures: a dark shell, white patterns that glow alone), the same mesh and material for the phone N4N0, about
  1,600 triangles; the Light option tints the patterns' glow, annoyed turns it red. The shader-drawn orb and the
  phones' face picture are gone.
- **Customizable parts** (`ItemParts`): a creator's `<Prefab>.parts.json` beside the prefab names parts others may make
  variants of (values on materials, a model, texts), each limited to the files the creator allows (pictures, one FBX,
  JSON; never scripts); variants are checked, applied in place with Undo, kept on the item and through gallery updates
  (see Orbiters' documentation, "Customizable parts and variants"). N4N0 has three: its menu theme, its model and its
  language (English, French, Spanish, Portuguese, Japanese), its menu pages drawn by Orbiters - N4N0 for the item's
  theme and language. The card's Look option is gone: Customize › Menu theme replaces it.
- **N4N0's pointers ride the hands**: each pointer is held to the hand bone where the index fingertip is with the
  finger straight, so curling the index to pull a trigger no longer moves it.
- **N4N0's menu glides back lazily**: out of sight for 0.4 s, it glides (0.5 s) to where its owner looked as it set
  off, a point that stays put in the world, and stays there; out of sight a moment more, it glides again. It never
  rides the head any more.
- **Out of recordings and 3D pictures**: while N4N0's capture kit records (`N4N0/Capture/Record`), its owner's client
  hides N4N0's screen, handle, link card, text panel and desktop dot, and shows them again after, the menu left open.
- **N4N0's full pictures** (screen version 8; Orbiters contracts, `Docs/orb-picture.md`): the screen shows the
  pictures Orbiters - N4N0 sends at the size of their place (the results' thumbnails at 128 × 72, the music's art and
  the link card's picture at 256 × 144) over the small thumbnails they belong to, for everyone once they arrived: the
  owner's first, through 64 local float parameters over OSC (`N4N0/Picture/Value0` to `Value63` and
  `N4N0/Picture/Send`, unsynced and unsaved: no synced bit), everyone else's through the text channel (pointers 188 to
  251, with 8 or 16 text slots; with 4 they reach their owner only). N4N0 carries a bridge renderer under its head
  (`Head/Pictures`, on only while chunks flow: it writes them into a keyed slot of the frame, a GrabPass before and
  after) and two Custom Render Textures in its screen folder: CRT A (512 × 256) keeps each picture's stream and decodes
  one picture at a time, CRT B (512 × 290) keeps the decoded pictures with their signatures and a heartbeat. Their
  shaders are written from the contracts' `OrbitersPicture.cginc`, every chunk value in a register of its own
  (`OrbPicturesHook` gives the bridge the screen's own folder before each upload). A picture shows only over the
  thumbnail whose first 8 bytes it carries, while CRT B is alive, and while a screen pixel spans under two of its
  texels; the thumbnail shows otherwise. Tests: `OrbPictureTests` (a contracts vector's chunks through the bridge and
  both CRTs' shaders into CRT B, pixel for pixel).
- N4N0's options folder is `Menu 15`, its screen revision 11.

## 0.4.6 — 2026-10-26

- **N4N0's capture kit and camera rig** (`OrbInstaller.Capture`; Orbiters contracts 0.7.0, `Docs/rig-frames.md`,
  N4N0's capture kit): what lets Orbiters - N4N0 record a VRChat world through VRChat's camera (3D pictures,
  recordings) is part of N4N0 now, instead of Mixer's "Orbiters Capture Kit" with its own Action Menu toggles. N4N0
  carries the rig (six square 90° cameras into 512² HDR render textures, and the renderer of their rig frames) and the
  kit's renderer under `Capture`, beside `Shown` (hiding N4N0 leaves capture working); the kit's skinned helper mesh,
  one triangle per humanoid bone, is made for the avatar's armature as the avatar is built for upload
  (`OrbCaptureHook`, after the platform's N4N0 is kept and before VRCFury; its files in
  `Assets/Orbiters/N4N0/Capture kit/<avatar>`), and phones' builds leave the capture part out. The contracts' shaders
  are copied into N4N0's folder under their own names (`Hidden/Orbiters/N4N0 Capture Kit`, `… Capture Rig`), their
  includes written into them. Orbiters - N4N0 drives them over OSC through local, unsynced, unsaved parameters (no
  synced bits): `N4N0/Capture/Record` (N4N0 › Record in the Action Menu sets it too), `N4N0/Capture/Depth`,
  `N4N0/Capture/Follow`, `N4N0/Capture/Rig`, `N4N0/Capture/RigX`, `RigY`, `RigZ`, `RigYaw`. While following, the rig
  stands at VRChat's camera (a VRChat Parent Constraint: the world origin's source, whose offsets the animator sets, or
  the hips'), and the kit's bounds, 4 m around the rig, go with it: the kit draws wherever the camera goes without
  bounds that rank the avatar "Very Poor". The rig's cameras and the kit draw on the wearer's client only (`IsLocal`).
  The six render textures count in VRChat's texture memory. N4N0 must be built again (a new gallery version) for
  avatars to get it; an avatar wearing Mixer's old kit should lose it.
- **N4N0 screen version 7** (revision 9, options folder `Menu 14`): in VR each index finger points at N4N0's menu with
  a beam and a dot the screen's own shader draws (`N4N0 Ray`). Two local contact senders per finger, its tip and a point
  0.3 m along it, are found by four receivers each, and Orbiters - N4N0 solves the ray, then presses with the trigger or
  a touch. Everyone else's index fingertips have four receivers of their own, so other players press by touching.
  VRChat's raycasts of versions 4 to 6 never met their own avatar. The menu stays where it is in the world and glides
  back before its owner's eyes only after 0.4 s out of their sight. Its handle is a pickup latched to the palm, as the
  Hand screen's is. Its size is the synced `N4N0/Screen/Size`, which Orbiters - N4N0 sets. N4N0 floats lazier and
  smoother.
- **N4N0's link card is an object of its own** (`OrbInstaller.Card`): Drop puts the card out (synced `N4N0/Card/Out`)
  in front of its owner, projected by N4N0's beam for 1.5 s. It then stays where it is in the world, apart from N4N0
  and the menu. Anyone's pickup grab moves it (its owner's hand latches it, `N4N0/Card/HeldLeft`/`HeldRight`), and
  grabbing it still copies its link. It draws with the screen's mesh and materials, with a memory of its own that
  every client writes from the text channel's blocks past the screen's (pointer 252 clears it). The screen is never
  the card any more: `N4N0/Screen/Drop` and pointer 253 are gone, and page 15 loses its countdown.
- **Toggles show the item's picture**: an accessory or piece of clothing whose creator gave it no menu toggle (no VRCFury
  Toggle or Full Controller, no Modular Avatar menu) gets Toolkit's toggle with the item's own picture as its menu
  icon, pictured once it is fitted with the renderer My Avatar's list and Mixer's Hierarchy use (transparent
  background, `AttachmentIcons`, `Assets/Orbiters/Accessory Icons/<avatar>/<item>.png`, 128 px). Each install, update
  or reinstall makes its picture; removing the item sweeps it. Items installed before keep the white dot until they
  are installed again or updated.
- **Faster avatar builds**: Unity rebuilds a whole animator controller whenever one of its states, transitions, masks or
  clips changes, so edits to big controllers (VRCFury's FX) cost seconds each. `ControllerEdits.Batch` gives the
  layers a stand-in state machine and no mask while edits run, then puts them back with one rebuild each, around the
  build's path rewrites (`AttachmentAnimationBuild`), short names, merged meshes' shape curves, blendshape links and My
  Avatar's texture swaps. The build changes the controllers VRCFury made for the build copy in place (so VRCFury's
  parameter compressor no longer copies the whole FX), copies clips without walking every key, and writes its cache
  once at the end. MasculineCanine's build steps went from 948 s to 113-213 s. New: `ControllerEdits`,
  `AttachmentAnimationBuild.Save`/`Edits`, `AttachmentIcons`, `ModelThumbnails.Picture` and `VrcFury.IWriter.Icon`.

## 0.4.5 — 2026-10-25

- **Sign in without a code**: one **Sign in** button opens Orbiters in the browser, where **Connect** (after signing in
  with Discord or Telegram if needed) hands the tool its credential through a listener on this computer's loopback
  (`OrbitersBrowserLogin.Callback`: a port the system picks and a random path); the tool trades that one-time code,
  with the secret only it holds, for its credential. No code to type, and a link someone else sends connects nothing.
  Needs the Orbiters server's new sign-in; older Toolkit versions can no longer sign in once it is live.
- **N4N0's menu looks projected and follows its owner**, as N4N0's speech panel does: the page's dark card is a tinted,
  see-through plate with a bright edge, a glow past it, a light from the top and faint scan lines, front and back,
  and N4N0's beam reaches its corners (a second part of the now skinned screen, `OrbBeam`). It no longer freezes in
  the world: it hangs from a PhysBone swing rooted at the eyes (`Head/Screen swing`), lagging behind the head and
  the avatar's turns and settling in front of them. The retro look keeps its own tube.
- **Link cards** (`OrbInstaller.Card`): Orbiters - N4N0 drops the screen in the world with a link on it; it stays
  where it is for its owner while `N4N0/Screen/Drop` is on, even in VR without Replace and through Menu off, and for
  everyone else from text pointer 253 until 254. Meanwhile it carries an `N4N0Card` contact sender, which every
  N4N0's hand receivers feel (`N4N0/Card/TouchL`, `TouchR`, local): a grab copies the link. Screen blocks now stay
  below pointer 253.
- **Screen version 5**: the seven-tile Home (with States), the States page and the link card's page, from
  Orbiters - N4N0's new pictures (16 page slices). Screen files revision 7, options files in `N4N0/Menu 12`. An N4N0
  built before this version must be updated as a whole (My Avatar) before its options change.
- **N4N0 on Quest** (`OrbInstaller.Mobile`): every N4N0 built from now on carries a mobile N4N0 beside the PC one
  (`Mobile`, off in the editor), and a build step before VRCFury (`OrbPlatformHook`) keeps it in Android and iOS builds
  and the PC N4N0 in PC builds, which stay as before: one avatar uploads to both. The mobile N4N0 is one skinned orb
  (384 triangles, one bone) with one `VRChat/Mobile/Toon Standard` material. Its face comes from `N4N0 faces.png`
  (3 × 3 cells of 256², drawn from the orb shader's eyes; the animator moves the texture's offset with `N4N0/State`,
  and Idle, Listening and Talking blink); it glows, bobs and hops as the PC N4N0 does, takes the Light option's colour
  at its brightness (green while off, red while annoyed), follows the head without the tether (no PhysBone), looks at
  its owner on their own client and ahead for everyone else, and its owner touches it (`N4N0/Touch`, local). A mobile
  build drops the PC controller but keeps N4N0's parameters and menu in the same order, so synced parameters line up
  between platforms, and adds `N4N0/Mobile/Version` (local, 1), which tells Orbiters - N4N0 that it drives the mobile
  N4N0. On its own it is Excellent on Quest; it costs an avatar one material slot and one skinned mesh, no PhysBone and
  no counted contact. No screen or text on phones yet.

## 0.4.4 — 2026-10-24

- N4N0's Menu toggle has the last word on its owner's screen: turning it off closes the screen and clears
  `N4N0/Screen/Open` (a parameter driver), also when Orbiters - N4N0 opened it and then quit or froze, where the screen
  stayed open and answered nothing. A screen the desktop opened with Menu off closes with Menu on, then off. Options
  files in `N4N0/Menu 11`: an N4N0 built with this version, or one whose options change, gets the fix.
- **Item settings** (`ItemSettings`): a creator's asset gets settings on its card, like N4N0's, from a file named like
  its prefab with `.settings.json` beside it (`Jacket.prefab`, `Jacket.settings.json`). A setting is a switch or a
  choice; an option shows or hides the item's objects (it hides what the setting's other options show), puts
  materials in renderer slots and sets blendshapes, with a picture if it has one. What holds is read back from the
  item, an update keeps it (matched by label), and `ItemSettings.Read` checks a file against its prefab. Orbiters'
  documentation: Item settings.

## 0.4.3 — 2026-10-24

- **N4N0** is Orb's new name in everything the Toolkit puts in a project or on an avatar: the prefab and its object,
  files and folders (`Assets/Orbiters/N4N0/…`), shaders, menu and parameters (`N4N0/…`), contact tags, screen pictures
  and texts. Orbiters desktop is Orbiters - N4N0, and texts name the Mixer console, Mixer Git and Avatar Gizmos (were the
  Logger, Unit Git and XRay Gizmos). The code keeps its names (`OrbInstaller`, `OrbState`, the `Orb*` templates,
  `IsOrb`, the avatar manifest's `orb` keys); the sphere on the tether's tip is `Ball`. An avatar with Orb needs N4N0
  installed again.
- **Colour thumbnails** on N4N0's projected screen (contracts 0.6.0): 64 × 36 pictures, blurred as their first layer
  arrives and sharper with each next one, decoded by straight-line shader code generated from `OrbThumbnail.json`
  (about 0.4 ms per million pixels at 128 bytes on an RTX 4080). A **Thumbnail size** option on N4N0's card picks
  72 bytes (as before) or 128 (sharper, slower to arrive, with 8 text slots or more).
- A **Light** option on N4N0's card: a point light under it in eight colours at a synced level, N4N0 › Light in the
  menu (17 synced bits; any light makes the avatar's PC performance rank Poor, and Quest leaves it out). **Screen
  version 3** adds the six-tile Home and the Music and Light pages.
- N4N0's screen shows its **back** from behind, mirrors included, and elements Orbiters - N4N0 greys out fade into the
  card. A **Look** option gives the screen a **retro** look: a cassette-futurism terminal with its own pages, a phosphor
  palette, hover outlines, inverted presses, bracketed choices and thumbnails as dithered green dots; the option shows a
  picture of each look on the card (`AttachmentOption.ChoicePictures`).
- N4N0's screen **points with rays** in VR (screen version 4): a VRChat raycast from each index fingertip meets the
  screen, its owner sees a beam and a bead, and a fingertip within 2.1 cm presses (owner only, while it is open).
  Shared files in `N4N0/Screen 6`, options in `N4N0/Menu 10`.
- **Stick to the skin** (`OrbitersAttachment.stickToSkin`, `SkinStick`): a small accessory on the skin follows the skin
  under it, through one VRChat Parent Constraint on the bones there, and the body's blendshapes there.
- `WorkingMeshes`: a tool editing a working copy of a mesh (Mixer) names the original, so the base fingerprint reads the
  original model while a session edits the body.

## 0.4.2 — 2026-10-21

- **Orb**, the Orbiters companion (`OrbInstaller.CreatePrefab(folder, textSync)`, Orbiters contracts' Docs/orb.md): a
  small glowing orb in Orbiters green with simple eyes that floats beside its owner's head, lazily and with a spring (a
  two-link PhysBone tether anyone can grab and let spring back), and the text panel it projects in front of its owner.
  Orbiters desktop drives it over OSC: `Orb/State` shows what it is doing (idle with a slow bob and blinks, listening
  brighter with wide eyes glowing up with its owner's voice, thinking with lidded eyes and a dashed ring turning around
  it, talking with the projector beam to the panel, asleep dim with closed eyes, happy arches, annoyed red slits,
  surprised round eyes and a hop: one shader, the eyes morph from state to state), and the panel shows 4 rows of 32
  glyphs sent a block of 4, 8 or 16 cells at a time (`Orb/Text/Pointer`, `Orb/Text/Sync*`, KillFrenzy Avatar Text's
  protocol: pointer 255 clears, the cells stay). Its owner talks to it by holding the menu's Talk button (`Orb/Talk`) or
  touching it (`Orb/Touch`, their own hands, on their client only). Every parameter is global in its VRCFury Full
  Controller, so OSC finds them by the contract's names; synced bits 1 + 8 + 1 + 8 + 8 per sync value (82 with 8).
  The cells are kept by one direct blend tree (Write Defaults on, as VRCFury keeps it), whatever the avatar's Write
  Defaults: VRCFury turns a Full Controller's layers to the avatar's setting, which would wipe cells kept with Write
  Defaults off. The panel is only as large as its text (a dark plate with a green edge, white glyphs with a dark outline),
  sits low and to the orb's side of its owner's view, out of its middle and below the face as people in front see it,
  reads the right way round from behind and in mirrors, and grows for viewers further away about its corner nearest the
  face. Its atlas (255 glyphs: ASCII, Western European accents, Russian Cyrillic, symbols; drawn by the contracts' tool
  from Cascadia Mono and Noto Sans JP) and both shaders are copied into the prop's folder; `Bind` fits it to the avatar's
  head bone, its facing and its eye height. The text shader derives from KillFrenzy Avatar Text's (MIT), see
  `Editor/THIRD_PARTY_NOTICES.md`.
- **Orb's text slots on an avatar** (`OrbInstaller.SetTextSync`, `TextSyncOf`): a placed orb switches between 4 and
  8 text sync values (50 or 82 synced bits; a full panel in about 10 or 5 s). Its VRCFury Full Controller takes the
  controller and parameters made once for that number in `Assets/Orbiters/Orb/Text slots N` and its global
  parameters follow, with Undo; the orb's own files (a gallery's) stay as they are.
- **Orb's Walk boost radial** (`OrbInstaller.SetOptions(orb, textSync, walkBoost)`, `OptionsOf`): an option on the orb's
  card adds Orb › Walk boost to its menu (`Orb/WalkBoost`, a local saved float, no synced bits), which sets Orbiters
  desktop's walk boost. Each choice of text slots and radial has its shared controller, parameters and menu in
  `Assets/Orbiters/Orb/Text slots N[ + walk boost]`.
- **Orb's 3D picture button** (`Orb/Photo3D`, local): Orb › 3D picture asks Orbiters desktop to record a second of
  VRChat's camera swinging around its subject and reconstruct it.
- **Orb › Call** (`Orb/Call/Menu`, `Pick`, `Go`, `End`, all local): the contacts on the panel, a radial choosing one, call
  or answer, decline or hang up, for Orbiters desktop's calls (contracts `Docs/orb-calls.md`). Orbs switched after this get
  their options' files in `Assets/Orbiters/Orb/Menu 3/` (earlier ones keep theirs).
- **Orb's projected screen** (on every orb; contracts Docs/orb.md, Screen): Orb's menu (fast walk, YouTube, calls, a
  3D picture) opens on a screen in front of its owner in VRChat's desktop mode (`VRMode` 0) and, in VR, with "Replace
  Orbiters overlay" on the orb's card (`OrbInstaller.SetOptions(orb, textSync, walkBoost, replace)`: the local
  `Orb/Screen/Replace`'s default, never saved); in VR without it the animator keeps the screen closed and Orbiters
  desktop's SteamVR overlay shows the menu. It shows the page pictures Orbiters
  desktop draws (`OrbScreenPages.png.bytes`, `OrbScreenLayout.png.bytes`, from `orbiters-desktop --orb-menu`) and draws
  over them what the desktop sends: texts, black and white thumbnails, hovered, pressed, chosen and hidden buttons. Its
  744 bytes of memory sit two to an animated parameter, written for its owner through local parameters (16 a block)
  and for everyone else through the text panel's synced channel (no synced bits of its own). It appears 42 cm before
  the eyes and stays frozen in the world while open; eight local contact receivers (four per index finger, not
  counted in VRChat's performance rank) tell the desktop where a finger touches it. A new orb's screen files are in
  its own folder; an orb made without a screen gets one (files shared in `Assets/Orbiters/Orb/Screen 2/`) when its
  options change; options' files are now in `Assets/Orbiters/Orb/Menu 7/`. Avatar manifests say `orb.screen`.
- **Orb's menu in desktop mode**: look at a button and left-click, as VRChat's own desktop menus work. Two local
  contact senders on the head bone (the eyes and a point 38 cm ahead on the line of sight, `OrbEye`/`OrbGaze`) and
  four receivers each on the screen (`Orb/Screen/Eye*`, `Orb/Screen/Gaze*`) tell Orbiters desktop the line of sight;
  a small dot shows where it points while the menu is open (its owner only, desktop mode only). Local contacts: nothing
  in VRChat's performance rank.
- **Orb's options survive an upload**: VRCFury reverts every override of its components in a prefab instance when an
  upload starts, so the options set on a placed orb (text slots, walk boost, Replace Orbiters overlay) came back off
  after each upload. `SetOptions` now replaces the prefab's Full Controller by a copy of the orb's own (the prefab's
  removed, an override VRCFury keeps). `AttachmentHooks.CarryOptions(from, to)` sets a placed prop's options as an
  older copy's are, by label and choice.
- **Orb's text no longer comes out scrambled** (`OrbInstaller.ValueLag`): VRChat could apply the sync values a frame
  before the pointer sent with them, so each block of the panel was written with the next one's letters (chunks of
  text in the wrong places). The panel reads the values two updates after the pointer (three against one), and
  everyone else's screen reads its bytes from those late values too. Orbiters desktop also never changes a pointer and
  its values in one step any more. The 3D picture page's cube no longer overlaps the 10 s timer button.
- **Orb › Menu** (`Orb/Menu`, local, no synced bits): a toggle that opens Orb's menu directly (the projected screen
  when the orb has one, else Orbiters desktop's SteamVR overlay), where the other triggers (the binding, the wake word,
  a hand on the orb) all go through Orbiters desktop. With the screen, the owner's screen shows the moment it is on: a
  screen that stays blank says the desktop does not answer. Options' files in `Assets/Orbiters/Orb/Menu 6/`.
- **Attachment options** (`AttachmentHooks.RegisterOptions`, `Options`, `AttachmentOption`): a prop offers a few
  settings once it is on an avatar, which tools show on its card (Orb's text slots in My Avatar).
- `AvatarObjectUpdate` / `IAvatarObjectUpdates` (`AvatarObjectTools.RegisterUpdates`, `UpdateOf`): a tool tells views of
  an avatar's objects (Mixer's Hierarchy) that a newer version waits for one of them (My Avatar's gallery items, MCB's
  custom base), how its update is going, and starts it as its own Update button does.
- **Avatar manifests** for Orbiters desktop (`AvatarManifestExporter`, Docs/avatar-manifest.md): while the VRChat SDK
  builds an avatar for Windows (Build & Test or an upload), after every other build step, the Toolkit writes
  `%LOCALAPPDATA%\Orbiters\Avatars\<blueprint id>.json`: the expression parameters with their final names (VRCFury's
  VF##_ ones), every control of the menu by its labels (each submenu once, rich text removed), what each sets, and tags
  from the words of labels, folders and parameter names in English, French, Spanish, German and Japanese (clothing,
  accessory, hair, body, face, nsfw kept to plain words, pose, emote, locomotion, prop, effect, system, orb), and the Orb
  prop's text sync count. A Build & Test build also writes it as `local_sdk_<avatar name>-<hash>.json`: as in contracts
  0.4.0, a name whose id had characters other than letters, digits, _ and - replaced ends with the FNV-1a hash of the
  id, so avatars named "ネコ" and "イヌ" get files of their own and Orbiters desktop finds the one VRChat loaded. An avatar
  that only gets its id at upload gets its file when the upload succeeds. Never stops or slows a build: problems are
  said in the Console. Android and iOS builds, Play Mode and hidden builds (`HiddenAvatarBuild` leaves it out) write
  nothing.
- `VrcFury.IWriter.FullController` takes global parameters (VRCFury's `AddGlobalParam`): they keep their names in the
  avatar. `HeldPropRig.Install` passes them on; `HeldPropRig.Parameter` makes ints, floats, saved and defaults too;
  `CopyFile` copies a binary template (a picture) into a prop's folder; `Quiet` is shared by the glowing props.
- `VrcParameters.OscDriven` counts `Orb/…`: optimizers leave Orb's parameters alone.
- `Editor/THIRD_PARTY_NOTICES.md` lists what ships with the Toolkit under its own license: Meshia Mesh Simplification
  (AlterMesh), KillFrenzy Avatar Text (Orb's text shader) and the fonts drawn into Orb's glyph atlas.

## 0.4.1 — 2026-10-21

- `VirtualList.SpanOf`: an item may take several rows' height (positions, scrolling and `Reveal` count rows; `TopOf`
  and `HeightOf` say where an item is). Without it the list behaves as before.
- `AvatarObjectTools` / `IAvatarObjectTool`: avatar tools (My Avatar) offer drops on an avatar root and the ReFit question
  of an item they added to views that are not their own, such as Mixer's Hierarchy.
- `VRChatLogo.Create()`: VRChat's logo (36 × 18 points, rasterized from VRChat's SVG at 1× and 2×, so it stays smooth
  where vectors or a shrunk image were jagged), to mark VRChat avatars.
- `ModelThumbnails.Get(root, key, transparent: true)`: pictures without a background, the model's alpha worked out from
  a render over black and one over white (avatar shaders' own alpha is never read). `keepOnDisk: false` keeps a picture
  for the session only (keys made of instance ids), `Release` frees one whose key changed, `HasFailed` tells a picture
  that cannot be made from one still waiting. Pictures are freed before script reloads, and those on the disk unused for
  30 days are deleted. A picture no longer destroys a renderer's own in-memory mesh (Mixer's working mesh was), nor
  changes a mesh's bounds.
- `SafePaths.ReplaceFile`: writes a small text file through a temporary file replaced in one step, so a crash mid-write
  leaves the previous file whole.
- `AvatarMeshEdits.LiveEditing` / `IsLiveEditing(root)`: tools say when they edit an avatar's meshes live (Mixer's
  sculpt and paint modes), and work that reads those meshes and can wait (My Avatar's optimization estimate) waits.
  Asking allocates nothing.
- `MeshBvh` queries (rays, spheres, closest points) walk the tree with a stack on the call's frame: no garbage per
  pointer move.
- Polygon edges show on the surface that is seen only (depth-tested): the back of the mesh and hidden parts no longer
  show through.
- `IconGlyph` gains Target, Clock, Sort, UnfoldAll, FoldAll, Star and StarFilled (Mixer's Project window).
- `SearchPattern`: what a search field asks for, a piece of text or a .NET regular expression, with an optional match
  case, as the Logger's search does (an invalid expression matches everything and says why). Mixer's search fields use it.
  An expression that times out on one value (nested repeats such as `(a+)+`) matches nothing more and says so
  (`TooSlow`): a long list costs one timeout, not one per item.
- Avatar Gizmos sit at the right end of the Scene view's top toolbar by default (a Scene view that already placed them
  keeps its place).
- `MeshBvh.RefitVertices` and `RefitTriangles` mark the triangles and nodes they visit instead of filling sets, and
  refit the whole tree in one pass when a quarter of it moved: a sculpt step over tens of thousands of vertices refits
  in about a millisecond.
- The hand screen is where its owner put it for everyone. Until now each guest worked out again on their own how it was held, from their copy of the owner's wrists as each grab reached them: the wrists' turn, the least exact part of a remote pose and multiplied by the screen's width, left every guest a different screen, and each grab added to it. Now a handle in a hand follows a frame at the middle of the palm that faces the owner's head and never turns with the wrist: held in both hands, each handle sits on its palm (everyone sees the screen exactly between the owner's hands, a guest who came late too); carried in one hand, the screen keeps where it was taken from, facing its owner, and turning the wrist no longer tilts it. Held from behind (crossed) it shows its back. Hand screens added before this version must be added again (the gallery's next release).
- FBX export: `FbxExport.Write` writes Unity meshes, skinned or not, to a binary FBX that Unity's importer and Blender read back as they were (Y up, metres). The renderer, its bones and the objects above them are written under the export root (bones as LimbNodes, the objects above them as Nulls), with normals, every UV set, colours, a material per submesh with its main texture linked, a skin cluster per weighted bone and a bind pose that gives back the mesh's bind poses, and blendshapes with their weights and in-between frames. Vertices Unity split are welded again where their place, weights and shapes agree; the file is written through a temporary file. `FbxExportMesh.Baked` applies blendshapes at their weight, as the renderer shows them (Clamp BlendShapes included), and leaves them out. An export never writes over a file the exported meshes, their bones or the rest of their avatar come from (the avatar's model, an outfit's FBX, the original of a refit, `FbxExportMesh.Sources`): it asks for another name instead. What a file can't hold is said with the warnings: a UV set's third and fourth values, a submesh without faces (the material slots after it move up). **Export FBX…** on a SkinnedMeshRenderer's or MeshRenderer's context menu exports it as it shows; `FbxExportDialog` saves where the user picks (the last folder by default), then imports and pings a file inside Assets or reveals one elsewhere.
- `RefitFbxExport` exports refitted meshes as they look fitted: the fit applied at the weight the renderer shows, the body's blendshapes kept, several meshes in one file with their shared armature, written at rest with the posing preview paused. A refit record's Inspector has **Export FBX…**; MCB, My Avatar and ReFit use the same export.
- Mesh polygon edges are drawn by the GPU: the Scene view cameras draw the selected skinned meshes a second time with a wireframe shader, so the edges follow the mesh as it shows (pose, blendshapes, Mixer's live sculpts and edits). There is no copy of the mesh, no hidden object under the avatar and no CPU work, and the Game view and builds never see them. The edges come back when another tool clears a Scene view camera's buffers. GPUs without geometry shaders (Metal) draw Unity's wireframe of the mesh as it shows instead.
- `XRayBonePickHandlers` lets a tool take the armature overlay's plain bone clicks (Mixer's Weight Paint sub-selects the bone instead of selecting it); the tool's picked bone is highlighted like a selected one. A weight paint overlay pinned by a tool switches to another bone of the same mesh by recolouring in place, without copying the mesh again.
- Live link: a connection counts only once it has sent a frame with the session's token. Strangers are never sent frames, goodbyes or the token, and are closed after 10 s; at most 16 connections can wait to prove themselves, and one more closes the oldest of them that has not sent the token, so strangers holding every place no longer keep Blender or Painter out. A client can ask Unity to prove it holds the token before sending it (frame type 0: an HMAC of its nonce and the port; Mixer's Blender add-on and My Avatar's Painter plugin do). Frames of a replaced or closed connection are dropped, a connection whose frames wait unhandled past a cap is read no further until the editor catches up, a frame check that fails closes only its connection, and endpoint files are written under a unique temporary name, still updated while a reader holds them open; a file that can't be written is said in the Console instead of stopping the link. What a connection has waiting is counted as it was read, so an `accept` check may let go of the payload it decoded. On single-client links with a closing type, the connection a newer one replaces gets its goodbye after what was queued for it (with the payload given as `replacedGoodbye`, so an app tells another client taking the link from Unity going away), then is closed, at the latest after 2 s.
- FBX: both binary readers refuse damaged files (record ends pointing backward or past their parent, nesting deeper than 64, lengths past the end, arrays announcing more than they hold or over 512 MB) instead of looping, overflowing the stack or filling the memory, and decode arrays straight into their typed arrays (about half the peak memory). A material slot past the model's materials and polygons (a damaged file) puts its polygons in the first slot instead of making billions of submeshes. `FbxModel` builds each model once under the first parent the file gives it, frees its meshes when building fails, and loads a skin without bones as a plain mesh. Record names over 255 bytes are refused when writing. TGA sizes are checked against the data before pixels are allocated; 32-bit TGAs that declare no alpha bits are opaque.
- Blender: finding Blender runs no executable on Windows or macOS (version resource, Info.plist; Linux asks once per session), release builds come before alpha, beta and RC builds, and quoted PATH entries, Steam installs known only to the registry and Blender.app are handled. `BlenderCli` quotes arguments by Windows' rules, and a job cancelled before it starts or stopped by a script reload still reports. On macOS, bringing Painter or Blender to the front no longer launches them.
- `SceneReference`: in Prefab Mode the prefab's objects answer first; names may contain "/"; instance IDs honour the scenes given. `MeshAssets.Replace` refuses meshes a model importer made, `Save` copies a mesh that is already an asset, `Clean` never gives ".", ".." or device names. Account pictures are freed before script reloads, updated in place, and a damaged cached picture is downloaded again.
- Avatar gizmos: the bone gizmo's and weight paint's hidden meshes and materials are destroyed with their objects (rebuilds, a deleted avatar or renderer, a closed scene, script reloads); nothing runs while the gizmos are off; the Avatar Gizmos window updates in place (no rebuild under a drag or on hover). Weight paint: the root bone shows its own weights, a mesh swapped under the overlay is followed and so are its blendshape weights, every influence counts, rigid meshes get a fallback bone, a pin without bones shows no weights and ends with its renderer. Moving a child bone rebuilds the bone gizmo, bone picking reads segments cached with it, and its pinned object survives script reloads. Hidden avatar copies (Painter bakes, builds) leave the gizmos' helpers out. `legacyPackages: ["orbiters.xraygizmos"]` makes VCC replace the former XRay Gizmos package with the Toolkit.
- `MeshAssets.Replace` writes the new mesh into the asset part by part (vertices, normals, tangents, colours, every UV
  set at its size, skin weights, bind poses, submeshes, blendshape frames, bounds): Unity's `CopySerialized`, used
  before, leaves a skinned mesh's vertices as they were, so Mixer's in-place commits of skinned meshes kept the old shape.
  A copy that fails puts the old mesh back instead of leaving the asset empty, and submeshes keep their base vertex (a
  16-bit mesh reaching vertices past 65535 no longer fails).
- `MeshNeighbors` welds a vertex only into a group whose normals agree with it; `MeshBvh.Mirrored` makes front-face rays and face normals right on mirrored meshes; `MeshNormals` keeps hard edges and touching sheets apart, turns tangents to the new normals and evaluates in-between frames.
- Issue reporting for every tool: `OrbitersIssueReporter.Register(new IssueReporterOptions { ToolName, SettingsKey, Namespaces, TestNamespaces, Feed, Endpoint, CanSend })` gives a tool its opt-in diagnostics (off by default, kept for every project on the computer), the text its opt-in shows (`ConsentText`) and `Submit` for a tool's own logger. One Console listener from the editor's start: a log is one tool's at most (the code that logged it; for an exception the first code outside .NET and Unity it was thrown from; the Toolkit's code counts as its caller's), never a test's. Each report is masked before it is cut, with the rules of the Logger's anonymized reports: any Orbiters token, sign-in headers and cookies, VRChat cookies, API and license keys, passwords, e-mails (%40 too), VRChat ids, names tools quote, the signed-in member's name, user and computer names, folders of paths (file and line kept), links other than Orbiters' own. The same problem goes once an hour (script reloads and Play mode remember what went), a warning repeating an exception is folded into it, at most one report every 10 s and 60 a session for each tool; exceptions are sent as errors. Sharing turned off in another project stops an open editor before its next report. A long log is cut before masking at a space, else at a character no token holds, else before the token, never inside one.
- Kept fits work: the build's "ReFit fit: name" shapes are the difference between a kept fit and the worn one as they rest with their fit shape applied (`RefitRecords.FitShape`); every kept fit was an empty shape before. A kept fit skinned to another armature is left out with a warning, `UseFit` drops the body shrink planned under the fit worn before, and `RefitRecords.ReplaceMesh` gives a refitted renderer its own copy of its mesh (the record and the worn kept fit follow).
- Body shrinks apply only to the body mesh they were planned on, checked when the build starts, before other steps copy meshes: a shrink planned on another version of the body, or on a mesh since deleted, is left out with a warning, and two shrinks on one body both apply (only the first did). Two pieces of the same name each keep their shrink, and a clip animating several objects above the clothing sets the shrink only where all of them show it.
- Refit cache: entries are kept per engine version (`Assets/Orbiters/ReFit/Cache/<engine>/`); the entries of the engine's earlier versions and of the former flat layout go once the new version loads or stores a result (the meshes they point to stay, and so does any other asset kept in the cache folder). An engine can say what else it reads (`IRefitInputs`; ReFit reports the body shapes the outfit's toggles set), so editing a toggle refits again instead of bringing back the old mesh.
- `OriginalArmature` keeps the 32 most recent custom bases in Library and reads one again after its model file was reimported; an original that could not be read is not tried again (half a minute each) until a custom base changes, script reloads and Play mode included, the order of use is saved with the entries, and a broken entry no longer leaves a hidden armature behind.
- A refit record's restore also shows again the objects the refit hid, such as an old armature of a prefab it could not remove (`RefitTransformState.activeCaptured`/`active`), so its PhysBones and contacts work after any tool's reset. The original base preview (`RefitGhost`) is made of hidden copies of the original's objects and renderers instead of an instance of it: the open scene is no longer marked changed, and none of the original's scripts run.

## 0.4.0 — 2026-10-19

- Avatar gizmos are part of the Toolkit: the bones, weight paint, mesh edges and avatar budget overlays, their Scene toolbar and the Avatar Gizmos window (Tools › Orbiters › Avatar Gizmos) moved here from their former package, which no longer exists. Namespaces `Orbiters.Toolkit.XRay` and `Orbiters.Toolkit.Editor.XRay`; settings and overlay ids unchanged.
- Mesh editing primitives for Mixer and the other tools: `MeshNeighbors` (vertices welded by position, or also only where their normals agree and caller keys match, their edges and triangles), `MeshBvh` (triangles and vertices in a sphere, rays, closest points, partial refits), `MeshAssets` (meshes saved as native assets: `Save` writes a new file and never takes the place of another mesh's file of the same name, `Replace` updates an existing asset in place so what points at it follows), `VirtualList` (the Logger's list of pooled fixed-height rows).
- `BlenderInstalls` finds the Blender executables (moved from MCB; the chosen one is EditorPrefs `Orbiters.Blender.Executable`) and `BlenderCli` runs Blender without its window on a thread of its own, with a log file and the result back on the main thread.
- `LiveLinkJson` and `LiveLinkEndpointFile` for live links' JSON payloads and endpoint files.
- `SceneReference.Resolve` names a scene object the way MCP tools take it: an instance ID, or a hierarchy path (`Root/Child`, `Scene:Root/Child`), refusing a path several objects answer to.
- Icons: Sculpt, Weights, Armature, Join, Render, Blender, Hierarchy, Eye, EyeOff, Link, Undo, Redo, Layers, Search, Trash, Settings, Minus.
- The live link server writes on a thread per connection (a peer that is busy rendering or stopped at a breakpoint no longer freezes the editor; one that stops reading for 512 MB is cut off), keeps collapsed frames in their place among the others (positions sent before a whole mesh are handled before it), and on single-client links lets a new connection replace the current one only once it sent a frame with the session's token, announcing the replaced one's departure first. A wrong token now ends a connection before its payload is read.
- Weight paint with only a mesh selected shows a bone's weights instead of nothing: the bone it showed last, else the one weighing most on the mesh (`XRayWeightPaintService.MostWeightedBone`), and its status says how to pick another.
- The weight paint overlay no longer leaves its hidden copy of the mesh behind each time it is rebuilt (every bone switch kept one in memory until Unity restarted).
- `XRayWeightPaintService.Pin` shows the weight overlay for a renderer and bones (or per-vertex weights of a tool's own) whatever the selection, `Repaint` recolours it in place, `Unpin` lets the selection drive it again.

## 0.3.24 — 2026-10-18

- Body shrinks for refitted clothing: a refit record can keep skin of the body to move in while the clothing is worn
  (ReFit plans it from its motion check). At upload the body gets it as a blendshape on a copy of its mesh, on while the
  clothing shows: every animation turning the clothing or an object above it on or off sets it too. A new fit drops it.
- Kept fits: a refitted renderer can keep several fits (`RefitRecords.KeepFit`, `UseFit`, `RemoveKeptFit`). At upload
  each kept fit other than the worn one becomes a blendshape of the worn mesh, and VRCFury toggles under "Fits/piece"
  pick one at a time (`RefitKeptFitsBuild`). Fits kept for another custom base are dropped with it.
- `orbiters_editor_window` captures a hidden window a script opened with `EditorWindowHiddenHost.Open` and set up (a
  page in a given state) as it is, by its window ID, and leaves it open for the script.
- The original base shown to line clothing up before a refit (`RefitGhost`) finds its hips by name when its model has no
  humanoid avatar (MCB's Masculine Canine imports as Generic: the line-up did nothing), and gives its root (`Root`) to fit
  the clothing's armature onto.
- Clothing fitted onto an avatar on a custom base (`AttachmentFit.Fit`, My Avatar and gallery installs) goes onto the
  original base's armature, placed over the avatar's body (`OriginalArmature`), instead of the custom base's: a custom
  base can move its bones on the same body, and the Rexouium FishingOutfit sat 8 cm high on the Ultipaw (its hips, spine
  and legs stand 8 cm above the Masculine Canine's). Clothing made for the original stays where it is, fitted all the
  same so the build keeps its offsets from the avatar's bones (a creator's VRCFury link put them on the bones); clothing
  that matches the custom base's own armature is left alone. The original's armature is read once per custom base
  version (resolving the original can import its model, half a minute for a body with hundreds of blendshapes) and kept
  in `Library/Orbiters/original-armatures.json`.
- A hidden copy of an avatar (`HiddenAvatarCopy`) made from an object that is never saved (a tool's own hidden copy, like
  the bodies ReFit's replays pose) stays in its preview scene: it landed in the open scene and stayed there.
- Garments named in one word are recognized for coverage (`ClothingCoverage`): a joined name is the thing its last word
  names, so `FashionSuitJacket` is a jacket and `ShirtButton` still a button. Vests and waistcoats count too, fitted
  between the shirt and the jacket. The FashionSuit's pieces were none of them: refitted onto the Ultipaw, its jacket
  and trousers were never covered and the arms and calves came through.
- A refit can say the clothing was made for another body and resized onto the avatar (`RefitJob.MadeForAnotherBody`,
  `RefitBatch.MadeForAnotherBody`, part of the cache key; `ClothingCoverage.Resized` tells it from the recorded fit).

## 0.3.23 — 2026-10-16

- Clothing dropped on an avatar (My Avatar) is placed on its skeleton and left as it is, clipping included, for ReFit to
  fit with a refit shape. It is no longer pushed out of the body by the volume of its bones, which made it blocky and
  was never asked for. Clothing a drop fitted that way keeps its record: cancelling its fit puts its own mesh back.

## 0.3.22 — 2026-10-13

- **Build-copy optimization** (`Orbiters.Toolkit.Editor.VRChat.Optimization`), shared by My Avatar's Optimization
  section. Each step changes a build copy only, never edits a source asset, and returns what it changed:
  - `AvatarUsage`: what a built avatar animates (playable layers, Animator components and legacy Animation components,
    each read from the object its clips' paths start at: shown objects, switched components, moved transforms, swapped
    materials), which components point at what, its humanoid bones and the parameters its animators read
    (`ReadsInPlayableLayers`: only the playable layers get VRChat's parameters). Editor-only components don't count,
    unless the caller says so for an avatar not built yet, or they belong to an optimizer that builds later (Avatar
    Optimizer, d4rk Avatar Optimizer: `AvatarOptimizers`); anything near those is left alone (`Pending`), and
    `BlendShapePruning.Used` keeps every shape of a renderer holding one. Shared by the steps: `AlwaysShown`, `Driven`
    (PhysBone chains and what constraints move, `PhysBoneChain`, `ConstraintTargets`), `Animators` and `Clips`.
  - `ComponentCleanup`: objects that start hidden and that no animation shows, components that start disabled and that
    no animation turns on, PhysBones that move nothing (the branches they ignore still follow them), PhysBone colliders
    no PhysBone lists, PhysBones that stop following animations nothing plays on their bones, and end bones that become
    a PhysBone's endpoint position (not on a chain two PhysBones share).
  - `MeshOptimization.PruneBones` drops unweighted bones from a skinned mesh's bone list (VRChat ranks every listed
    bone); `MergeSlots` drops empty submeshes and merges the ones sharing an opaque or cutout material (one that blends
    keeps its slot, whatever its queue), unless an animation swaps the renderer's materials. Both leave a mesh weighted
    to bones it doesn't list alone, and indices past 65535 get a 32-bit index buffer.
  - `MeshMerge.MergeStill` merges the skinned meshes always shown into the one animations use, per group drawn the same
    way; a mesh bound in the same rest pose moves into its mesh space and shares its bones. Static props (mesh
    renderers always shown) join too, skinned to their own transform. A mesh whose materials have a property the merged
    renderer's animations set stays apart, and every mesh does when they set its own settings (shadows, probes);
    renderers a particle system emits from, or a build tool still works on, stay as they are. `MergeTogether` merges the
    skinned meshes every animation shows and hides together (the same toggle keys in every clip, the same state at
    rest); their animated blendshapes join the merged mesh and the build's copies of the animations move their curves
    onto it. A part Unity drew mirrored otherwise than the merged renderer (a prop scaled -1) keeps its faces outwards
    and its normal maps; normals, tangents and their blendshape offsets come out of the bones as before; the culling box
    is measured from each part's own box (hidden renderers included).
  - `MaterialCleanup` gives renderers copies of their materials without the textures their shader doesn't declare (the
    main texture stays, for VRChat's fallback shader), and (`MergeDuplicateTextures`) pointing at one texture where two
    are the same file imported the same way, package cache files included.
  - `MenuIcons.Shrink` copies the expression menus holding an icon larger than 64 px and points them at copies of the
    icons at 64 px at most, compressed for the platform (BC7, sides a multiple of 4; ASTC 6x6 on Android).
  - `ShortNames.Rename` gives objects and the parameters only animators use the shortest names, rewriting the build's
    copies of the animations (paths, masks, conditions, blend trees, parameter drivers, audio from its own animator),
    PhysBones and contacts. The humanoid model's bones, the root's `Body`, VRChat's parameters and the menu's (VRChat's
    default menu's without custom expressions) keep their names; no new name lands on a binding left dangling, and a
    step that fails puts back the names it changed.
  - `MeshParts` takes a mesh apart and back together: `Subset` (the triangles chosen, with every attribute, weights,
    bind poses and the blendshapes asked for; lines and points stay out), `Replace` (part of a mesh swapped for another,
    blendshapes joined by name in one pass, lines and points kept), `Join` (blendshape frames at every weight either mesh
    has one at), `DropEmptySubmeshes`, `ShapeAt` (a blendshape's offsets at a weight), `Reach` (the vertices each
    blendshape moves) and `Within` (whether points lie on a surface within a distance, and shade the same).
  - `BodyCover.HideCovered` removes the body's triangles that opaque clothing worn all the time hides from every side
    (rays from each vertex through a grid of the clothing's triangles). Only clothing that moves with the body covers it
    and only skin that moves with the skeleton goes: what a PhysBone, a constraint or an animation swings (skirts, coat
    tails, long hair, a tail) is left out. Meshes at any scale are measured where they show.
  - `StaticBones.Fold` skins bones that never move against their parent through the nearest bone that can move, with
    bind poses that keep every vertex in place, so meshes list fewer bones.
- `Parameters.VrcParameters.Protected`: the parameters VRChat and VRCFaceTracking set by name, shared by every build
  step that changes or renames parameters; `PhysBoneSuffixes` and `DefaultExpressionParameters` too.
- `AttachmentAnimationBuild.Owned`, `OwnedByAnimator` and `Clips` give the build's copies of the avatar's animation data
  to other build steps. Legacy Animation components' clips are copied too, so they follow moved and renamed objects
  (`Animations.LegacyAnimationClips` reads and replaces them).
- `BlendShapePruning.Prune` also works on meshes imported without Read/Write, outside Play mode.
- `AvatarBounds.Unify` gives every skinned mesh that moves with the hips the same culling box: around all of them as
  they stand, with room for the limbs, the hips as root bone. Meshes on a bone a constraint moves (from anywhere: a
  VRChat constraint's target), or below one, keep their own. The room never takes a box that fits VRChat's Excellent
  bounds (2.5 m) out of it.
- **AlterMesh** (`Orbiters.AlterMesh`, editor only, needs Burst 1.8.7 and Collections 2.1.4, the VRChat SDK's own, and
  Mathematics 1.3.2, whose geometry types it uses): Orbiters' mesh simplifier, derived from Meshia Mesh Simplification
  3.2.0 (MIT, `Editor/AlterMesh/LICENSE-Meshia.md`). Fixed there: blendshape normal and tangent offsets are no longer
  normalized at each collapse, empty submeshes no longer crash the k-d tree, empty bit ranges are no longer counted
  (Collections throws). See `Editor/AlterMesh/README.md`.
- `HiddenAvatarBuild`: the avatar built as an upload builds it, without uploading: a copy in its own preview scene put
  through every SDK build step in order, one per editor frame (Poiyomi's lock, the build cache, VRCFury's blueprint hook
  and the caller's skips left out), released before scripts reload.
- **Checks** (`Orbiters.Toolkit.Editor.VRChat.Checks`), run on a built avatar, each returning `AvatarIssue`s (level,
  title, what happens in game and how to fix it, where): `MenuAudit` (pages over 8 controls, controls on missing or
  unread parameters, empty submenus, synced parameters no animator reads), `AnimatorFlow` (parameters a layer reads
  that its controller doesn't have, states nothing leads to), `BodyHoles` (shrink shapes left on while clothing can
  come off) and `QuestParity` (desktop shaders, components Quest removes, the Quest rank; `Compare` and `Counterparts`
  for the other platform's version of the same blueprint).
- `VrcFury.IsComponent` and `ModularAvatar.IsComponent` tell a build tool's components apart (Modular Avatar's check
  was repeated in two places). `VrcFury.Find` remembers the types it did not find too: without VRCFury, a budget no
  longer searches every assembly each time it asks. `AvatarParameterBudget.IsSynced` is public.
- The build cache (`AttachmentAnimationBuild.Keep`) stores textures in the binary asset with meshes.
- `BlendShapePruning.Keep`: blendshapes a caller needs on an avatar it is building (My Avatar's VRM export keeps the
  shapes its expressions set): every prune counts them as used. `Prune` also takes the names to keep.
- `HiddenAvatarBuild.Start` takes a step that changes the copy before the build (a setting the build reads).
- `SurfaceFollow` no longer throws on a mesh renderer without a mesh filter.
- `SafeArchive` stages into, and `SafePaths.ReplaceDirectory` sets the previous folder aside as, folders ending in "~"
  (`SafePaths.HiddenSibling`): one that cannot be deleted is never imported beside the folder it was for (duplicate
  GUIDs). `SafePaths.RequireLength` also refuses folders over the 247 characters Windows makes, and its message names
  the project only when the path is in it (with a shorter example only when one is shorter). `IsDeviceName` also knows
  COM0, LPT0, the superscript COM¹²³ and LPT¹²³, CONIN$, CONOUT$ and "NUL .txt".
- `OrbitersMenuIcons` writes each icon with the same GUID in every project, so menus and gallery packages made in one
  find them in another; a picture already there is never overwritten, and one written inside an asset-editing batch is
  set up when it imports.
- Props in one folder no longer overwrite each other's parameters, menu and sparkle (`HeldPropRig` numbers a taken name).
- `AnimatorControllerCopy.Tree` keeps a direct blend tree's normalized blend values.
- Window captures: a hidden copy whose Inspector could not be locked is closed, hidden copies are closed before scripts
  reload (and any a reload kept, after it), and a dialog a hidden window opens is shown, not hidden with it.
- `AssetPaths.FileName` is gone: `SafePaths.FileName` makes every file name from a display name (a device name gets a
  "_" in front, at most 64 characters).
- `ShortNames.Rename`: a step failing after the animations followed the new names moves them back with the objects.
- Tests: the prop and attachment tests remove the menu icons folder they made (`SharedFolders`); the optimization and
  blendshape tests build their avatars in preview scenes; `SafeArchiveTests` skips the path-length cases when the
  temporary folder's path is too long for them.
- `ProjectReferences.ReachableAsync` / `Reachable`: every project file some files use, directly or through other files,
  read a slice per editor tick and kept per file in Library (only changed files are read again); a file missing for a
  while counts again once it is back. `UnityPackageFiles.ReplaceAssets` swaps files inside a `.unitypackage`.
- `VrcFury.LeaveWriteDefaults` and `VrcFury.MobileAlignment`, shared by hidden builds and MCB's validation.
- A hidden build frees what its steps made for the copy (controllers, clips, menus, materials, meshes, textures)
  when it is released.
- `AuthenticationService.RegisterAuthForEnv` (clipboard Magic Sync) is gone: tools sign in through the browser login.

## 0.3.21 — 2026-10-11

- `SafeArchive.Extract` stages into `<folder>.building-<8 hex>` and `SafePaths.ReplaceDirectory` sets the previous
  folder aside as `<folder>.trash-<8 hex>`, both named by `SafePaths.Sibling` (they added a whole 32-digit GUID). Their
  files must fit the 259 characters Unity can use on Windows (`SafePaths.MaxPathLength`; Unity is not long-path aware,
  whatever the system setting): MCB downloads failed with "Could not find a part of the path" in ordinary project
  folders.
- `SafeArchive.RequireRoom` and `SafePaths.RequireLength`: an archive whose files would not fit is refused before
  anything is written, with a message saying how many characters shorter the project's folder path must be. Callers
  that know the files can check before downloading (MCB does).
- A staging folder that cannot be deleted no longer hides why the extraction failed.

## 0.3.20 — 2026-10-10

- `OrbitersMenuIcons`: Orbiters' default VRChat menu icons, written once as 64 × 64 PNGs into
  `Assets/Orbiters/Menu Icons/` (a white dot for toggles, a gauge for sliders, a folder for folders, an arrow for
  **Next** pages). The toggles `VrcFuryWriter` makes, and the held props' toggles, buttons, sliders and folders
  (`HeldPropRig`: drawing pen, hand screen), get them.
- `AvatarParameterBudget`: `BuildRemovedParameters` is also asked about the avatar's own expression parameters (with its
  `VRCAvatarDescriptor`; a Full Controller's global parameters left out there are left out too), and
  `BuildRemovedToggles` tells which VRCFury Toggles a build leaves out. My Avatar uses both for items left out of the
  upload. `VrcFury.IsGlobalParameter` reads a Full Controller's global parameter rules.
- `AnimatorControllerCopy.Tree`: a new blend tree with another's settings and children. Unity asserts when a blend tree
  holding other trees is instantiated; the controller copy and My Avatar's face tracking build copy trees this way.
- Refit coverage (`ClothingCoverage`) counts underwear, swimwear and one-piece garments as body clothing by name
  (jockstrap, briefs, boxers, thong, bra, speedo, trunks, lingerie, bikini, swimsuit, leotard, bodysuit, onesie,
  jumpsuit): underwear is fitted first, as the innermost layer, and one-pieces with shirts and trousers.
- `OrbitCamera` and `OffscreenPreview`: the 3D previews' camera (turn, move and zoom with the pointer, values gliding to
  their targets, view presets with `LookFrom`) and their render into UI Toolkit textures, which always restores the
  editor's lighting after `PreviewRenderUtility.Render`. MCB's version comparison and ReFit's stage use them.

## 0.3.19 — 2026-10-09

- **Hand screen** (`HandScreenInstaller.CreatePrefab`): the video the world is playing (its `_Udon_VideoTex` global
  texture, shared by ProTV 3 and other players), letterboxed on a 16:9 screen with a handle on each side. One hand
  carries it, both hands stretch it: each handle follows its own wrist and constraints work the screen's corners out
  from the two handles (no animator maths). Let go, it stays in the world. Only its owner moves it; 4 bits of
  parameters; needs VRCFury. Without a video in the world it shows a quiet play sign, as on its back. The prefab
  carries its own shader and no Orbiters script (Toolkit releases ship without .meta files, so their GUIDs differ in
  every project): a gallery copy works wherever it is installed, and Toolkit finds it by its left handle's grab.
- **Hand screen**: the second hand takes the other handle while the first hand still holds its own. The first hand's
  native grab used to have to end before the second hand counted, which on Touch only happens when the trigger is
  pulled into a full fist, so with grip alone only one handle at a time could be held. Each one-hand hold now knows
  which handle it holds and only the other handle's grab brings in the second hand.
- **Drawing pen**: ink stops as soon as the trigger eases off past half way (it starts above 0.5 and stops below 0.4
  of the fist's trigger pressure), instead of only once the trigger was almost released (below 0.2).
- `UnityPackageReader`: the one `.unitypackage` reader of every Orbiters tool. It streams the archive once, checking its
  structure as `UnityPackageIndex` did (tar checksums, record names, sizes, the expanded size and entry count, metadata
  GUIDs, one GUID per path), and hands each record to the caller. `UnityPackageIndex`, `UnityPackageFiles.AssetHashes`
  and `CopyEntries` (which now check the archive too, and copy long-name records as stored), MCB's source FBX extraction
  and the Unity Package Manager all read through it. `Options.FirstLinePathname` (and `UnityPackageIndex.Read`'s
  `firstLinePathname`) reads a pathname's first line, as Unity's importer does, for tools that only read a package.
  `UnityPackageIndex.ExpandedBytes` gives the archive's decompressed size: a package nested in a ZIP is checked in one
  pass instead of being decompressed twice.
- The About window's third-party cards show "Author’s terms" for a project whose notice says it publishes no license
  (GoGoLoco in My Avatar), instead of a "License" chip with nothing behind it.
- `OrbitersAccountElement` takes `confirmLogout` to ask before signing out of every tool (MCB's account row uses it).
- The Orbiters settings window names the development server `127.0.0.1:4100`, the address the tools use.
- `HeldPropRig`: the drawing pen's grab rig (PhysBone pickup, wrist hold measured where it was grabbed, world-fixed
  drop) and controller pieces, shared by the pen and the hand screen.
- **Drawing pen** (and the hand screen): follows the hand the moment it is grabbed (the frozen "catch" lasted a whole
  0.2 s clip, since an exit-time transition waits for the clip to loop), lets go as soon as the hand relaxes (Neutral or
  Open) instead of only fully open, waits in front of the face wherever the owner looks (its spawn point follows the
  head bone while it is off) and tells which hand took it from further away. Pens made before keep their behaviour
  until made again.
- **Drawing pen** carries no Orbiters script any more (the `OrbitersDrawingPen` marker stays only so older pens load
  without a missing script): a gallery pen is found by its grab and fitted in any project, where it used to arrive with
  a missing script and never follow the hands (Toolkit's script GUIDs differ in every project). `DrawingPenInstaller.Bind`
  takes the pen's transform; `Find` is replaced by the internal `FindAll`.
- `AttachmentHooks.Register(hook, claims)` and `AttachmentHooks.Fits`: a package of a prop a hook fits declares the
  Toolkit version it needs (My Avatar 0.9.7's gallery packager), even when it refers to none of Toolkit's files.
- Item pictures of props built from Unity's primitives (the drawing pen, the hand screen) no longer log "Destroying
  assets is not permitted": only the meshes baked for the picture are freed.

## 0.3.18 — 2026-10-08

- `AvatarParameterBudget` counts a face tracking template apart: objects an `AvatarParameterBudget.FaceTrackingOwners` hook claims (My Avatar's) have their parameters in `ParameterBudget.FaceTrackingBits`, taken out of `AvatarBits` and, when the custom base also owns them, out of `CustomBaseBits`. `BudgetCount` gets a `FaceTracking` share.
- `AnimatorControllerCopy`: a deep copy of an animator controller in memory (layers, state machines, states,
  transitions, behaviours, blend trees and clips, with an optional clip rewrite), freed with `Destroy()`.
- `BlendShapeLinkEngine` also applies links to in-memory controllers on the descriptor (build previews such as My
  Avatar's face tracking test), and only saves assets when an asset controller changed.
- `OrbitersVectorLogo` draws relative SVG path commands and H, V and S, reads the viewBox (also one not starting at 0)
  and fills each path with its own `fill` colour (white, or `Fill`, without one).
- `VrcAvatarCard` and `VrcCardStage`: VRChat's in-game avatar card (badges, two-line name, author) in its menu
  backdrop, moved here from My Avatar so other tools show the same card (Unit Git's upload releases). The host sets the
  platforms, the performance warning and the author; `VrcCardStage.SetAmbient` lights the backdrop with the thumbnail's
  colours. `VrcLinks.AvatarPage` gives an avatar's page on vrchat.com.
- `RefitRecords.Discard` takes a refit back for every tool: it removes the record and tells the custom base provider
  (`ICustomBaseFits`, implemented by MCB) to forget the fit it saved for the version. `RefitRunner.IsRunning` / `Hold`
  tell whether a refit is at work on an avatar. The temporary original-base import is shared by reference count.
- Mode-lock ordering: the clothing links (ReFit -8960, Follow Body Blendshapes -8950, accessories -8900) run after MCB
  locks its modes (-8970), so they copy the locked values.

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
the animated background; `OrbitersVectorLogo` draws SVG paths (M, L, H, V, C, S, Z, absolute or relative) in their own fill colours.
These editor-guarded services live in `Orbiters.Toolkit`, allowing components with
editor-only helpers to reference them. Player builds contain none of this editor
logic. `Orbiters.Toolkit.Editor` continues to own posing and screenshot utilities.
The package also depends on Unity's Newtonsoft JSON package.

## Mesh comparison

`Orbiters.Toolkit.Editor.Meshes` compares versions of a model without importing them: `FbxReader` reads a binary FBX
(meshes placed in the file root's space with Unity's axes and scale, blendshape signatures, bones, material names; with
`FbxReadOptions.Render` UV-split vertices and one submesh per material slot, with `FbxReadOptions.ShapeOffsets` each
blendshape's offsets). `MeshComparison` measures how far each vertex moved, index by index or to the other version's
surface when the topology changed, and describes parts as moved, reshaped, re-materialed, added or removed. Mixer Git's 3D
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

Select an avatar or one of its bones and enable **Mirror** in the Avatar Gizmos
Scene-view toolbar or the Avatar Gizmos window. Rotate or move one paired bone in
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

### Stick to the skin

`OrbitersAttachment.stickToSkin` (the **Stick to the skin** switch My Avatar shows on a small accessory's card, an
`AttachmentOption` registered by `SkinStick`) makes a small accessory on the skin follow the skin under it rather than
one bone. `SkinStick.Find(attachment)` measures where it sits: the accessory (a My Avatar prop on one bone, or a
creator's single non-recursive VRCFury Armature Link that leaves it where it stands; never clothing or something a
constraint holds) must be at most a tenth of the avatar's height across, and its closest vertex within 1.5 % of the
height from one of the avatar's own skins. The skin under the touching part (the mean of its vertices' closest skin
points, within 3 mm of the closest) gives a triangle; its corners' bone weights, blended by where the spot lies in it,
are the bones and shares a point of skin there moves by (up to 8, 1 % or more). Skins are baked at their current pose
and weights into a BVH, kept until a bone, a weight or the mesh changes.

At build (-8960, after VRCFury placed the accessories) each stuck accessory gets a locked VRChat Parent Constraint on
those bones, offsets measured from each bone's position and rotation without its scale (as the constraint applies them,
and as Haï's Skinned Mesh Constraint computes them, rather than its Activate), so it stays where it stands and then moves
as linear blend skinning moves the skin; and an `OrbitersSurfaceFollow` so the body's blendshapes there move it too.
`AttachmentFollow.Links` gives it a `FollowLink.Blend` of the same bones, which the posing preview, Substance Painter's
pose and ReFit's motion check apply. An accessory moved off the skin since keeps its switch and follows its bone, with a
warning in the build log.

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

## N4N0

`OrbInstaller.CreatePrefab(folder, textSync = 8)` builds N4N0, the Orbiters companion, into a folder below Assets/ (the
gallery packages each version in a folder of its own): the prefab, its controller, menu (**N4N0** › Show N4N0, Talk) and
parameters, its two shaders and its glyph atlas (copied from the Toolkit's `Orb.shader.txt`, `OrbText.shader.txt` and
`OrbText.png.bytes`, so the package refers to no Toolkit file), meshes and materials. VRCFury merges it (a Full
Controller whose parameters are global); `Bind`, run by `AttachmentHooks` when it is attached, ties it to the avatar's
head bone, turns it with the avatar and sizes it by the avatar's eye height.

N4N0 was called Orb. Its code keeps that name (`OrbInstaller`, `OrbState`, the `Orb*` template files, `IsOrb`, the
avatar manifest's `orb` keys and tag); everything it puts in a project or on an avatar says N4N0: the prefab and its
object, files and folders (`Assets/Orbiters/N4N0/…`), shaders (`Orbiters/N4N0`, `Orbiters/N4N0 Text`, `Orbiters/N4N0
Screen (…)`), menu and parameters (`N4N0/…`) and contact tags (`N4N0PointerL`/`R`, `N4N0Eye`, `N4N0Gaze`). The sphere
on the tether's tip is `Ball`.

What Orbiters - N4N0 sends is the contract in Orbiters contracts' Docs/orb.md: `N4N0/Visible` (saved, on by default),
`N4N0/State` (`OrbState`: idle, listening, thinking, talking, sleeping, happy, annoyed, surprised), `N4N0/Text/Visible`,
`N4N0/Text/Pointer` and `N4N0/Text/Sync0`… (a block of 4, 8 or 16 of the panel's 128 cells per pointer; 255 clears), and
it reads `N4N0/Talk` (the menu's button) and `N4N0/Touch` (the owner's hands, local). Synced bits: 1 + 8 + 1 + 8 + 8 per
sync value.

- `Shown/Head` follows the head bone (position constraint) turned like the avatar (rotation constraint on the avatar
  root): N4N0 and its panel stay where its owner can look at them however the head turns or leans.
- `Shown/Head/Tether`: a two-link PhysBone (pull 0.15, momentum 0.8, no gravity, 20 % immobile) whose tip carries the
  ball (`…/Ball`, left out of the chain): it lags, overshoots and settles; anyone can grab it and let it spring back.
  N4N0 rests beside the head, 26 cm to the right of the eyes and 12 cm before them. `Ball/Hop` jumps when surprised;
  `Ball/Hop/Body` draws the core and its halo (two materials of `Orbiters/N4N0`: face, blinks and bob on the GPU; a
  ring while thinking); `Ball/Touch` is the contact.
- `Shown/Head/Panel`: one skinned mesh for the plate and the beam (`Orbiters/N4N0 Text`), its pivot the plate's corner
  nearest the face, 6 cm right, 18 cm below and 50 cm before the eyes, facing ahead. The shader shrinks the plate to the
  text, flips it for readers behind it and in mirrors, and grows it (up to 1.6 times, from 3 m) for viewers further than
  arm's length, away from the face.
- Layers: Base (N4N0/Visible), State (each state sets everything it shows; any other value is idle), Text visible, Text
  block (the pointer as a float) and Text: one direct blend tree (Write Defaults on, which VRCFury keeps) holding each
  cell in an animator parameter, so the cells stay whatever the avatar's Write Defaults.
- **Light** (the N4N0 card's option, `SetOptions(..., light: true)`): `…/Ball/Hop/Light`, a point light without shadows
  reaching 5 m at 1.6 m eye height (`Bind` scales it), off until `N4N0/Light` (synced) turns it on in the colour
  `N4N0/Light/Color` (synced, saved: White, Warm, Sunset, Pink, Violet, Blue, Cyan, Green) at `N4N0/Light/Level` (synced,
  saved, 0.6 by default: intensity 0.15 to 3); N4N0 takes the light's colour (no other layer animates `_Color`), and
  each of the Light layer's states sets everything it animates, off included, so any Write Defaults leave nothing
  behind. **N4N0 › Light** in the menu: the toggle, a Brightness radial and the eight colours. 17 synced bits; any light
  makes the avatar's PC performance rank Poor, and Quest leaves it out. Orbiters - N4N0 sets it from N4N0's menu too.
- **Screen version 3** (`N4N0/Screen/Version`, local, unsaved, 3 by default): the page pictures have 13 slices, for the
  six-tile Home (page 10), Music (11) and Light (12); Orbiters - N4N0 shows a screen without the parameter the four-tile
  Home. Changing N4N0's options swaps an older screen's material for the current files (`ScreenCurrent`: the shader
  for its thumbnail size and pages with this version's slices). Shared files in `N4N0/Screen 6`, options in `N4N0/Menu 10`.
- Seen from behind (by the people around, or in a mirror), N4N0's screen shows its back: N4N0 over "N4N0 menu" and "look
  at the other side", slice 0 of the pages, drawn by Orbiters - N4N0 as its SteamVR overlay's back. Which side the
  camera is on comes from its place, not from the face drawn (VRChat's mirrors turn that round). The shader carries its
  files' revision (tag `OrbScreen`, 6): changing N4N0's options swaps a screen of an older one for these. Elements
  the desktop greys out (memory bytes 21 to 23: fast walk while VRChat runs in desktop mode, "Only in VR") fade into
  the card.
- A **Look** option on N4N0's card (`SetLook`, `RetroOf`): Orbiters' own, or **Retro**, a cassette-futurism terminal
  (contracts' Docs/orb.md, Retro look): its own page pictures (`OrbScreenPagesRetro.png.bytes`, cut corners, bracketed
  icons, monospace capitals, a dot grid, a double frame), the phosphor palette, hover outlines, pressed elements
  inverted, chosen ones in brackets, scanlines, and thumbnails as 96 × 54 green dots in four dithered steps, read from
  the same bytes as Orbiters' own look. Text arriving a block at a time reads as typed out. It is the screen's material
  alone ("N4N0 screen retro" beside each size's), kept through the other options: no parameter, no synced bit. The
  option shows a picture of each look (`AttachmentOption.ChoicePictures`, from `orbiters-desktop --orb-menu`), and
  Orbiters - N4N0's overlay has the same look as a setting of its own.
- **Pointing** (screen version 4, contracts' Docs/orb.md, Pointing; `AddPointing`): a VRChat raycast (`VRCRaycast`,
  "Hit Worlds And Players", 0.6 m scaled with N4N0) from each index fingertip along the finger meets a thin solid box
  on the screen's face, shown to its owner as a beam and a bead (N4N0's own mesh and material); the bead's local
  contact (tags `N4N0PointerL`/`N4N0PointerR`, radius 2 mm) on three touch points per hand tells Orbiters - N4N0 exactly
  where it is, and the fingertip within 2.1 cm along the ray presses (`N4N0/Screen/Ray/L`, `R`: `_Hit`, `_Ratio`, local).
  The pointers, the box and the raycasts exist only for the owner, in VR, while the screen is open (layers Pointing,
  Pointer L, Pointer R). `Bind` holds them to the index fingertips (the last finger bone's child, else as far past it as
  that bone is long); N4N0's options changing gives an older screen its pointers. Finger contacts no longer touch the
  screen. Shared files in `N4N0/Screen 6` (revision 6), options in `N4N0/Menu 10`.

## Avatar manifests

`AvatarManifestExporter` (a VRChat build step at `int.MaxValue`, after VRCFury's parameter compressor, Modular Avatar /
NDMF and every Orbiters step) writes `%LOCALAPPDATA%\Orbiters\Avatars\<blueprint id>.json` while the SDK builds an
avatar for Windows: Orbiters contracts' Docs/avatar-manifest.md, read by Orbiters - N4N0 when VRChat loads that avatar.
`AvatarManifest.Read(builtAvatar, id)` takes the expression parameters as built (VRCFury's `VF##_` names included) and
every control of the menu by its labels (rich text removed, each submenu once at its first place), with what it sets and
its tags (`AvatarManifestTags`: words of the label, its folders and its parameters' names, in English, French, Spanish,
German and Japanese); `orb.text_sync` when the N4N0 prop is there. Build & Test also writes
`local_sdk_<avatar name>-<hash>.json` (`AvatarManifest.FileName`: characters other than letters, digits, _ and - become
_, and then the FNV-1a hash of the id follows, as Orbiters - N4N0 names it); an avatar given its id only at upload gets
its file when the upload succeeds. Problems are said in the Console and never
stop a build; Android and iOS builds, Play Mode and `HiddenAvatarBuild` write nothing.

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
- `delay_ms` (0..8000) waits that much longer before the capture: an animation settled,
  or a state reached a moment after the window opens.
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
