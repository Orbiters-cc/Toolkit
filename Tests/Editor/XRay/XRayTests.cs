using NUnit.Framework;

namespace Orbiters.Toolkit.Editor.Tests.XRay
{
    /// <summary>
    /// The XRay self-checks as NUnit tests. Each static <c>RunOrThrow</c> class keeps its own entry point so the checks
    /// can also be run by reflection from the editor without the Test Runner.
    /// </summary>
    public class XRayTests
    {
        [Test] public void Geometry() => XRayGeometryTests.RunOrThrow();
        [Test] public void Picking() => XRayPickingTests.RunOrThrow();
        [Test] public void TargetFinder() => XRayTargetFinderTests.RunOrThrow();
        [Test] public void BlendShapes() => XRayBlendShapeTests.RunOrThrow();
        [Test] public void EdgeSkinning() => XRayEdgeSkinningTests.RunOrThrow();
        [Test] public void EdgeSkinningAllInfluences() => XRayEdgeSkinningTests.AllInfluencesOrThrow();
    }
}
