# AlterMesh

Orbiters' mesh simplifier: Burst jobs that collapse the edges whose removal changes a mesh's shape the least (quadric
error), keeping its submeshes, UVs, colours, bone weights and blendshapes. My Avatar's **Fewer polygons** step uses it.

AlterMesh is derived from [Meshia Mesh Simplification](https://github.com/RamType0/Meshia.MeshSimplification) 3.2.0 by
Ram.Type-0 (MIT, see `LICENSE-Meshia.md`). Changes from Meshia 3.2.0:

- Namespace `Orbiters.AlterMesh`, assembly `Orbiters.AlterMesh` (editor only); Meshia's editor, NDMF and localization
  parts are left out.
- Blendshape normal and tangent offsets are interpolated as they are at each collapse. Meshia normalizes them, as if
  they were directions: a shape that bends normals (a muscle flex) then reshades the mesh wherever an edge collapsed.
- An empty submesh no longer crashes the k-d tree (it read the first node of an empty list).
- Bits are counted only over non-empty ranges (Collections refuses a count of zero bits: an empty submesh).
- A lines or points submesh without vertices no longer stops the simplification (its bits were set with a count of zero).
- The k-d tree holds every point. Meshia 3.2.0 left the point just before the median out of each split (an upstream bug),
  so smart links never joined the vertices it dropped.
- Native memory is always freed: when a step throws, and when `SimplifyAsync` is cancelled (on the editor's thread, before
  a script reload, the jobs are completed and their memory freed before the cancel returns).
