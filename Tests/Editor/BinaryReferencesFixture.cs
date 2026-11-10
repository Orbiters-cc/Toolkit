using UnityEngine;

/// <summary>
/// Tests: an asset Unity writes in its binary format whatever the project's serialization mode (a Force Text project
/// too), as LightingData or NavMesh data are: what it uses can only be known from Unity's import of it.
/// </summary>
[PreferBinarySerialization]
public sealed class BinaryReferencesFixture : ScriptableObject
{
    public Object used;
}
