using NUnit.Framework;

namespace Orbiters.Toolkit.Editor.Tests
{
    public sealed class VrcLinksTests
    {
        [Test]
        public void AvatarPageIsOnVrchatHome()
        {
            Assert.AreEqual("https://vrchat.com/home/avatar/avtr_b11054d2-80c6-47c2-a3a9-9514f9084587",
                VrcLinks.AvatarPage(" avtr_B11054D2-80c6-47c2-a3a9-9514f9084587 "));
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("wrld_b11054d2-80c6-47c2-a3a9-9514f9084587")]
        [TestCase("avtr_b11054d2")]
        [TestCase("avtr_b11054d2-80c6-47c2-a3a9-9514f9084587/../../user")]
        public void AnythingElseHasNoPage(string id)
        {
            Assert.IsFalse(VrcLinks.IsAvatarId(id));
            Assert.IsNull(VrcLinks.AvatarPage(id));
        }
    }
}
