namespace Orbiters.Toolkit.XRay
{
    public enum XRayArmatureMeshType
    {
        Pyramid,
        Tapered,
        Box
    }

    public enum XRayGizmoTargetMode
    {
        SelectedObject,
        PinnedObject,
        WholeScene
    }

    /// <summary>Where the XRay editor files live inside the Toolkit package.</summary>
    public static class XRayPaths
    {
        public const string Base = "Packages/orbiters.toolkit/Editor/XRay";
    }
}
