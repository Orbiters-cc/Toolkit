using System;
using System.Collections.Generic;
using System.Linq;
using MCPForUnity.Editor.Helpers;
using MCPForUnity.Editor.Tools;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace Orbiters.Toolkit.Editor
{
    [McpForUnityTool("orbiters_editor_window",
        Description = "List Unity editor windows or capture one window's actual UI to PNG, always in the background: Unity is never brought forward and the user's layout, tabs, scrolling and selection are left alone. " +
                      "A shown window is captured as it is. A window that is not open (open_if_missing), an inactive docked tab, or a capture with width/height/scroll_to is rendered in a hidden copy outside every display, closed afterwards. " +
                      "inspect captures a hidden Inspector locked on any object (instance ID, scene path like \"Root/Child\" or \"Scene:Root/Child\", or asset path) without selecting it; tall heights show long inspectors whole. Returns fullPath for image inspection.",
        RequiresPolling = true, PollAction = "status", MaxPollSeconds = 30)]
    public static class EditorWindowScreenshotTool
    {
        public class Parameters
        {
            [ToolParameter("list, capture, or status")] public string action { get; set; }
            [ToolParameter("Window instance ID from list", Required = false)] public int? window_id { get; set; }
            [ToolParameter("Exact full EditorWindow type name; ambiguous open instances require window_id", Required = false)] public string window_type { get; set; }
            [ToolParameter("Object to show in a hidden Inspector locked on it: instance ID, scene path (\"Root/Child\", \"Scene:Root/Child\") or asset path; replaces window_id/window_type", Required = false)] public string inspect { get; set; }
            [ToolParameter("Open a hidden window_type window when none is open (never focused)", Required = false)] public bool open_if_missing { get; set; }
            [ToolParameter("Hidden copy width in points (default: the window's own, or 520 for inspect)", Required = false)] public int width { get; set; }
            [ToolParameter("Hidden copy height in points, up to 8000 (default: the window's own, or 2400 for inspect); tall values show long windows whole", Required = false)] public int height { get; set; }
            [ToolParameter("Scroll a hidden copy so the first element whose name, USS class or text matches is at the top", Required = false)] public string scroll_to { get; set; }
            [ToolParameter("Maximum image edge, 64..8192, or 0 for native pixels; default 0", Required = false)] public int max_resolution { get; set; }
            [ToolParameter("Extra wait before the capture in milliseconds, 0..8000: lets animations settle or catches a later state", Required = false)] public int delay_ms { get; set; }
            [ToolParameter("Capture job identifier returned by capture", Required = false)] public string job_id { get; set; }
        }

        private sealed class Job
        {
            internal string Id;
            internal EditorWindow Window;
            internal bool Hidden;
            internal string ScrollTo;
            internal bool Scrolled;
            internal int MaxResolution;
            internal double Delay;
            internal double Started;
            internal int Ticks;
            internal object Result;
        }

        private static readonly Dictionary<string, Job> Jobs = new Dictionary<string, Job>();
        private static Job active;
        private static string latestJobId;

        public static object HandleCommand(JObject args)
        {
            try
            {
                var p = args?.ToObject<Parameters>() ?? throw new ArgumentException("Parameters are required.");
                switch (p.action)
                {
                    case "list":
                        return new SuccessResponse("Open Unity editor windows. shown=false means an inactive docked tab: capture still works, in a hidden copy.", Windows().Select(w => new
                        {
                            windowId = w.GetInstanceID(), windowType = w.GetType().FullName,
                            title = w.titleContent.text, focused = EditorWindow.focusedWindow == w, shown = EditorWindowCapture.IsShown(w),
                            width = w.position.width, height = w.position.height
                        }).ToArray());
                    case "capture": return Start(p);
                    case "status":
                        // MCP's polling middleware resends the original parameters with action=status.
                        // It does not copy the job_id from the PendingResponse into that request.
                        string id = p.job_id ?? latestJobId;
                        if (id == null || !Jobs.TryGetValue(id, out var job))
                            return new ErrorResponse("Unknown capture job. A domain reload clears jobs; request a new capture.");
                        return job.Result ?? Pending(job);
                    default: return new ErrorResponse("Expected action: list, capture, or status.");
                }
            }
            catch (Exception ex) { return new ErrorResponse(ex.Message); }
        }

        // Hidden copies made for captures are not the user's windows.
        private static EditorWindow[] Windows() => Resources.FindObjectsOfTypeAll<EditorWindow>().Where(w => (w.hideFlags & HideFlags.HideAndDontSave) != HideFlags.HideAndDontSave).ToArray();

        private static object Start(Parameters p)
        {
            if (Application.isBatchMode) throw new InvalidOperationException("A graphical Unity editor is required.");
            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
                throw new InvalidOperationException("Wait for Unity compilation and asset refresh to finish.");
            if (active != null) throw new InvalidOperationException("Another window capture is still pending.");
            if (p.max_resolution != 0 && (p.max_resolution < 64 || p.max_resolution > 8192))
                throw new ArgumentException("max_resolution must be 0 or between 64 and 8192.");
            if (p.delay_ms < 0 || p.delay_ms > 8000) throw new ArgumentException("delay_ms must be between 0 and 8000.");
            if (p.width < 0 || p.height < 0 || p.width > 8000 || p.height > 8000) throw new ArgumentException("width and height must be between 1 and 8000 points.");

            EditorWindow window;
            bool hidden;
            if (!string.IsNullOrWhiteSpace(p.inspect))
            {
                if (p.window_id.HasValue || !string.IsNullOrWhiteSpace(p.window_type)) throw new ArgumentException("inspect replaces window_id and window_type.");
                var target = EditorWindowHiddenHost.Resolve(p.inspect) ?? throw new ArgumentException("No object matches inspect: use an instance ID, a scene path such as \"Root/Child\" or an asset path.");
                window = EditorWindowHiddenHost.Inspector(target, new Vector2(p.width > 0 ? p.width : 520, p.height > 0 ? p.height : 2400));
                hidden = true;
            }
            else
            {
                if (p.window_id.HasValue == !string.IsNullOrWhiteSpace(p.window_type))
                    throw new ArgumentException("Specify exactly one of window_id, window_type and inspect.");
                var matches = Windows().Where(w => p.window_id.HasValue
                    ? w.GetInstanceID() == p.window_id.Value : w.GetType().FullName == p.window_type).ToArray();
                if (matches.Length > 1) throw new ArgumentException("Several windows match; select a window_id from list.");
                var open = matches.SingleOrDefault();
                if (open == null && !(p.open_if_missing && !string.IsNullOrWhiteSpace(p.window_type)))
                    throw new ArgumentException("Window not found. Use list, or set open_if_missing with window_type to capture a hidden one.");
                hidden = open == null || !EditorWindowCapture.IsShown(open) || p.width > 0 || p.height > 0 || !string.IsNullOrWhiteSpace(p.scroll_to);
                if (hidden)
                {
                    var type = open != null ? open.GetType() : TypeCache.GetTypesDerivedFrom<EditorWindow>().SingleOrDefault(t => t.FullName == p.window_type);
                    if (type == null || type.IsAbstract) throw new ArgumentException("No concrete EditorWindow type matches.");
                    var size = new Vector2(p.width > 0 ? p.width : open != null ? open.position.width : 520, p.height > 0 ? p.height : open != null ? open.position.height : 720);
                    window = EditorWindowHiddenHost.Open(type, size, open);
                }
                else window = open;
            }
            EditorWindowCapture.RepaintWithoutFocus(window);

            foreach (var key in Jobs.Where(pair => pair.Value.Result != null).Select(pair => pair.Key).ToArray())
                if (Jobs.Count >= 16) Jobs.Remove(key);
            var job = new Job { Id = Guid.NewGuid().ToString("N"), Window = window, Hidden = hidden, ScrollTo = string.IsNullOrWhiteSpace(p.scroll_to) ? null : p.scroll_to.Trim(),
                MaxResolution = p.max_resolution, Delay = p.delay_ms / 1000d, Started = EditorApplication.timeSinceStartup };
            Jobs.Add(job.Id, job);
            latestJobId = job.Id;
            active = job;
            window.Repaint();
            EditorApplication.update += Tick;
            EditorApplication.QueuePlayerLoopUpdate();
            return Pending(job);
        }

        private static PendingResponse Pending(Job job) => new PendingResponse(
            "Waiting for the editor window to repaint.", 0.5, new { job_id = job.Id });

        private static void Tick()
        {
            var job = active;
            if (job == null) { EditorApplication.update -= Tick; return; }
            try
            {
                double elapsed = EditorApplication.timeSinceStartup - job.Started;
                if (elapsed > 20 + job.Delay) throw new TimeoutException("Window capture timed out; retry when Unity is responsive.");
                if (job.Window == null) throw new InvalidOperationException("The window closed before capture.");
                // Yield to Unity's event loop; sleeping on the editor thread cannot complete a repaint. A hidden copy builds its
                // UI first, then scrolls, then lays out once more.
                if (++job.Ticks < 3 || elapsed < (job.Hidden ? 1.0 : 0.35) + job.Delay) { job.Window.Repaint(); return; }
                if (job.ScrollTo != null && !job.Scrolled)
                {
                    if (!EditorWindowHiddenHost.ScrollTo(job.Window, job.ScrollTo))
                    {
                        if (elapsed > 6) throw new InvalidOperationException("Nothing in the window matches scroll_to \"" + job.ScrollTo + "\".");
                        job.Window.Repaint(); return;
                    }
                    job.Scrolled = true; job.Ticks = 0; job.Started = EditorApplication.timeSinceStartup - 0.7;
                    job.Window.Repaint(); return;
                }
                EditorWindowCapture.RepaintWithoutFocus(job.Window);
                job.Result = new SuccessResponse("Editor window screenshot saved.", EditorWindowCapture.Save(job.Window, job.MaxResolution, job.Hidden));
            }
            catch (Exception ex) { job.Result = new ErrorResponse(ex.Message); }
            if (job.Result == null) return;
            active = null;
            EditorApplication.update -= Tick;
            if (job.Hidden && job.Window != null) job.Window.Close();
        }
    }
}
