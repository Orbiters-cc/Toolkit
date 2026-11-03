using System.Collections.Generic;
using NUnit.Framework;
using Newtonsoft.Json.Linq;
using Orbiters.Toolkit.Editor.Preview;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

/// <summary>
/// The light a 3D preview records (contracts' Docs/project-preview.md, <c>lighting</c>) for a scene without a sun whose
/// ambient probe the editor had not computed yet: no lights and <c>ambient: null</c>, which Orbiters - N4N0 draws in its
/// studio light. A black probe is computed before it counts as unknown; an ambient intensity of 0 is black, known.
/// </summary>
public sealed class PreviewLightingTests
{
    private Scene scene;

    [SetUp] public void SetUp() => scene = EditorSceneManager.NewPreviewScene();
    [TearDown] public void TearDown() => EditorSceneManager.ClosePreviewScene(scene);

    private Light Add(string name, LightType type, bool enabled = true, string tag = "Untagged")
    {
        var go = new GameObject(name) { tag = tag };
        SceneManager.MoveGameObjectToScene(go, scene);
        var light = go.AddComponent<Light>();
        light.type = type;
        light.enabled = enabled;
        return light;
    }

    private PreviewData.SceneLighting Read() =>
        PreviewLighting.Read(new HashSet<Scene> { scene }, t => t.CompareTag("EditorOnly"));

    private static SphericalHarmonicsL2 Flat(Color c)
    {
        var sh = new SphericalHarmonicsL2();
        sh.AddAmbientLight(c);
        return sh;
    }

    [Test] public void OnlyEnabledDirectionalLightsCount()
    {
        Add("Lamp", LightType.Point);
        Add("Spot", LightType.Spot);
        Add("Off", LightType.Directional, enabled: false);
        Add("Editor sun", LightType.Directional, tag: "EditorOnly");
        Assert.That(Read().Lights, Is.Empty, "no sun: a point and a spot light, a disabled and an EditorOnly sun");
        var sun = Add("Sun", LightType.Directional);
        sun.intensity = 0.5f;
        var lights = Read().Lights;
        Assert.That(lights.Count, Is.EqualTo(1));
        Assert.That(lights[0].Intensity, Is.EqualTo(0.5f));
    }

    [Test] public void NoSunAndAProbeStillBlackAfterComputingWriteNoLightsAndNoAmbient()
    {
        var lighting = new PreviewData.SceneLighting { AmbientMode = "skybox" };
        int computed = 0;
        PreviewLighting.SkyboxAmbient(lighting, 1, () => new SphericalHarmonicsL2(), () => computed++);
        Assert.That(computed, Is.EqualTo(1), "a black probe is computed first");
        Assert.That(lighting.Sky.HasValue || lighting.Equator.HasValue || lighting.Ground.HasValue, Is.False);
        var json = PreviewFile.Lighting(lighting);
        Assert.That((JArray)json["lights"], Is.Empty);
        Assert.That(json["ambient"].Type, Is.EqualTo(JTokenType.Null), "unknown: N4N0 draws the studio light");
    }

    [Test] public void AProbeNotComputedYetIsComputedAndWritten()
    {
        var lighting = new PreviewData.SceneLighting { AmbientMode = "skybox" };
        var sky = new Color(0.108f, 0.145f, 0.171f);
        bool computed = false;
        PreviewLighting.SkyboxAmbient(lighting, 1, () => computed ? Flat(sky) : new SphericalHarmonicsL2(), () => computed = true);
        Assert.That(computed, Is.True);
        foreach (var v in new[] { lighting.Sky, lighting.Equator, lighting.Ground })
        {
            Assert.That(v.HasValue, Is.True);
            Assert.That(Vector3.Distance(v.Value, new Vector3(sky.r, sky.g, sky.b)), Is.LessThan(1e-4f));
        }
        Assert.That(PreviewFile.Lighting(lighting)["ambient"]["sky"][2].Value<float>(), Is.EqualTo(0.171f).Within(1e-3f));
    }

    [Test] public void AComputedProbeIsNotComputedAgain()
    {
        var lighting = new PreviewData.SceneLighting { AmbientMode = "skybox" };
        PreviewLighting.SkyboxAmbient(lighting, 1, () => Flat(new Color(0.2f, 0.2f, 0.2f)), () => Assert.Fail("computed again"));
        Assert.That(lighting.Sky.Value.x, Is.EqualTo(0.2f).Within(1e-4f));
    }

    [Test] public void AmbientIntensityZeroIsBlackAndKnown()
    {
        var lighting = new PreviewData.SceneLighting { AmbientMode = "skybox" };
        PreviewLighting.SkyboxAmbient(lighting, 0, () => Flat(Color.white), () => Assert.Fail("nothing to compute"));
        Assert.That(lighting.Sky, Is.EqualTo(Vector3.zero));
        var ambient = PreviewFile.Lighting(lighting)["ambient"];
        Assert.That(ambient.Type, Is.EqualTo(JTokenType.Object), "black, not unknown");
        Assert.That(ambient["ground"].ToObject<float[]>(), Is.EqualTo(new[] { 0f, 0f, 0f }));
    }

    [Test] public void TheEditorComputesABlackProbeAtOnce()
    {
        // What the capture relies on: DynamicGI.UpdateEnvironment fills the open scene's probe in the same call (the
        // default sky's, or the Ambient Color without a skybox), and the scene stays as it was.
        if (RenderSettings.ambientMode != AmbientMode.Skybox || RenderSettings.ambientIntensity <= 0)
            Assert.Ignore("the open scene's ambient light does not come from its skybox");
        var active = SceneManager.GetActiveScene();
        bool dirty = active.isDirty;
        var before = RenderSettings.ambientProbe;
        try
        {
            RenderSettings.ambientProbe = new SphericalHarmonicsL2();
            var lighting = new PreviewData.SceneLighting { AmbientMode = "skybox" };
            PreviewLighting.SkyboxAmbient(lighting, RenderSettings.ambientIntensity, () => RenderSettings.ambientProbe, DynamicGI.UpdateEnvironment);
            Assert.That(lighting.Sky.HasValue, Is.True, "computed at once");
            Assert.That(Mathf.Max(lighting.Sky.Value.x, lighting.Sky.Value.y, lighting.Sky.Value.z), Is.GreaterThan(0.01f));
            Assert.That(active.isDirty, Is.EqualTo(dirty), "the scene unchanged");
        }
        finally
        {
            RenderSettings.ambientProbe = before;
        }
    }
}
