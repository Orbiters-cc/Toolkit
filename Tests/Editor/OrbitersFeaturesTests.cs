using System.Collections.Generic;
using NUnit.Framework;
using Orbiters.Toolkit.Editor;
using UnityEditor;

namespace Orbiters.Toolkit.Editor.Tests
{
    public sealed class OrbitersFeaturesTests
    {
        private const string Base = "tests.orbiters-features.base", Child = "tests.orbiters-features.child";
        private readonly List<string> changed = new List<string>();

        [SetUp]
        public void SetUp()
        {
            Cleanup();
            OrbitersFeatures.Changed += changed.Add;
        }

        [TearDown]
        public void TearDown()
        {
            OrbitersFeatures.Changed -= changed.Add;
            Cleanup();
        }

        private void Cleanup()
        {
            changed.Clear();
            foreach (var key in new[] { Base, Child })
            {
                EditorPrefs.DeleteKey("Orbiters.Feature." + key);
                OrbitersFeatures.Unregister(key);
            }
        }

        [Test]
        public void RegisterDefaultsPrefKeyAndIsIdempotent()
        {
            var first = OrbitersFeatures.Register(new OrbitersFeature { Key = Base, Product = "Tests", Stage = FeatureStage.Alpha });
            var second = OrbitersFeatures.Register(new OrbitersFeature { Key = Base, Product = "Other", Default = true });

            Assert.AreSame(first, second);
            Assert.AreEqual("Orbiters.Feature." + Base, first.PrefKey);
            Assert.AreEqual(1, CountRegistered(Base));
            Assert.IsFalse(OrbitersFeatures.IsEnabled(Base));
        }

        [Test]
        public void DefaultAppliesUntilSet()
        {
            OrbitersFeatures.Register(new OrbitersFeature { Key = Base, Default = true });
            Assert.IsTrue(OrbitersFeatures.IsEnabled(Base));

            OrbitersFeatures.SetEnabled(Base, false);
            Assert.IsFalse(OrbitersFeatures.IsEnabled(Base));
            Assert.IsFalse(EditorPrefs.GetBool("Orbiters.Feature." + Base, true));
            CollectionAssert.AreEqual(new[] { Base }, changed);

            OrbitersFeatures.SetEnabled(Base, false);
            Assert.AreEqual(1, changed.Count, "Setting the same value again must not raise Changed.");
        }

        [Test]
        public void UnknownKeyIsOff()
        {
            Assert.IsFalse(OrbitersFeatures.IsEnabled("tests.orbiters-features.missing"));
        }

        [Test]
        public void RequiredFeatureGatesAndSwitchesOffDependents()
        {
            OrbitersFeatures.Register(new OrbitersFeature { Key = Base });
            OrbitersFeatures.Register(new OrbitersFeature { Key = Child, Requires = Base });

            OrbitersFeatures.SetEnabled(Child, true);
            Assert.IsFalse(OrbitersFeatures.IsEnabled(Child), "Off while the required feature is off.");

            OrbitersFeatures.SetEnabled(Base, true);
            Assert.IsTrue(OrbitersFeatures.IsEnabled(Child));

            OrbitersFeatures.SetEnabled(Base, false);
            OrbitersFeatures.SetEnabled(Base, true);
            Assert.IsFalse(OrbitersFeatures.IsEnabled(Child), "Switching the required feature off also switches dependents off.");
        }

        private static int CountRegistered(string key)
        {
            int count = 0;
            foreach (var feature in OrbitersFeatures.All) if (feature.Key == key) count++;
            return count;
        }
    }
}
