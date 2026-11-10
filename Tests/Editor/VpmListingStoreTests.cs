#if ORBITERS_VPM
using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Orbiters.Toolkit.Editor.Vpm;

namespace Orbiters.Toolkit.Editor.Tests
{
    // TKV-30: each VPM listing's last copy is kept with its validators, so the repository is only asked whether it changed.
    public sealed class VpmListingStoreTests
    {
        private string folder;

        [SetUp] public void SetUp()
        {
            folder = Path.Combine(Path.GetTempPath(), "OrbitersVpmListings-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            ListingStore.Folder = folder;
        }

        [TearDown] public void TearDown()
        {
            ListingStore.Folder = null;
            if (folder.Contains("OrbitersVpmListings-") && Directory.Exists(folder)) Directory.Delete(folder, true);
        }

        private const string Listing = "{\"packages\":{\"com.example.tool\":{\"versions\":{\"1.0.0\":{\"displayName\":\"Tool\"},\"1.2.0\":{\"vpmDependencies\":{\"com.vrchat.base\":\">=3.7.0\"}}}}}}";

        [Test] public void AListingsCopyComesBackWithItsValidators()
        {
            const string url = "https://example.com/index.json";
            Assert.That(ListingStore.Read(url), Is.Null);
            ListingStore.Write(url, Listing, "\"v1\"", null);
            var entry = ListingStore.Read(url);
            Assert.That(entry.etag, Is.EqualTo("\"v1\""));
            Assert.That(entry.body, Is.EqualTo(Listing));
            // The copy parses to what the download did.
            var fromCopy = VpmListingReader.Parse(entry.body, url);
            var fromDownload = VpmListingReader.Parse(Listing, url);
            Assert.That(fromCopy.Select(c => c.Id + "@" + c.Version), Is.EqualTo(fromDownload.Select(c => c.Id + "@" + c.Version)));
            Assert.That(fromCopy.First().Version, Is.EqualTo("1.2.0"), "newest first");

            Assert.That(ListingStore.Read("https://example.com/other.json"), Is.Null, "another listing's copy is not this one's");
            ListingStore.Forget(url);
            Assert.That(ListingStore.Read(url), Is.Null);
        }

        [Test] public void ACopyWithoutValidatorsOrUnreadableCountsAsNone()
        {
            const string url = "https://example.com/index.json";
            ListingStore.Write(url, Listing, null, null);
            Assert.That(ListingStore.Read(url), Is.Null, "nothing to ask the server with");
            ListingStore.Write(url, Listing, null, "Tue, 10 Nov 2026 10:00:00 GMT");
            Assert.That(ListingStore.Read(url)?.lastModified, Is.EqualTo("Tue, 10 Nov 2026 10:00:00 GMT"));
            foreach (string file in Directory.GetFiles(folder)) File.WriteAllText(file, "{ not json");
            Assert.That(ListingStore.Read(url), Is.Null);
        }
    }
}
#endif
